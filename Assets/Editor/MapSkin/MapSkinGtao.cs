using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace MapSkin
{
    /// <summary>
    /// GTAO 调档 / 关闭 / 恢复（派单 B.1 + B.5，可逆，写盘）。
    ///
    /// 为什么需要它：实测画面暗的主因是 GTAO —— 同机位同构图，S_01 iso 视图读数
    ///   现状 0.183 → 环境光拉满 0.193（几乎无变化）→ +主光 2.0 白 0.211 → **关 GTAO 0.356（死黑 15.5%→0.8%）**。
    ///
    /// 落地口径（**不碰 URP-Renderer 资产**，比"改渲染器单字段"的先例更保守）：
    ///   GTAO 的可调参数来自 Volume 组件 `GroundTruthAmbientOcclusion`（intensity / radius / distributionPower /
    ///   directLightingStrength / multiBounceStrength / ssdoIntensity…，默认 intensity=1.0, radius=1.0,
    ///   directLightingStrength=0.25, multiBounce=0.5）。Level1 的 Global Volume **原本没有挂 profile**，
    ///   所以 GTAO 一直跑在 VolumeComponent 的默认值上。
    ///   本工具给该 Global Volume 挂一个 **专属 profile 资产**（`Assets/Settings/MapSkin_GTAO.asset`）并写覆盖值；
    ///   恢复 = 删掉这个覆盖（菜单一键），URP-Renderer / TA 的 9 个特性资产一字未动。
    ///
    /// 先用 directLightingStrength=0 这条试：它的语义是"AO 影响直接光的比例"（0 = 物理更安全，只压间接项），
    /// 往往是"保留 GTAO 但画面起来"的性价比最高的一档；再往下压 intensity / radius。
    ///
    /// 菜单：MapSkin/⑧ GTAO 调档(可逆·写盘) / ⑧ GTAO 关闭(可逆·写盘) / ⑧ GTAO 恢复默认 / ⑧ GTAO 现状打印
    /// 批处理：-executeMethod MapSkin.MapSkinGtao.Run     （调档）
    ///         -executeMethod MapSkin.MapSkinGtao.Off     （关，等价 -gtaointensity 0 -gtaoenable false）
    ///         -executeMethod MapSkin.MapSkinGtao.Restore （恢复默认，去覆盖）
    ///         -executeMethod MapSkin.MapSkinGtao.Status  （只打印）
    ///   可选：-gtaointensity 0.35  -gtaoradius 0.8  -gtaodirect 0  -gtaomultibounce 0.5  -gtaossdo 0  -gtaoenable true|false
    /// </summary>
    public static class MapSkinGtao
    {
        const string ScenePath = "Assets/Scenes/Level1.unity";
        const string ProfilePath = "Assets/Settings/MapSkin_GTAO.asset";

        static bool EnableAO = true;
        static float Intensity = 0.35f;
        static float Radius = 0.8f;
        static float Direct = 0f;
        static float MultiBounce = 0.5f;
        static float Ssdo = 0.25f;
        static bool HasIntensity, HasRadius, HasDirect, HasMultiBounce, HasSsdo;

        [MenuItem("MapSkin/⑧ GTAO 调档(可逆·写盘)")]
        public static void RunMenu() => Run();

        [MenuItem("MapSkin/⑧ GTAO 关闭(可逆·写盘)")]
        public static void OffMenu() { Intensity = 0f; EnableAO = false; HasIntensity = true; Run(); }

        [MenuItem("MapSkin/⑧ GTAO 恢复默认(去掉覆盖)")]
        public static void RestoreMenu() => Restore();

        [MenuItem("MapSkin/⑧ GTAO 现状打印")]
        public static void StatusMenu() => Status();

        public static void Run() { ParseArgs(); Apply(); }

        public static void Off()
        {
            EnableAO = false; Intensity = 0f; HasIntensity = true;
            ParseArgs();
            Apply();
        }

        public static void Restore()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var vol = FindGlobalVolume();
            var rep = new StringBuilder();
            if (vol == null) { Debug.Log("[GTAO] 场景里没有全局 Volume，无需恢复"); return; }
            var prof = vol.sharedProfile;
            if (prof != null && prof.TryGet<GroundTruthAmbientOcclusion>(out var g))
            {
                prof.Remove<GroundTruthAmbientOcclusion>();
                EditorUtility.SetDirty(prof);
                rep.AppendLine("[GTAO] 已移除 Global Volume 上的 GroundTruthAmbientOcclusion 覆盖 → 回到 VolumeComponent 默认值");
            }
            else rep.AppendLine("[GTAO] 本就没有 GTAO 覆盖，无需恢复");

            // profile 清空后如果它只是本工具的壳，就摘掉（别的场景没引用它）
            if (prof != null && prof.components.Count == 0 && AssetDatabase.GetAssetPath(prof) == ProfilePath)
            {
                vol.sharedProfile = null;
                AssetDatabase.DeleteAsset(ProfilePath);
                rep.AppendLine("[GTAO] 摘掉空 profile 并删除 " + ProfilePath);
            }
            EditorSceneManager.MarkSceneDirty(scene);
            bool saved = EditorSceneManager.SaveScene(scene);
            rep.AppendLine(saved ? "[GTAO] 场景已存盘" : "[GTAO] **存盘失败**");
            Debug.Log(rep.ToString());
        }

        public static void Status()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var vol = FindGlobalVolume();
            var rep = new StringBuilder();
            rep.AppendLine($"[GTAO 现状] 全局 Volume={(vol ? vol.name : "无")}，profile={(vol && vol.sharedProfile ? vol.sharedProfile.name : "无")}");
            if (vol != null && vol.sharedProfile != null && vol.sharedProfile.TryGet<GroundTruthAmbientOcclusion>(out var g))
            {
                rep.AppendLine($"  覆盖值: enableAO={g.enableAO.value} intensity={g.intensity.value:F2} radius={g.radius.value:F2} " +
                               $"directLighting={g.directLightingStrength.value:F2} multiBounce={g.multiBounceStrength.value:F2} " +
                               $"ssdo={g.ssdoIntensity.value:F2} distributionPower={g.distributionPower.value:F2}");
            }
            else
            {
                rep.AppendLine("  无覆盖 → 跑 VolumeComponent 默认（intensity 1.0 / radius 1.0 / directLighting 0.25 / multiBounce 0.5 / ssdo 0.25）");
                rep.AppendLine("  实测这一档会让白模 iso 视图 mean 掉到 0.18 左右（死黑 ~15%），是当前画面偏暗的主因");
            }
            Debug.Log(rep.ToString());
        }

        // ---------------------------------------------------------------- 落地

        static void Apply()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var rep = new StringBuilder();
            var vol = FindGlobalVolume();
            if (vol == null)
            {
                var go = new GameObject("Global Volume");
                vol = go.AddComponent<Volume>();
                vol.isGlobal = true;
                rep.AppendLine("[GTAO] 场景没有全局 Volume → 新建「Global Volume」（isGlobal）");
            }

            var prof = vol.sharedProfile;
            if (prof == null)
            {
                prof = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
                if (prof == null)
                {
                    prof = ScriptableObject.CreateInstance<VolumeProfile>();
                    AssetDatabase.CreateAsset(prof, ProfilePath);
                    rep.AppendLine("[GTAO] 新建专属 profile 资产 " + ProfilePath);
                }
                vol.sharedProfile = prof;
                EditorUtility.SetDirty(vol);
            }
            else if (AssetDatabase.GetAssetPath(prof) != ProfilePath)
            {
                rep.AppendLine($"[GTAO] 注意：Global Volume 已挂别人的 profile「{prof.name}」（{AssetDatabase.GetAssetPath(prof)}），" +
                               "为免污染他场景，**直接在这份上写覆盖**（恢复菜单会移除本组件）");
            }

            var g = prof.TryGet<GroundTruthAmbientOcclusion>(out var exist) ? exist : prof.Add<GroundTruthAmbientOcclusion>(true);
            g.active = true;
            g.enableAO.overrideState = true; g.enableAO.value = EnableAO;
            g.intensity.overrideState = true; g.intensity.value = Mathf.Clamp(Intensity, 0f, 4f);
            if (HasRadius || true) { g.radius.overrideState = true; g.radius.value = Mathf.Clamp(Radius, 0.01f, 5f); }
            if (HasDirect || true) { g.directLightingStrength.overrideState = true; g.directLightingStrength.value = Mathf.Clamp01(Direct); }
            if (HasMultiBounce || true) { g.multiBounceStrength.overrideState = true; g.multiBounceStrength.value = Mathf.Clamp01(MultiBounce); }
            if (HasSsdo || true) { g.ssdoIntensity.overrideState = true; g.ssdoIntensity.value = Mathf.Max(0f, Ssdo); }
            EditorUtility.SetDirty(g);
            EditorUtility.SetDirty(prof);

            rep.AppendLine($"[GTAO] 写入覆盖（Volume 侧，URP-Renderer 资产未动）：enableAO={g.enableAO.value} " +
                           $"intensity={g.intensity.value:F2} radius={g.radius.value:F2} " +
                           $"directLightingStrength={g.directLightingStrength.value:F2} multiBounce={g.multiBounceStrength.value:F2} " +
                           $"ssdoIntensity={g.ssdoIntensity.value:F2}");
            rep.AppendLine("[GTAO] 登记：本档位可一键回退 —— 菜单 `MapSkin/⑧ GTAO 恢复默认(去掉覆盖)` 或 " +
                           "`-executeMethod MapSkin.MapSkinGtao.Restore`；TA 的 9 个特性开关与 URP-Renderer.asset 均未改动");
            if (!EnableAO || Intensity <= 0f)
                rep.AppendLine("[GTAO] 当前 = **关闭档**（enableAO=false 且 intensity=0）。按规范 §五的先例处理：改的是场景 Global Volume 的覆盖，" +
                               "不是渲染器资产，恢复菜单一键可逆；务必把本档位与读数登记进风险/现状文档");

            EditorSceneManager.MarkSceneDirty(scene);
            bool saved = EditorSceneManager.SaveScene(scene);
            rep.AppendLine(saved ? "[GTAO] 场景已存盘：" + ScenePath : "[GTAO] **存盘失败**，改动只在内存");
            Debug.Log(rep.ToString());
        }

        static Volume FindGlobalVolume()
        {
            var vols = Object.FindObjectsOfType<Volume>();
            var v = vols.FirstOrDefault(x => x != null && x.isGlobal && x.enabled);
            return v ?? vols.FirstOrDefault(x => x != null && x.enabled);
        }

        static void ParseArgs()
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i], v = i + 1 < args.Length ? args[i + 1] : null;
                switch (a)
                {
                    case "-gtaointensity": if (TryF(v, out var x1)) { Intensity = x1; HasIntensity = true; } break;
                    case "-gtaoradius": if (TryF(v, out var x2)) { Radius = x2; HasRadius = true; } break;
                    case "-gtaodirect": if (TryF(v, out var x3)) { Direct = x3; HasDirect = true; } break;
                    case "-gtaomultibounce": if (TryF(v, out var x4)) { MultiBounce = x4; HasMultiBounce = true; } break;
                    case "-gtaossdo": if (TryF(v, out var x5)) { Ssdo = x5; HasSsdo = true; } break;
                    case "-gtaoenable":
                        if (v != null) EnableAO = v.ToLower().StartsWith("true") || v == "1";
                        break;
                }
            }
        }

        static bool TryF(string s, out float f) =>
            float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f);
    }
}
