using System.IO;
using UnityEditor;
using UnityEngine;

namespace HST.EditorTools
{
    /// <summary>
    /// 收尾批（可破坏件 ×3 + 楼梯 ×3）材质工厂 + 预制体工厂。
    /// 材质基线由 Blender 端 manifest 生成（PropMatTable.Generated.cs，勿手改）：
    /// 槽名对不上材质表时按灰色兜底并告警；procedural 细节（粗糙度噪声/边缘磨损/警示斜纹）
    /// 留在 Blender 端，后续需要时再出共享细节贴图。
    /// 菜单：HST/收尾批/①生成材质与预制体
    /// </summary>
    public static class PropKitImport
    {
        const string Kit = "Assets/Art/Environment/HST_Kit";

        // [模型目录, 预制体目录, 材质目录]；第二列目录不存在会自动建
        static readonly string[][] Batches =
        {
            new[] { Kit + "/Models/Destructibles", Kit + "/Prefabs/Destructibles", Kit + "/Materials/Destructibles" },
            new[] { Kit + "/Models/Stairs",        Kit + "/Prefabs/Stairs",        Kit + "/Materials/Stairs" },
        };

        const string UrpLit = "Universal Render Pipeline/Lit";

        [MenuItem("HST/收尾批/①生成材质与预制体")]
        public static void RunAll()
        {
            AssetDatabase.Refresh();
            int fbxCount = 0, missing = 0;
            foreach (var b in Batches)
            {
                if (!Directory.Exists(b[0]))
                {
                    Debug.LogWarning("[PropKit] 模型目录不存在，跳过：" + b[0]);
                    continue;
                }
                foreach (var fbx in Directory.GetFiles(b[0], "*.fbx"))
                {
                    fbxCount++;
                    missing += Process(fbx, b[1], b[2], destructible: b[0].EndsWith("Destructibles"));
                }
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[PropKit] 完成：" + fbxCount + " 个 FBX，缺槽材质合计 " + missing + "（0=全绿）");
        }

        static int Process(string fbxPath, string prefabDir, string matDir, bool destructible)
        {
            AssetDatabase.ImportAsset(fbxPath, ImportAssetOptions.ForceUpdate);
            var src = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (src == null)
            {
                Debug.LogError("[PropKit] FBX 导入失败：" + fbxPath);
                return 1;
            }
            Directory.CreateDirectory(prefabDir);
            Directory.CreateDirectory(matDir);

            var inst = (GameObject)PrefabUtility.InstantiatePrefab(src);
            int missing = 0;

            // 材质：按槽名查表换成 URP 材质（FBX 内嵌占位材质不保留）
            foreach (var r in inst.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null) { missing++; continue; }
                    var um = GetOrCreate(mats[i].name, matDir);
                    if (um == null) { missing++; continue; }
                    mats[i] = um;
                }
                r.sharedMaterials = mats;
            }

            // 碰撞：可破坏件=逐件凸包（碎片化预留）；楼梯=精确非凸（踏步要踩得住）
            foreach (var mf in inst.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.GetComponent<MeshCollider>() != null) continue;
                var mc = mf.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;
                mc.convex = destructible;
            }

            var prefabPath = prefabDir + "/" + src.name + ".prefab";
            PrefabUtility.SaveAsPrefabAsset(inst, prefabPath);
            Object.DestroyImmediate(inst);
            Debug.Log("[PropKit] " + src.name + " → " + prefabPath + "（缺槽 " + missing + "）");
            return missing;
        }

        static Material GetOrCreate(string slotName, string matDir)
        {
            PropMatTable.Def def;
            if (!PropMatTable.All.TryGetValue(slotName, out def))
            {
                Debug.LogWarning("[PropKit] 材质表无槽 " + slotName + "，按灰色兜底");
                def = new PropMatTable.Def
                {
                    baseColor = new Color(0.4f, 0.4f, 0.4f, 1f),
                    metallic = 0.5f, smoothness = 0.5f,
                    emission = Color.black, emissionIntensity = 0f,
                };
            }
            var path = matDir + "/M_" + slotName + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                var shader = Shader.Find(UrpLit);
                if (shader == null)
                {
                    Debug.LogError("[PropKit] 找不到 " + UrpLit + "（项目不是 URP？）");
                    return null;
                }
                m = new Material(shader);
                AssetDatabase.CreateAsset(m, path);
            }

            var baseC = def.baseColor;
            if (def.glass) baseC.a = 0.45f;
            m.SetColor("_BaseColor", baseC);
            m.SetFloat("_Metallic", def.metallic);
            m.SetFloat("_Smoothness", def.smoothness);

            if (def.glass)
            {
                m.SetFloat("_Surface", 1f);
                m.SetFloat("_Blend", 0f);
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.SetOverrideTag("RenderType", "Transparent");
                m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                m.SetInt("_ZWrite", 0);
                m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
            else
            {
                m.SetFloat("_Surface", 0f);
                m.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.SetOverrideTag("RenderType", "");
                m.SetInt("_ZWrite", 1);
                m.renderQueue = -1;
            }

            if (def.emissionIntensity > 0f)
            {
                m.EnableKeyword("_EMISSION");
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                m.SetColor("_EmissionColor", def.emission * def.emissionIntensity);
            }
            else
            {
                m.DisableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", Color.black);
            }
            EditorUtility.SetDirty(m);
            return m;
        }
    }
}
