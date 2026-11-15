#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
namespace HST {
  public static class Uv2Check {
    [MenuItem("HST/检查 UV2(光贴图 UV)")]
    public static void Run() {
      SceneBuilder.EnableLightmapUVs();   // 先按新策略生成 UV2
      EditorSceneManager.OpenScene("Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
      int noUv2 = 0, tot = 0;
      foreach (var mf in Object.FindObjectsOfType<MeshFilter>(true)) {
        var m = mf.sharedMesh; if (m == null) continue; tot++;
        bool has = m.uv2 != null && m.uv2.Length == m.vertexCount && m.vertexCount > 0;
        if (!has) { noUv2++; if (noUv2 <= 25) Debug.Log($"[UV2] 缺 UV2: {mf.name} verts={m.vertexCount} uv={m.uv?.Length ?? -1} uv2={(m.uv2 == null ? -1 : m.uv2.Length)}"); }
      }
      Debug.Log($"[UV2] 共 {tot} 个网格, 缺 UV2 的 {noUv2} 个");
    }
  }
}
#endif
