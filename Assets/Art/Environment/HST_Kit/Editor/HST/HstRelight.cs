#if UNITY_EDITOR
// 三场景纯实时重打光 (2026-10-12 用户令: 不烘焙 / 无光晕无过曝 / 只要亮堂)。
// 权威规范: taptap-game-jem/docs/技术/NPR管线打光与制图规范.md §三(超净室三件套) §四(烘焙政策) §六(三场景整改清单)。
//
// Relight(): 每场景做四件事并**写盘**:
//   ① 全部灯转 Realtime; 点/聚光去阴影(原设计靠烘焙软化, 实时下阴影只添乱);
//   ② 方向光全部去阴影; 缺主光的场景补一盏 HST_KeyLight(旋转 50,-30,0, 冷灰 0.796, 无影, Realtime);
//   ③ Flat 环境光 = 起始表值 × ambientK (超净室底亮主体; 场景有烘焙数据时它被顶掉, ④ 删掉就复活);
//   ④ 只删 LightingData.asset(反射探针 cubemap / customReflection 都是独立资产, 不受影响), 然后存盘。
// Shoot(): 逐场景读盘出图(无任何临时灯/材质克隆), 每机位 [HRL-STAT] mean/死黑/过曝 → renders/relight_nobake/。
// 迭代口径: 改 tmp/relight/tune.json 的 ambientK/lightK/keyK 在 Shoot 里渲染期快调, 数字收敛后
//          把最终值写回下面 AMBIENT/KEY 常量复跑 Relight, 再跑一次 Shoot 验证"读场即所见"。
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Cinemachine;
using UnityEngine.Rendering.Universal;

namespace HST
{
    public static class HstRelight
    {
        const string OutDir  = @"F:\VSCode\工业级渲染\renders\relight_nobake";
        const string WorkDir = @"F:\VSCode\工业级渲染\tmp\relight";
        const int W = 1920, H = 1080;

        class SCfg
        {
            public string scene, tag;
            public float[] ambient;   // Flat 环境光起始值(规范 §3.1, 出图校准后回填正式值)
            public float keyIntensity; // >0 = 该场景要一盏 HST_KeyLight; 0 = 用场景自带方向光做主光
            public float skyboxExposure; // >0 = 克隆低曝光天空盒写入场景(压透过玻璃顶的天光过曝/光晕; 2026-10-13 概念厅)
            public float sunPersist;  // >0 = 概念厅这类"自带 sun"的场景: 把 sun 强度持久化为该值(校准终值)
            public float pointPersist; // >0 = 场景点/聚光按基线 ×该系数持久化(军用厅地面提亮, 基线幂等)
            public float[] shadowColor; // >0 = 影带色持久化到该场景前缀材质(白超净室去蓝)
            public bool floorMatte;     // 军用厅: M_V6_FloorM 关光滑度贴图改哑光 0.40(镜面地板掠射过曝的根治)
        }

        // 出图校准终值(2026-10-13, tune 迭代 5 轮收敛; 走廊 tune 全 1 故 ambient 即基准值):
        // Corr amb(0.26,0.29,0.33) key1.0 / Conc amb(0.30,0.33,0.37)×1.15 sun1.26×0.6 / Mil amb(0.30,0.33,0.37) key1.3
        static readonly SCfg[] Cfgs =
        {
            new SCfg { scene = "Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_Pragmata.unity", tag = "Corr",
                       ambient = new[] { 0.26f, 0.29f, 0.33f }, keyIntensity = 1.0f, skyboxExposure = 0f, sunPersist = 0f },
            new SCfg { scene = "Assets/Art/Environment/HST_Kit/Scenes/SC_Atrium_Concept.unity", tag = "Conc",
                       ambient = new[] { 0.345f, 0.380f, 0.426f }, keyIntensity = 0f, skyboxExposure = 0.7f, sunPersist = 0.756f },
            new SCfg { scene = "Assets/Art/Environment/HST_Kit/Scenes/SC_Atrium_Military.unity", tag = "Mil",
                       ambient = new[] { 0.68f, 0.72f, 0.82f }, keyIntensity = 1.7f, skyboxExposure = 0f, sunPersist = 0f,
                       pointPersist = 1.3f,
                       shadowColor = new[] { 0.36f, 0.46f, 0.64f }, floorMatte = false },   // 2026-10-13 晚: 全局光照加大(用户令:
                       // 不要局部打光, 全局更亮, 均匀白) —— 唯一动作 = 环境光整体加大; 地板保持原镜面(哑光已撤)
        };

        [System.Serializable]
        class Tune
        {
            // 逐场景渲染期快调(下标 = Cfgs 顺序: 0=走廊 1=概念 2=军用); 收敛后把终值写回 Cfg 常量复跑 Relight。
            public float[] ambientK = { 1f, 1f, 1f };
            public float[] lightK = { 1f, 1f, 1f };
            public float[] keyK = { 1f, 1f, 1f };
        }

        static Tune LoadTune()
        {
            string p = Path.Combine(WorkDir, "tune.json");
            if (!File.Exists(p)) return new Tune();
            var t = JsonUtility.FromJson<Tune>(File.ReadAllText(p));
            Debug.Log("[HRL] tune: " + File.ReadAllText(p).Replace("\n", " "));
            return t;
        }

        [MenuItem("HST/三场景 纯实时重打光(不烘焙, 写盘)")]
        public static void Relight()
        {
            FixGlow();
            FixGlass();
            foreach (var c in Cfgs) DoRelight(c);
            SetVolumetric(VolumetricDefaultOn);
            Debug.Log("[HRL] Relight 全部完成(场景已存盘, 烘焙数据已清)");
        }

        // ---------------------------------------------------------------- 体积光全局开关(2026-10-13 裁定: 默认关)
        // 依据: 2026-10-12 用户令"不要光晕、不需要过曝, 只要场景亮堂"。诊断(HstDiagBlow, 概念厅 CAM_PodUpMega):
        // 过曝 20.5% 在关 TA Volumetric Light 后 → 0.63%, 而主光/环境/天空盒/玻璃/点光五个场景侧旋钮全部无效
        // —— 体积光是仰视机位光晕与过曝的唯一来源。全局关闭(改 URP-Renderer 资产的 m_Active, 单字段可逆);
        // 恢复 = 菜单"HST/体积光 开关"再点一次。雾/SSR/Bloom 等 TA 其余特性原样保留。
        public const bool VolumetricDefaultOn = false;
        const string RendererAssetPath = "Assets/Settings/URP-Renderer.asset";

        [MenuItem("HST/体积光 开关(TA Volumetric Light, 全局)")]
        public static void ToggleVolumetric() => SetVolumetric(!GetVolumetric());

        static bool GetVolumetric()
        {
            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererAssetPath);
            foreach (var f in renderer.rendererFeatures)
                if (f.name.Contains("Volumetric"))
                    return new SerializedObject(f).FindProperty("m_Active").boolValue;
            return false;
        }

        static void SetVolumetric(bool on)
        {
            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererAssetPath);
            int n = 0;
            foreach (var f in renderer.rendererFeatures)
            {
                if (!f.name.Contains("Volumetric")) continue;
                var fso = new SerializedObject(f);
                fso.FindProperty("m_Active").boolValue = on;
                fso.ApplyModifiedPropertiesWithoutUndo();
                n++;
            }
            Debug.Log($"[HRL] TA Volumetric Light → {(on ? "开" : "关")}({n} 个特性)");
        }

        // ---------------------------------------------------------------- 发光件基色 ≤1 (规范 §2.3, 无光晕无过曝)
        // 基线记原值, 幂等: 已 ≤1.02 的不再动。目标 max=0.92: sRGB≈0.967 < 0.98 过曝线, 且远低于 Bloom 阈值 1.0。
        static readonly string[] GlowPrefixes = { "M_M_", "M_T_", "M_V6_" };
        // 2026-10-13 用户令: 点缀灯(青嵌条/蓝圆点)允许轻微发光 —— 不参与 ≤0.92 压值,
        // HDR 基线的恢复动作在 HstAtriumPolish.ApplyAccentGlow (亮度略过 Bloom 阈值 1.0 = 轻微光晕)。
        static readonly string[] GlowKeep = { "M_M_cyan", "M_M_blue_dot" };
        const float GlowTargetMax = 0.92f;
        static readonly string GlowBaseline = WorkDir + "/glow_baseline.psv";

        static bool IsGlow(Material m)
        {
            if (m.IsKeywordEnabled("_EMISSION") && m.HasProperty("_EmissionColor")
                && m.GetColor("_EmissionColor").maxColorComponent > 0.02f) return true;
            if (m.HasProperty("_BaseColor") && m.GetColor("_BaseColor").maxColorComponent > 1.02f) return true;
            return false;
        }

        static void FixGlow()
        {
            Directory.CreateDirectory(WorkDir);
            var baseline = new Dictionary<string, Color>();
            if (File.Exists(GlowBaseline))
                foreach (var line in File.ReadAllLines(GlowBaseline))
                {
                    var kv = line.Split('=');
                    if (kv.Length != 2) continue;
                    var rgba = kv[1].Split(',');
                    if (rgba.Length != 4) continue;
                    baseline[kv[0]] = new Color(float.Parse(rgba[0]), float.Parse(rgba[1]),
                                                float.Parse(rgba[2]), float.Parse(rgba[3]));
                }

            int fixedN = 0, skip = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets/Art/Environment/HST_Kit/Materials/HST_Prags" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (m == null || !GlowPrefixes.Any(p => m.name.StartsWith(p)) || !IsGlow(m)) continue;
                if (GlowKeep.Contains(m.name)) continue;   // 点缀灯豁免(2026-10-13 用户令: 轻微发光)
                if (!m.HasProperty("_BaseColor")) continue;
                if (!baseline.ContainsKey(path)) baseline[path] = m.GetColor("_BaseColor");
                var orig = baseline[path];
                float max = Mathf.Max(orig.r, Mathf.Max(orig.g, orig.b));
                if (max <= 1.02f) { skip++; continue; }   // 已收敛(或本来就 ≤1), 不动
                float k = GlowTargetMax / max;
                m.SetColor("_BaseColor", new Color(orig.r * k, orig.g * k, orig.b * k, orig.a));
                EditorUtility.SetDirty(m);
                Debug.Log($"[HRL] 发光件 {m.name}: ({orig.r:F2},{orig.g:F2},{orig.b:F2}) ×{k:F3} → max={GlowTargetMax}");
                fixedN++;
            }
            AssetDatabase.SaveAssets();
            File.WriteAllLines(GlowBaseline, baseline.Select(kv =>
                $"{kv.Key}={kv.Value.r:F6},{kv.Value.g:F6},{kv.Value.b:F6},{kv.Value.a:F6}"));
            Debug.Log($"[HRL] 发光件压值: 改 {fixedN} / 已达标跳过 {skip} (基线 {GlowBaseline})");
        }

        // ---------------------------------------------------------------- 玻璃降镜面 (概念厅过曝根因之二)
        // 玻璃 _Smoothness 0.95 ≈ 镜面: SSR(TA 固定参数 强度1.44×亮度1.4)把灯盘的反射放大到 >1,
        // 撞 Bloom 阈值出光晕(透过夹层玻璃仰视时全画 14% 过曝)。降到 0.72 仍是玻璃, 反射不再超阈。
        static readonly string[] GlassNames = { "M_M_glass" };   // 概念厅玻璃; 军用厅玻璃机位本来就干净, 不动
        const float GlassTargetSmooth = 0.72f;
        static readonly string GlassBaseline = WorkDir + "/glass_baseline.psv";

        static void FixGlass()
        {
            Directory.CreateDirectory(WorkDir);
            var baseline = new Dictionary<string, float>();
            if (File.Exists(GlassBaseline))
                foreach (var line in File.ReadAllLines(GlassBaseline))
                {
                    var kv = line.Split('=');
                    float v;
                    if (kv.Length == 2 && float.TryParse(kv[1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out v)) baseline[kv[0]] = v;
                }
            int n = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets/Art/Environment/HST_Kit/Materials/HST_Prags" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (m == null || !GlassNames.Any(p => m.name.StartsWith(p))) continue;
                if (!m.HasProperty("_Smoothness")) continue;
                if (!baseline.ContainsKey(path)) baseline[path] = m.GetFloat("_Smoothness");
                if (baseline[path] <= GlassTargetSmooth + 0.01f) continue;   // 已降过, 幂等
                m.SetFloat("_Smoothness", GlassTargetSmooth);
                EditorUtility.SetDirty(m);
                Debug.Log($"[HRL] 玻璃 {m.name}: _Smoothness {baseline[path]:F2} → {GlassTargetSmooth}");
                n++;
            }
            AssetDatabase.SaveAssets();
            File.WriteAllLines(GlassBaseline, baseline.Select(kv =>
                kv.Key + "=" + kv.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
            Debug.Log($"[HRL] 玻璃降镜面: 改 {n} (基线 {GlassBaseline})");
        }

        [MenuItem("HST/三场景 纯实时出图(读场, 不动资产)")]
        public static void Shoot()
        {
            Directory.CreateDirectory(OutDir);
            var t = LoadTune();
            for (int i = 0; i < Cfgs.Length; i++) DoShoot(Cfgs[i], i, t);
            Debug.Log("[HRL] Shoot 全部完成 → " + OutDir);
        }

        [MenuItem("HST/三场景 纯实时重打光+出图(一步)")]
        public static void Run() { DedupPlayers(); EnsureVcam(); EnsureFreeLook(); Relight(); Shoot(); }

        // ---------------------------------------------------------------- Cinemachine 跟随机位 (2026-10-13 用户报: 运行黑屏)
        // 病根: CinemachineBrain 在 HST_FollowCamera 上, 但三场景一个 vcam 都没有 → 运行时无可切机位,
        // 镜头僵在存档位置(军用厅 (29,1.7,10) 贴右墙朝墙) → 黑屏。补一个跟随 Player 的 vcam(优先级 10)。
        // ---------------------------------------------------------------- 主控小人去重(2026-10-13 用户令: 场景里别出现多个)
        // 两厅曾被存了 2 个 Player.prefab 实例(根名 mixamorig:Head)。每场保留一个, 其余删除。
        // 跟随相机(HST_FollowCamera + CinemachineBrain)每场 1 个, 需配 vcam(见 EnsureVcam)。
        [MenuItem("HST/场景 主控小人去重(每场留一个 Player)")]
        public static void DedupPlayers()
        {
            // 两遍: ① Tag=Player(顶层) ② 按预制体路径识别(概念厅的实例 Tag 被覆盖, Tag 找不到)
            foreach (var c in Cfgs)
            {
                EditorSceneManager.OpenScene(c.scene, OpenSceneMode.Single);
                var tagged = UnityEngine.Object.FindObjectsOfType<GameObject>(true)
                    .Where(g => g != null && g.CompareTag("Player")).ToList();
                // 只认顶层: 预制体里 Hitbox 等子物体也带 Player 标签, 连子带父删会 NRE(已踩)
                var all = tagged.Where(g => !tagged.Any(o => o != g && g.transform.IsChildOf(o.transform))).ToList();
                if (all.Count <= 1)
                {
                    // ② 按预制体路径再查一遍(概念厅的实例 Tag 被覆盖, Tag 找不到)
                    var roots = UnityEngine.Object.FindObjectsOfType<GameObject>(true)
                        .Where(g => PrefabUtility.IsPartOfPrefabInstance(g)
                                    && PrefabUtility.GetOutermostPrefabInstanceRoot(g) == g
                                    && PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(g) == "Assets/Prefabs/Player.prefab")
                        .ToList();
                    if (roots.Count <= 1)
                    {
                        Debug.Log($"[HRL] {c.tag} 主控小人 ×0, 无需去重");
                        continue;
                    }
                    all = roots;
                }
                string keptName = all[0].name + "@" + all[0].transform.position.ToString("F1");
                for (int i = 1; i < all.Count; i++) UnityEngine.Object.DestroyImmediate(all[i]);
                var sceneD = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                EditorSceneManager.MarkSceneDirty(sceneD);
                EditorSceneManager.SaveScene(sceneD);
                Debug.Log($"[HRL] {c.tag} 主控小人 {all.Count}→1, 保留 {keptName}, 已存盘");
            }
        }

        // ---------------------------------------------------------------- vcam 兜底 (EnsureFreeLook 的前置)
        // 保证每厅至少一个 vcam 跟随 Player(没有才建); 鼠标环绕由 EnsureFreeLook 的现成 FreeLook 负责。
        static void EnsureVcam()
        {
            foreach (var c in Cfgs)
            {
                if (c.tag == "Corr") continue;
                EditorSceneManager.OpenScene(c.scene, OpenSceneMode.Single);
                var vcams = UnityEngine.Object.FindObjectsOfType<Cinemachine.CinemachineVirtualCamera>(true);
                var followOk = vcams.Any(v => v.Follow != null);
                if (vcams.Length > 0 && followOk) { Debug.Log($"[HRL] {c.tag} vcam 已就绪 ×{vcams.Length}"); continue; }
                var player = GameObject.Find("Player");
                if (player == null)
                    foreach (var g in UnityEngine.Object.FindObjectsOfType<GameObject>(true))
                        if (PrefabUtility.IsPartOfPrefabInstance(g)
                            && PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(g) == "Assets/Prefabs/Player.prefab")
                        { player = g; break; }
                if (player == null) { Debug.LogWarning($"[HRL] {c.tag} 无 Player, vcam 兜底跳过"); continue; }
                var target = vcams.Length > 0 ? vcams[0] : null;
                if (target == null)
                {
                    var go = new GameObject("CM_HallVcam");
                    go.transform.position = player.transform.position + new Vector3(0f, 2.2f, -3.5f);
                    target = go.AddComponent<Cinemachine.CinemachineVirtualCamera>();
                    target.Priority = 10;
                    var body = target.AddCinemachineComponent<Cinemachine.CinemachineTransposer>();
                    body.m_FollowOffset = new Vector3(0f, 2.2f, -3.5f);
                }
                target.Follow = player.transform;
                EditorUtility.SetDirty(target);
                var sceneV = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                EditorSceneManager.MarkSceneDirty(sceneV);
                EditorSceneManager.SaveScene(sceneV);
                Debug.Log($"[HRL] {c.tag} vcam 跟随 → {player.name}@{player.transform.position:F1}, 场景已存盘");
            }
        }

        // ---------------------------------------------------------------- 现成 Cinemachine FreeLook 跟随 (2026-10-13)
        // 用户裁定: 用现成组件, 不写代码。CinemachineFreeLook 自带鼠标环绕(Mouse X/Y 轴),
        // Follow = Player, Play 即第三人称跟随视角。
        [MenuItem("HST/场景 换现成 Cinemachine FreeLook 跟随(两厅)")]
        public static void EnsureFreeLook()
        {
            foreach (var c in Cfgs)
            {
                if (c.tag == "Corr") continue;   // 走廊接的是游戏流程, 不动
                EditorSceneManager.OpenScene(c.scene, OpenSceneMode.Single);

                // 清掉自定义自由相机组件(已裁定不用)
                int removed = 0;
                foreach (var g in UnityEngine.Object.FindObjectsOfType<GameObject>(true))
                    foreach (var comp in g.GetComponents<Component>())
                        if (comp != null && comp.GetType().Name == "HST_FreeLookCam")
                        { UnityEngine.Object.DestroyImmediate(comp); removed++; }

                // 清掉此前误建的 Transposer vcam
                var oldVcams = UnityEngine.Object.FindObjectsOfType<Cinemachine.CinemachineVirtualCamera>(true);
                foreach (var v in oldVcams)
                    if (v != null && v.name == "CM_HallVcam") UnityEngine.Object.DestroyImmediate(v.gameObject);

                // 现成 CinemachineFreeLook: 鼠标环绕内置, 零代码
                if (UnityEngine.Object.FindObjectsOfType<Cinemachine.CinemachineFreeLook>(true).Length == 0)
                {
                    var player = GameObject.Find("Player");
                    if (player == null)
                        foreach (var g in UnityEngine.Object.FindObjectsOfType<GameObject>(true))
                            if (PrefabUtility.IsPartOfPrefabInstance(g)
                                && PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(g) == "Assets/Prefabs/Player.prefab")
                            { player = g; break; }
                    if (player == null) { Debug.LogWarning($"[HRL] {c.tag} 无 Player, FreeLook 没有跟随目标, 跳过"); continue; }

                    var go = new GameObject("CM_HallFreeLook");
                    go.transform.position = player.transform.position + new Vector3(0f, 2f, -4f);
                    var fl = go.AddComponent<Cinemachine.CinemachineFreeLook>();
                    fl.Priority = 10;
                    fl.Follow = player.transform;
                    fl.m_Lens.FieldOfView = 60f;
                    fl.m_Orbits[0].m_Radius = 2.5f; fl.m_Orbits[0].m_Height = 2.2f;   // 顶环
                    fl.m_Orbits[1].m_Radius = 4.0f; fl.m_Orbits[1].m_Height = 1.2f;   // 中环
                    fl.m_Orbits[2].m_Radius = 2.5f; fl.m_Orbits[2].m_Height = -0.4f;  // 底环
                    EditorUtility.SetDirty(fl);
                }

                var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                Debug.Log($"[HRL] {c.tag} 已换现成 Cinemachine FreeLook(移除自定义组件 {removed} 个), 场景已存盘");
            }
        }

        // ---------------------------------------------------------------- Relight

        static void DoRelight(SCfg c)
        {
            var scene = EditorSceneManager.OpenScene(c.scene, OpenSceneMode.Single);
            try { var ls = UnityEditor.Lightmapping.lightingSettings; if (ls != null) ls.autoGenerate = false; }
            catch (System.Exception) { }

            // 健康守卫(2026-10-13 险情): 走廊场景文件曾在磁盘上带 Duplicate identifier, 打开即残缺
            // (灯/机位为 0)——绝不能把残缺加载 SaveScene 回去覆盖好文件。灯或相机为零 = 拒绝本场景。
            // (相机要带 inactive: 走廊的 CAM_01..06 挂在未激活 GameObject 上, 是出图工具临时激活的。)
            int guardLights = UnityEngine.Object.FindObjectsOfType<Light>(true).Length;
            int guardCams = UnityEngine.Object.FindObjectsOfType<Camera>(true).Length;
            if (guardLights == 0 || guardCams == 0)
            {
                Debug.LogError($"[HRL] {c.tag} 场景打开后 灯{guardLights}/相机{guardCams} (文件损坏或打开失败), 已中止且不存盘: {c.scene}");
                return;
            }

            int nLights = 0;
            bool hasKey = false;
            foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>(true))
            {
                l.lightmapBakeType = LightmapBakeType.Realtime;   // Baked/Mixed → 全实时
                if (l.type == LightType.Directional)
                {
                    l.shadows = LightShadows.None;                // 室内方向光带阴影 = 全厅楼板阴影(规范 §3.3 铁律)
                    if (l.name == "HST_KeyLight") hasKey = true;
                }
                else
                {
                    l.shadows = LightShadows.None;
                }
                EditorUtility.SetDirty(l);
                nLights++;
            }

            if (c.keyIntensity > 0f)
            {
                Light key = null;
                foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>(true))
                    if (l.name == "HST_KeyLight") { key = l; break; }
                if (key == null)
                {
                    var go = new GameObject("HST_KeyLight");
                    key = go.AddComponent<Light>();
                    go.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
                    Debug.Log($"[HRL] {c.tag} 新建主方向光 HST_KeyLight");
                }
                key.type = LightType.Directional;
                key.color = new Color(0.796f, 0.796f, 0.796f);
                key.intensity = c.keyIntensity;
                key.shadows = LightShadows.None;
                key.lightmapBakeType = LightmapBakeType.Realtime;
                EditorUtility.SetDirty(key);
                hasKey = true;
            }
            else if (c.sunPersist > 0f)
            {
                // 自带 sun 的场景(概念厅): sun 强度持久化为校准终值, 出图期 keyK 收敛后应设回 1。
                foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>(true))
                    if (l.type == LightType.Directional && l.name.ToLower().Contains("sun"))
                    {
                        l.intensity = c.sunPersist;
                        EditorUtility.SetDirty(l);
                        Debug.Log($"[HRL] {c.tag} {l.name} 强度持久化 = {c.sunPersist}");
                    }
            }

            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(c.ambient[0], c.ambient[1], c.ambient[2], 1f);

            // 点/聚光持久化系数(军用厅地面提亮): 基线记原始强度, 幂等不叠加。
            if (c.pointPersist > 0f)
            {
                var baseFile = WorkDir + "/light_baseline_" + c.tag + ".psv";
                var baseline = new Dictionary<string, float>();
                if (File.Exists(baseFile))
                    foreach (var line in File.ReadAllLines(baseFile))
                    {
                        var kv = line.Split('=');
                        float v;
                        if (kv.Length == 2 && float.TryParse(kv[1], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out v)) baseline[kv[0]] = v;
                    }
                int np = 0;
                foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>(true))
                {
                    if (l.type == LightType.Directional || l.name == "HST_KeyLight") continue;
                    string key = c.scene + "|" + l.name;
                    if (!baseline.ContainsKey(key)) baseline[key] = l.intensity;   // 首跑记原值
                    float target = baseline[key] * c.pointPersist;
                    if (Mathf.Abs(l.intensity - target) > 1e-4f) { l.intensity = target; EditorUtility.SetDirty(l); }
                    np++;
                }
                File.WriteAllLines(baseFile, baseline.Select(kv =>
                    kv.Key + "=" + kv.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
                Debug.Log($"[HRL] {c.tag} 点/聚光 ×{c.pointPersist} 持久化 {np} 盏(基线 {baseFile})");
            }

            // 影带色持久化(白超净室去蓝): 只动本线前缀材质(M_V6_/M_M_/M_T_ 只被两厅引用, 已验证不外溢)。
            if (c.shadowColor != null && c.shadowColor.Length >= 3)
            {
                string[] prefixes = c.tag == "Mil" ? new[] { "M_V6_" } : new[] { "M_M_", "M_T_", "M_V6_" };
                var col = new Color(c.shadowColor[0], c.shadowColor[1], c.shadowColor[2], 1f);
                int ns = 0;
                foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>(true))
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m == null || !m.HasProperty("_ShadowColor")) continue;
                        if (!prefixes.Any(p => m.name.StartsWith(p))) continue;
                        m.SetColor("_ShadowColor", col);
                        EditorUtility.SetDirty(m);
                        ns++;
                    }
                AssetDatabase.SaveAssets();
                Debug.Log($"[HRL] {c.tag} 影带色 → ({col.r:F2},{col.g:F2},{col.b:F2}) 命中 {ns} 材质实例(注意 M_V6_ 两厅共用)");
            }

            scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[HRL] {c.tag} 灯转实时 {nLights} 盏 主光={(hasKey ? "有" : "无")} " +
                      $"环境光=({RenderSettings.ambientLight.r:F3},{RenderSettings.ambientLight.g:F3},{RenderSettings.ambientLight.b:F3}) 场景已存盘");
        }

        // ---------------------------------------------------------------- Shoot

        static void DoShoot(SCfg c, int idx, Tune t)
        {
            EditorSceneManager.OpenScene(c.scene, OpenSceneMode.Single);
            try { var ls = UnityEditor.Lightmapping.lightingSettings; if (ls != null) ls.autoGenerate = false; }
            catch (System.Exception) { }

            if (Mathf.Abs(t.ambientK[idx] - 1f) > 1e-3f)
                RenderSettings.ambientLight *= t.ambientK[idx];   // 渲染期快调, 不存盘
            int nK = 0, nL = 0;
            foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>(true))
            {
                if (!l.enabled) continue;
                bool isKey = l.type == LightType.Directional &&
                             (l.name == "HST_KeyLight" || l.name.ToLower().Contains("sun"));
                if (isKey && Mathf.Abs(t.keyK[idx] - 1f) > 1e-3f) { l.intensity *= t.keyK[idx]; nK++; }
                else if (!isKey && Mathf.Abs(t.lightK[idx] - 1f) > 1e-3f) { l.intensity *= t.lightK[idx]; nL++; }
            }
            Debug.Log($"[HRL] {c.tag} 渲染期快调: 主光×{t.keyK[idx]}({nK}) 其余×{t.lightK[idx]}({nL}) 环境×{t.ambientK[idx]}");

            // 走廊的 CAM_01..06 挂在未激活 GameObject 上(出图时临时激活) —— 必须带 inactive 枚举。
            var cams = UnityEngine.Object.FindObjectsOfType<Camera>(true)
                .Where(cam => cam.name.StartsWith("CAM_"))
                .OrderBy(cam => cam.name).ToArray();
            if (cams.Length == 0) { Debug.LogWarning($"[HRL] {c.tag} 无 CAM_ 机位"); return; }

            // 首帧冷启动防翻转(已修坑): 第一台先拍一张丢弃, 再正式拍。
            Shot(cams[0], Path.Combine(WorkDir, "warmup.png"), c.tag + "/warmup");

            foreach (var cam in cams)
            {
                var go = cam.gameObject;
                bool goB = go.activeSelf, enB = cam.enabled;
                go.SetActive(true); cam.enabled = true;
                var cd = cam.GetUniversalAdditionalCameraData();
                bool ppB = cd != null && cd.renderPostProcessing;
                if (cd != null) cd.renderPostProcessing = true;
                cam.nearClipPlane = 0.05f; cam.farClipPlane = 3000f;
                // TAA 需要≥2帧收敛, 单帧读数在轮次间漂移(实测 desk/shaft ±5% 过曝) → 每台渲两帧取第二帧。
                Shot(cam, Path.Combine(WorkDir, "warm2.png"), c.tag + "/warm2");
                Shot(cam, Path.Combine(OutDir, $"unity_{c.tag}_{cam.name}.png"), c.tag + "/" + cam.name);
                if (cd != null) cd.renderPostProcessing = ppB;
                cam.enabled = enB; go.SetActive(goB);
            }
        }

        // ---------------------------------------------------------------- 出图 + 统计 (与 HstHallNprShoot.Shot 同口径)
        static void Shot(Camera cam, string path, string label)
        {
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Linear);
            var prev = cam.targetTexture;
            cam.targetTexture = rt;
            cam.aspect = (float)W / H;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGBAFloat, false, true);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            var px = tex.GetPixels();
            var t8 = new Texture2D(W, H, TextureFormat.RGBA32, false, false);
            var c8 = new Color[px.Length];
            double sum = 0; int dark = 0, blown = 0;
            for (int k = 0; k < px.Length; k++)
            {
                float r = L2S(Mathf.Clamp01(px[k].r)), g = L2S(Mathf.Clamp01(px[k].g)), b = L2S(Mathf.Clamp01(px[k].b));
                c8[k] = new Color(r, g, b, 1f);
                float lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                sum += lum;
                if (lum < 0.05f) dark++;
                if (lum > 0.98f) blown++;
            }
            t8.SetPixels(c8);
            t8.Apply();
            File.WriteAllBytes(path, t8.EncodeToPNG());
            cam.targetTexture = prev;
            RenderTexture.active = null;
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(tex);
            UnityEngine.Object.DestroyImmediate(t8);
            Debug.Log(string.Format("[HRL-STAT] {0} mean={1:F3} 死黑={2:F2}% 过曝={3:F2}%",
                label, sum / px.Length, 100.0 * dark / px.Length, 100.0 * blown / px.Length));
        }

        static float L2S(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(Mathf.Max(c, 0f), 1f / 2.4f) - 0.055f;
    }
}
#endif
