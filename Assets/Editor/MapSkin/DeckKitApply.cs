using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MapSkin
{
    /// <summary>
    /// S_04 模块化浮空甲板：按 DECK_MODULES.json 把 11 块灰盒的**视觉**替换成 kit 模块。
    ///
    /// 铁律：
    ///   · 只关原对象的 MeshRenderer，Collider / MeshCollider / 层级 / Transform 全部不动；
    ///   · 视觉层单独生成到 DEC_Kit/S_04，每个模块一个 MeshFilter + MeshRenderer，无 Collider；
    ///   · 材质用 CopyAsset 从 NPR 母材质复制（不 new Material），贴图 tiling = 1,1；
    ///   · Run 幂等：先清空 DEC_Kit/S_04 再按 manifest 重建；Restore 删视觉层并恢复 Renderer。
    ///
    /// 批处理：
    ///   -executeMethod MapSkin.DeckKitApply.Run
    ///   -executeMethod MapSkin.DeckKitApply.Restore
    /// 菜单：
    ///   MapSkin/DeckKit/① Apply S_04
    ///   MapSkin/DeckKit/② Restore S_04
    /// </summary>
    public static class DeckKitApply
    {
        const string ScenePath = "Assets/Scenes/Level1.unity";
        const string DefaultModulesFile = "F:/VSCode/taptap-game-jem/_session_audit/DECK_MODULES.json";
        const string DefaultFbxRoot = "F:/VSCode/工业级渲染/tmp/unity_export/deck_kit";
        const string DefaultTexRoot = "F:/VSCode/工业级渲染/exports/deck_kit";
        const string NprMother = "Assets/Art/Materials/NPRBase.mat";
        const string RootName = "BOX_S_04";
        const string ContainerRoot = "DEC_Kit";
        const string ContainerName = "S_04";

        [Serializable]
        public class KitDoc
        {
            public int schema;
            public string block;
            public string scene;
            public string container;
            public string sourceFbxRoot;
            public string sourceTexRoot;
            public string assetFbxRoot;
            public string assetTexRoot;
            public string materialRoot;
            public KitModule[] modules;
        }

        [Serializable]
        public class KitModule
        {
            public string id;
            // C# 允许 @object；JsonUtility 序列化名仍是 "object"
            public string @object;
            public float[] center;
            public float[] aabb;
            public string type;
            public string asset;
            public string mesh;
            public string color;
            public int variant;
            public string texture;
            public float rotY;
            public float height;
            public float yOffsetMm;   // 重叠区视觉错层，见 DECK_MODULES.json
            public int colorGroup;
            public string note;
        }

        [MenuItem("MapSkin/DeckKit/① Apply S_04")]
        public static void Run()
        {
            EnsureScene();
            var doc = LoadDoc();
            if (doc == null || doc.modules == null || doc.modules.Length == 0)
            {
                Debug.LogError("[DeckKit] DECK_MODULES.json 读不到或 modules 为空");
                return;
            }
            string fbxRoot = FirstExisting(doc.sourceFbxRoot, DefaultFbxRoot);
            string texRoot = FirstExisting(doc.sourceTexRoot, DefaultTexRoot);
            string fbxDstRoot = string.IsNullOrEmpty(doc.assetFbxRoot)
                ? "Assets/Art/Environment/Deck/Kit" : doc.assetFbxRoot;
            string texDstRoot = string.IsNullOrEmpty(doc.assetTexRoot)
                ? "Assets/Art/Images/Deck/Kit" : doc.assetTexRoot;
            string matRoot = string.IsNullOrEmpty(doc.materialRoot)
                ? "Assets/Art/Materials/MapSkin/DeckKit" : doc.materialRoot;
            EnsureFolder(fbxDstRoot);
            EnsureFolder(texDstRoot);
            EnsureFolder(matRoot);

            // 1) 复制/导入所有唯一 FBX
            var meshByAsset = new Dictionary<string, Mesh>();
            foreach (var asset in doc.modules.Select(m => m.asset).Distinct())
            {
                string src = Path.Combine(fbxRoot, asset + ".fbx");
                string dst = $"{fbxDstRoot}/{asset}.fbx";
                if (!File.Exists(src))
                {
                    Debug.LogError($"[DeckKit] 缺 FBX: {src}");
                    continue;
                }
                if (!File.Exists(dst) || File.GetLastWriteTimeUtc(src) > File.GetLastWriteTimeUtc(dst))
                    File.Copy(src, dst, true);
                AssetDatabase.ImportAsset(dst, ImportAssetOptions.ForceUpdate);
                var mesh = LoadMesh(dst);
                if (mesh == null) Debug.LogError($"[DeckKit] FBX 里没 Mesh: {dst}");
                else meshByAsset[asset] = mesh;
            }

            // 2) 视觉容器：幂等清空；顺手清掉旧"布尔并集"方案留下的整块视觉体
            var legacy = GameObject.Find("DECK_S04_VISUAL");
            if (legacy != null) UnityEngine.Object.DestroyImmediate(legacy);
            var container = EnsureContainer();
            ClearChildren(container.transform);

            var root = GameObject.Find(RootName);
            if (root == null) Debug.LogWarning($"[DeckKit] 场景里没找到 {RootName}，仍按中心点匹配 Renderer");

            var allRenderers = root != null
                ? root.GetComponentsInChildren<Renderer>(true)
                : UnityEngine.Object.FindObjectsOfType<Renderer>(true);

            int placed = 0, hidden = 0, missing = 0;
            foreach (var m in doc.modules)
            {
                if (!meshByAsset.TryGetValue(m.asset, out var mesh))
                {
                    missing++;
                    continue;
                }
                var center = new Vector3(m.center[0], m.center[1], m.center[2]);
                var mat = EnsureMaterial(texRoot, texDstRoot, matRoot, m.texture);
                if (mat == null)
                {
                    missing++;
                    continue;
                }

                var go = new GameObject(m.id);
                go.transform.SetParent(container.transform, false);
                // 灰盒块互相重叠（union 才是地面）：用 manifest 的 mm 级错层消 z-fighting，
                // Collider 仍钉在原位，视觉高差 ≤ ±5mm。
                go.transform.position = center + Vector3.up * (m.yOffsetMm * 0.001f);
                go.transform.rotation = Quaternion.Euler(0f, m.rotY, 0f);
                go.transform.localScale = Vector3.one;
                var mf = go.AddComponent<MeshFilter>();
                var mr = go.AddComponent<MeshRenderer>();
                mf.sharedMesh = mesh;
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                mr.receiveShadows = true;
                // 视觉层不加 Collider：碰撞完全由原对象承担
                placed++;

                // 3) 只关视觉：按 bounds.center 匹配原对象（名字有 (1)/(2) 后缀，不可靠），
                //    中心匹配失败再退回 JSON 里的层级路径。
                var src = ResolveRenderer(allRenderers, m);
                if (src == null)
                {
                    Debug.LogWarning($"[DeckKit] 找不到中心 {center} 对应的原 Renderer（模块 {m.id}）");
                }
                else
                {
                    if (src.enabled) hidden++;
                    src.enabled = false;
                }
            }

            // 补漏：按中心 ±0.6m 再关一遍原板（prefab 层级/命名差异会漏配）
            foreach (var r in allRenderers)
            {
                if (r == null || !r.enabled) continue;
                var c = r.bounds.center;
                foreach (var m in doc.modules)
                {
                    var center = new Vector3(m.center[0], m.center[1], m.center[2]);
                    if (Mathf.Abs(c.x - center.x) < 0.6f &&
                        Mathf.Abs(c.y - center.y) < 0.6f &&
                        Mathf.Abs(c.z - center.z) < 0.6f)
                    { r.enabled = false; hidden++; break; }
                }
            }

            var scene = EditorSceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[DeckKit] Apply 完成：放置 {placed} 个模块，隐藏 {hidden} 个原 Renderer，跳过 {missing}；" +
                      $"容器 {ContainerRoot}/{ContainerName}；Collider 未动。");
        }

        [MenuItem("MapSkin/DeckKit/② Restore S_04")]
        public static void Restore()
        {
            EnsureScene();
            var doc = LoadDoc();
            int restored = 0;
            if (doc != null && doc.modules != null)
            {
                var root = GameObject.Find(RootName);
                var allRenderers = root != null
                    ? root.GetComponentsInChildren<Renderer>(true)
                    : UnityEngine.Object.FindObjectsOfType<Renderer>(true);
                foreach (var m in doc.modules)
                {
                    var center = new Vector3(m.center[0], m.center[1], m.center[2]);
                    var src = ResolveRenderer(allRenderers, m);
                    if (src != null && !src.enabled)
                    {
                        src.enabled = true;
                        restored++;
                    }
                }
            }

            var kitRoot = GameObject.Find(ContainerRoot);
            var kit = kitRoot != null ? kitRoot.transform.Find(ContainerName) : null;
            if (kit != null)
            {
                UnityEngine.Object.DestroyImmediate(kit.gameObject);
                if (kitRoot.transform.childCount == 0)
                    UnityEngine.Object.DestroyImmediate(kitRoot);
            }
            else
            {
                var stale = GameObject.Find($"{ContainerRoot}/{ContainerName}");
                if (stale != null) UnityEngine.Object.DestroyImmediate(stale);
            }

            var scene = EditorSceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[DeckKit] Restore 完成：删除 {ContainerRoot}/{ContainerName}，恢复 {restored} 个 Renderer。");
        }

        // ---------------------------------------------------------------- 资产

        static Material EnsureMaterial(string texRoot, string texDstRoot, string matRoot, string texture)
        {
            if (string.IsNullOrEmpty(texture)) return null;
            string srcDir = Path.Combine(texRoot, texture);
            string dstDir = $"{texDstRoot}/{texture}";
            if (!Directory.Exists(srcDir))
            {
                Debug.LogError($"[DeckKit] 缺贴图目录: {srcDir}");
                return null;
            }
            EnsureFolder(dstDir);
            foreach (var suf in new[] { "BC", "N", "R", "M", "AO", "H", "MS", "ORM", "E" })
            {
                string src = Path.Combine(srcDir, $"{texture}_{suf}.png");
                if (File.Exists(src))
                    File.Copy(src, $"{dstDir}/{texture}_{suf}.png", true);
            }
            AssetDatabase.ImportAsset(dstDir, ImportAssetOptions.ImportRecursive);
            SetImp($"{dstDir}/{texture}_BC.png", TextureImporterType.Default, false);
            SetImp($"{dstDir}/{texture}_N.png", TextureImporterType.NormalMap, true);
            foreach (var suf in new[] { "R", "M", "AO", "H", "MS", "ORM" })
                SetImp($"{dstDir}/{texture}_{suf}.png", TextureImporterType.Default, true);
            SetImp($"{dstDir}/{texture}_E.png", TextureImporterType.Default, false);

            string matPath = $"{matRoot}/{texture}.mat";
            if (AssetDatabase.LoadAssetAtPath<Material>(matPath) == null)
            {
                if (!AssetDatabase.CopyAsset(NprMother, matPath))
                {
                    Debug.LogError($"[DeckKit] CopyAsset 失败: {NprMother} -> {matPath}");
                    return null;
                }
                AssetDatabase.ImportAsset(matPath);
            }
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null) return null;

            var bc = AssetDatabase.LoadAssetAtPath<Texture2D>($"{dstDir}/{texture}_BC.png");
            var nm = AssetDatabase.LoadAssetAtPath<Texture2D>($"{dstDir}/{texture}_N.png");
            var ms = AssetDatabase.LoadAssetAtPath<Texture2D>($"{dstDir}/{texture}_MS.png");
            var ao = AssetDatabase.LoadAssetAtPath<Texture2D>($"{dstDir}/{texture}_AO.png");
            var em = AssetDatabase.LoadAssetAtPath<Texture2D>($"{dstDir}/{texture}_E.png");

            mat.SetTexture("_BaseMap", bc);
            mat.SetTexture("_BumpMap", nm);
            mat.SetFloat("_BumpScale", 1f);
            mat.SetTexture("_MetallicGlossMap", ms);
            mat.SetTexture("_OcclusionMap", ao);       // AO 用灰度 AO 图（URP 取 G 通道）
            mat.SetFloat("_OcclusionStrength", 1f);
            mat.SetTexture("_EmissionMap", em);
            mat.SetFloat("_EmissionEnabled", 1f);
            mat.SetFloat("_EmissionUseMap", 1f);
            mat.SetColor("_EmissionColor", Color.white);
            mat.SetFloat("_EmissionIntensity", 1.2f);

            mat.SetColor("_BaseColor", Color.white);
            mat.SetFloat("_Metallic", 0f);
            mat.SetFloat("_Smoothness", 1f);
            mat.SetFloat("_SmoothnessTextureChannel", 0f); // 0 = Metallic Alpha
            mat.SetFloat("_Surface", 0f);                   // Opaque
            mat.SetFloat("_AlphaClip", 0f);
            mat.SetColor("_ShadowColor", new Color(0.30f, 0.48f, 0.53f));
            mat.SetFloat("_ToonSoftness", 0.15f);
            mat.SetFloat("_ToonThreshold", 1.0f);

            mat.SetTextureScale("_BaseMap", new Vector2(1f, 1f));
            mat.SetTextureOffset("_BaseMap", Vector2.zero);
            mat.SetTextureScale("_BumpMap", new Vector2(1f, 1f));
            mat.SetTextureOffset("_BumpMap", Vector2.zero);
            mat.SetTextureScale("_MetallicGlossMap", new Vector2(1f, 1f));
            mat.SetTextureOffset("_MetallicGlossMap", Vector2.zero);
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
            ti.mipmapEnabled = true;
            ti.SaveAndReimport();
        }

        static Mesh LoadMesh(string path)
        {
            return AssetDatabase.LoadAllAssetsAtPath(path)
                .OfType<Mesh>()
                .OrderByDescending(m => m.vertexCount)
                .FirstOrDefault();
        }

        // ---------------------------------------------------------------- 场景

        static GameObject EnsureContainer()
        {
            var root = GameObject.Find(ContainerRoot);
            if (root == null) root = new GameObject(ContainerRoot);
            var child = root.transform.Find(ContainerName);
            if (child == null)
            {
                var go = new GameObject(ContainerName);
                go.transform.SetParent(root.transform, false);
                child = go.transform;
            }
            return child.gameObject;
        }

        static void ClearChildren(Transform t)
        {
            for (int i = t.childCount - 1; i >= 0; i--)
                UnityEngine.Object.DestroyImmediate(t.GetChild(i).gameObject);
        }

        static Renderer FindRendererAt(Renderer[] renderers, Vector3 center)
        {
            Renderer best = null;
            float bestD = 0.30f;
            foreach (var r in renderers)
            {
                if (r == null || !(r is MeshRenderer)) continue;
                float d = Vector3.Distance(r.bounds.center, center);
                if (d < bestD) { bestD = d; best = r; }
            }
            return best;
        }

        static Renderer ResolveRenderer(Renderer[] renderers, KitModule m)
        {
            var center = new Vector3(m.center[0], m.center[1], m.center[2]);
            var r = FindRendererAt(renderers, center);
            if (r != null) return r;
            var go = GameObject.Find(m.@object);
            return go != null ? go.GetComponent<Renderer>() : null;
        }

        static void EnsureScene()
        {
            var cur = EditorSceneManager.GetActiveScene();
            if (cur.IsValid() && cur.path == ScenePath) return;
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        static KitDoc LoadDoc()
        {
            string path = DefaultModulesFile;
            if (!File.Exists(path))
            {
                string alt = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "_session_audit", "DECK_MODULES.json"));
                if (File.Exists(alt)) path = alt;
            }
            if (!File.Exists(path))
            {
                Debug.LogError($"[DeckKit] 找不到 {DefaultModulesFile}");
                return null;
            }
            try
            {
                return JsonUtility.FromJson<KitDoc>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                Debug.LogError($"[DeckKit] 解析 DECK_MODULES.json 失败: {e.Message}");
                return null;
            }
        }

        static string FirstExisting(string a, string b)
        {
            if (!string.IsNullOrEmpty(a) && Directory.Exists(a)) return a;
            return b;
        }

        static void EnsureFolder(string assetPath)
        {
            assetPath = assetPath.Replace('\\', '/');
            if (AssetDatabase.IsValidFolder(assetPath)) return;
            var parts = assetPath.Split('/');
            string cur = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = cur + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(cur, parts[i]);
                cur = next;
            }
        }
    }
}
