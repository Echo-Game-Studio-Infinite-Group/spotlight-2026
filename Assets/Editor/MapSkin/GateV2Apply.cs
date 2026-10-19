// GateV2Apply.cs — 把已认可的 GateKit v2 装到 14 个门位，替换 v1 占位（只加视觉，碰撞不动）
// 菜单：MapSkin/Gate/① 装配已认可门
// 批处理：-executeMethod MapSkin.GateV2Apply.Run
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MapSkin
{
    public static class GateV2Apply
    {
        const string ScenePath = "Assets/Scenes/Level1.unity";
        const string SrcRoot = "F:/VSCode/工业级渲染/exports/gatekit_v2";
        const string FbxDst = "Assets/Art/Environment/Gates/V2/GATE_KIT.fbx";
        const string TexRoot = "Assets/Art/Images/Gates/V2";
        const string MatRoot = "Assets/Art/Materials/MapSkin/Gates/V2";
        const string Layout = SrcRoot + "/GATE_LAYOUT.json";

        const float NumX0 = 0.01f, NumY0 = 0.74833f, NumX1 = 0.24f, NumY1 = 0.91833f; // cell 01
        const float LabX0 = 0.76f, LabY0 = 0.415f, LabX1 = 0.99f, LabY1 = 0.585f;      // cell IN

        [System.Serializable] class LayoutData { public List<Gate> gates; }
        [System.Serializable] class Gate {
            public string block; public string type; public float[] pos; public float yaw;
            public string numberCell; public string labelCell;
        }

        [MenuItem("MapSkin/Gate/② 打印门尺寸")]
        public static void Dump()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var go = GameObject.Find("DEC_Shared/GateV2");
            if (go == null) { Debug.LogError("[GateV2] 无 GateV2"); return; }
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r.name.Contains("FRAME") || r.name.Contains("GATE"))
                {
                    Debug.Log($"[GateV2·dump] {r.name} bounds={r.bounds.size} center={r.bounds.center} scale={r.transform.lossyScale}");
                    break;
                }
            }
        }

        [MenuItem("MapSkin/Gate/① 装配已认可门")]
        public static void Run()
        {
            if (!File.Exists(Layout)) { Debug.LogError("[GateV2] 缺 " + Layout); return; }
            CopyFbx();
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var data = JsonUtility.FromJson<LayoutData>(File.ReadAllText(Layout));
            if (data == null || data.gates == null) { Debug.LogError("[GateV2] 布局解析失败"); return; }

            var mats = BuildMaterials();
            // 关掉 v1 占位（保留 collider）
            var old = GameObject.Find("DEC_Shared/Gate");
            if (old != null)
                foreach (var r in old.GetComponentsInChildren<Renderer>()) r.enabled = false;

            var root = GameObject.Find("DEC_Shared");
            if (root == null) root = new GameObject("DEC_Shared");
            var parent = root.transform.Find("GateV2");
            if (parent != null) Object.DestroyImmediate(parent.gameObject);
            var container = new GameObject("GateV2").transform;
            container.SetParent(root.transform, false);

            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(FbxDst);
            if (fbx == null) { Debug.LogError("[GateV2] FBX 导入失败"); return; }
            int placed = 0;
            foreach (var g in data.gates)
            {
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
                inst.name = $"GATE_{g.block}_{g.type}";
                inst.transform.SetParent(container, false);
                inst.transform.position = new Vector3(g.pos[0], g.pos[1], g.pos[2]);
                inst.transform.rotation = Quaternion.Euler(0f, g.yaw, 0f);
                // FBX 导入后实测门套约为设计尺寸 2 倍，缩回 0.5
                inst.transform.localScale = Vector3.one;
                bool isIn = g.type != null && g.type.ToLower().Contains("entr");
                Assign(inst, mats, isIn);
                RemapLightbox(inst, isIn, g.block);
                placed++;
            }
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[GateV2] 装配 {placed} 樘 → GateV2");
        }

        static void RemapLightbox(GameObject inst, bool isIn, string block)
        {
            // 目标格子
            float tx0, ty0, tx1, ty1;
            string label = isIn ? "IN" : "OUT";
            // atlas cells from GATE_MANIFEST
            if (label == "IN") { tx0 = 0.76f; ty0 = 0.415f; tx1 = 0.99f; ty1 = 0.585f; }
            else { tx0 = 0.01f; ty0 = 0.08167f; tx1 = 0.24f; ty1 = 0.25167f; }
            int num = 1;
            if (!string.IsNullOrEmpty(block) && block.Length >= 2) int.TryParse(block.Substring(block.Length - 2), out num);
            num = Mathf.Clamp(num, 1, 7);
            // 数字格子：01..07
            float nx0, ny0, nx1, ny1;
            switch (num)
            {
                case 1: nx0 = 0.01f; ny0 = 0.74833f; break;
                case 2: nx0 = 0.26f; ny0 = 0.74833f; break;
                case 3: nx0 = 0.51f; ny0 = 0.74833f; break;
                case 4: nx0 = 0.76f; ny0 = 0.74833f; break;
                case 5: nx0 = 0.01f; ny0 = 0.415f; break;
                case 6: nx0 = 0.26f; ny0 = 0.415f; break;
                default: nx0 = 0.51f; ny0 = 0.415f; break;
            }
            nx1 = nx0 + 0.23f; ny1 = ny0 + 0.17f;
            foreach (var t in inst.GetComponentsInChildren<Transform>(true))
            {
                if (t.name.Contains("LIGHTBOX_NUM")) Remap(t, NumX0, NumY0, NumX1, NumY1, nx0, ny0, nx1, ny1);
                else if (t.name.Contains("LIGHTBOX_LABEL")) Remap(t, LabX0, LabY0, LabX1, LabY1, tx0, ty0, tx1, ty1);
            }
        }

        static void Remap(Transform t, float sx0, float sy0, float sx1, float sy1,
                          float dx0, float dy0, float dx1, float dy1)
        {
            var mf = t.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return;
            var mesh = Object.Instantiate(mf.sharedMesh);
            mesh.name = mf.sharedMesh.name + "_cell";
            var uv = mesh.uv;
            float sx = (dx1 - dx0) / (sx1 - sx0), sy = (dy1 - dy0) / (sy1 - sy0);
            for (int i = 0; i < uv.Length; i++)
                uv[i] = new Vector2(dx0 + (uv[i].x - sx0) * sx, dy0 + (uv[i].y - sy0) * sy);
            mesh.uv = uv;
            mf.sharedMesh = mesh;
        }

        static void Assign(GameObject inst, Dictionary<string, Material> m, bool isIn)
        {
            foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
            {
                string n = r.gameObject.name.ToUpperInvariant();
                Material mat = null;
                if (n.Contains("FRAME")) mat = m["Frame"];
                else if (n.Contains("LEAF")) mat = m["Leaf"];
                else if (n.Contains("SEAL")) mat = m["Rubber"];
                else if (n.Contains("GLASS")) mat = m["Glass"];
                else if (n.Contains("STEEL")) mat = m["Steel"];
                else if (n.Contains("THRESHOLD")) mat = m["Threshold"];
                else if (n.Contains("ACCENT")) mat = m[isIn ? "Accent_IN" : "Accent_OUT"];
                else if (n.Contains("ARROW")) mat = m[isIn ? "Arrow_IN" : "Arrow_OUT"];
                else if (n.Contains("LIGHTBOX")) mat = m[isIn ? "Light_IN" : "Light_OUT"];
                if (mat == null) continue;
                var arr = new Material[r.sharedMaterials.Length];
                for (int i = 0; i < arr.Length; i++) arr[i] = mat;
                r.sharedMaterials = arr;
            }
        }

        static Dictionary<string, Material> BuildMaterials()
        {
            var d = new Dictionary<string, Material>();
            foreach (var s in new[] { "Frame", "Leaf", "Rubber", "Glass", "Steel", "Threshold", "Lightbox", "Accent", "Arrow" })
                d[s] = Mat(s);
            d["Accent_IN"] = Tint(d["Accent"], new Color(1f, 0.48f, 0.09f), 3.6f, "Accent_IN");
            d["Accent_OUT"] = Tint(d["Accent"], new Color(0.07f, 0.86f, 0.92f), 3.9f, "Accent_OUT");
            d["Arrow_IN"] = Tint(d["Arrow"], new Color(1f, 0.48f, 0.09f), 2.2f, "Arrow_IN");
            d["Arrow_OUT"] = Tint(d["Arrow"], new Color(0.07f, 0.86f, 0.92f), 2.4f, "Arrow_OUT");
            d["Light_IN"] = Tint(d["Lightbox"], new Color(1f, 0.55f, 0.15f), 3.6f, "Light_IN");
            d["Light_OUT"] = Tint(d["Lightbox"], new Color(0.1f, 0.9f, 0.95f), 3.9f, "Light_OUT");
            return d;
        }

        static Material Tint(Material src, Color emit, float strength, string name)
        {
            Ensure(MatRoot);
            string path = $"{MatRoot}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(src); AssetDatabase.CreateAsset(m, path); }
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", emit * strength);
            if (name.StartsWith("Accent")) m.SetColor("_BaseColor", emit * 0.35f);
            EditorUtility.SetDirty(m);
            return m;
        }

        static Material Mat(string set)
        {
            Ensure(MatRoot);
            string path = $"{MatRoot}/{set}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(m, path); }
            string texDir = $"{TexRoot}/{set}";
            var bc = Tex($"T_Gate_{set}", "BC", texDir); if (bc) m.SetTexture("_BaseMap", bc);
            var n = Tex($"T_Gate_{set}", "N", texDir); if (n) { m.SetTexture("_BumpMap", n); m.EnableKeyword("_NORMALMAP"); }
            var ms = Tex($"T_Gate_{set}", "MS", texDir); if (ms) { m.SetTexture("_MetallicGlossMap", ms); m.EnableKeyword("_METALLICSPECGLOSSMAP"); }
            var ao = Tex($"T_Gate_{set}", "AO", texDir); if (ao) { m.SetTexture("_OcclusionMap", ao); m.EnableKeyword("_OCCLUSIONMAP"); }
            var e = Tex($"T_Gate_{set}", "E", texDir); if (e) { m.SetTexture("_EmissionMap", e); m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", Color.white); }
            m.SetColor("_BaseColor", Color.white);
            m.SetFloat("_Metallic", 1f);
            m.SetFloat("_Smoothness", 1f);
            EditorUtility.SetDirty(m);
            return m;
        }

        static Texture2D Tex(string baseName, string ch, string destDir)
        {
            string src = Path.Combine(SrcRoot, "textures", $"{baseName}_{ch}.png");
            if (!File.Exists(src)) return null;
            Ensure(destDir);
            string rel = $"{destDir}/{baseName}_{ch}.png";
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

        static void CopyFbx()
        {
            string src = Path.Combine(SrcRoot, "GATE_KIT.fbx");
            string dst = Path.Combine(Directory.GetCurrentDirectory(), FbxDst);
            if (!File.Exists(dst) || File.GetLastWriteTimeUtc(src) > File.GetLastWriteTimeUtc(dst))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst) ?? ".");
                File.Copy(src, dst, true);
                AssetDatabase.ImportAsset(FbxDst, ImportAssetOptions.ForceUpdate);
            }
        }

        static void Ensure(string f)
        {
            if (AssetDatabase.IsValidFolder(f)) return;
            int i = f.LastIndexOf('/');
            Ensure(i > 0 ? f.Substring(0, i) : "Assets");
            AssetDatabase.CreateFolder(i > 0 ? f.Substring(0, i) : "Assets", f.Substring(i + 1));
        }
    }
}
