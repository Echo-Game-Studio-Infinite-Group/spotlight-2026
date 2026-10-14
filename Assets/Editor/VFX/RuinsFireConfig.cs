using System;
using UnityEngine;

namespace Spotlight.VFX
{
    // RuinsFireConfig —— 废墟氛围火的**唯一调参入口**（ScriptableObject 资产）
    //
    // 为什么要有它：原来这些数（火焰播放速度、点光半径与强度、地面光斑尺寸、距离淡出、
    // 余烬寿命/尺寸/发射率、LOD 两个距离……）全是 RuinsFireBuilder 里的 C# 字面量，
    // 改一个数就要改代码 + 等重编译。搬到资产里之后：**Inspector 里改 → 重跑一次构建 → 生效**，
    // 而且这些值本身进了版本库，可 diff、可 review。
    //
    // 口径：
    //   · 本资产是**配方（输入）**；`Assets/Art/VFX/Fire/` 下的是**产物**。改这里 → 重跑构建 → 产物更新。
    //   · 构建会把这里的值写进材质与预制体组件 → **重跑构建会覆盖你在预制体/材质上手工调的同类值**。
    //     想保住手工调参：把值回填到本资产（推荐，一处权威），或者别再跑构建。
    //   · 默认值逐项对齐 2026-10-14 那版字面量；所以第一次构建出的产物应当与之前**逐字节一致**
    //     （构建后拿 `git status` 验证这件事：产物不该有 diff）。
    //   · 产物里的贴图是**按文件名**找的（不是资产引用）—— 所以配置不持有任何资产引用，
    //     重生成同名贴图不会产生 GUID churn。
    [CreateAssetMenu(fileName = "RuinsFireConfig", menuName = "超高速行者/VFX/废墟氛围火配置")]
    public sealed class RuinsFireConfig : ScriptableObject
    {
        // ==================================================================
        // 图集（7 张）
        // ==================================================================
        public enum SheetKind { Flame, Smoke, Glow, Ember }

        [Serializable]
        public sealed class SheetSpec
        {
            public SheetKind kind = SheetKind.Flame;
            public string fileName = "T_FireFlame_A.png";
            [Tooltip("Flame / Smoke 用：火舌风格（火舌根数、侵蚀、温度落差、闪点密度…）")]
            public FireFlipbookGen.FlameStyle style = FireFlipbookGen.RoadsideFlame;
            [Tooltip("确定性种子：变体只换种子（剪影同族），同一组参数+种子必得同一张图")]
            public int seed = 1011;
            [Tooltip("Glow / Ember 用（Flame/Smoke 的尺寸由 style.cols × style.tile 决定）")]
            public int pixelSize = 256;
            [Tooltip("导入 maxTextureSize")]
            public int importMaxSize = 2048;
            [Tooltip("true = 颜色贴图（sRGB 开、alpha 当透明，被当 RGB 读，如余烬）；false = 数据贴图（sRGB 关、不压缩）")]
            public bool colorTexture = false;
        }

        // ==================================================================
        // 材质
        // ==================================================================
        [Serializable]
        public sealed class FlameMaterialSpec
        {
            public string fileName = "M_RuinsFire_FlameA.mat";
            public string sheet = "T_FireFlame_A.png";
            public float cols = 8f, rows = 8f;
            [Tooltip("flipbook 播放速度（帧/秒/图集数）= 这里的『时间』；也决定循环周期 = cols×rows / speed")]
            public float speed = 26f;
            public float brightness = 1.65f;
            public float opacity = 1f;
            public float densityPower = 1.35f;
            public Color baseColor = Color.white;
            public Color emissionColor = new Color(1f, 0.93f, 0.85f, 1f);
            [Tooltip("0 = 走黑体（Planck 轨迹）配色；1 = flat 用 _EmissionColor")]
            public float colorMode = 0f;
            public float kelvinLow = 950f, kelvinHigh = 2400f, saturation = 1.35f;
            public float hotspotBoost = 1.6f;
            public float randomPhase = 1f;
            public float windStrength = 0.35f, swayAmp = 0.06f, swaySpeed = 1.7f;
            public float softNear = 0.05f, softFar = 1.6f;
            public float cameraNearFade = 1.2f, cameraFarFade = 1f;
            public float fadeStart = 70f, fadeEnd = 120f;
            public int renderQueue = 3000;
        }

        [Serializable]
        public sealed class GlowMaterialSpec
        {
            public string fileName = "M_RuinsFire_GroundGlow.mat";
            public string sheet = "T_FireGlow.png";
            [Tooltip("true = 竖直光晕（现已不用，光晕交给 Bloom）；false = 平铺地面当『照亮地面』用")]
            public bool billboard = false;
            public Color emissionColor = new Color(0.62f, 0.23f, 0.075f, 1f);
            public float opacity = 0.17f;
            public float densityPower = 1f;
            public float cameraNearFade = 0.6f, cameraFarFade = 1f;
            public float fadeStart = 70f, fadeEnd = 120f;
            public int renderQueue = 2995;
        }

        [Serializable]
        public sealed class SmokeMaterialSpec
        {
            public string fileName = "M_RuinsFire_Smoke.mat";
            public string sheet = "T_FireSmoke.png";
            public Color emissionColor = new Color(0.17f, 0.165f, 0.18f, 1f);
            public float opacity = 0.30f;
            public float densityPower = 1.1f;
            public float cols = 6f, rows = 6f, speed = 9f;
            public float windStrength = 1.1f, swayAmp = 0.12f, swaySpeed = 0.9f;
            public float softNear = 0.1f, softFar = 1.2f;
            public float cameraNearFade = 1.5f, cameraFarFade = 1f;
            public float fadeStart = 55f, fadeEnd = 100f;
            public int renderQueue = 3005;
        }

        // 余烬：用 URP 官方 Particles/Unlit。**只准写 URP 校验器"读"的属性**
        // （_Surface / _Blend / _QueueOffset / _SoftParticlesEnabled / _CameraFadingEnabled …），
        // 因为它会在导入时按这些反推并覆写 _SrcBlend/_DstBlend/_ZWrite/RenderType/renderQueue。
        // 详见 RuinsFireBuilder.EmberMat 的铁律注释与实现说明 §六 #1。
        [Serializable]
        public sealed class EmberMaterialSpec
        {
            public string fileName = "M_RuinsFire_Ember.mat";
            public string sheet = "T_FireEmber.png";
            public Color baseColor = new Color(1f, 0.72f, 0.34f, 1f);
            public float surface = 1f;      // 1 = Transparent
            public float blend = 2f;        // 2 = Additive
            public float queueOffset = 12f; // renderQueue = 3000 + 该值
            public float colorMode = 0f;    // Multiply
            public float softParticlesEnabled = 0f;
            public float cameraFadingEnabled = 0f;
            public float flipbookBlending = 0f;
            public float alphaClip = 0f;
            public float softNearFadeDistance = 0f, softFarFadeDistance = 1f;
        }

        // ==================================================================
        // 预制体：卡片 / 余烬 / 灯 / 裁剪
        // ==================================================================
        [Serializable]
        public sealed class CardSpec
        {
            public string name = "Flame_A";
            [Tooltip("材质文件名（在 config.materials 里按 fileName 解析）")]
            public string material = "M_RuinsFire_FlameA.mat";
            public Vector3 position = Vector3.zero;
            [Tooltip("欧拉角（度）")]
            public Vector3 euler = Vector3.zero;
            public Vector3 scale = Vector3.one;
        }

        [Serializable]
        public sealed class EmberSpec
        {
            public float duration = 5f;
            public float lifetimeMin = 1.3f, lifetimeMax = 2.6f;
            public float speedMin = 0.55f, speedMax = 1.5f;
            public float sizeMin = 0.028f, sizeMax = 0.075f;
            public Color colorA = new Color(1f, 0.62f, 0.22f);
            public Color colorB = new Color(1f, 0.86f, 0.5f);
            public float gravityModifier = -0.03f;
            public int maxParticles = 40;
            public float rateOverTime = 7f;
            public float shapeAngle = 15f, shapeRadius = 0.14f;
            public Color gradColorA = new Color(1f, 0.85f, 0.5f);
            public Color gradColorB = new Color(0.9f, 0.35f, 0.1f);
            [Tooltip("生命周期里 alpha 开始衰减的位置（0–1）")]
            public float alphaHold = 0.55f;
            [Tooltip("生命末期的尺寸倍率（1 → 该值）")]
            public float sizeEnd = 0.25f;
        }

        [Serializable]
        public sealed class LightSpec
        {
            public Color color = new Color(1f, 0.56f, 0.22f);
            [Tooltip("离火底的高度")]
            public float height = 0.75f;
            public float intensity = 0.85f;
            [Tooltip("半径。本工程不透明面走 NPR 按阈值分带的光照 —— 半径一大，断墙上就出一个边沿偏硬的大椭圆光池")]
            public float range = 3.2f;
            public float flickerSpeed = 1.15f, intensityAmplitude = 0.22f, rangeAmplitude = 0.08f;
            [Tooltip("每堆火的相位错开（避免整排火一起呼吸）")]
            public float phaseOffset = 1.7f;
        }

        [Serializable]
        public sealed class LodSpec
        {
            [Tooltip("超过这个距离关掉下面的 detailObjects（余烬/烟/动态灯）")]
            public float detailDistance = 45f;
            [Tooltip("超过这个距离整堆火关闭")]
            public float cullDistance = 130f;
            [Tooltip("只在近处/驻足档出现的子物体名（对应 CardSpec.name 或 \"Embers\" / \"Light\"）")]
            public string[] detailObjects = new string[0];
            public bool runtimeOnly = true;
        }

        [Serializable]
        public sealed class TierSpec
        {
            public CardSpec[] cards = new CardSpec[0];
            [Tooltip("勾上才生成 Embers 子物体（掠过档不需要真粒子）")]
            public bool embers;
            [Tooltip("勾上才生成 Light 子物体（动态灯只给驻足档）")]
            public bool light;
            public EmberSpec emberSpec = new EmberSpec();
            public LightSpec lightSpec = new LightSpec();
            public LodSpec lod = new LodSpec();
        }

        // ==================================================================
        // 数据
        // ==================================================================
        [Header("图集（生成为 Assets/Art/VFX/Fire/Textures/）")]
        public SheetSpec[] sheets = new SheetSpec[0];

        [Header("材质（生成为 Assets/Art/VFX/Fire/Materials/）")]
        public FlameMaterialSpec[] flameMaterials = new FlameMaterialSpec[0];
        public GlowMaterialSpec[] glowMaterials = new GlowMaterialSpec[0];
        public SmokeMaterialSpec[] smokeMaterials = new SmokeMaterialSpec[0];
        public EmberMaterialSpec[] emberMaterials = new EmberMaterialSpec[0];

        [Header("预制体")]
        public TierSpec roadside = new TierSpec();
        public TierSpec linger = new TierSpec();

        // ------------------------------------------------------------------
        // 默认值 = 2026-10-14 那版 C# 字面量（逐项对齐，构建产物应与之前一致）
        // ------------------------------------------------------------------
        public static RuinsFireConfig CreateDefault()
        {
            var c = CreateInstance<RuinsFireConfig>();

            c.sheets = new[]
            {
                new SheetSpec { kind = SheetKind.Flame, fileName = "T_FireFlame_A.png", style = FireFlipbookGen.RoadsideFlame, seed = 1011, importMaxSize = 2048 },
                new SheetSpec { kind = SheetKind.Flame, fileName = "T_FireFlame_B.png", style = FireFlipbookGen.RoadsideFlame, seed = 2027, importMaxSize = 2048 },
                new SheetSpec { kind = SheetKind.Flame, fileName = "T_FireFlame_C.png", style = FireFlipbookGen.RoadsideFlame, seed = 3041, importMaxSize = 2048 },
                new SheetSpec { kind = SheetKind.Flame, fileName = "T_FireLingerFlame.png", style = FireFlipbookGen.LingerFlame, seed = 4099, importMaxSize = 2048 },
                new SheetSpec { kind = SheetKind.Smoke, fileName = "T_FireSmoke.png", style = FireFlipbookGen.Smoke, seed = 5077, importMaxSize = 2048 },
                new SheetSpec { kind = SheetKind.Glow,  fileName = "T_FireGlow.png",  pixelSize = 256, importMaxSize = 256 },
                new SheetSpec { kind = SheetKind.Ember, fileName = "T_FireEmber.png", pixelSize = 96,  importMaxSize = 128, colorTexture = true },
            };

            // 四张火焰材质只差 sheet / speed / brightness / 淡出距离，其余同参
            c.flameMaterials = new[]
            {
                Flame("M_RuinsFire_FlameA.mat",      "T_FireFlame_A.png",      26f, 1.65f, 70f, 120f),
                Flame("M_RuinsFire_FlameB.mat",      "T_FireFlame_B.png",      31f, 1.45f, 60f, 105f),
                Flame("M_RuinsFire_FlameC.mat",      "T_FireFlame_C.png",      22f, 1.80f, 80f, 135f),
                Flame("M_RuinsFire_LingerFlame.mat", "T_FireLingerFlame.png",  19f, 1.95f, 90f, 150f),
            };

            c.glowMaterials = new[]
            {
                new GlowMaterialSpec
                {
                    fileName = "M_RuinsFire_Glow.mat", sheet = "T_FireGlow.png", billboard = true,
                    emissionColor = new Color(2.0f, 0.72f, 0.26f, 1f), opacity = 0.15f, renderQueue = 3010,
                },
                new GlowMaterialSpec
                {
                    fileName = "M_RuinsFire_GroundGlow.mat", sheet = "T_FireGlow.png", billboard = false,
                    emissionColor = new Color(0.62f, 0.23f, 0.075f, 1f), opacity = 0.17f, renderQueue = 2995,
                },
            };

            c.smokeMaterials = new[] { new SmokeMaterialSpec() };
            c.emberMaterials = new[] { new EmberMaterialSpec() };

            // ---------- 掠过档：两张火舌片 + 地面溢光；无粒子、无灯 ----------
            c.roadside = new TierSpec
            {
                cards = new[]
                {
                    Card("Flame_A", "M_RuinsFire_FlameA.mat", new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 0f), new Vector3(0.90f, 1.15f, 1f)),
                    Card("Flame_B", "M_RuinsFire_FlameB.mat", new Vector3(0.10f, 0f, 0.05f), new Vector3(0f, 0f, 4f), new Vector3(0.62f, 0.82f, 1f)),
                    // FireCard 是**底边中心为轴**（y∈[0,1]），绕 X 转 90° 后 y 轴映射到 +Z →
                    // 光斑重心会跑到 z = 0.5·scaleY；位置反向补 -0.5·scaleY（1.8 → -0.9）把它拉回原点。
                    Card("GroundGlow", "M_RuinsFire_GroundGlow.mat", new Vector3(0f, 0.03f, -0.9f), new Vector3(90f, 0f, 0f), new Vector3(1.8f, 1.8f, 1f)),
                },
                embers = false,
                light = false,
                lod = new LodSpec { detailDistance = 45f, cullDistance = 130f, detailObjects = new string[0] },
            };

            // ---------- 驻足档：在掠过档基础上补 主火舌片 + 烟 + 余烬 + 闪烁灯 ----------
            c.linger = new TierSpec
            {
                cards = new[]
                {
                    Card("Flame_Main", "M_RuinsFire_LingerFlame.mat", Vector3.zero, Vector3.zero, new Vector3(1.05f, 1.55f, 1f)),
                    Card("Flame_A", "M_RuinsFire_FlameA.mat", new Vector3(-0.16f, 0f, 0.06f), new Vector3(0f, 12f, 0f), new Vector3(0.78f, 1.05f, 1f)),
                    Card("Flame_B", "M_RuinsFire_FlameB.mat", new Vector3(0.18f, 0f, -0.05f), new Vector3(0f, -9f, 5f), new Vector3(0.66f, 0.9f, 1f)),
                    Card("GroundGlow", "M_RuinsFire_GroundGlow.mat", new Vector3(0f, 0.03f, -1.2f), new Vector3(90f, 0f, 0f), new Vector3(2.4f, 2.4f, 1f)),
                    Card("Smoke", "M_RuinsFire_Smoke.mat", new Vector3(0f, 1.55f, 0.20f), Vector3.zero, new Vector3(1.35f, 2.6f, 1f)),
                },
                embers = true,
                light = true,
                lod = new LodSpec
                {
                    detailDistance = 45f, cullDistance = 130f,
                    detailObjects = new[] { "Smoke", "Embers", "Light" },
                },
            };

            return c;
        }

        static FlameMaterialSpec Flame(string file, string sheet, float speed, float brightness, float fadeStart, float fadeEnd)
            => new FlameMaterialSpec
            {
                fileName = file, sheet = sheet, speed = speed, brightness = brightness,
                fadeStart = fadeStart, fadeEnd = fadeEnd,
            };

        static CardSpec Card(string name, string material, Vector3 pos, Vector3 euler, Vector3 scale)
            => new CardSpec { name = name, material = material, position = pos, euler = euler, scale = scale };
    }
}
