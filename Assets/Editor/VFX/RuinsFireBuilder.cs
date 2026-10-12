using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Spotlight.VFX
{
    // RuinsFireBuilder —— 一键构建"废墟氛围火"全套资产（图集/材质/网格/预制体）
    //
    // 产出（Assets/Art/VFX/Fire/）：
    //   Textures/  T_FireFlame_A|B|C（掠过档三变体，剪影不同）/ T_FireLingerFlame / T_FireSmoke / T_FireGlow / T_FireEmber
    //   Meshes/    FireCard（底部中心为轴的竖直细分卡片：风弯曲要它能弯出弧）
    //   Materials/ M_RuinsFire_FlameA|B|C / M_RuinsFire_LingerFlame / M_RuinsFire_Glow / M_RuinsFire_GroundGlow / M_RuinsFire_Smoke / M_RuinsFire_Ember
    //   Prefabs/   FX_RuinsFire_Roadside（掠过档：无粒子、无灯、面片+光斑）/ FX_RuinsFire_Linger（驻足档：+烟+余烬+闪烁灯）
    //
    // 与调研（docs/技术/游戏特效与火焰调研.md §6.5/§6.6）的对应：
    //   掠过档 = 自发光 flipbook 面片 + 烘焙等效光斑（这里用 GroundGlow 面片顶替"照亮地面"，
    //            正式工程应把火的静态照明烘进光照贴图，见 §6.4）；**不用真粒子**。
    //   驻足档 = 在其上补齐 烟 / 余烬 / 动态闪烁灯（动态灯只给驻足档，见 §6.4）。
    public static class RuinsFireBuilder
    {
        const string Root = "Assets/Art/VFX/Fire";
        const string TexDir = Root + "/Textures";
        const string MeshDir = Root + "/Meshes";
        const string MatDir = Root + "/Materials";
        const string PrefabDir = Root + "/Prefabs";
        const string ShaderName = "OurFunction/VFX/RuinsFireFlipbook";

        [MenuItem("超高速行者/VFX/构建废墟氛围火资产")]
        public static void Build()
        {
            EnsureFolders();

            // ---------- 1. 图集 ----------
            string flameA = Gen(TexDir + "/T_FireFlame_A.png", () => FireFlipbookGen.GenerateFlameSheet(TexDir + "/T_FireFlame_A.png", FireFlipbookGen.RoadsideFlame, 1011));
            string flameB = Gen(TexDir + "/T_FireFlame_B.png", () => FireFlipbookGen.GenerateFlameSheet(TexDir + "/T_FireFlame_B.png", FireFlipbookGen.RoadsideFlame, 2027));
            string flameC = Gen(TexDir + "/T_FireFlame_C.png", () => FireFlipbookGen.GenerateFlameSheet(TexDir + "/T_FireFlame_C.png", FireFlipbookGen.RoadsideFlame, 3041));
            string linger = Gen(TexDir + "/T_FireLingerFlame.png", () => FireFlipbookGen.GenerateFlameSheet(TexDir + "/T_FireLingerFlame.png", FireFlipbookGen.LingerFlame, 4099));
            string smoke = Gen(TexDir + "/T_FireSmoke.png", () => FireFlipbookGen.GenerateSmokeSheet(TexDir + "/T_FireSmoke.png", 5077));
            string glow = Gen(TexDir + "/T_FireGlow.png", () => FireFlipbookGen.GenerateGlow(TexDir + "/T_FireGlow.png", 256));
            string ember = Gen(TexDir + "/T_FireEmber.png", () => FireFlipbookGen.GenerateEmber(TexDir + "/T_FireEmber.png", 96));

            // ---------- 2. 导入设置（关键：数据贴图必须 sRGB=false）----------
            ImportData(flameA, 2048); ImportData(flameB, 2048); ImportData(flameC, 2048);
            ImportData(linger, 2048); ImportData(smoke, 2048);
            ImportData(glow, 256); ImportColor(ember, 128);

            var tFlameA = AssetDatabase.LoadAssetAtPath<Texture2D>(flameA);
            var tFlameB = AssetDatabase.LoadAssetAtPath<Texture2D>(flameB);
            var tFlameC = AssetDatabase.LoadAssetAtPath<Texture2D>(flameC);
            var tLinger = AssetDatabase.LoadAssetAtPath<Texture2D>(linger);
            var tSmoke = AssetDatabase.LoadAssetAtPath<Texture2D>(smoke);
            var tGlow = AssetDatabase.LoadAssetAtPath<Texture2D>(glow);
            var tEmber = AssetDatabase.LoadAssetAtPath<Texture2D>(ember);

            var sh = Shader.Find(ShaderName);
            if (sh == null) { Debug.LogError($"[RuinsFire] 找不到着色器 {ShaderName}（是否编译失败？）"); return; }
            var urpParticlesUnlit = Shader.Find("Universal Render Pipeline/Particles/Unlit");

            // ---------- 3. 网格 ----------
            var card = BuildFireCard(MeshDir + "/FireCard.asset");

            // ---------- 4. 材质 ----------
            var mA = FlameMat(MatDir + "/M_RuinsFire_FlameA.mat", sh, tFlameA, 8, 8, 26f, 1.65f, 70f, 120f);
            var mB = FlameMat(MatDir + "/M_RuinsFire_FlameB.mat", sh, tFlameB, 8, 8, 31f, 1.45f, 60f, 105f);
            var mC = FlameMat(MatDir + "/M_RuinsFire_FlameC.mat", sh, tFlameC, 8, 8, 22f, 1.8f, 80f, 135f);
            var mLinger = FlameMat(MatDir + "/M_RuinsFire_LingerFlame.mat", sh, tLinger, 8, 8, 19f, 1.95f, 90f, 150f);
            var mGlow = GlowMat(MatDir + "/M_RuinsFire_Glow.mat", sh, tGlow, true);
            var mGround = GlowMat(MatDir + "/M_RuinsFire_GroundGlow.mat", sh, tGlow, false);
            var mSmoke = SmokeMat(MatDir + "/M_RuinsFire_Smoke.mat", sh, tSmoke);
            var mEmber = EmberMat(MatDir + "/M_RuinsFire_Ember.mat", urpParticlesUnlit, tEmber);

            AssetDatabase.SaveAssets();

            // ---------- 5. 预制体 ----------
            BuildRoadsidePrefab(card, mA, mB, mGlow, mGround);
            BuildLingerPrefab(card, mLinger, mA, mB, mGlow, mGround, mSmoke, mEmber);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            VerifyMaterials();   // 必须放在导入之后：URP 的材质后处理器是在导入时跑并覆写属性的
            Debug.Log($"[RuinsFire] 构建完成 → {Root}（掠过档 + 驻足档）");
        }

        // ------------------------------------------------------------------
        static void EnsureFolders()
        {
            string[] dirs = { "Assets/Art/VFX/Fire", TexDir, MeshDir, MatDir, PrefabDir };
            foreach (var d in dirs)
                if (!AssetDatabase.IsValidFolder(d))
                    AssetDatabase.CreateFolder(Path.GetDirectoryName(d).Replace('\\', '/'), Path.GetFileName(d));
        }

        static string Gen(string path, System.Func<string> f)
        {
            string abs = Path.GetFullPath(Path.Combine(Application.dataPath, "..", path));
            // 每次都重生成：种子确定 → 输出确定（改生成器参数即生效，不会因为"文件已存在"而沉默地跳过）
            f();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            Debug.Log($"[RuinsFire] 生成图集 {path}");
            return path;
        }

        // 数据贴图导入：sRGB 关（否则黑体温度被伽马二次污染）、不压缩（避免数据通道块状伪影）、
        // mip 开（远距离不闪）、Clamp（我们按 tile 手动索引 UV）。
        static void ImportData(string path, int maxSize)
        {
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null) { Debug.LogWarning($"[RuinsFire] 无导入器 {path}"); return; }
            ti.textureType = TextureImporterType.Default;
            ti.sRGBTexture = false;
            ti.alphaSource = TextureImporterAlphaSource.None;
            ti.alphaIsTransparency = false;
            ti.textureCompression = TextureImporterCompression.Uncompressed;
            ti.mipmapEnabled = true;
            ti.filterMode = FilterMode.Bilinear;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.npotScale = TextureImporterNPOTScale.None;
            ti.maxTextureSize = maxSize;
            ti.SaveAndReimport();
        }

        // 颜色贴图（余烬/火星这类被当 RGB 读的）：sRGB 开、Alpha 当透alpha
        static void ImportColor(string path, int maxSize)
        {
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null) return;
            ti.textureType = TextureImporterType.Default;
            ti.sRGBTexture = true;
            ti.alphaSource = TextureImporterAlphaSource.FromInput;
            ti.alphaIsTransparency = true;
            ti.textureCompression = TextureImporterCompression.CompressedHQ;
            ti.mipmapEnabled = true;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.maxTextureSize = maxSize;
            ti.SaveAndReimport();
        }

        // 底部中心为轴的竖直卡片（细分 8 段 → 风弯曲能弯出弧线，而不是折成两段）
        static Mesh BuildFireCard(string path)
        {
            var exist = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (exist != null) return exist;

            const int cols = 1, rows = 8;
            int nv = (cols + 1) * (rows + 1);
            var verts = new Vector3[nv];
            var uvs = new Vector2[nv];
            var normals = new Vector3[nv];
            int i = 0;
            for (int j = 0; j <= rows; j++)
                for (int k = 0; k <= cols; k++, i++)
                {
                    float u = (float)k / cols, v = (float)j / rows;
                    verts[i] = new Vector3(u - 0.5f, v, 0f);   // 底部中心为轴、y ∈ [0,1]
                    uvs[i] = new Vector2(u, v);
                    normals[i] = new Vector3(0, 0, -1);
                }

            var tris = new List<int>();
            for (int j = 0; j < rows; j++)
                for (int k = 0; k < cols; k++)
                {
                    int a = j * (cols + 1) + k, b = a + 1, c = a + (cols + 1), d = c + 1;
                    tris.Add(a); tris.Add(c); tris.Add(b);
                    tris.Add(b); tris.Add(c); tris.Add(d);
                }

            var mesh = new Mesh { name = "FireCard" };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetNormals(normals);
            mesh.SetTriangles(tris, 0);
            // 顶点着色器会位移（billboard 旋转 + 风弯曲），包围盒必须给足，否则会被视锥剔除误裁
            mesh.bounds = new Bounds(new Vector3(0f, 1.2f, 0f), new Vector3(9f, 9f, 9f));
            AssetDatabase.CreateAsset(mesh, path);
            return AssetDatabase.LoadAssetAtPath<Mesh>(path);
        }

        static Material NewMat(string path, Shader sh)
        {
            var old = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (old != null) AssetDatabase.DeleteAsset(path);
            var m = new Material(sh);
            AssetDatabase.CreateAsset(m, path);
            return AssetDatabase.LoadAssetAtPath<Material>(path);
        }

        // 火焰材质：黑体配色 + 圆柱朝向 + 风弯曲 + 软粒子 + 距离淡出 + premultiply 混合
        static Material FlameMat(string path, Shader sh, Texture2D sheet, int cols, int rows,
                                 float speed, float brightness, float fadeStart, float fadeEnd)
        {
            var m = NewMat(path, sh);
            m.SetTexture("_BaseMap", sheet);
            m.SetColor("_BaseColor", Color.white);
            m.SetColor("_EmissionColor", new Color(1f, 0.93f, 0.85f, 1f));
            m.SetFloat("_Brightness", brightness);
            m.SetFloat("_Opacity", 1f);
            m.SetFloat("_DensityPower", 1.35f);
            m.SetFloat("_FlipbookCols", cols);
            m.SetFloat("_FlipbookRows", rows);
            m.SetFloat("_Speed", speed);
            m.SetFloat("_RandomPhase", 1f);
            m.SetFloat("_ColorMode", 0f);              // 黑体
            m.SetFloat("_KelvinLow", 950f);
            m.SetFloat("_KelvinHigh", 2400f);
            m.SetFloat("_Saturation", 1.35f);
            m.SetFloat("_HotspotBoost", 1.6f);
            m.SetFloat("_WindStrength", 0.35f);
            m.SetFloat("_SwayAmp", 0.06f);
            m.SetFloat("_SwaySpeed", 1.7f);
            m.SetFloat("_SoftParticlesNear", 0.05f);
            m.SetFloat("_SoftParticlesFar", 1.6f);
            m.SetFloat("_CameraNearFade", 1.2f);
            m.SetFloat("_CameraFarFade", 1.0f);
            m.SetFloat("_FadeStart", fadeStart);
            m.SetFloat("_FadeEnd", fadeEnd);
            // premultiply：Src=One, Dst=OneMinusSrcAlpha
            m.SetFloat("_SrcBlend", (float)BlendMode.One);
            m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_Cull", (float)CullMode.Off);   // 卡片要两面可见
            m.EnableKeyword("_PREMULTIPLY_ON");
            m.EnableKeyword("_BILLBOARD_ON");
            m.renderQueue = 3000;
            EditorUtility.SetDirty(m);
            return m;
        }

        // 光晕：flat 色 + 加色。billboard=true 是竖直光晕；false 平铺在地面当"照亮地面"的廉价顶替
        static Material GlowMat(string path, Shader sh, Texture2D tex, bool billboard)
        {
            var m = NewMat(path, sh);
            m.SetTexture("_BaseMap", tex);
            m.SetColor("_BaseColor", Color.white);
            m.SetColor("_EmissionColor", new Color(billboard ? 2.0f : 0.62f, billboard ? 0.72f : 0.23f, billboard ? 0.26f : 0.075f, 1f));
            m.SetFloat("_Opacity", billboard ? 0.15f : 0.17f);
            m.SetFloat("_DensityPower", 1.0f);
            m.SetFloat("_FlipbookCols", 1f);
            m.SetFloat("_FlipbookRows", 1f);
            m.SetFloat("_Speed", 0f);
            m.SetFloat("_RandomPhase", 0f);
            m.SetFloat("_ColorMode", 1f);              // flat tint（光晕不需要黑体）
            m.SetFloat("_WindStrength", 0f);
            m.SetFloat("_SwayAmp", 0f);
            m.SetFloat("_SoftParticlesNear", 0f);
            m.SetFloat("_SoftParticlesFar", 0f);       // 光晕不做软粒子：省一次深度采样
            m.SetFloat("_CameraNearFade", 0.6f);
            m.SetFloat("_CameraFarFade", 1.0f);
            m.SetFloat("_FadeStart", 70f);
            m.SetFloat("_FadeEnd", 120f);
            m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)BlendMode.One);   // 加色
            m.SetFloat("_Cull", (float)CullMode.Off);
            if (billboard) m.EnableKeyword("_BILLBOARD_ON");
            m.renderQueue = billboard ? 3010 : 2995;
            EditorUtility.SetDirty(m);
            return m;
        }

        // 烟：premultiply 暗灰、薄（调研 #15461：木火几乎看不到黑烟 → _Opacity 压低）
        static Material SmokeMat(string path, Shader sh, Texture2D sheet)
        {
            var m = NewMat(path, sh);
            m.SetTexture("_BaseMap", sheet);
            m.SetColor("_BaseColor", Color.white);
            m.SetColor("_EmissionColor", new Color(0.17f, 0.165f, 0.18f, 1f));
            m.SetFloat("_Opacity", 0.30f);
            m.SetFloat("_DensityPower", 1.1f);
            m.SetFloat("_FlipbookCols", 6f);
            m.SetFloat("_FlipbookRows", 6f);
            m.SetFloat("_Speed", 9f);
            m.SetFloat("_RandomPhase", 1f);
            m.SetFloat("_ColorMode", 1f);              // flat：烟不发光，是遮挡
            m.SetFloat("_WindStrength", 1.1f);         // 烟比火更受风（飘得更斜）
            m.SetFloat("_SwayAmp", 0.12f);
            m.SetFloat("_SwaySpeed", 0.9f);
            m.SetFloat("_SoftParticlesNear", 0.1f);
            m.SetFloat("_SoftParticlesFar", 1.2f);
            m.SetFloat("_CameraNearFade", 1.5f);
            m.SetFloat("_CameraFarFade", 1.0f);
            m.SetFloat("_FadeStart", 55f);
            m.SetFloat("_FadeEnd", 100f);
            m.SetFloat("_SrcBlend", (float)BlendMode.One);
            m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_Cull", (float)CullMode.Off);
            m.EnableKeyword("_PREMULTIPLY_ON");
            m.EnableKeyword("_BILLBOARD_ON");
            m.renderQueue = 3005;
            EditorUtility.SetDirty(m);
            return m;
        }

        // 余烬：用 URP 官方 Particles/Unlit（stock，零风险；见调研"真粒子只用在驻足档/余烬"）
        //
        // ⚠️ 铁律：这个材质**只能写 URP 校验器"读"的属性，绝不能写它"写"的属性**。
        //    URP 的 Editor/AssetPostProcessors/MaterialPostprocessor.InitializeLatest 会对**新建**材质调
        //    ShaderUtils.UpdateMaterial(..., CreatedNewMaterial, ...) →
        //      · BaseShaderGUI.SetupMaterialBlendMode 按 _Surface/_Blend 反推并**覆写**
        //        _SrcBlend / _DstBlend / _ZWrite / RenderType 标签 / renderQueue；
        //      · ParticleGUI.SetMaterialKeywords 按 _SoftParticlesEnabled/_CameraFadingEnabled 反推并覆写
        //        _SOFTPARTICLES_ON / _SoftParticleFadeParams。
        //    导入发生在 AssetDatabase.SaveAssets() 之后 → 脚本里直接 SetFloat 这些值**必被冲掉**。
        //    实测事故：材质落到 _Surface 0 的不透明态（One/Zero/ZWrite 1），余烬渲染成
        //    "黑底方形 + 暖色圆点"（方块是贴图的黑色四角，圆心是径向渐变）。
        //    正确做法见下：只设"输入项"，让 URP 自己推导出正确状态。
        //    本项目自写的 OurFunction/* 着色器不受影响（GetShaderID 返回 Unknown → 后处理器直接跳过）。
        static Material EmberMat(string path, Shader sh, Texture2D tex)
        {
            var m = NewMat(path, sh);
            m.SetTexture("_BaseMap", tex);
            m.SetColor("_BaseColor", new Color(1f, 0.72f, 0.34f, 1f));
            m.SetFloat("_SoftParticlesNearFadeDistance", 0f);
            m.SetFloat("_SoftParticlesFarFadeDistance", 1f);
            // ---- 输入项：URP 读这些，自己推导 blend / 关键字 / renderQueue ----
            m.SetFloat("_Surface", 1f);                 // 1 = Transparent（0 会推导成 One/Zero 不透明）
            m.SetFloat("_Blend", 2f);                   // 2 = Additive：SrcAlpha/One，余烬是"发光"
            m.SetFloat("_QueueOffset", 12f);            // → renderQueue = 3000 + 12
            m.SetFloat("_ColorMode", 0f);               // Multiply：贴图灰度 × _BaseColor × 顶点色
            m.SetFloat("_SoftParticlesEnabled", 0f);    // 3cm 的火星做软粒子没意义，关掉省一次深度采样
            m.SetFloat("_CameraFadingEnabled", 0f);
            m.SetFloat("_FlipbookBlending", 0f);
            m.SetFloat("_AlphaClip", 0f);
            EditorUtility.SetDirty(m);
            return m;
        }

        // 自检：把"材质被 URP 校验器冲成默认态"这类静默失败变成**响亮的报错**。
        // 必须在 Refresh() 之后调用（后处理器是在导入时跑的）。
        static void VerifyMaterials()
        {
            CheckMat(MatDir + "/M_RuinsFire_Ember.mat", expectSurface: 1f, expectBlend: 2f, expectQueue: 3012,
                     expectKeyword: null);
            CheckMat(MatDir + "/M_RuinsFire_FlameA.mat", expectSurface: null, expectBlend: null, expectQueue: 3000,
                     expectKeyword: "_BILLBOARD_ON");
            CheckMat(MatDir + "/M_RuinsFire_Smoke.mat", expectSurface: null, expectBlend: null, expectQueue: 3005,
                     expectKeyword: "_BILLBOARD_ON");
            CheckMat(MatDir + "/M_RuinsFire_Glow.mat", expectSurface: null, expectBlend: null, expectQueue: 3010,
                     expectKeyword: "_BILLBOARD_ON");
        }

        static void CheckMat(string path, float? expectSurface, float? expectBlend, int expectQueue, string expectKeyword)
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { Debug.LogError($"[RuinsFire][自检] 材质缺失 {path}"); return; }
            string bad = null;
            if (expectSurface.HasValue && !Mathf.Approximately(m.GetFloat("_Surface"), expectSurface.Value))
                bad += $" _Surface={m.GetFloat("_Surface")}(期望{expectSurface.Value})";
            if (expectBlend.HasValue && !Mathf.Approximately(m.GetFloat("_Blend"), expectBlend.Value))
                bad += $" _Blend={m.GetFloat("_Blend")}(期望{expectBlend.Value})";
            if (m.renderQueue != expectQueue) bad += $" renderQueue={m.renderQueue}(期望{expectQueue})";
            if (expectKeyword != null && !m.IsKeywordEnabled(expectKeyword)) bad += $" 缺关键字{expectKeyword}";
            if (bad != null)
                Debug.LogError($"[RuinsFire][自检] {m.name} 状态不对：{bad} —— 大概率又被 URP 材质后处理器覆写了");
            else
                Debug.Log($"[RuinsFire][自检] {m.name} ok（queue={m.renderQueue} src={m.GetFloat("_SrcBlend")} dst={m.GetFloat("_DstBlend")} zwrite={m.GetFloat("_ZWrite")}）");
        }

        // ------------------------------------------------------------------
        static GameObject Card(string name, Mesh mesh, Material mat, Vector3 pos, Vector3 rotEuler, Vector3 scale)
        {
            var go = new GameObject(name);
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(rotEuler);
            go.transform.localScale = scale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;   // 火不投影（省，且投影会露馅）
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            return go;
        }

        static void BuildRoadsidePrefab(Mesh card, Material mFlameA, Material mFlameB, Material mGlow, Material mGround)
        {
            string path = PrefabDir + "/FX_RuinsFire_Roadside.prefab";
            var old = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (old != null) AssetDatabase.DeleteAsset(path);

            var root = new GameObject("FX_RuinsFire_Roadside");
            Card("Flame_A", card, mFlameA, Vector3.zero, new Vector3(0f, 0f, 0f), new Vector3(0.90f, 1.15f, 1f)).transform.SetParent(root.transform, false);
            Card("Flame_B", card, mFlameB, new Vector3(0.10f, 0f, 0.05f), new Vector3(0f, 0f, 4f), new Vector3(0.62f, 0.82f, 1f)).transform.SetParent(root.transform, false);
            // 竖直"光晕卡片"已删除：光晕交给工程的 Bloom（阈值 1.0），加色卡片只会露出可见椭圆。
            // 只保留地面溢光片 —— 它是"照亮地面"的廉价顶替（正式做法是把火的静态照明烘进光照贴图）。
            // 偏心修正：FireCard 是**底边中心为轴**（y∈[0,1]），绕 X 转 90° 后 y 轴映射到 +Z，
            // 于是光斑中心会跑到 z = 0.5·scaleY 处、而不是落在火堆上。要把重心拉回原点，
            // 位置得反向补 -0.5·scaleY（scaleY=1.8 → -0.9）。
            Card("GroundGlow", card, mGround, new Vector3(0f, 0.03f, -0.9f), new Vector3(90f, 0f, 0f), new Vector3(1.8f, 1.8f, 1f)).transform.SetParent(root.transform, false);

            root.AddComponent<RuinsFireLod>();   // 掠过档没有"细货"，这里只是保证远距整体剔除
            PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            Debug.Log($"[RuinsFire] 掠过档预制体 {path}");
        }

        static void BuildLingerPrefab(Mesh card, Material mLinger, Material mA, Material mB,
                                      Material mGlow, Material mGround, Material mSmoke, Material mEmber)
        {
            string path = PrefabDir + "/FX_RuinsFire_Linger.prefab";
            var old = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (old != null) AssetDatabase.DeleteAsset(path);

            var root = new GameObject("FX_RuinsFire_Linger");
            Card("Flame_Main", card, mLinger, Vector3.zero, Vector3.zero, new Vector3(1.05f, 1.55f, 1f)).transform.SetParent(root.transform, false);
            Card("Flame_A", card, mA, new Vector3(-0.16f, 0f, 0.06f), new Vector3(0f, 12f, 0f), new Vector3(0.78f, 1.05f, 1f)).transform.SetParent(root.transform, false);
            Card("Flame_B", card, mB, new Vector3(0.18f, 0f, -0.05f), new Vector3(0f, -9f, 5f), new Vector3(0.66f, 0.9f, 1f)).transform.SetParent(root.transform, false);
            // 同掠过档：位置补 -0.5·scaleY = -1.2，把光斑重心拉回火堆上
            Card("GroundGlow", card, mGround, new Vector3(0f, 0.03f, -1.2f), new Vector3(90f, 0f, 0f), new Vector3(2.4f, 2.4f, 1f)).transform.SetParent(root.transform, false);
            var smoke = Card("Smoke", card, mSmoke, new Vector3(0f, 1.55f, 0.20f), Vector3.zero, new Vector3(1.35f, 2.6f, 1f));
            smoke.transform.SetParent(root.transform, false);

            // 余烬（真粒子：只在驻足档出现）
            var embers = new GameObject("Embers");
            embers.transform.SetParent(root.transform, false);
            var ps = embers.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.duration = 5f; main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.3f, 2.6f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.55f, 1.5f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.028f, 0.075f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.62f, 0.22f), new Color(1f, 0.86f, 0.5f));
            main.gravityModifier = -0.03f;
            main.maxParticles = 40;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.playOnAwake = true;
            main.prewarm = true;
            var em = ps.emission; em.rateOverTime = 7f;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 15f; shape.radius = 0.14f;
            shape.rotation = new Vector3(-90f, 0f, 0f);
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(new Color(1f, 0.85f, 0.5f), 0f), new GradientColorKey(new Color(0.9f, 0.35f, 0.1f), 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.55f), new GradientAlphaKey(0f, 1f) });
            col.color = new ParticleSystem.MinMaxGradient(grad);
            var size = ps.sizeOverLifetime; size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 0.25f)));
            var pr = embers.GetComponent<ParticleSystemRenderer>();
            pr.sharedMaterial = mEmber;
            pr.renderMode = ParticleSystemRenderMode.Billboard;
            pr.alignment = ParticleSystemRenderSpace.View;
            pr.shadowCastingMode = ShadowCastingMode.Off;
            pr.receiveShadows = false;

            // 动态闪烁灯（只给驻足档）
            // 强度/半径都压得很小：本工程的不透明面走 NPR 卡通光照，光是**按阈值分带**的，
            // 点光一打到断墙上就是一个"边缘偏硬的大椭圆光池"（实测放大半径 6m 时极其抢戏）。
            // 真实的废墟氛围火照度本来就低，且正式做法是把火的静态照明**烘进光照贴图**（调研 §6.4），
            // 动态灯只在"驻足档"留一点近距离闪烁感。所以：range 收到 3.2m、intensity 0.85。
            var lightGo = new GameObject("Light");
            lightGo.transform.SetParent(root.transform, false);
            lightGo.transform.localPosition = new Vector3(0f, 0.75f, 0f);
            var li = lightGo.AddComponent<Light>();
            li.type = LightType.Point;
            li.color = new Color(1f, 0.56f, 0.22f);
            li.intensity = 0.85f;
            li.range = 3.2f;
            li.shadows = LightShadows.None;          // 火光不投影（成本/收益不划算）
            li.renderMode = LightRenderMode.ForcePixel;
            var flick = lightGo.AddComponent<RuinsFireFlicker>();
            flick.baseIntensity = 0.85f; flick.baseRange = 3.2f; flick.phaseOffset = 1.7f;

            var lod = root.AddComponent<RuinsFireLod>();
            lod.detailDistance = 45f;
            lod.cullDistance = 130f;
            lod.detailObjects = new[] { smoke, embers, lightGo };

            PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            Debug.Log($"[RuinsFire] 驻足档预制体 {path}");
        }
    }
}
