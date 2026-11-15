#if UNITY_EDITOR
// 网格密度普查(只读) —— 判定 Unity 侧收到的 FBX 到底有没有"倒角几何":
// 源头 blend 里每个箱体基础是 8 顶点/12 三角; 倒角(2 段)应用后会涨到数十顶点。
// 所以"顶点数≈24" = 没倒角的裸盒, "顶点数 100+" = 倒角在。
// 顺带按名字前缀聚合, 一次看出哪些件是裸盒。
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace HST
{
    public static class HstMeshStats
    {
        [MenuItem("HST/网格密度普查(只读)")]
        public static void Dump()
        {
            var path = "F:/VSCode/工业级渲染/tmp/mesh_stats.txt";
            EditorSceneManager.OpenScene("Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            var sb = new System.Text.StringBuilder();
            var seen = new HashSet<int>();
            var byPrefix = new Dictionary<string, (int n, int vmin, int vmax, int tris)>();
            int totalVerts = 0, totalTris = 0;
            foreach (var mr in Object.FindObjectsOfType<MeshRenderer>(true))
            {
                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                var me = mf.sharedMesh;
                if (!seen.Add(me.GetInstanceID())) continue;   // 同 mesh 只统计一次
                int v = me.vertexCount, t = me.triangles.Length / 3;
                totalVerts += v; totalTris += t;
                string pre = mr.name.Split('_')[0];
                if (!byPrefix.TryGetValue(pre, out var agg)) agg = (0, int.MaxValue, 0, 0);
                byPrefix[pre] = (agg.n + 1, Mathf.Min(agg.vmin, v), Mathf.Max(agg.vmax, v), agg.tris + t);
                if (v < 40) sb.AppendLine($"裸盒候选: {mr.name} verts={v} tris={t}");
            }
            sb.AppendLine($"\n# 合计 mesh={seen.Count} verts={totalVerts} tris={totalTris}");
            sb.AppendLine("\n# 按前缀: 数量 / 顶点 min-max / 三角合计");
            foreach (var kv in byPrefix.OrderBy(k => k.Key))
                sb.AppendLine($"{kv.Key,-22} n={kv.Value.n,-4} verts={kv.Value.vmin}..{kv.Value.vmax} tris={kv.Value.tris}");
            File.WriteAllText(path, sb.ToString());
            Debug.Log("[HST-MESH] 网格普查 → " + path + " | 合计 mesh=" + seen.Count + " tris=" + totalTris);
        }
    }
}
#endif
