// DeckRoadApply.cs — S_04 连续浮空道路装配（Editor-only，可回退）
//
// 输入：
//   · F:/VSCode/工业级渲染/exports/deck_road/DECK_ROAD.fbx
//   · DECK_ROAD_MANIFEST.json（222 个面板 / 216 个材质槽 / 裙边 + 灯带槽）
//   · 贴图目录 exports/deck_road/tex/<panelId>/（存在则贴，缺则用同风格平色）
// 输出：
//   · DEC_Kit/S_04/Road（视觉层，无 Collider）
//   · Assets/Art/Materials/MapSkin/DeckRoad/*.mat
//   · 原 DeckKit 模块 Renderer 关闭（保留 Collider），manifest 记录以便 Clear 还原
//
// 菜单：MapSkin/Deck/③ 装配连续道路
// 批处理：-executeMethod MapSkin.DeckRoadApply.Run
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MapSkin
{
    public static class DeckRoadApply
    {
        const string ScenePath = "Assets/Scenes/Level1.unity";
        const string ExportRoot = "F:/VSCode/工业级渲染/exports/deck_road";
        const string FbxSrc = ExportRoot + "/DECK_ROAD.fbx";
        const string ManifestSrc = ExportRoot + "/DECK_ROAD_MANIFEST.json";
        const string FbxDst = "Assets/Art/Environment/Deck/Road/DECK_ROAD.fbx";
        const string ImportTexRoot = "Assets/Art/Images/Deck/Road";
        const string MatRoot = "Assets/Art/Materials/MapSkin/DeckRoad";
        const string RestorePath = "F:/VSCode/taptap-game-jem/_session_audit/deckroad_restore.json";

        [Serializable] class Manifest { public List<Slot> slots; public List<Panel> panels; public List<SkirtSlot> skirt_slots; public Stats stats; }
        [Serializable] class Slot { public int index; public string id; public string kind; public string material; public string texture_dir; public float[] size; }
        [Serializable] class Panel { public string id; public int slot; public string type; public float[] center; public float[] size; public string module; }
        [Serializable] class SkirtSlot { public int index; public string id; public string kind; public string material; public string size; }
        [Serializable] class Stats { public int slots; public int panels; }
        [Serializable] class Restore { public List<string> hidden = new List<string>(); }

        [MenuItem("MapSkin/Deck/③ 装配连续道路")]
        public static void Run()
        {
            if (!File.Exists(FbxSrc) || !File.Exists(ManifestSrc))
            {
                Debug.LogError($"[DeckRoad] 缺输入：{FbxSrc} / {ManifestSrc}");
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(FbxDst) ?? ".");
            if (File.GetLastWriteTimeUtc(FbxSrc) > File.GetLastWriteTimeUtc(Full(FbxDst)) || !File.Exists(Full(FbxDst)))
                File.Copy(FbxSrc, Full(FbxDst), true);
            AssetDatabase.ImportAsset(FbxDst, ImportAssetOptions.ForceUpdate);

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var kit = GameObject.Find("DEC_Kit/S_04");
            if (kit == null) { Debug.LogError("[DeckRoad] 找不到 DEC_Kit/S_04"); return; }

            // 清旧
            var old = kit.transform.Find("Road");
            if (old != null) UnityEngine.Object.DestroyImmediate(old.gameObject);

            var manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(ManifestSrc));
            if (manifest == null || manifest.slots == null) { Debug.LogError("[DeckRoad] manifest 解析失败"); return; }

            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(FbxDst);
            if (fbx == null) { Debug.LogError($"[DeckRoad] FBX 导入失败 {FbxDst}"); return; }
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
            inst.name = "Road";
            inst.transform.SetParent(kit.transform, false);
            inst.transform.localPosition = Vector3.zero;
            inst.transform.localRotation = Quaternion.identity;
            inst.transform.localScale = Vector3.one;

            foreach (var c in inst.GetComponentsInChildren<Transform>(true).Where(t => t.name == "Cube").ToArray())
                UnityEngine.Object.DestroyImmediate(c.gameObject);

            // 每个槽一份材质
            if (!AssetDatabase.IsValidFolder(MatRoot))
            {
                if (!AssetDatabase.IsValidFolder("Assets/Art/Materials/MapSkin"))
                    AssetDatabase.CreateFolder("Assets/Art/Materials", "MapSkin");
                AssetDatabase.CreateFolder("Assets/Art/Materials/MapSkin", "DeckRoad");
            }
            var mats = new Material[manifest.slots.Count];
            for (int i = 0; i < manifest.slots.Count; i++)
                mats[i] = BuildMaterial(manifest.slots[i]);

            int assigned = 0;
            foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
            {
                int n = Mathf.Max(1, r.sharedMaterials.Length);
                var arr = new Material[n];
                for (int i = 0; i < n; i++) arr[i] = i < mats.Length ? mats[i] : mats[mats.Length - 1];
                r.sharedMaterials = arr;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                r.receiveShadows = true;
                assigned++;
            }

            // 关闭旧 DeckKit 模块的视觉（保留 Collider / 原对象）
            var restore = new Restore();
            foreach (var r in kit.GetComponentsInChildren<Renderer>(true))
            {
                if (r.transform.IsChildOf(inst.transform)) continue;
                if (r.transform.name == "Supports" || (r.transform.parent != null && r.transform.parent.name == "Supports")) continue;
                if (!r.enabled) continue;
                r.enabled = false;
                restore.hidden.Add(Hierarchy(r.transform));
            }
            File.WriteAllText(RestorePath, JsonUtility.ToJson(restore, true));

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[DeckRoad] 装配 {assigned} 个 renderer；槽 {mats.Length}；关闭旧模块 {restore.hidden.Count} 个");
        }

        [MenuItem("MapSkin/Deck/④ 还原旧甲板视觉")]
        public static void Clear()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (!File.Exists(RestorePath)) { Debug.LogWarning("[DeckRoad] 无还原表"); return; }
            var restore = JsonUtility.FromJson<Restore>(File.ReadAllText(RestorePath));
            int n = 0;
            foreach (var path in restore.hidden)
            {
                var go = GameObject.Find(path);
                if (go == null) continue;
                var r = go.GetComponent<Renderer>();
                if (r != null) { r.enabled = true; n++; }
            }
            var kit = GameObject.Find("DEC_Kit/S_04");
            if (kit != null)
            {
                var road = kit.transform.Find("Road");
                if (road != null) UnityEngine.Object.DestroyImmediate(road.gameObject);
            }
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[DeckRoad] 还原 {n} 个旧 renderer");
        }

        static Material BuildMaterial(Slot slot)
        {
            string path = $"{MatRoot}/{Safe(slot.material)}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(m, path);
            }
            Color baseCol;
            float metallic, smooth;
            switch (slot.kind)
            {
                case "info_band": baseCol = new Color(0.30f, 0.34f, 0.40f); metallic = 0.6f; smooth = 0.6f; break;
                case "trim_steel": baseCol = new Color(0.22f, 0.23f, 0.25f); metallic = 0.85f; smooth = 0.55f; break;
                case "emissive": baseCol = new Color(0.2f, 0.6f, 0.7f); metallic = 0.2f; smooth = 0.4f; break;
                default: baseCol = new Color(0.36f, 0.38f, 0.40f); metallic = 0.7f; smooth = 0.5f; break;
            }
            m.SetColor("_BaseColor", baseCol);
            m.SetFloat("_Metallic", metallic);
            m.SetFloat("_Smoothness", smooth);
            // 甲板体是闭合实体，但 FBX 的绕序在 Unity 里可能反；双面渲染保证俯视可见
            m.SetFloat("_Cull", 0f);

            string texDir = Path.Combine(ExportRoot, slot.texture_dir ?? "");
            var b = LoadTex(texDir, slot.id, "BC");
            var nrm = LoadTex(texDir, slot.id, "N");
            var ms = LoadTex(texDir, slot.id, "MS");
            var ao = LoadTex(texDir, slot.id, "AO");
            var e = LoadTex(texDir, slot.id, "E");
            if (b != null)
            {
                m.SetTexture("_BaseMap", b);
                m.EnableKeyword("_NORMALMAP");
                if (nrm != null) m.SetTexture("_BumpMap", nrm);
                if (ms != null) { m.SetTexture("_MetallicGlossMap", ms); m.EnableKeyword("_METALLICSPECGLOSSMAP"); }
                if (ao != null) { m.SetTexture("_OcclusionMap", ao); m.EnableKeyword("_OCCLUSIONMAP"); }
                if (e != null) { m.SetTexture("_EmissionMap", e); m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", Color.white); }
                m.SetFloat("_Metallic", 1f); m.SetFloat("_Smoothness", 1f);
            }
            else if (slot.kind == "emissive")
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", new Color(0.30f, 0.85f, 1.0f) * 3f);
            }
            EditorUtility.SetDirty(m);
            return m;
        }

        static Texture2D LoadTex(string dir, string id, string ch)
        {
            if (!Directory.Exists(dir)) return null;
            foreach (var name in new[] { $"{id}_{ch}.png", $"{Safe(id)}_{ch}.png", $"{ch}.png" })
            {
                string p = Path.Combine(dir, name);
                if (File.Exists(p)) return ImportTexture(p, ch);
            }
            return null;
        }

        static Texture2D ImportTexture(string src, string ch)
        {
            string relDir = ImportTexRoot + "/" + Path.GetFileName(Path.GetDirectoryName(src));
            EnsureFolder(relDir);
            string rel = relDir + "/" + Path.GetFileName(src);
            string full = Full(rel);
            if (!File.Exists(full) || File.GetLastWriteTimeUtc(src) > File.GetLastWriteTimeUtc(full))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full) ?? ".");
                File.Copy(src, full, true);
                AssetDatabase.ImportAsset(rel, ImportAssetOptions.ForceUpdate);
            }
            var imp = AssetImporter.GetAtPath(rel) as TextureImporter;
            if (imp != null)
            {
                imp.textureType = ch == "N" ? TextureImporterType.NormalMap : TextureImporterType.Default;
                imp.sRGBTexture = ch == "BC" || ch == "E";
                imp.maxTextureSize = 4096;
                imp.mipmapEnabled = true;
                imp.wrapMode = TextureWrapMode.Clamp;
                imp.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(rel);
        }

        static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            int i = folder.LastIndexOf('/');
            string parent = i > 0 ? folder.Substring(0, i) : "Assets";
            string name = i >= 0 ? folder.Substring(i + 1) : folder;
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, name);
        }

        static string Full(string assetPath) => Path.Combine(Directory.GetCurrentDirectory(), assetPath);
        static string Safe(string s) => string.IsNullOrEmpty(s) ? "x" : string.Join("_", s.Split(Path.GetInvalidFileNameChars()));
        static string Hierarchy(Transform t)
        {
            var parts = new List<string>();
            for (var p = t; p != null; p = p.parent) parts.Add(p.name);
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
