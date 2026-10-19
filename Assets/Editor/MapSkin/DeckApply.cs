using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MapSkin
{
    /// <summary>
    /// S_04 浮空地面：用并集甲板替换 11 块原板的**视觉**，原对象只关 Renderer、Collider 全保留。
    ///
    /// 网格来自 Blender：scripts/deck/gen_floor_union.py（俯视并集 + 挤出 + 每块原板独立 UV 岛）。
    /// 材质槽顺序与脚本一致：0..10 = 11 块原板顶面；11 白裙；12 蓝裙；13 底面。
    ///
    /// 批处理：-executeMethod MapSkin.DeckApply.Run
    ///         -executeMethod MapSkin.DeckApply.Restore
    /// </summary>
    public static class DeckApply
    {
        const string ScenePath = "Assets/Scenes/Level1.unity";
        const string FbxSrc = "F:/VSCode/工业级渲染/tmp/unity_export/deck/DECK_S04_FLOOR.fbx";
        const string TexSrcRoot = "F:/VSCode/工业级渲染/exports/deck";
        const string AssetDir = "Assets/Art/Environment/Deck";
        const string FbxDst = AssetDir + "/DECK_S04_FLOOR.fbx";
        const string TexDir = "Assets/Art/Images/Deck";
        const string MatDir = "Assets/Art/Materials/MapSkin/Deck";
        const string VisualName = "DECK_S04_VISUAL";
        const string ObjectsFile = "F:/VSCode/taptap-game-jem/_session_audit/selection.txt";

        // (slot, texture stem, variant) —— 与 gen_floor_union.py 的 SLABS 顺序一致
        static readonly (int slot, string tex, string variant)[] Slots = {
            (0,  "DECKTOP_blue_16x28", "blue"),
            (1,  "DECKTOP_white_16x16", "white"),
            (2,  "DECKTOP_white_16x28", "white"),
            (3,  "DECKTOP_white_11.92x37.27", "white"),
            (4,  "DECKTOP_blue_16x16", "blue"),
            (5,  "DECKTOP_white_16x28", "white"),
            (6,  "DECKTOP_white_16x28", "white"),
            (7,  "DECKTOP_blue_16x28", "blue"),
            (8,  "DECKTOP_white_11.92x37.27", "white"),
            (9,  "DECKTOP_blue_16x16", "blue"),
            (10, "DECKTOP_blue_16x28", "blue"),
        };

        public static void Run()
        {
            EnsureScene();
            Directory.CreateDirectory(AssetDir);
            if (!File.Exists(FbxSrc)) { Debug.LogError($"[甲板] 缺 FBX {FbxSrc}"); return; }
            File.Copy(FbxSrc, FbxDst, true);
            AssetDatabase.ImportAsset(FbxDst, ImportAssetOptions.ForceUpdate);

            var mesh = LoadMesh(FbxDst);
            if (mesh == null) { Debug.LogError("[甲板] FBX 里没找到 Mesh"); return; }
            Debug.Log($"[甲板] 网格 {mesh.name} subMeshes={mesh.subMeshCount} verts={mesh.vertexCount}");

            if (!AssetDatabase.IsValidFolder("Assets/Art/Images/Deck"))
                AssetDatabase.CreateFolder("Assets/Art/Images", "Deck");
            if (!AssetDatabase.IsValidFolder(MatDir))
                AssetDatabase.CreateFolder("Assets/Art/Materials/MapSkin", "Deck");

            // 先读 FBX 内嵌材质名（顺序 = submesh 顺序），再按名字映射到我们的定制材质
            var embedded = LoadEmbeddedMaterialNames(FbxDst);
            var fallback = AssetDatabase.LoadAssetAtPath<Material>("Assets/Art/Models/MikuGandam/materials/Material.014.mat");
            var mats = new List<Material>();
            foreach (var name in embedded)
            {
                Material m = null;
                var slot = SlotOfName(name);
                if (slot >= 0 && slot <= 10)
                {
                    var entry = Slots[slot];
                    m = EnsureMaterial(entry.tex);
                }
                else if (name.Contains("SKIRT_WHITE"))
                    m = AssetDatabase.LoadAssetAtPath<Material>("Assets/Art/Materials/MapSkin/Palette/MS_White_Base.mat");
                else if (name.Contains("SKIRT_BLUE"))
                    m = AssetDatabase.LoadAssetAtPath<Material>("Assets/Art/Materials/MapSkin/Palette/MS_Cyan_Base.mat");
                else if (name.Contains("BOTTOM"))
                    m = AssetDatabase.LoadAssetAtPath<Material>("Assets/Art/Models/MikuGandam/materials/Material.013.mat");
                mats.Add(m != null ? m : fallback);
            }
            Debug.Log($"[甲板] FBX 材质槽 {mats.Count}：{string.Join(", ", embedded)}");

            // 关掉原 11 块板的 Renderer（Collider 保留）
            int hidden = HideOriginalSlabs();

            // 生成/更新视觉甲板
            var go = GameObject.Find(VisualName);
            if (go == null) go = new GameObject(VisualName);
            var mf = go.GetComponent<MeshFilter>();
            if (mf == null) mf = go.AddComponent<MeshFilter>();
            var mr = go.GetComponent<MeshRenderer>();
            if (mr == null) mr = go.AddComponent<MeshRenderer>();
            mf.sharedMesh = mesh;
            mr.sharedMaterials = mats.ToArray();
            go.transform.position = Vector3.zero;
            go.transform.rotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            var scene = EditorSceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[甲板] 已替换视觉：隐藏原板 {hidden} 件，{VisualName} 使用 14 个材质槽；Collider 未动。");
        }

        public static void Restore()
        {
            EnsureScene();
            var go = GameObject.Find(VisualName);
            if (go != null) Object.DestroyImmediate(go);
            int n = 0;
            foreach (var t in Object.FindObjectsOfType<Transform>(true))
            {
                if (t == null) continue;
                if (!t.name.StartsWith("L1_Beam_") && !t.name.StartsWith("S_04_Beam_")) continue;
                var r = t.GetComponent<Renderer>();
                if (r != null && !r.enabled) { r.enabled = true; n++; }
            }
            var scene = EditorSceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[甲板] 已还原：删除视觉层，恢复 {n} 个 Renderer");
        }

        static int HideOriginalSlabs()
        {
            // 按位置匹配（名字有 "(1)" 后缀等不可靠）：解析 selection.txt 的 c=(x,y,z)
            var centers = new List<Vector3>();
            if (File.Exists(ObjectsFile))
                foreach (var line in File.ReadAllLines(ObjectsFile))
                {
                    if (line.StartsWith("#") || string.IsNullOrWhiteSpace(line)) continue;
                    var i = line.IndexOf("c=(", System.StringComparison.Ordinal);
                    if (i < 0) continue;
                    var j = line.IndexOf(')', i);
                    var nums = line.Substring(i + 3, j - i - 3).Split(',');
                    if (nums.Length == 3 &&
                        float.TryParse(nums[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) &&
                        float.TryParse(nums[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y) &&
                        float.TryParse(nums[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z))
                        centers.Add(new Vector3(x, y, z));
                }
            int n = 0;
            foreach (var r in Object.FindObjectsOfType<Renderer>())
            {
                if (r == null || !r.enabled) continue;
                if (!r.gameObject.name.StartsWith("L1_Beam_") && !r.gameObject.name.StartsWith("S_04_Beam_")) continue;
                var c = r.bounds.center;
                foreach (var t in centers)
                    if (Mathf.Abs(c.x - t.x) < 0.15f && Mathf.Abs(c.y - t.y) < 0.3f && Mathf.Abs(c.z - t.z) < 0.15f)
                    { r.enabled = false; n++; break; }
            }
            return n;
        }

        static List<string> LoadEmbeddedMaterialNames(string fbxPath)
        {
            var names = new List<string>();
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(fbxPath))
            {
                if (o is GameObject go)
                {
                    var mr = go.GetComponent<MeshRenderer>();
                    if (mr == null) mr = go.GetComponentInChildren<MeshRenderer>();
                    if (mr != null)
                    {
                        foreach (var m in mr.sharedMaterials)
                            names.Add(m != null ? m.name : "-");
                        return names;
                    }
                }
            }
            return names;
        }

        static int SlotOfName(string name)
        {
            // M_DECK_03_W 之类
            if (name == null || !name.StartsWith("M_DECK_")) return -1;
            var parts = name.Split('_');
            if (parts.Length < 3) return -1;
            return int.TryParse(parts[2], out var v) ? v : -1;
        }

        static Mesh LoadMesh(string path)
        {
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                if (o is Mesh m) return m;
            return null;
        }

        static Material EnsureMaterial(string texStem)
        {
            var matPath = $"{MatDir}/{texStem}.mat";
            if (AssetDatabase.LoadAssetAtPath<Material>(matPath) == null)
                AssetDatabase.CopyAsset("Assets/Art/Models/MikuGandam/materials/Material.014.mat", matPath);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null) return null;

            var srcDir = $"{TexSrcRoot}/{texStem}";
            if (!AssetDatabase.IsValidFolder(TexDir)) AssetDatabase.CreateFolder("Assets/Art/Images", "Deck");
            var dstDir = $"{TexDir}/{texStem}";
            if (!AssetDatabase.IsValidFolder(dstDir)) AssetDatabase.CreateFolder(TexDir, texStem);
            foreach (var suf in new[] { "BC", "N", "MS", "ORM", "H", "E" })
            {
                var src = $"{srcDir}/{texStem}_{suf}.png";
                if (!File.Exists(src)) continue;
                File.Copy(src, $"{dstDir}/{texStem}_{suf}.png", true);
            }
            AssetDatabase.ImportAsset(dstDir, ImportAssetOptions.ImportRecursive);
            SetImp($"{dstDir}/{texStem}_N.png", TextureImporterType.NormalMap, false);
            SetImp($"{dstDir}/{texStem}_MS.png", TextureImporterType.Default, true);
            SetImp($"{dstDir}/{texStem}_ORM.png", TextureImporterType.Default, true);
            SetImp($"{dstDir}/{texStem}_H.png", TextureImporterType.Default, true);

            mat.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{dstDir}/{texStem}_BC.png"));
            mat.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{dstDir}/{texStem}_N.png"));
            mat.SetFloat("_BumpScale", 1f);
            mat.SetTexture("_MetallicGlossMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{dstDir}/{texStem}_MS.png"));
            mat.SetTexture("_OcclusionMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{dstDir}/{texStem}_ORM.png"));
            mat.SetColor("_BaseColor", Color.white);
            mat.SetFloat("_Smoothness", 1f);
            mat.SetVector("_BaseMap_ST", new Vector4(1, 1, 0, 0));
            var em = AssetDatabase.LoadAssetAtPath<Texture2D>($"{dstDir}/{texStem}_E.png");
            if (em != null)
            {
                mat.SetFloat("_EmissionEnabled", 1f);
                mat.SetFloat("_EmissionUseMap", 1f);
                mat.SetTexture("_EmissionMap", em);
                mat.SetColor("_EmissionColor", Color.white);
                mat.SetFloat("_EmissionIntensity", 1.2f);
            }
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
            return mat;
        }

        static void SetImp(string path, TextureImporterType type, bool linear)
        {
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null) return;
            ti.textureType = type;
            ti.sRGBTexture = !linear;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.maxTextureSize = 4096;
            ti.SaveAndReimport();
        }

        static void EnsureScene()
        {
            var cur = EditorSceneManager.GetActiveScene();
            if (cur.IsValid() && cur.path == ScenePath) return;
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }
    }
}
