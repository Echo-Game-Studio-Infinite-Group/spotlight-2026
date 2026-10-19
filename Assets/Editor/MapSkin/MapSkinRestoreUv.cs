using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MapSkin
{
    /// <summary>
    /// 把 UV0 从 UV1/UV2 恢复 —— 给“要挂整面定制贴图（T_CUSTOM_*）”的对象用。
    ///
    /// 背景：MapSkinUvBake 把世界三平面 UV 写进 UV0，原 UV 备份在 UV1（mesh.uv2）或 UV2（mesh.uv3）；
    /// MapSkinFaceUv 又把“每面 0–1”写进 UV0，原 UV 备份进空闲的 UV1/UV2。
    /// 整面定制贴图要求 UV0 是对象原始立面的 0–1，所以挂载前要对这些对象跑一遍本工具。
    ///
    /// 与 MapSkinFaceUv.Clear 的分工：
    ///   · Clear  —— 把 MeshFilter 指回 **原 mesh 资产**，并删除 FACEUV_* 副本（整批回退）；
    ///   · 本工具 —— 不换 mesh，只把**当前 mesh** 的 UV0 换成备份值（适合只需改 UV、保留倒角/装饰几何的对象）。
    ///
    /// 备份通道判定：
    ///   1) mesh 名以 FACEUV_B1_ / FACEUV_B2_ / FACEUV_B0_ 开头 → 标记写死，直接采用；
    ///   2) 其他 mesh（典型是 MapSkinUvBake 的 UV_* 副本）：
    ///      · 只有 UV1 或只有 UV2 → 用有的那条；
    ///      · 两条都有 → 选“更接近 0–1”的那条（原 UV 通常是图元 0–1；世界三平面 UV 会大幅越界）；
    ///      · 可用 -channel 1|2 强制指定。
    ///
    /// 安全边界：
    ///   · 只改 Assets 下的 .asset mesh（导入的 FBX 子资产不可就地持久化）；
    ///   · 同一个 mesh 被多个 MeshFilter 引用时不改（避免一改全变），`-force 1` 可强制；
    ///   · 已是恢复状态（UV0 与备份逐点相同）→ 跳过（幂等）。
    ///
    /// 菜单：MapSkin/⑧ 恢复 UV0(从 UV1/UV2)·干跑(只报数)
    ///       MapSkin/⑧ 恢复 UV0(从 UV1/UV2)·执行(写盘)
    /// 批处理：-executeMethod MapSkin.MapSkinRestoreUv.DryRun / Run
    /// 参数：-scene <场景路径>  -only <对象名前缀>  -channel auto|1|2  -force 1
    /// </summary>
    public static class MapSkinRestoreUv
    {
        const string DefaultScene = "Assets/Scenes/Level1.unity";
        const string FaceUvPrefix = "FACEUV_";

        static string ScenePath = DefaultScene;
        static string OnlyPrefix = null;
        static int ChannelOverride = 0;   // 0=auto, 1=UV1, 2=UV2
        static bool Force = false;

        class Item
        {
            public MeshFilter Mf;
            public Mesh Mesh;
            public string Name;
            public int Channel;          // 1/2
            public Vector2[] Backup;
            public string Skip;
            public bool Already;         // UV0 已等于备份
        }

        class Stats
        {
            public int Scanned;
            public int Candidates;
            public int Restored;
            public int Already;
            public int Channel1, Channel2;
            public readonly Dictionary<string, int> SkipHist = new Dictionary<string, int>();
            public readonly Dictionary<string, List<string>> Examples = new Dictionary<string, List<string>>();

            public void Skip(string reason, string name)
            {
                SkipHist.TryGetValue(reason, out int c);
                SkipHist[reason] = c + 1;
                if (!Examples.TryGetValue(reason, out var l))
                {
                    l = new List<string>();
                    Examples[reason] = l;
                }
                if (l.Count < 3) l.Add(name);
            }
        }

        // ---------------------------------------------------------------- 菜单/入口

        [MenuItem("MapSkin/⑧ 恢复 UV0(从 UV1/UV2)·干跑(只报数)")]
        public static void DryRunMenu() => DryRun();

        [MenuItem("MapSkin/⑧ 恢复 UV0(从 UV1/UV2)·执行(写盘)")]
        public static void RunMenu()
        {
            ParseArgs();
            var plan = Plan(out _);
            int n = plan.Count(i => i.Skip == null && !i.Already);
            if (!EditorUtility.DisplayDialog("恢复 UV0",
                    $"将把 {n} 个 mesh 的 UV0 从 UV1/UV2 恢复（就地改 .asset，不换 mesh）\n\n继续？",
                    "执行", "取消"))
            {
                Debug.Log("[RestoreUV] 用户取消");
                return;
            }
            Run();
        }

        /// <summary>干跑：只统计能恢复多少件、跳过原因。 -executeMethod MapSkin.MapSkinRestoreUv.DryRun</summary>
        public static void DryRun()
        {
            ParseArgs();
            var plan = Plan(out var stats);
            Report(plan, stats, executed: false);
        }

        /// <summary>执行：把 UV0 设为 UV1/UV2 的备份。 -executeMethod MapSkin.MapSkinRestoreUv.Run</summary>
        public static void Run()
        {
            ParseArgs();
            var plan = Plan(out var stats);
            var rep = new StringBuilder();

            foreach (var it in plan)
            {
                if (it.Skip != null || it.Already) continue;
                try
                {
                    var list = new List<Vector2>(it.Backup);
                    it.Mesh.SetUVs(0, list);
                    EditorUtility.SetDirty(it.Mesh);
                    stats.Restored++;
                }
                catch (Exception e)
                {
                    stats.Skip("写 UV 异常: " + e.GetType().Name, it.Name);
                    rep.AppendLine($"    [异常] {it.Name}: {e.Message}");
                }
            }

            AssetDatabase.SaveAssets();
            var scene = EditorSceneManager.GetActiveScene();
            if (scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(scene);
                if (scene.path == ScenePath) EditorSceneManager.SaveScene(scene);
            }
            Debug.Log(rep + Report(plan, stats, executed: true));
        }

        // ---------------------------------------------------------------- 计划

        static void EnsureScene()
        {
            var cur = EditorSceneManager.GetActiveScene();
            if (cur.IsValid() && cur.path == ScenePath) return;
            if (cur.IsValid() && cur.isDirty && !string.IsNullOrEmpty(cur.path))
            {
                Debug.LogWarning($"[RestoreUV] 当前场景 {cur.path} 有未保存改动，不替你切换场景；请在目标场景打开时跑本工具");
                return;
            }
            if (!File.Exists(Path.Combine(Directory.GetCurrentDirectory(), ScenePath)))
            {
                Debug.LogError($"[RestoreUV] 场景不存在：{ScenePath}");
                return;
            }
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Debug.Log($"[RestoreUV] 已打开场景 {ScenePath}");
        }

        static List<Item> Plan(out Stats stats)
        {
            EnsureScene();
            stats = new Stats();
            var filters = UnityEngine.Object.FindObjectsOfType<MeshFilter>(true);

            // 共享 mesh 使用计数：同一份 .asset 被多个对象引用时默认不改
            var usage = new Dictionary<int, int>();
            foreach (var mf in filters)
            {
                if (mf == null || mf.sharedMesh == null) continue;
                int id = mf.sharedMesh.GetInstanceID();
                usage.TryGetValue(id, out int c);
                usage[id] = c + 1;
            }

            var list = new List<Item>();
            foreach (var mf in filters)
            {
                if (mf == null) continue;
                stats.Scanned++;
                var mesh = mf.sharedMesh;
                var it = new Item { Mf = mf, Mesh = mesh, Name = mf.gameObject.name };
                list.Add(it);

                if (mesh == null) { it.Skip = "sharedMesh 为空"; continue; }
                if (OnlyPrefix != null && !it.Name.StartsWith(OnlyPrefix, StringComparison.Ordinal))
                { it.Skip = $"-only 前缀不匹配（{OnlyPrefix}）"; continue; }

                int ch = ChannelOverride != 0 ? ChannelOverride : DetectChannel(mesh);
                if (ch == 0)
                {
                    it.Skip = "无 UV1/UV2 备份（或 FACEUV_B0_ 本来就没有原 UV）";
                    continue;
                }
                var backup = ch == 1 ? mesh.uv2 : mesh.uv3;
                if (backup == null || backup.Length != mesh.vertexCount || backup.Length == 0)
                {
                    it.Skip = $"UV{ch} 长度与顶点数不一致";
                    continue;
                }

                var path = AssetDatabase.GetAssetPath(mesh);
                if (string.IsNullOrEmpty(path))
                {
                    it.Skip = "无资产路径（内置/运行时 mesh，不可就地持久化）";
                    continue;
                }
                if (!path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                {
                    it.Skip = "导入模型子资产（只读，不能就地改 .asset）";
                    continue;
                }
                usage.TryGetValue(mesh.GetInstanceID(), out int used);
                if (used > 1 && !Force)
                {
                    it.Skip = $"共享 mesh（{used} 个对象引用；-force 1 可强制）";
                    continue;
                }

                it.Channel = ch;
                it.Backup = backup;
                it.Already = SameUv(mesh.uv, backup);
                stats.Candidates++;
                if (it.Already) stats.Already++;
                else if (ch == 1) stats.Channel1++;
                else stats.Channel2++;
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return list;
        }

        /// <summary>自动判备份通道：优先记号；否则“更接近 0–1”的那条（世界三平面 UV 会大幅越界）。</summary>
        static int DetectChannel(Mesh m)
        {
            string n = m.name ?? "";
            if (n.StartsWith(FaceUvPrefix + "B1_")) return 1;
            if (n.StartsWith(FaceUvPrefix + "B2_")) return 2;
            if (n.StartsWith(FaceUvPrefix + "B0_")) return 0;

            bool h1 = m.uv2 != null && m.uv2.Length == m.vertexCount && m.uv2.Length > 0;
            bool h2 = m.uv3 != null && m.uv3.Length == m.vertexCount && m.uv3.Length > 0;
            if (!h1 && !h2) return 0;
            if (h1 && !h2) return 1;
            if (!h1 && h2) return 2;
            return InRange(m.uv2) >= InRange(m.uv3) ? 1 : 2;
        }

        /// <summary>UV 落在 [0,1] 的比例（原 UV 通常几乎全在范围内；世界三平面 UV 大量越界）。</summary>
        static float InRange(Vector2[] uv)
        {
            if (uv == null || uv.Length == 0) return 0f;
            int ok = 0;
            for (int i = 0; i < uv.Length; i++)
            {
                float u = uv[i].x, v = uv[i].y;
                if (u >= -1e-4f && u <= 1f + 1e-4f && v >= -1e-4f && v <= 1f + 1e-4f) ok++;
            }
            return (float)ok / uv.Length;
        }

        static bool SameUv(Vector2[] a, Vector2[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                var d = a[i] - b[i];
                if (Mathf.Abs(d.x) > 1e-5f || Mathf.Abs(d.y) > 1e-5f) return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- 报告

        static string Report(List<Item> plan, Stats s, bool executed)
        {
            var rep = new StringBuilder();
            int skipped = plan.Count(i => i.Skip != null);
            rep.AppendLine($"[RestoreUV·{(executed ? "执行" : "干跑")}] 扫描 MeshFilter {s.Scanned} 个；" +
                           $"有备份 {s.Candidates} 个；{(executed ? "已恢复" : "待恢复")} " +
                           $"{(executed ? s.Restored : s.Channel1 + s.Channel2)} 个；" +
                           $"已是恢复状态 {s.Already} 个；跳过 {skipped} 个");
            rep.AppendLine($"  备份通道：UV1 {s.Channel1} 个 / UV2 {s.Channel2} 个" +
                           (executed ? $"（实际写入 {s.Restored}）" : ""));
            if (s.SkipHist.Count > 0)
            {
                rep.AppendLine("  跳过原因直方图：");
                foreach (var kv in s.SkipHist.OrderByDescending(k => k.Value))
                {
                    string ex = s.Examples.TryGetValue(kv.Key, out var l) ? string.Join(", ", l) : "";
                    rep.AppendLine($"    [{kv.Value,5} 个] {kv.Key}    例: {ex}");
                }
            }
            return rep.ToString();
        }

        // ---------------------------------------------------------------- 参数

        static void ParseArgs()
        {
            ScenePath = DefaultScene;
            OnlyPrefix = null;
            ChannelOverride = 0;
            Force = false;

            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Next() => i + 1 < args.Length ? args[i + 1] : null;
                if (a == "-scene") { var v = Next(); if (!string.IsNullOrEmpty(v)) { ScenePath = v; i++; } }
                else if (a == "-only" || a == "-restoreuvonly")
                { var v = Next(); if (!string.IsNullOrEmpty(v)) { OnlyPrefix = v; i++; } }
                else if (a == "-channel" || a == "-restoreuvchannel")
                {
                    var v = Next();
                    if (v != null)
                    {
                        if (v == "1") ChannelOverride = 1;
                        else if (v == "2") ChannelOverride = 2;
                        else ChannelOverride = 0;
                        i++;
                    }
                }
                else if (a == "-force" || a == "-restoreuvforce")
                {
                    var v = Next();
                    Force = v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
                    if (v != null) i++;
                }
            }
        }
    }
}
