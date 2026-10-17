using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Spotlight.VFX
{
    // RuinsFireDemo —— 组装"废墟跑道 + 路边氛围火"演示场景（可重复执行，幂等重建）
    //
    // 场景内容（全部程序化生成，确定性种子）：
    //   · 一条 8m 宽走廊式跑道（对齐项目光廊的 8m 内宽口径），两侧断墙 + 缺口 + 瓦砾 + 塌落顶板
    //   · 路边氛围火（掠过档）沿跑道节奏性布点：**"每隔一段一个暖点"**，不是均匀撒点
    //   · 2 处"驻足档"火：在落脚平台/检查点旁，补齐烟 + 余烬 + 动态闪烁灯
    //   · 检查点用自发光条标记（复用工程的 HST/Emit），演示"火的暖点"与"引导色的冷点"共存
    //   · 环境极暗 + 一盏很弱的冷色"月光"方向光 → 火是场上唯一暖光源（这正是 §6.11 的验收条件）
    //
    // 墙地材质用工程自带的 NPR 母材质（OurFunction/NPR/Deferred Toon），
    // 于是这些火点光会走 NPR 延迟光照把断墙"卡通地"照亮 —— 顺带验证 §5.4 那条接缝。
    public static class RuinsFireDemo
    {
        const string ScenePath = "Assets/Scenes/SC_VFX_RuinsFire.unity";
        const string MatDir = "Assets/Art/VFX/Fire/DemoMaterials";
        const string PrefabDir = "Assets/Art/VFX/Fire/Prefabs";

        const float RoadHalfWidth = 4.0f;   // 8m 宽（对齐项目光廊内宽口径）
        const float RoadLength = 128f;

        [MenuItem("超高速行者/VFX/重建废墟氛围火演示场景")]
        public static void Build()
        {
            var roadside = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabDir + "/FX_RuinsFire_Roadside.prefab");
            var linger = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabDir + "/FX_RuinsFire_Linger.prefab");
            if (roadside == null || linger == null)
            {
                Debug.LogError("[RuinsFireDemo] 缺预制体，请先跑「构建废墟氛围火资产」");
                return;
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var rnd = new System.Random(20261011);

            // ---------------- 环境光：极暗冷调（火是唯一暖光源）----------------
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.030f, 0.038f, 0.055f, 1f);
            RenderSettings.skybox = null;
            RenderSettings.fog = false;   // 雾交给工程的 HeightFog 渲染特性（全屏后置）

            var moon = new GameObject("L_Moon");
            var ml = moon.AddComponent<Light>();
            ml.type = LightType.Directional;
            ml.color = new Color(0.45f, 0.56f, 0.86f);
            ml.intensity = 0.16f;
            ml.shadows = LightShadows.Soft;
            ml.shadowStrength = 0.55f;
            moon.transform.rotation = Quaternion.Euler(38f, 214f, 0f);

            // ---------------- 材质 ----------------
            EnsureFolder(MatDir);
            var mFloor = NprMat(MatDir + "/M_Ruins_Floor.mat", new Color(0.085f, 0.083f, 0.078f), 0.06f);
            var mWall = NprMat(MatDir + "/M_Ruins_Wall.mat", new Color(0.145f, 0.140f, 0.126f), 0.12f);
            var mWallDark = NprMat(MatDir + "/M_Ruins_WallDark.mat", new Color(0.095f, 0.093f, 0.086f), 0.08f);
            var mRust = NprMat(MatDir + "/M_Ruins_Rust.mat", new Color(0.175f, 0.086f, 0.052f), 0.28f);

            var marker = EmissiveMat(MatDir + "/M_Ruins_Marker.mat", new Color(0.18f, 0.72f, 0.86f));

            // ---------------- 跑道地面（分段 + 轻微高低错位 → 废墟感；也让软粒子有用武之地）----------------
            var ground = new GameObject("RUBBLE_GROUND").transform;
            for (float z = 0f; z < RoadLength; z += 8f)
            {
                float len = 7.6f + (float)rnd.NextDouble() * 0.3f;
                var slab = Box(ground, "FloorSlab", new Vector3(0f, -0.15f, z + len * 0.5f),
                               new Vector3(RoadHalfWidth * 2f, 0.3f, len), mFloor, rnd);
                slab.transform.localPosition += new Vector3(0f, (float)rnd.NextDouble() * 0.012f, 0f);
            }

            // ---------------- 两侧断墙（每段独立高度/缺口；部分倾倒）----------------
            var walls = new GameObject("RUINS_WALLS").transform;
            for (int side = 0; side < 2; side++)
            {
                float x = (side == 0 ? -1f : 1f) * (RoadHalfWidth + 0.35f);
                float z = 0f;
                while (z < RoadLength)
                {
                    float seg = 4.5f + (float)rnd.NextDouble() * 3.5f;
                    float gap = 0.35f + (float)rnd.NextDouble() * 2.2f;
                    float h = 1.5f + (float)rnd.NextDouble() * 2.9f;
                    float thick = 0.42f + (float)rnd.NextDouble() * 0.18f;
                    var mat = rnd.NextDouble() < 0.25 ? mWallDark : mWall;

                    if (rnd.NextDouble() < 0.10)
                    {
                        // 倾倒的墙段：躺在地上、歪斜 → 断壁残垣的关键语汇
                        var fallen = Box(walls, "WallFallen", new Vector3(x * 1.15f, 0.25f, z + seg * 0.5f),
                                         new Vector3(thick, h, seg), mat, rnd);
                        fallen.transform.rotation = Quaternion.Euler(
                            (float)(rnd.NextDouble() * 12.0 - 6.0) + 82f,
                            (float)(rnd.NextDouble() * 30.0 - 15.0),
                            (float)(rnd.NextDouble() * 20.0 - 10.0));
                    }
                    else
                    {
                        var wall = Box(walls, "WallSeg", new Vector3(x, h * 0.5f, z + seg * 0.5f),
                                       new Vector3(thick, h, seg), mat, rnd);
                        // 顶边参差：再放一块半截墙在顶上
                        if (rnd.NextDouble() < 0.45)
                            Box(wall.transform, "WallTopChip",
                                new Vector3(0f, 0.5f + 0.25f, (float)(rnd.NextDouble() - 0.5) * 0.5f),
                                new Vector3(thick * 1.06f, 0.5f, seg * 0.45f), mat, rnd);
                    }
                    z += seg + gap;
                }
            }

            // ---------------- 塌落的顶板（留出破洞 → 火光/烟气有"破口"可走）----------------
            var ceiling = new GameObject("RUINS_CEILING").transform;
            for (float z = 3f; z < RoadLength; z += 11f)
            {
                if (rnd.NextDouble() < 0.45) continue;                 // 大段破洞
                float len = 3.5f + (float)rnd.NextDouble() * 4.5f;
                var slab = Box(ceiling, "CeilingSlab", new Vector3((float)(rnd.NextDouble() - 0.5) * 2.4f, 4.1f, z),
                               new Vector3(RoadHalfWidth * 1.9f, 0.32f, len), mWallDark, rnd);
                slab.transform.rotation = Quaternion.Euler(0f, (float)(rnd.NextDouble() * 8.0 - 4.0), (float)(rnd.NextDouble() * 3.5 - 1.75));
            }

            // ---------------- 瓦砾 ----------------
            var rubble = new GameObject("RUINS_RUBBLE").transform;
            for (int i = 0; i < 200; i++)
            {
                float z = (float)rnd.NextDouble() * RoadLength;
                float x = ((float)rnd.NextDouble() * 2f - 1f) * (RoadHalfWidth + 1.6f);
                float s = 0.12f + (float)rnd.NextDouble() * 0.55f;
                var mat = rnd.NextDouble() < 0.35 ? mRust : mWall;
                var b = Box(rubble, "Rubble", new Vector3(x, s * 0.42f, z),
                            new Vector3(s, s * 0.72f, s * (0.7f + (float)rnd.NextDouble() * 0.7f)), mat, rnd);
                b.transform.rotation = Quaternion.Euler(
                    (float)(rnd.NextDouble() * 40.0 - 20.0),
                    (float)rnd.NextDouble() * 360f,
                    (float)(rnd.NextDouble() * 40.0 - 20.0));
            }

            // ---------------- 横梁残骸（斜插/横躺，增加剪影层次）----------------
            var beams = new GameObject("RUINS_BEAMS").transform;
            for (int i = 0; i < 10; i++)
            {
                float z = 6f + (float)rnd.NextDouble() * (RoadLength - 12f);
                var b = Box(beams, "Beam", new Vector3(((float)rnd.NextDouble() * 2f - 1f) * 3.6f, 0.35f + (float)rnd.NextDouble() * 1.8f, z),
                            new Vector3(0.34f, 0.34f, 3.2f + (float)rnd.NextDouble() * 3.4f), mRust, rnd);
                b.transform.rotation = Quaternion.Euler(
                    (float)(rnd.NextDouble() * 46.0 - 23.0),
                    (float)rnd.NextDouble() * 360f,
                    (float)(rnd.NextDouble() * 52.0 - 26.0));
            }

            // ---------------- 驻足点（落脚平台 + 冷色引导条）----------------
            Vector2[] lingerSpots = { new Vector2(2.75f, 34f), new Vector2(-2.75f, 92f) };
            foreach (var spot in lingerSpots)
            {
                Box(null, "LedgePlatform", new Vector3(spot.x * 1.25f, 0.42f, spot.y),
                    new Vector3(3.2f, 0.55f, 5.0f), mWallDark, rnd);
                // 引导色条：冷色自发光（= 工程既有的"引导用专属色"，与火的暖橙形成分工）
                var strip = Box(null, "GuideStrip", new Vector3(spot.x * 1.25f, 0.72f, spot.y),
                                new Vector3(0.5f, 0.02f, 3.6f), marker, rnd);
                var mr = strip.GetComponent<MeshRenderer>();
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.sharedMaterial = marker;
            }

            // ---------------- 火：掠过档沿跑道节奏布点 ----------------
            var fires = new GameObject("RUINS_FIRES").transform;
            int roadsideCount = 0;
            for (float z = 9f; z < RoadLength - 6f; z += 10.5f)
            {
                bool left = ((int)(z / 10.5f) % 2) == 0;
                float x = (left ? -1f : 1f) * (2.55f + (float)rnd.NextDouble() * 0.7f);
                var go = (GameObject)PrefabUtility.InstantiatePrefab(roadside);
                go.transform.SetParent(fires, false);
                go.transform.localPosition = new Vector3(x, 0.02f, z + (float)(rnd.NextDouble() * 2.2 - 1.1));
                go.transform.localRotation = Quaternion.Euler(0f, (float)rnd.NextDouble() * 360f, 0f);
                float sc = 0.75f + (float)rnd.NextDouble() * 0.65f;      // 尺度层次：小火为主，个别稍大
                go.transform.localScale = new Vector3(sc, sc * (0.85f + (float)rnd.NextDouble() * 0.5f), sc);
                roadsideCount++;
            }

            // ---------------- 火：驻足档（补齐烟/余烬/闪烁灯）----------------
            int lingerCount = 0;
            foreach (var spot in lingerSpots)
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(linger);
                go.transform.SetParent(fires, false);
                go.transform.localPosition = new Vector3(spot.x, 0.72f, spot.y);
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one * 1.15f;
                lingerCount++;
            }

            // ---------------- 风向（全场景统一：火苗、烟、余烬同一股风）----------------
            var windGo = new GameObject("RuinsWind");
            var wind = windGo.AddComponent<RuinsFireWind>();
            wind.direction = new Vector2(0.35f, 1f);      // 顺着跑道吹
            wind.baseStrength = 0.5f;
            wind.gustAmplitude = 0.4f;

            // ---------------- 相机（高速掠过机位）----------------
            var camGo = new GameObject("CAM_RuinsFire_Flyby");
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 62f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 260f;
            cam.allowHDR = true;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.012f, 0.016f, 0.026f, 1f);
            camGo.AddComponent<RuinsFireFlyby>();
            camGo.tag = "MainCamera";

            EditorSceneManager.MarkSceneDirty(scene);
            EnsureFolder("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[RuinsFireDemo] 场景已生成 {ScenePath}：掠过档火 {roadsideCount} 堆 / 驻足档火 {lingerCount} 处");
        }

        // ------------------------------------------------------------------
        static GameObject Box(Transform parent, string name, Vector3 pos, Vector3 size, Material mat, System.Random rnd)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());   // 演示场景不需要碰撞
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = size;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.On;
            r.receiveShadows = true;
            r.lightProbeUsage = LightProbeUsage.Off;   // 无探针，靠实时光（火点光）与极暗环境光
            return go;
        }

        static Material NprMat(string path, Color baseColor, float smoothness)
        {
            var sh = Shader.Find("OurFunction/NPR/Deferred Toon");
            if (sh == null) { Debug.LogError("[RuinsFireDemo] 找不到 NPR 母材质着色器"); return null; }
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(sh); AssetDatabase.CreateAsset(m, path); }
            m.shader = sh;
            m.SetColor("_BaseColor", baseColor);
            m.SetColor("_ShadowColor", new Color(0.055f, 0.075f, 0.115f, 1f));   // 冷调暗部（废墟基调）
            m.SetFloat("_ToonThreshold", 0.70f);
            m.SetFloat("_ToonSoftness", 0.10f);
            m.SetFloat("_ToonSpecular", 0.12f);
            m.SetFloat("_Smoothness", smoothness);
            m.SetFloat("_Metallic", 0f);
            m.SetFloat("_Hatching", 0f);
            // 环境几何关描边（母材质的描边是给角色/关键物件的；废墟铺满描边会糊成一团）
            m.SetFloat("_OverrideOutline", 1f);
            m.SetFloat("_OutlineEnabled", 0f);
            EditorUtility.SetDirty(m);
            return m;
        }

        static Material EmissiveMat(string path, Color emission)
        {
            var sh = Shader.Find("HST/Emit");
            if (sh == null) return null;
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(sh); AssetDatabase.CreateAsset(m, path); }
            m.SetColor("_BaseColor", Color.black);
            m.SetColor("_EmissionColor", emission);
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive;
            EditorUtility.SetDirty(m);
            return m;
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
        }
    }
}
