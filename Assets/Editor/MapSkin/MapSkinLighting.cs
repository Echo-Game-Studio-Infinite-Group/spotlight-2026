using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;

namespace MapSkin
{
    /// <summary>
    /// Level1 纯实时打光 v2（2026-10-18 按 TA 示例场景 `Assets/TA/LookDev/Scenes/TATestScene.unity` 对齐，写盘）。
    ///
    /// 依据（TA 示例场景实测，见派单）：
    ///   · 环境光 = **Gradient 三色**（`m_AmbientMode: 1` = Trilight），Sky (0.50,0.69,0.71) / Equator (0.36,0.55,0.58) /
    ///     Ground (0.22,0.39,0.42)，强度 1 —— 这是"环境光怎么调都没反应"的**真正根因**：
    ///     Flat 单色在 TA 这条 NPR 路径下给不出方向性 SH 分段，单拉数值全场读数纹丝不动（实测 Flat 0.52→1.0：mean 0.193→0.193）。
    ///   · 点光 = TA 用 18–28 candela 量级（我们上一版是 0 盏，这是画面平、无局部亮池的主因）
    ///   · 主方向光 = TA 0.75 灰（规范 §3.1 写 1.0，差 25%，此处按 TA 对齐）
    ///   · 全局反射强度 = TA 0.08（TA 地板自带镜面材质）；我们地板起步，用 0.8 托底
    ///
    /// 本步做四件事（只碰灯光/环境/反射设置，不动对象结构、名字、碰撞）：
    ///   ① 环境光 Gradient 三色（TA 值原样）+ 强度 1
    ///   ② 主方向光：**恰好一盏**、`shadows = None`（铁律，TA 开阴影的路子不能学——室内楼板阴影会掐死卡通层）、
    ///      强度 0.75、色 (0.796,0.796,0.796)、旋转 (50,-30,0)；其余方向光去影降权
    ///   ③ 逐区块（BOX_S_01..07）摆 **2–4 盏 Realtime 无影点光**：主基调青白 (0.68,0.84,0.9)、每区一盏暖白 (1.0,0.94,0.82)
    ///      做焦点；高度 = 区块顶下方 4–6 m；强度 18–28 candela；半径覆盖本区；打印摆位表
    ///   ④ 2 个 Realtime ReflectionProbe（反射不是照明，规范 §四保留口径）+ `RenderSettings.reflectionIntensity`
    ///
    /// 烘焙：仍按规范 §四清（不烘焙）；**不新增非本场景的 LightingData**。
    /// 幂等：点光/探针按名字复用并覆盖参数，不重复堆叠。
    /// 菜单：MapSkin/④ Level1 纯实时打光(写盘)
    /// 批处理：-executeMethod MapSkin.MapSkinLighting.Run
    ///   可选：-ambientmode flat|gradient   -ambient r,g,b   -sky r,g,b   -equator r,g,b   -ground r,g,b
    ///         -key 0.75   -pointcd a,b   -reflintensity 0.8   -probeondemand
    /// </summary>
    public static class MapSkinLighting
    {
        const string ScenePath = "Assets/Scenes/Level1.unity";

        // ---- ① 环境光（TA 三色原样）----
        static Color Sky = new Color(0.50f, 0.69f, 0.71f);
        static Color Equator = new Color(0.36f, 0.55f, 0.58f);
        static Color Ground = new Color(0.22f, 0.39f, 0.42f);
        static Color FlatAmbient = new Color(0.52f, 0.54f, 0.58f);   // -ambientmode flat 时用
        static bool UseGradient = true;

        // ---- ② 主光（对齐 TA 0.75）----
        static readonly Color KeyColor = new Color(0.796f, 0.796f, 0.796f);
        static float KeyIntensity = 0.75f;
        static readonly Vector3 KeyEuler = new Vector3(50f, -30f, 0f);
        const float ExtraDirIntensity = 0.2f;

        // ---- ③ 区块点光 ----
        static readonly Color PointCyan = new Color(0.68f, 0.84f, 0.90f);
        static readonly Color PointWarm = new Color(1.00f, 0.94f, 0.82f);
        static float PointCd = 22f;          // 青白主基调（TA 量级 18–28）
        static float PointWarmCd = 26f;      // 暖白焦点
        /// <summary>点光落在区块顶下方多少米（派单：4–6 m）。</summary>
        const float PointDropFromTop = 5f;

        // ---- ④ 反射 ----
        static float ReflectionIntensity = 0.8f;   // 0.6–1.0 托底
        static bool ProbeEveryFrame = true;

        const string PointPrefix = "HST_PL_";
        const string ProbePrefix = "HST_RP_";

        [MenuItem("MapSkin/④ Level1 纯实时打光(写盘)")]
        public static void RunMenu() => Run();

        public static void Run()
        {
            ParseArgs();
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var rep = new StringBuilder();

            // ---------- ① 环境光 ----------
            if (UseGradient)
            {
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
                RenderSettings.ambientSkyColor = Clamp01(Sky);
                RenderSettings.ambientEquatorColor = Clamp01(Equator);
                RenderSettings.ambientGroundColor = Clamp01(Ground);
                rep.AppendLine($"① 环境光：Gradient 三色（对齐 TA TATestScene）" +
                               $"Sky{Clamp01(Sky)} Equator{Clamp01(Equator)} Ground{Clamp01(Ground)}，强度 1");
            }
            else
            {
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = Clamp01(FlatAmbient);
                rep.AppendLine($"① 环境光：Flat 单色 {Clamp01(FlatAmbient)}（-ambientmode flat；注意 TA 口径是 Gradient）");
            }
            RenderSettings.ambientIntensity = 1f;
            RenderSettings.fog = false;

            // ---------- ② 恰好一盏无影主方向光 ----------
            var dirs = new List<Light>();
            foreach (var l in Object.FindObjectsOfType<Light>())
                if (l.type == LightType.Directional && l.enabled) dirs.Add(l);
            Light key = dirs.FirstOrDefault(l => l.shadows == LightShadows.None)
                     ?? dirs.OrderByDescending(l => l.intensity).FirstOrDefault();
            if (key == null)
            {
                key = new GameObject("HST_KeyLight").AddComponent<Light>();
                key.type = LightType.Directional;
                dirs.Add(key);
                rep.AppendLine("② 场景原本 0 盏方向光 → 新建 HST_KeyLight 作主光");
            }
            else
            {
                rep.AppendLine($"② 主光选定 = 「{key.gameObject.name}」（原 intensity={key.intensity:F2}、" +
                               $"shadows={key.shadows}、原色=({key.color.r:F2},{key.color.g:F2},{key.color.b:F2})）");
            }
            key.enabled = true;
            key.type = LightType.Directional;
            key.shadows = LightShadows.None;
            key.intensity = KeyIntensity;
            key.color = Clamp01(KeyColor);
            key.lightmapBakeType = LightmapBakeType.Realtime;
            key.transform.rotation = Quaternion.Euler(KeyEuler);
            rep.AppendLine($"    → shadows=None、intensity={KeyIntensity:F2}（TA 0.75 / 规范 §3.1 写 1.0，本步按 TA 对齐）、" +
                           $"色{Clamp01(KeyColor)}、旋转{KeyEuler}、Realtime");
            foreach (var l in dirs)
            {
                if (l == key) continue;
                string before = $"intensity={l.intensity:F2}、shadows={l.shadows}";
                l.shadows = LightShadows.None;
                if (l.intensity > ExtraDirIntensity) l.intensity = ExtraDirIntensity;
                l.lightmapBakeType = LightmapBakeType.Realtime;
                rep.AppendLine($"    · 多余方向光「{l.gameObject.name}」：{before} → shadows=None、intensity={l.intensity:F2}（只进 PBR 层）");
            }

            // ---------- ③ 区块点光 ----------
            var blocks = scene.GetRootGameObjects().Where(g => g.name.StartsWith("BOX_S_")).OrderBy(g => g.name).ToList();
            var keep = new HashSet<string>();
            int made = 0, updated = 0;
            rep.AppendLine("③ 区块 Realtime 无影点光摆位表（青白主基调 + 每区一盏暖白焦点）：");
            rep.AppendLine("    区块      灯名                 色               强度    位置                        半径");
            foreach (var b in blocks)
            {
                var bb = CollectBounds(new[] { b });
                if (bb.size.sqrMagnitude < 1e-6f) { rep.AppendLine($"    {b.name}: 无渲染器，跳过"); continue; }
                float longAxis = Mathf.Max(bb.size.x, bb.size.z);
                bool alongX = bb.size.x >= bb.size.z;
                int n = Mathf.Clamp(Mathf.RoundToInt(longAxis / 45f) + 1, 2, 4);
                float y = Mathf.Max(bb.max.y - PointDropFromTop, bb.min.y + 3f);
                float range = Mathf.Max(longAxis / n * 1.3f, 25f);

                for (int i = 0; i < n; i++)
                {
                    bool warm = i == 0;                     // 每区第 1 盏 = 暖白焦点，落在区块中心
                    float t = warm ? 0.5f : (i + 0.5f) / (n - 1 + 0.5f);
                    var pos = new Vector3(
                        alongX ? Mathf.Lerp(bb.min.x, bb.max.x, t) : bb.center.x,
                        y,
                        alongX ? bb.center.z : Mathf.Lerp(bb.min.z, bb.max.z, t));
                    string name = $"{PointPrefix}{b.name.Replace("BOX_", "")}_{(warm ? "Warm" : "C" + i)}";
                    var col = warm ? Clamp01(PointWarm) : Clamp01(PointCyan);
                    float cd = warm ? PointWarmCd : PointCd;
                    var go = GameObject.Find(name);
                    if (go == null)
                    {
                        go = new GameObject(name);
                        go.transform.SetParent(null, false);
                        go.AddComponent<Light>();
                        made++;
                    }
                    else updated++;
                    var lp = go.GetComponent<Light>();
                    lp.type = LightType.Point;
                    lp.color = col;
                    lp.intensity = cd;
                    lp.range = range;
                    lp.shadows = LightShadows.None;            // 铁律：室内开阴影掐死卡通层（不学 TA）
                    lp.lightmapBakeType = LightmapBakeType.Realtime;
                    go.transform.position = pos;
                    keep.Add(name);
                    rep.AppendLine($"    {b.name}  {name,-20} ({col.r:F2},{col.g:F2},{col.b:F2})  {cd,5:F0}   " +
                                   $"({pos.x,7:F1},{pos.y,6:F1},{pos.z,7:F1})  {range,6:F1}");
                }
            }
            // 清掉本工具上一轮多出来、本轮不再需要的灯（幂等：区块灯数变了也不留孤儿）
            int removed = 0;
            foreach (var l in Object.FindObjectsOfType<Light>())
            {
                var n = l.gameObject.name;
                if (!n.StartsWith(PointPrefix) || keep.Contains(n)) continue;
                Object.DestroyImmediate(l.gameObject);
                removed++;
            }
            rep.AppendLine($"    点光：新建 {made}、更新 {updated}、清掉多余 {removed}；" +
                           $"总计 {keep.Count} 盏（强度青白 {PointCd:F0} / 暖白 {PointWarmCd:F0} candela，shadows=None）");

            // ---------- ④ 反射探针 + 全局反射强度 ----------
            RenderSettings.reflectionIntensity = Mathf.Clamp01(ReflectionIntensity);
            rep.AppendLine($"④ 全局反射强度 = {RenderSettings.reflectionIntensity:F2}（TA 用 0.08 —— 它地板自带镜面；我们起步托底 0.8）");
            var all = scene.GetRootGameObjects().ToList();
            var lb = CollectBounds(all);
            var probeKeep = new HashSet<string>();
            for (int i = 0; i < 2; i++)
            {
                string name = $"{ProbePrefix}Level_{i}";
                probeKeep.Add(name);
                var go = GameObject.Find(name);
                if (go == null) go = new GameObject(name);
                var rp = go.GetComponent<ReflectionProbe>();
                if (rp == null) rp = go.AddComponent<ReflectionProbe>();
                rp.mode = ReflectionProbeMode.Realtime;
                rp.refreshMode = ProbeEveryFrame ? ReflectionProbeRefreshMode.EveryFrame : ReflectionProbeRefreshMode.ViaScripting;
                rp.timeSlicingMode = ReflectionProbeTimeSlicingMode.AllFacesAtOnce;
                rp.resolution = 128;
                rp.boxProjection = true;
                rp.intensity = 1f;
                rp.nearClipPlane = 0.3f;
                rp.farClipPlane = 1000f;
                // 两个探针把全图沿长轴（x）一分为二，中心抬到关卡中部高度之上
                float t = i == 0 ? 0.3f : 0.7f;
                rp.center = Vector3.zero;
                rp.size = new Vector3(lb.size.x * 0.6f + 50f, Mathf.Max(lb.size.y * 2f, 200f), lb.size.z * 1.4f + 50f);
                go.transform.position = new Vector3(Mathf.Lerp(lb.min.x, lb.max.x, t),
                                                    lb.center.y + lb.size.y * 0.5f,
                                                    lb.center.z);
                rep.AppendLine($"    探针 {name}: Realtime/{rp.refreshMode}/128px/boxProjection " +
                               $"位置({go.transform.position.x:F0},{go.transform.position.y:F0},{go.transform.position.z:F0}) size{rp.size}");
            }
            int probeRemoved = 0;
            foreach (var go in scene.GetRootGameObjects())
            {
                if (!go.name.StartsWith(ProbePrefix) || probeKeep.Contains(go.name)) continue;
                Object.DestroyImmediate(go);
                probeRemoved++;
            }
            if (probeRemoved > 0) rep.AppendLine($"    清掉多余探针 {probeRemoved} 个");
            rep.AppendLine("    口径：反射探针是「反射」不是「照明」（规范 §四保留），不计入烘焙；" +
                           $"refreshMode={(ProbeEveryFrame ? "EveryFrame" : "ViaScripting")}" +
                           (ProbeEveryFrame ? "（保证运行时即所见；两个 128px 探针开销可接受）" : "（省开销；-probeondemand 走 ViaScripting，需自行 RenderProbe）"));

            // ---------- ⑤ 清烘焙数据 ----------
            int lightmapRefs = 0;
            foreach (var r in Object.FindObjectsOfType<Renderer>())
                if (r.lightmapIndex >= 0 && r.lightmapIndex < 65534) lightmapRefs++;
            var oldAsset = Lightmapping.lightingDataAsset;
            string oldAssetPath = oldAsset != null ? AssetDatabase.GetAssetPath(oldAsset) : null;
            Lightmapping.ClearLightingDataAsset();
            Lightmapping.Clear();
            Lightmapping.giWorkflowMode = Lightmapping.GIWorkflowMode.OnDemand;
            var ls = Lightmapping.lightingSettings;
            if (ls != null)
            {
                ls.autoGenerate = false;
                ls.bakedGI = false;
                ls.realtimeGI = false;
                EditorUtility.SetDirty(ls);
            }
            bool stillThere = !string.IsNullOrEmpty(oldAssetPath)
                              && File.Exists(Path.Combine(Directory.GetCurrentDirectory(), oldAssetPath));
            rep.AppendLine($"⑤ 烘焙：ClearLightingDataAsset + Clear 已执行" +
                           (string.IsNullOrEmpty(oldAssetPath) ? "（原本未绑 LightingData）" : $"；清前绑定 {oldAssetPath}" +
                            (stillThere ? "（该资产被多个场景共用，只解绑、不删文件）" : "")) +
                           $"；autoGenerate/bakedGI/realtimeGI = false；原带 lightmap 索引的渲染器 {lightmapRefs} 个已解绑");

            // ---------- 写盘 ----------
            EditorSceneManager.MarkSceneDirty(scene);
            bool saved = EditorSceneManager.SaveScene(scene);
            rep.AppendLine(saved ? "⑥ 场景已存盘：" + ScenePath : "⑥ **存盘失败**，改动只在内存里");
            Debug.Log("[MapSkin·打光 v2] 完成\n" + rep);
        }

        // ---------------------------------------------------------------- 工具

        static Color Clamp01(Color c) => new Color(Mathf.Min(c.r, 1f), Mathf.Min(c.g, 1f), Mathf.Min(c.b, 1f), 1f);

        static Bounds CollectBounds(IEnumerable<GameObject> roots)
        {
            bool any = false; var b = new Bounds();
            foreach (var root in roots)
            {
                if (root == null) continue;
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || !r.enabled) continue;
                    if (r.GetComponent<Camera>() != null || r.GetComponent<ReflectionProbe>() != null) continue;
                    if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
                }
            }
            return b;
        }

        static void ParseArgs()
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i], v = i + 1 < args.Length ? args[i + 1] : null;
                switch (a)
                {
                    case "-ambientmode":
                        if (v != null) UseGradient = !v.ToLower().StartsWith("flat");
                        break;
                    case "-ambient": if (TryColor(v, out var c1)) FlatAmbient = c1; break;
                    case "-sky": if (TryColor(v, out var c2)) Sky = c2; break;
                    case "-equator": if (TryColor(v, out var c3)) Equator = c3; break;
                    case "-ground": if (TryColor(v, out var c4)) Ground = c4; break;
                    case "-key": if (TryFloat(v, out var k)) KeyIntensity = k; break;
                    case "-pointcd":
                        if (v != null)
                        {
                            var p = v.Split(',');
                            if (p.Length >= 1 && TryFloat(p[0], out var a1)) PointCd = a1;
                            if (p.Length >= 2 && TryFloat(p[1], out var a2)) PointWarmCd = a2;
                        }
                        break;
                    case "-reflintensity": if (TryFloat(v, out var ri)) ReflectionIntensity = ri; break;
                    case "-probeondemand": ProbeEveryFrame = false; break;
                }
            }
        }

        static bool TryFloat(string s, out float f) =>
            float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f);

        static bool TryColor(string s, out Color c)
        {
            c = Color.white;
            if (string.IsNullOrEmpty(s)) return false;
            var p = s.Split(',');
            if (p.Length < 3) return false;
            if (!TryFloat(p[0], out var r) || !TryFloat(p[1], out var g) || !TryFloat(p[2], out var b)) return false;
            c = new Color(r, g, b);
            return true;
        }
    }
}
