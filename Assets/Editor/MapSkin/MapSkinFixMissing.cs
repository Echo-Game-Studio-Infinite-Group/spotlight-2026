using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MapSkin
{
    /// <summary>
    /// 修复撤回后遗留的"缺材质"引用（派生材质被删、原引用没还原 → 出图里是洋红块）。
    /// 只做临时归位：按对象角色分到语义基底材质，真正的统一色板由 MapSkinPalette 接管。
    ///
    /// 批处理：-executeMethod MapSkin.MapSkinFixMissing.Run
    /// </summary>
    public static class MapSkinFixMissing
    {
        const string ScenePath = "Assets/Scenes/Level1.unity";

        public static void Run()
        {
            var cur = EditorSceneManager.GetActiveScene();
            if (!cur.IsValid() || cur.path != ScenePath)
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            var white = AssetDatabase.LoadAssetAtPath<Material>("Assets/Art/Models/MikuGandam/materials/Material.014.mat");
            var black = AssetDatabase.LoadAssetAtPath<Material>("Assets/Art/Models/MikuGandam/materials/Material.005.mat");
            var cyan = AssetDatabase.LoadAssetAtPath<Material>("Assets/Art/Models/MikuGandam/materials/Material.015.mat");
            if (white == null) { Debug.LogError("[缺材质] 基底材质加载失败"); return; }

            int fixedN = 0;
            var byName = new StringBuilder();
            foreach (var mf in Object.FindObjectsOfType<MeshFilter>())
            {
                if (mf == null || mf.sharedMesh == null) continue;
                var r = mf.GetComponent<Renderer>();
                if (r == null || !r.enabled) continue;
                var mats = r.sharedMaterials;
                bool miss = mats == null || mats.Length == 0;
                if (!miss) foreach (var m in mats) if (m == null) { miss = true; break; }
                if (!miss) continue;

                string n = mf.gameObject.name;
                Material pick = white;
                if (n.Contains("Arch") || n.Contains("Hazard") || n.Contains("Climb")) pick = black;
                else if (n.Contains("Floor") || n.Contains("Deck") || n.Contains("Tray")) pick = cyan;
                r.sharedMaterials = new[] { pick };
                fixedN++;
                if (byName.Length < 400) byName.Append(n).Append(", ");
            }

            if (fixedN > 0)
            {
                EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
                EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            }
            Debug.Log($"[缺材质] 修复 {fixedN} 件 → 白/黑/青基底。清单：{byName}");
        }
    }
}
