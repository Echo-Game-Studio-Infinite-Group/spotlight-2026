// QuickWallSample.cs — 单面墙样张：把 composer 生成的整面墙贴图直接套到一个 renderer
// 目的：不拆 mesh、不等批量，先看一面墙在引擎里的真实观感。
// 菜单：MapSkin/Sample/① 一面墙样张
// 批处理：-executeMethod MapSkin.QuickWallSample.Run
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MapSkin
{
    public static class QuickWallSample
    {
        const string ScenePath = "Assets/Scenes/Level1.unity";
        const string OutScene = "Assets/Scenes/Level1_Sample.unity";
        const string Src = "F:/VSCode/工业级渲染/exports/wall_composer";
        const string TexDir = "Assets/Art/Images/WallSample";
        const string MatDir = "Assets/Art/Materials/MapSkin/WallSample";

        [MenuItem("MapSkin/Sample/① 一面墙样张")]
        public static void Run()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var target = GameObject.Find("S_01_Arch_001");
            if (target == null) { Debug.LogError("[Sample] 找不到 S_01_Arch_001"); return; }
            string id = "BOX_S_01_S_01_Arch_001_f01";
            Ensure(TexDir); Ensure(MatDir);
            var bc = Tex(System.IO.Path.Combine(Src, id, id + "_BC.png"), "BC");
            var n = Tex(System.IO.Path.Combine(Src, id, id + "_N.png"), "N");
            var ms = Tex(System.IO.Path.Combine(Src, id, id + "_MS.png"), "MS");
            var ao = Tex(System.IO.Path.Combine(Src, id, id + "_AO.png"), "AO");
            var e = Tex(System.IO.Path.Combine(Src, id, id + "_E.png"), "E");
            string mp = $"{MatDir}/{id}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(mp);
            if (mat == null) { mat = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(mat, mp); }
            mat.SetTexture("_BaseMap", bc);
            if (n) { mat.SetTexture("_BumpMap", n); mat.EnableKeyword("_NORMALMAP"); }
            if (ms) { mat.SetTexture("_MetallicGlossMap", ms); mat.EnableKeyword("_METALLICSPECGLOSSMAP"); }
            if (ao) { mat.SetTexture("_OcclusionMap", ao); mat.EnableKeyword("_OCCLUSIONMAP"); }
            if (e) { mat.SetTexture("_EmissionMap", e); mat.EnableKeyword("_EMISSION"); mat.SetColor("_EmissionColor", Color.white); }
            mat.SetColor("_BaseColor", Color.white);
            mat.SetFloat("_Metallic", 1f);
            mat.SetFloat("_Smoothness", 1f);
            mat.SetFloat("_BumpScale", 1f);
            mat.SetVector("_BaseMap_ST", Vector4.one);
            mat.SetVector("_BumpMap_ST", Vector4.one);
            mat.SetVector("_MetallicGlossMap_ST", Vector4.one);
            mat.SetVector("_OcclusionMap_ST", Vector4.one);
            EditorUtility.SetDirty(mat);

            int nR = 0;
            foreach (var r in target.GetComponentsInChildren<Renderer>(true))
            {
                var arr = new Material[r.sharedMaterials.Length];
                for (int i = 0; i < arr.Length; i++) arr[i] = mat;
                r.sharedMaterials = arr; nR++;
            }
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, OutScene);
            Debug.Log($"[Sample] {target.name} renderers={nR} -> {OutScene}");
        }

        static Texture2D Tex(string src, string ch)
        {
            if (!File.Exists(src)) { Debug.LogWarning("[Sample] 缺 " + src); return null; }
            Ensure(TexDir);
            string rel = $"{TexDir}/{Path.GetFileName(src)}";
            string full = Path.Combine(Directory.GetCurrentDirectory(), rel);
            if (!File.Exists(full)) { File.Copy(src, full, true); AssetDatabase.ImportAsset(rel, ImportAssetOptions.ForceUpdate); }
            var imp = AssetImporter.GetAtPath(rel) as TextureImporter;
            if (imp != null)
            {
                imp.textureType = ch == "N" ? TextureImporterType.NormalMap : TextureImporterType.Default;
                imp.sRGBTexture = ch == "BC" || ch == "E";
                imp.wrapMode = TextureWrapMode.Clamp;
                imp.maxTextureSize = 2048;
                imp.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(rel);
        }

        static void Ensure(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            int i = folder.LastIndexOf('/');
            string parent = i > 0 ? folder.Substring(0, i) : "Assets";
            string name = i >= 0 ? folder.Substring(i + 1) : folder;
            Ensure(parent); AssetDatabase.CreateFolder(parent, name);
        }
    }
}
