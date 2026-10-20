using System;
using System.IO;
using GameJam.Flow;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GameJam.FlowEditor
{
    public static class StabilityLampSetup
    {
        public const string PrefabPath = "Assets/Prefabs/Props/StabilityLamp.prefab";
        public const string VisualPath = "Assets/Prefabs/Props/StabilityLampVisual.prefab";
        private const string ModelPath = "Assets/Art/Models/titanic-lamp/source/Lamp.fbx";
        private const string ArtFolder = "Assets/Art/Models/titanic-lamp";
        private const string MaterialFolder = ArtFolder + "/Materials";

        [MenuItem("超高速行者/流程与地图/替换混乱度球为 Titanic 灯")]
        [MenuItem("超高速行者/流程与地图/修正混乱度灯底座贴地")]
        public static void Install()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("请先退出播放模式。");
            BuildAssets();
            int count = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[]
                { "Assets/Settings/FormalFlow/Templates", "Assets/Settings/FlowExamples/Templates" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponentsInChildren<StabilityDevice>(true).Length == 0) continue;
                GameObject root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    foreach (StabilityDevice device in root.GetComponentsInChildren<StabilityDevice>(true))
                    { ConfigureDevice(device); SnapToGround(device, root.transform); count++; }
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[StabilityLampSetup] 已装配 {count} 个稳定装置并将底座贴地，编号和混乱度配表保持不变。");
        }

        public static StabilityDevice Create(Transform parent, Vector3 position)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) == null) BuildAssets();
            var root = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath), parent);
            root.name = "稳定装置";
            root.transform.localPosition = position;
            var device = root.GetComponent<StabilityDevice>();
            SnapToGround(device, parent.root);
            return device;
        }

        private static void SnapToGround(StabilityDevice device, Transform roomRoot)
        {
            Physics.SyncTransforms();
            Renderer[] renderers = device.Visual.GetComponentsInChildren<Renderer>(true);
            Bounds visualBounds = renderers[0].bounds;
            foreach (Renderer renderer in renderers) visualBounds.Encapsulate(renderer.bounds);
            float nearest = float.PositiveInfinity, ground = 0;
            foreach (Collider collider in roomRoot.GetComponentsInChildren<Collider>(true))
            {
                if (collider.isTrigger || collider.transform.IsChildOf(device.transform)) continue;
                Bounds bounds = collider.bounds;
                Vector3 origin = device.transform.position;
                origin.y = Mathf.Max(origin.y, bounds.max.y) + visualBounds.size.y;
                if (!collider.Raycast(new Ray(origin, Vector3.down), out RaycastHit hit,
                        origin.y - bounds.min.y + visualBounds.size.y) || hit.normal.y < .9f) continue;
                // 多层地图只选择最接近原装置高度的地板，避免把灯吸到屋顶。
                float distance = Mathf.Abs(hit.point.y - device.transform.position.y);
                if (distance < nearest) { nearest = distance; ground = hit.point.y; }
            }
            if (float.IsPositiveInfinity(nearest)) throw new InvalidOperationException(device.name + "：灯脚下没有可贴合的地面。");
            device.transform.position += Vector3.up * (ground - visualBounds.min.y);
            if (PrefabUtility.IsPartOfPrefabInstance(device)) PrefabUtility.RecordPrefabInstancePropertyModifications(device.transform);
            EditorUtility.SetDirty(device.transform);
        }

        private static void ConfigureDevice(StabilityDevice device)
        {
            GameObject visualAsset = AssetDatabase.LoadAssetAtPath<GameObject>(VisualPath);
            if (device.Visual == null || PrefabUtility.GetCorrespondingObjectFromSource(device.Visual) != visualAsset)
            {
                if (device.Visual != null) Object.DestroyImmediate(device.Visual);
                device.Visual = (GameObject)PrefabUtility.InstantiatePrefab(visualAsset, device.transform);
                device.Visual.name = "TitanicLamp";
            }
            var gib = device.GetComponent<GibComponent>();
            if (gib == null) gib = device.gameObject.AddComponent<GibComponent>();
            var settings = new SerializedObject(gib);
            settings.FindProperty("_cutoutShader").objectReferenceValue = Shader.Find("GameJam/EnemyWaistCutout");
            settings.FindProperty("_cutColor").colorValue = new Color(.18f, .13f, .065f);
            settings.ApplyModifiedPropertiesWithoutUndo();
            device.BreakEffect = gib;
            if (device.GetComponent<CameraWallFadeIgnore>() == null) device.gameObject.AddComponent<CameraWallFadeIgnore>();
            var light = device.GetComponent<Light>();
            if (light == null)
            {
                light = device.gameObject.AddComponent<Light>();
                light.type = LightType.Point; light.range = 8f; light.intensity = 2f;
            }
            light.color = new Color(1f, .65f, .25f);
            EditorUtility.SetDirty(gib); EditorUtility.SetDirty(device); EditorUtility.SetDirty(light);
        }

        private static void BuildAssets()
        {
            EnsureFolder("Assets/Prefabs/Props"); EnsureFolder(MaterialFolder);
            var importer = (ModelImporter)AssetImporter.GetAtPath(ModelPath);
            if (importer == null) throw new InvalidOperationException("找不到 Titanic Lamp 模型。");
            // 静态网格切割和封口需要读取顶点；保留 FBX 原有的轴向和单位换算。
            if (!importer.isReadable) { importer.isReadable = true; importer.SaveAndReimport(); }
            string textureFolder = ArtFolder + "/textures/";
            string normalPath = textureFolder + "Lamp_Lamp_Normal.png";
            var normalImporter = (TextureImporter)AssetImporter.GetAtPath(normalPath);
            if (normalImporter.textureType != TextureImporterType.NormalMap)
            { normalImporter.textureType = TextureImporterType.NormalMap; normalImporter.SaveAndReimport(); }
            string packedPath = MaterialFolder + "/Lamp_MetallicSmoothness.png";
            PackMetallicSmoothness(textureFolder, packedPath);
            Material body = MaterialAsset(MaterialFolder + "/Lamp_Body.mat");
            body.SetColor("_BaseColor", Color.white);
            body.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(textureFolder + "Lamp_Lamp_BaseColor.png"));
            body.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>(normalPath));
            body.SetTexture("_MetallicGlossMap", AssetDatabase.LoadAssetAtPath<Texture2D>(packedPath));
            body.SetFloat("_Metallic", 1f); body.SetFloat("_Smoothness", 1f);
            body.EnableKeyword("_NORMALMAP"); body.EnableKeyword("_METALLICSPECGLOSSMAP");
            Material emission = MaterialAsset(MaterialFolder + "/Lamp_Emission.mat");
            emission.SetColor("_BaseColor", new Color(1f, .85f, .5f));
            emission.SetColor("_EmissionColor", new Color(6f, 3f, .8f));
            emission.EnableKeyword("_EMISSION");
            emission.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            EditorUtility.SetDirty(body); EditorUtility.SetDirty(emission);

            var visual = new GameObject("StabilityLampVisual");
            try
            {
                var model = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath), visual.transform);
                Renderer[] renderers = model.GetComponentsInChildren<Renderer>(true);
                Bounds bounds = renderers[0].bounds;
                foreach (Renderer renderer in renderers)
                {
                    bounds.Encapsulate(renderer.bounds);
                    Material[] materials = renderer.sharedMaterials;
                    for (int i = 0; i < materials.Length; i++) materials[i] = materials[i].name == "Emission" ? emission : body;
                    renderer.sharedMaterials = materials;
                }
                // FBX 根节点自带位移；必须在缩放后重算包围盒，不能把原位移再次乘入居中偏移。
                float scale = 1.6f / bounds.size.y;
                model.transform.localScale *= scale;
                Bounds scaledBounds = renderers[0].bounds;
                foreach (Renderer renderer in renderers) scaledBounds.Encapsulate(renderer.bounds);
                model.transform.localPosition -= scaledBounds.center;
                var collider = visual.AddComponent<BoxCollider>();
                collider.size = bounds.size * scale;
                PrefabUtility.SaveAsPrefabAsset(visual, VisualPath);
            }
            finally { Object.DestroyImmediate(visual); }
            var root = new GameObject("StabilityLamp");
            try
            {
                ConfigureDevice(root.AddComponent<StabilityDevice>());
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally { Object.DestroyImmediate(root); }
            AssetDatabase.SaveAssets();
        }

        private static void PackMetallicSmoothness(string folder, string output)
        {
            var metallic = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            var roughness = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            Texture2D packed = null;
            try
            {
                metallic.LoadImage(File.ReadAllBytes(folder + "Lamp_Lamp_Metallic.png"));
                roughness.LoadImage(File.ReadAllBytes(folder + "Lamp_Lamp_Roughness.png"));
                packed = new Texture2D(metallic.width, metallic.height, TextureFormat.RGBA32, false, true);
                Color[] pixels = metallic.GetPixels();
                for (int y = 0; y < metallic.height; y++) for (int x = 0; x < metallic.width; x++)
                {
                    int i = y * metallic.width + x;
                    pixels[i] = new Color(pixels[i].r, 0, 0,
                        1f - roughness.GetPixelBilinear((x + .5f) / metallic.width, (y + .5f) / metallic.height).r);
                }
                packed.SetPixels(pixels); packed.Apply(); File.WriteAllBytes(output, packed.EncodeToPNG());
            }
            finally { Object.DestroyImmediate(metallic); Object.DestroyImmediate(roughness); if (packed != null) Object.DestroyImmediate(packed); }
            AssetDatabase.ImportAsset(output);
            var importer = (TextureImporter)AssetImporter.GetAtPath(output);
            importer.sRGBTexture = false; importer.SaveAndReimport();
        }

        private static Material MaterialAsset(string path)
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null) return material;
            material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            AssetDatabase.CreateAsset(material, path); return material;
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = path.Substring(0, path.LastIndexOf('/'));
            EnsureFolder(parent); AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
