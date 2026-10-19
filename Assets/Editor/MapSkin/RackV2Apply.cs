// RackV2Apply.cs — 把 exports/rack_v2 的 32 套机架贴图接到 S_03 分段墙
// 只换材质，不动几何/碰撞；每段一份真材质资产（不用 MPB）。
// 菜单：MapSkin/S_03 机架墙/④ 应用 v2 贴图
// 批处理：-executeMethod MapSkin.RackV2Apply.Run
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MapSkin
{
    public static class RackV2Apply
    {
        const string ScenePath = "Assets/Scenes/Level1.unity";
        const string SrcRoot = "F:/VSCode/工业级渲染/exports/rack_v2";
        const string TexRoot = "Assets/Art/Images/RackV2";
        const string MatRoot = "Assets/Art/Materials/MapSkin/RackV2";
        const string Template = "Assets/Art/Materials/MapSkin/Dec/S_04/F_A_0.mat";

        [MenuItem("MapSkin/S_03 机架墙/④ 应用 v2 贴图")]
        public static void Run()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var segs = new List<Transform>();
            foreach (var t in Object.FindObjectsOfType<Transform>())
                if (t.name.StartsWith("S03_RackWall_Seg_")) segs.Add(t);
            if (segs.Count == 0) { Debug.LogError("[RackV2] 找不到 S03_RackWall_Seg_*"); return; }

            var dirs = new List<string>(Directory.GetDirectories(SrcRoot));
            dirs.RemoveAll(d => Path.GetFileName(d).Contains("prof") || Path.GetFileName(d).Contains("test"));
            dirs.Sort();
            if (!AssetDatabase.IsValidFolder(TexRoot))
            {
                if (!AssetDatabase.IsValidFolder("Assets/Art/Images")) AssetDatabase.CreateFolder("Assets/Art", "Images");
                AssetDatabase.CreateFolder("Assets/Art/Images", "RackV2");
            }
            if (!AssetDatabase.IsValidFolder(MatRoot))
                AssetDatabase.CreateFolder("Assets/Art/Materials/MapSkin", "RackV2");

            int done = 0;
            foreach (var t in segs)
            {
                // 名称形如 S03_RackWall_Seg_Beam_009_01
                string n = t.name;
                int wallIdx = 0;
                var m = System.Text.RegularExpressions.Regex.Match(n, @"Beam_(\d+)_(\d+)");
                if (m.Success) wallIdx = int.Parse(m.Groups[1].Value);
                int segIdx = m.Success ? int.Parse(m.Groups[2].Value) : 0;
                // 选择对应墙的段；没有就循环
                string pick = null;
                foreach (var d in dirs)
                    if (Path.GetFileName(d).Contains($"Beam_{wallIdx:000}_seg{segIdx % 4:00}")) { pick = d; break; }
                if (pick == null) pick = dirs[(done) % dirs.Count];
                string set = Path.GetFileName(pick);

                string texDir = $"{TexRoot}/{set}";
                EnsureFolder(texDir);
                var bc = Tex(pick, set, "BC", texDir);
                var nm = Tex(pick, set, "N", texDir);
                var ms = Tex(pick, set, "MS", texDir);
                var ao = Tex(pick, set, "AO", texDir);
                var e = Tex(pick, set, "E", texDir);
                if (bc == null) { Debug.LogWarning($"[RackV2] {set} 缺 BC"); continue; }

                string mp = $"{MatRoot}/{set}.mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(mp);
                if (mat == null)
                {
                    var tmpl = AssetDatabase.LoadAssetAtPath<Material>(Template);
                    mat = tmpl != null ? new Material(tmpl) : new Material(Shader.Find("Universal Render Pipeline/Lit"));
                    AssetDatabase.CreateAsset(mat, mp);
                }
                mat.SetTexture("_BaseMap", bc);
                if (nm) { mat.SetTexture("_BumpMap", nm); mat.EnableKeyword("_NORMALMAP"); }
                if (ms) { mat.SetTexture("_MetallicGlossMap", ms); mat.EnableKeyword("_METALLICSPECGLOSSMAP"); }
                if (ao) { mat.SetTexture("_OcclusionMap", ao); mat.EnableKeyword("_OCCLUSIONMAP"); }
                if (e) { mat.SetTexture("_EmissionMap", e); mat.EnableKeyword("_EMISSION"); mat.SetColor("_EmissionColor", Color.white); }
                mat.SetColor("_BaseColor", Color.white);
                mat.SetFloat("_Metallic", 1f);
                mat.SetFloat("_Smoothness", 1f);
                mat.SetVector("_BaseMap_ST", Vector4.one);
                mat.SetVector("_BumpMap_ST", Vector4.one);
                mat.SetVector("_MetallicGlossMap_ST", Vector4.one);
                mat.SetVector("_OcclusionMap_ST", Vector4.one);
                EditorUtility.SetDirty(mat);
                var r = t.GetComponent<Renderer>();
                if (r != null) { r.sharedMaterial = mat; done++; }
            }
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[RackV2] 应用 {done}/{segs.Count} 段");
        }

        static Texture2D Tex(string dir, string set, string ch, string texDir)
        {
            string src = Directory.GetFiles(dir, "*_" + ch + ".png").FirstOrDefault();
            if (src == null) return null;
            string rel = $"{texDir}/{set}_{ch}.png";
            string full = Path.Combine(Directory.GetCurrentDirectory(), rel);
            if (!File.Exists(full)) { File.Copy(src, full, true); AssetDatabase.ImportAsset(rel, ImportAssetOptions.ForceUpdate); }
            var ti = AssetImporter.GetAtPath(rel) as TextureImporter;
            if (ti != null && ti.textureType != (ch == "N" ? TextureImporterType.NormalMap : TextureImporterType.Default))
            {
                ti.textureType = ch == "N" ? TextureImporterType.NormalMap : TextureImporterType.Default;
                ti.sRGBTexture = ch == "BC" || ch == "E";
                ti.wrapMode = TextureWrapMode.Clamp;
                ti.maxTextureSize = 4096;
                ti.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(rel);
        }

        static void EnsureFolder(string f)
        {
            if (AssetDatabase.IsValidFolder(f)) return;
            int i = f.LastIndexOf('/');
            EnsureFolder(i > 0 ? f.Substring(0, i) : "Assets");
            AssetDatabase.CreateFolder(i > 0 ? f.Substring(0, i) : "Assets", f.Substring(i + 1));
        }
    }
}
