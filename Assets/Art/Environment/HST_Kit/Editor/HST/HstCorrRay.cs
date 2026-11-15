#if UNITY_EDITOR
// 门墙射线探针 (2026-10-11, 只读) —— 用户反馈"门所在的那面墙材质有点问题"取证用。
// 原理: 走廊物件 GO 缩放 100 + 本地旋转 -90°绕X, 位置一律以运行时包围盒为准;
//       渲染是否看得见由**面法线朝向**决定(背面剔除)。射线取 RaycastHit.normal:
//       · normal.x > 0 → 面朝 +x = 朝走廊内侧 = 正对观察者(可见)
//       · normal.x < 0 → 面朝 -x = 背对观察者 → 被剔除 → 看穿到黑外壳(症状: 门区一圈发黑)
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace HST
{
    public static class HstCorrRay
    {
        const string Out = "F:/VSCode/工业级渲染/tmp/doorwall_ray.txt";

        [MenuItem("HST/门墙射线探针(只读)")]
        public static void Probe()
        {
            EditorSceneManager.OpenScene("Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            Physics.SyncTransforms();
            var sb = new StringBuilder();
            sb.AppendLine("# 门墙射线探针 " + System.DateTime.Now.ToString("HH:mm:ss"));
            sb.AppendLine("# 沿 -x 从 x=-10 打向门墙, 报 [命中物体 / 命中面法线x分量(>0 朝走廊=可见, <0 背对=被剔除) / 距离]");
            sb.AppendLine();

            var nameMat = new Dictionary<string, string>();
            var mats = new List<string>();

            foreach (float y in new[] { 0.4f, 1.0f, 1.5f, 2.0f, 2.5f, 2.9f, 3.3f, 3.9f, 4.6f, 5.1f })
            {
                sb.Append("y=" + y.ToString("0.0") + "\n");
                for (float z = -4.5f; z <= 4.51f; z += 0.5f)
                {
                    RaycastHit h;
                    sb.Append("  z=" + z.ToString("+0.0;-0.0;0.0").PadLeft(5) + " : ");
                    if (Physics.Raycast(new Vector3(-10f, y, z), Vector3.left, out h, 20f))
                    {
                        var go = h.collider.gameObject;
                        var mr = go.GetComponent<MeshRenderer>();
                        string mat = mr != null && mr.sharedMaterial != null ? mr.sharedMaterial.name : "-";
                        if (!nameMat.ContainsKey(go.name)) { nameMat[go.name] = mat; mats.Add(go.name + " → " + mat); }
                        sb.Append(go.name + "  n.x=" + h.normal.x.ToString("+0.00;-0.00;0.00") +
                                  "  法线=(" + h.normal.x.ToString("+0.0;-0.0;0.0") + "," + h.normal.y.ToString("+0.0;-0.0;0.0") + "," + h.normal.z.ToString("+0.0;-0.0;0.0") + ")" +
                                  "  d=" + h.distance.ToString("0.00"));
                    }
                    else sb.Append("(无命中)");
                    sb.AppendLine();
                }
            }
            sb.AppendLine();
            sb.AppendLine("## 命中对象 → 材质");
            mats.Sort();
            foreach (var m in mats) sb.AppendLine("  " + m);

            File.WriteAllText(Out, sb.ToString(), Encoding.UTF8);
            Debug.Log("[HST-RAY] 落盘 " + Out + " (" + mats.Count + " 种对象)");
        }
    }
}
#endif
