using System.IO;
using System.Threading.Tasks;
using UnityEngine;

// FireFlipbookGen —— 程序化生成"无缝循环"的火焰/烟雾 flipbook（图集 = 数据贴图）
//
// 为什么要自己造这个"电锯"：调研（docs/技术/游戏特效与火焰调研.md §2.3）里，业界火焰素材走
// Houdini/EmberGen 离线模拟 → 导出图集。本项目是 21 天、Windows 单机、路边氛围火（"赛车级精细度"），
// 不值得为它上模拟管线。所以这里直接**用解析场合成图集**。
//
// 核心思想（本文件唯一的技术难点，也是它能"无缝循环"的原因）：
//   一切随时间 t 变化的频率都取 **整数**：
//     · 湍流/摆动：sin(2π·(fx·u + fy·v + ft·t))，ft ∈ ℤ（空间频率见下）
//     · 单根火舌的"升腾次数"：life = frac(t·k + off)，k ∈ ℤ
//   于是所有量对 t 都以 1 为周期 → 把 t 采样成 t = f/N（f = 0..N-1）循环播放，
//   **数学上就没有接缝**（不靠首尾叠化、不靠手工修补）。
//   火舌两端再用归零包络，保证"生/灭"都发生在不可见处，最后一帧回到第一帧看不出跳。
//   空间频率分两套用途：Flow（域扭曲/侵蚀，会被别的量乘掉）用整数，省事；
//   NoiseField（闪点，要被高阈值"切"出来）必须用**非整数**，否则切出规则点阵。
//
// 通道打包（省 samplers，与调研里的做法一致）：
//   R = 密度(alpha)   G = 温度(喂黑体配色)   B = 高温闪点(余烬跳光)   A = 备用
// 重要：这是**数据贴图**，导入时必须 sRGB = false，否则黑体温度被伽马二次污染。
//
// 线程：数值计算走 Parallel.For（纯数学、不碰 Unity API）；Texture2D 的创建与编码回主线程。
public static class FireFlipbookGen
{
    // ------------------------------------------------------------------
    // 风格参数：三套火（路边 / 驻足 / 烟）只是同一套公式换数字
    // [Serializable] 是给 RuinsFireConfig 用的：这套风格参数要能搬进 ScriptableObject 资产，
    // 在 Inspector 里改、进版本库 —— 而不是每次调参都改 C# 字面量再等重编译。
    // 下面三个静态属性仍保留，作为"没接配置资产时的兜底默认值"。
    // ------------------------------------------------------------------
    [System.Serializable]
    public struct FlameStyle
    {
        public int cols, rows, tile;
        public int gutter;         // 每 tile 四周留空的"隔离带"（防 mip 相邻 tile 渗色）
        public int puffs;          // 火舌根数
        public int maxCycles;      // 单根火舌一个循环里升腾几次（整数！决定无缝）
        public int swayHarmonics;  // 摆动谐波上限（整数）
        public float rise;         // 竖直行程（占片高比例）
        public float spread;       // 底部横向铺开
        public float tallStretch;  // 火舌竖直拉长比
        public float sizeMin, sizeMax;
        public float swayAmp;
        public float erosion;      // 轮廓侵蚀强度
        public float contrast;     // 密度对比
        public float coreHot;      // 底部炽核强度
        public float baseV;        // 火舌最低点（0 = 片底）
        public float tempSpread;   // 温度纵向落差
        public float hotspot;      // 闪点密度
        public float sideTaper;    // 左右收边
        public float topTaper;     // 顶部收边起点
    }

    // 掠过档：低矮、温和、偏矮（调研 #11482：小火应"更柔的火苗 + 上升气流"，不是喷射）
    public static FlameStyle RoadsideFlame => new FlameStyle
    {
        cols = 8, rows = 8, tile = 160, gutter = 6,
        puffs = 30, maxCycles = 2, swayHarmonics = 2,
        rise = 0.78f, spread = 0.23f, tallStretch = 2.15f,
        sizeMin = 0.045f, sizeMax = 0.115f,
        swayAmp = 0.05f, erosion = 0.30f, contrast = 1.25f, coreHot = 0.85f,
        baseV = 0.09f, tempSpread = 1.0f, hotspot = 0.30f,
        sideTaper = 0.30f, topTaper = 0.86f,
    };

    // 驻足档：更高、更厚，允许被看久一点
    public static FlameStyle LingerFlame => new FlameStyle
    {
        cols = 8, rows = 8, tile = 224, gutter = 8,
        puffs = 34, maxCycles = 2, swayHarmonics = 2,
        rise = 0.82f, spread = 0.26f, tallStretch = 2.3f,
        sizeMin = 0.042f, sizeMax = 0.110f,
        swayAmp = 0.065f, erosion = 0.28f, contrast = 1.35f, coreHot = 1.0f,
        baseV = 0.07f, tempSpread = 1.0f, hotspot = 0.36f,
        sideTaper = 0.32f, topTaper = 0.88f,
    };

    // 烟：淡、宽、慢（调研 #15461：木火几乎看不到黑烟，只做很薄一层）
    public static FlameStyle Smoke => new FlameStyle
    {
        cols = 6, rows = 6, tile = 128, gutter = 6,
        puffs = 14, maxCycles = 1, swayHarmonics = 1,
        rise = 0.85f, spread = 0.34f, tallStretch = 1.35f,
        sizeMin = 0.14f, sizeMax = 0.30f,
        swayAmp = 0.09f, erosion = 0.26f, contrast = 0.80f, coreHot = 0f,
        baseV = 0.22f, tempSpread = 0.25f, hotspot = 0f,
        sideTaper = 0.36f, topTaper = 0.55f,
    };

    // ------------------------------------------------------------------
    // 入口
    // ------------------------------------------------------------------
    public static string GenerateFlameSheet(string path, FlameStyle style, int seed, bool smokeMode = false)
    {
        Color32[] px = RenderSheet(style, seed, smokeMode);
        return WriteTexture(path, style.cols * style.tile, style.rows * style.tile, px);
    }

    public static string GenerateSmokeSheet(string path, int seed)
        => GenerateFlameSheet(path, Smoke, seed, smokeMode: true);

    // 光晕：单帧径向渐变（"加色光斑"用；也是远档唯一可能保留的东西）
    public static string GenerateGlow(string path, int size = 256)
    {
        var px = new Color32[size * size];
        float c = (size - 1) * 0.5f;
        Parallel.For(0, size, y =>
        {
            for (int x = 0; x < size; x++)
            {
                float dx = (x - c) / c, dy = (y - c) / c;
                float rr = Mathf.Sqrt(dx * dx + dy * dy);
                // 纯柔和高斯 + r→1 处平滑归零：加色贴在暗面上不能出现"可见圆盘"边
                float d = Mathf.Exp(-(rr * rr) * 3.0f) * (1f - Mathf.Clamp01(rr * rr));
                px[y * size + x] = Pack(Mathf.Clamp01(d), 0f, 0f);
            }
        });
        return WriteTexture(path, size, size, px);
    }

    // 余烬/火星：小而亮的点 + 软晕。
    // 注意：这张是**颜色**贴图（被 URP 官方 Particles/Unlit 当 RGB 读），
    // 所以必须写成灰度（R=G=B=密度）——早先误按数据通道打包(G=温度=1)，
    // 结果余烬的晕圈被当绿色渲染（渲染图里那些绿点就是这么来的）。
    public static string GenerateEmber(string path, int size = 96)
    {
        var px = new Color32[size * size];
        float c = (size - 1) * 0.5f;
        Parallel.For(0, size, y =>
        {
            for (int x = 0; x < size; x++)
            {
                float dx = (x - c) / c, dy = (y - c) / c;
                float rr = Mathf.Sqrt(dx * dx + dy * dy);
                float core = Mathf.Exp(-(rr * rr) / 0.16f);
                float halo = Mathf.Exp(-rr * rr * 3.0f) * 0.35f;
                float d = Mathf.Clamp01(core + halo) * (1f - Mathf.Clamp01(rr * rr));
                d = Mathf.Clamp01(d);
                px[y * size + x] = Pack(d, d, d);
            }
        });
        return WriteTexture(path, size, size, px);
    }

    // ------------------------------------------------------------------
    // 图集合成
    // ------------------------------------------------------------------
    static Color32[] RenderSheet(FlameStyle s, int seed, bool smokeMode)
    {
        int N = s.cols * s.rows;
        int W = s.cols * s.tile;
        int H = s.rows * s.tile;
        var px = new Color32[W * H];

        // 火舌参数：确定性 LCG（不碰 UnityEngine.Random 的全局状态，保证可复现）
        var rnd = new Lcg((uint)(seed * 2654435761u + 12345u));
        var pBaseX = new float[s.puffs];
        var pOffset = new float[s.puffs];
        var pCycles = new int[s.puffs];   // 升腾次数（整数 → 无缝）
        var pSwayH = new int[s.puffs];    // 摆动谐波（整数 → 无缝）
        var pSize = new float[s.puffs];
        var pAmp = new float[s.puffs];
        var pPhase = new float[s.puffs];

        for (int i = 0; i < s.puffs; i++)
        {
            pBaseX[i] = (rnd.Next01() * 2f - 1f) * s.spread;
            pOffset[i] = rnd.Next01();
            pCycles[i] = 1 + (int)(rnd.Next01() * s.maxCycles * 0.999f);
            pSwayH[i] = 1 + (int)(rnd.Next01() * s.swayHarmonics * 0.999f);
            pSize[i] = Mathf.Lerp(s.sizeMin, s.sizeMax, Mathf.Pow(rnd.Next01(), 0.8f));
            pAmp[i] = 0.35f + rnd.Next01() * 0.9f;
            pPhase[i] = rnd.Next01() * Mathf.PI * 2f;
        }

        Parallel.For(0, H, globalY =>
        {
            // px 数组经 SetPixels32 后 y=0 是**纹理底部**（v≈0）。"第 r 行 tile（自图片顶端数）"
            // 占据的纹理 v 区间是 [1-(r+1)/rows, 1-r/rows]，tile 内 localY 自该区间**底部**起算：
            //   r = ⌊(H-1-y)/tile⌋，localY = y - (H - (r+1)·tile)
            // （早先误用取模 (H-1-y)%tile → 整片上下翻转，热区跑到顶上）
            int tileRowFromTop = (H - 1 - globalY) / s.tile;
            int localY = globalY - (H - (tileRowFromTop + 1) * s.tile);
            float v0 = (localY + 0.5f) / s.tile;

            for (int globalX = 0; globalX < W; globalX++)
            {
                int col = globalX / s.tile;
                int localX = globalX % s.tile;
                float u0 = (localX + 0.5f) / s.tile;

                // gutter 隔离带：tile 四周留空。妙处在于火的密度本来就在 tile 边缘收到 0，
                // 所以"留空"与火自身的边缘一致 —— mip 平均时拉到的是透明而不是隔壁 tile 的亮火。
                if (localX < s.gutter || localX >= s.tile - s.gutter ||
                    localY < s.gutter || localY >= s.tile - s.gutter)
                {
                    px[globalY * W + globalX] = new Color32(0, 0, 0, 255);
                    continue;
                }

                float t = (float)(tileRowFromTop * s.cols + col) / N;   // 0..1

                EvalPixel(s, u0, v0, t, smokeMode,
                          pBaseX, pOffset, pCycles, pSwayH, pSize, pAmp, pPhase,
                          out float density, out float temp, out float hot);

                // 8bit 量化前加 4×4 有序抖动，压掉温度/密度上的色带
                float dith = (Bayer4(localX, localY) - 0.5f) / 255f;
                px[globalY * W + globalX] = Pack(
                    Mathf.Clamp01(density + dith),
                    Mathf.Clamp01(temp + dith * 0.6f),
                    hot);
            }
        });

        return px;
    }

    static void EvalPixel(FlameStyle s, float u, float v, float t, bool smokeMode,
        float[] pBaseX, float[] pOffset, int[] pCycles, int[] pSwayH, float[] pSize, float[] pAmp, float[] pPhase,
        out float density, out float temp, out float hot)
    {
        // ---- 周期湍流场（fx/fy/ft 全为整数 → 对 t 严格周期 1）----
        float flowA = Flow(u, v, t, 2, 3, 1) * 0.6f + Flow(u, v, t, 3, 1, 2) * 0.4f;
        float flowB = Flow(u * 1.7f, v * 0.8f, t, 1, 2, 2);
        float flowC = Flow(u, v, t, 5, 4, 3);
        float flowD = Flow(u * 2.3f, v * 1.6f, t, 6, 9, 5);   // 高频：把火舌边缘咬成缕

        float uu = u + flowA * 0.045f;      // 域扭曲：火舌不再是规则椭圆
        float vv = v + flowB * 0.03f;

        // acc = 密度权重和；hAcc = Σ(权重·温度) —— T = hAcc/acc 由构造保证落在 [0,1]，
        // 不会再出现"炽核只加半个密度却加满温度"把整片温度顶到饱和的毛病。
        float acc = 0f, hAcc = 0f;

        for (int i = 0; i < s.puffs; i++)
        {
            float life = Frac(t * pCycles[i] + pOffset[i]);         // k 为整数 → 周期 1

            // 两端归零的包络：生/灭都在不可见处，循环看不到跳
            float env = Mathf.Pow(Mathf.Sin(Mathf.PI * life), 0.85f)
                      * Smoothstep(0f, 0.14f, life)
                      * Smoothstep(0f, 0.14f, 1f - life);
            if (env <= 0.0005f) continue;

            float y = s.baseV + life * s.rise + flowB * 0.02f;
            // 摆动：谐波整数 → 同样周期性
            float xWob = pBaseX[i] + Mathf.Sin(2f * Mathf.PI * (t * pSwayH[i] + pPhase[i] / (2f * Mathf.PI))) * s.swayAmp * pAmp[i];

            float r = pSize[i] * (0.45f + 1.15f * life) * (1f - 0.18f * life);

            float dx = (uu - (0.5f + xWob)) / r;
            float dy = (vv - y) / (r * s.tallStretch);
            float q = 1f - Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy));

            float puff = q * q * (3f - 2f * q) * env;

            // 轮廓侵蚀：上部被湍流"咬"成火舌
            if (puff > 0f)
            {
                float bite = 1f - s.erosion * Smoothstep(0.15f, 0.85f, v)
                                  * (0.75f * Mathf.Clamp01(flowC * 0.5f + 0.5f)
                                   + 0.25f * Mathf.Clamp01(flowD * 0.5f + 0.5f));
                puff *= Mathf.Clamp01(bite);
            }

            // 越年轻越热：年轻火舌在底部出生（y 小），于是"底白黄、顶橙红"的梯度自然成立
            float ti = Mathf.Clamp01(Mathf.Pow(1f - life, 1.25f) * s.tempSpread);
            acc += puff;
            hAcc += puff * ti;
        }

        // ---- 底部炽核：火的"根"，热且实 ----
        float cdx = (u - 0.5f) / 0.13f;
        float cdy = (v - (s.baseV + 0.05f)) / 0.19f;
        float core = Mathf.Exp(-(cdx * cdx + cdy * cdy) * 2.2f) * s.coreHot;
        float cw = core * 0.5f;
        acc += cw;
        hAcc += cw * 1f;   // 炽核是最热的（温度 1）

        // ---- 密度整形 ----
        float d = acc * 0.72f;
        d = 1f - Mathf.Exp(-d * s.contrast * 2.2f);                       // 软化饱和，防死白
        d *= Mathf.Pow(Mathf.Clamp01(1f - Mathf.Abs(u - 0.5f) / (0.5f * Mathf.Max(0.25f, 1f - s.sideTaper * v))), 0.9f);
        d *= 1f - Smoothstep(s.topTaper, 1.0f, v);
        d *= Smoothstep(0f, smokeMode ? 0.18f : 0.05f, v);
        d = Mathf.Clamp01(d);

        // ---- 温度 → 0..1（喂黑体）----
        float T = acc > 1e-4f ? hAcc / acc : 0f;            // 构造上 ∈ [0,1]
        T *= 1f - 0.42f * Smoothstep(0.10f, 1.0f, v);        // 越往上越冷 → 明显的"底白顶红"
        T *= Smoothstep(0.02f, 0.14f, d);      // 密度≈0 处温度归零（数据贴图要干净）
        T = Mathf.Pow(Mathf.Clamp01(T), 0.85f);
        if (smokeMode) T = 0.12f;   // 烟：恒定"冷"，颜色交给 shader 的 flat tint

        // ---- 高温闪点（B 通道）：稀疏地出现在有密度的中下部 ----
        // 阈值切的是值噪声 fBm（实测 mean≈0.47、σ≈0.143、max≈0.86）。
        // 0.68 是 ~93 分位 → 每个 tile 覆盖 1.0~3.5%、全亮仅 0.22%：是"稀疏闪点"不是"满身麻点"。
        // 阈值与分布用 tools/noise_field_probe.py 在 Python 里量出来定的（不用改一次跑一次 Unity）。
        float hp = 0f;
        if (s.hotspot > 0f)
        {
            float sp = NoiseField(u, v, t);             // ∈ [0,1]，均值≈0.47
            hp = Mathf.Clamp01(Smoothstep(0.68f, 0.82f, sp)
                             * Mathf.Clamp01(d * 1.4f)
                             * (1f - Smoothstep(0.25f, 0.8f, v))
                             * s.hotspot * 2f);
        }

        density = d; temp = T; hot = hp;
    }

    // 周期场：Σ a_k·sin(2π(fx·u + fy·v + ft·t) + φ)，fx/fy/ft 均为整数 → 对 t 周期严格为 1。
    // 注意：**固定相位 + 整数频率**意味着它的极值点是规则格点，所以它只适合做"域扭曲/侵蚀"
    // 这类要被别的东西乘掉的量；**不能拿高阈值直接切它当斑点**（会切出规则点阵，见 NoiseField）。
    static float Flow(float u, float v, float t, int fx, int fy, int ft)
    {
        float f = 0f;
        f += Mathf.Sin(2f * Mathf.PI * (fx * u + fy * v + ft * t));
        f += 0.6f * Mathf.Sin(2f * Mathf.PI * ((fx + 1) * u - fy * v + (ft + 1) * t) + 1.3f);
        f += 0.4f * Mathf.Sin(2f * Mathf.PI * (fx * u + (fy + 2) * v + ft * t) + 2.7f);
        f += 0.3f * Mathf.Sin(2f * Mathf.PI * ((fx + 2) * u + fy * v + (ft + 2) * t) + 0.4f);
        return f / 2.3f;
    }

    // ------------------------------------------------------------------
    // 闪点用的噪声场 —— 专治"规则网格"
    //
    // 教训（第一版怎么错的）：拿"几个正弦叠加"当噪声，再用高阈值切片，**切出来必然是网格**。
    // 原因是平面波叠加后的极值集合落在规则格点上（莫尔）；而当时每个 tile 里 t 是常量，
    // 叠加式还会退化成纯空间平面波，fx/fy 又都限制在 ±轴向且量级相近 → 叠成斜条纹。
    // 换成"随机相位多八度"也没救：只要基元是正弦，方向偏置就一直存在。
    //
    // 正确做法：**值噪声 fBm**（哈希格点 + 平滑插值 + 多八度），各向同性、无方向偏置，
    // 水平集是有机斑块而不是格点。两个约束同时满足：
    //   · 格点索引按 period 环绕 → 空间可平铺（V 方向不留缝）
    //   · 时间只用来做**整数频率的空间平流**（ox/oy 是 sin/cos(2π·k·t)，k ∈ ℤ）
    //     → 场对 t 严格周期 1，接过循环依然无缝
    // ------------------------------------------------------------------
    const int NoiseOctaves = 5;
    const int NoiseBasePeriod = 8;   // 基八度横向格点数（= 可平铺周期）
    const float NoiseLacunarity = 2f;
    const float NoiseAmpDecay = 0.5f;

    static readonly float NoiseNorm = ComputeNoiseNorm();
    static float ComputeNoiseNorm()
    {
        float n = 0f, a = 1f;
        for (int k = 0; k < NoiseOctaves; k++) { n += a; a *= NoiseAmpDecay; }
        return n;
    }

    static float Hash01(int x, int y, int period)
    {
        int xi = ((x % period) + period) % period;
        int yi = ((y % period) + period) % period;
        uint h = (uint)(xi * 374761393 + yi * 668265263);
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;
        return (h & 0xFFFFFFu) * (1f / 16777215f);
    }

    // 值噪声：格点哈希 + smoothstep 双线性插值，x/y 按 period 环绕 → 可平铺
    static float ValueNoise(float x, float y, int period)
    {
        int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
        float fx = x - x0, fy = y - y0;
        float ux = fx * fx * (3f - 2f * fx), uy = fy * fy * (3f - 2f * fy);
        float a = Hash01(x0, y0, period), b = Hash01(x0 + 1, y0, period);
        float c = Hash01(x0, y0 + 1, period), d = Hash01(x0 + 1, y0 + 1, period);
        return Mathf.Lerp(Mathf.Lerp(a, b, ux), Mathf.Lerp(c, d, ux), uy);
    }

    // fBm：叠加 5 个八度（幅值 1/0.5/0.25/0.125/0.0625，周期同步翻倍以保持可平铺）
    // 平流量 ox/oy 只含整数频率 → 对 t 周期严格 1
    static float NoiseField(float u, float v, float t)
    {
        float tw = 2f * Mathf.PI * t;
        float ox = 1.10f * Mathf.Sin(tw) + 0.55f * Mathf.Sin(3f * tw) + 0.25f * Mathf.Sin(5f * tw);
        float oy = 1.10f * Mathf.Cos(2f * tw) + 0.55f * Mathf.Sin(7f * tw) + 0.25f * Mathf.Cos(5f * tw);
        float s = 0f, amp = 1f;
        int per = NoiseBasePeriod;
        for (int k = 0; k < NoiseOctaves; k++)
        {
            s += amp * ValueNoise(u * per + ox, v * per + oy, per);
            amp *= NoiseAmpDecay;
            per = Mathf.RoundToInt(per * NoiseLacunarity);
        }
        return s / NoiseNorm;   // ∈ [0,1]，≈ 均值 0.5
    }

    // ------------------------------------------------------------------
    // 工具
    // ------------------------------------------------------------------
    static float Frac(float x) => x - Mathf.Floor(x);

    static float Smoothstep(float a, float b, float x)
    {
        float t = Mathf.Clamp01((x - a) / Mathf.Max(1e-5f, b - a));
        return t * t * (3f - 2f * t);
    }

    static readonly float[] Bayer = {
         0f,  8f,  2f, 10f,
        12f,  4f, 14f,  6f,
         3f, 11f,  1f,  9f,
        15f,  7f, 13f,  5f
    };
    static float Bayer4(int x, int y) => Bayer[(y & 3) * 4 + (x & 3)] / 16f;

    static Color32 Pack(float r, float g, float b)
        => new Color32((byte)(r * 255f + 0.5f), (byte)(g * 255f + 0.5f), (byte)(b * 255f + 0.5f), 255);

    static string WriteTexture(string path, int w, int h, Color32[] px)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
        tex.SetPixels32(px);
        tex.Apply(false, false);
        byte[] png = tex.EncodeToPNG();
        Object.DestroyImmediate(tex);

        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, png);
        return path;
    }

    // 确定性 LCG
    struct Lcg
    {
        uint _s;
        public Lcg(uint seed) { _s = seed == 0 ? 1u : seed; }
        public float Next01()
        {
            _s = _s * 1664525u + 1013904223u;
            return (_s >> 8) * (1f / 16777216f);
        }
    }
}
