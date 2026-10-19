using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MapSkin
{
    /// <summary>
    /// 样板角工具：把"按面尺寸现写的一组贴图"贴到**一个指定的可见面**上，
    /// 用来做 10–20m² 的样板验收（面贴合 UV + 定制贴图 + NPR 材质）。
    ///
    /// 批处理：
    ///   -executeMethod MapSkin.SampleCorner.Run -obj S_03_Beam_009 -tex T_CUSTOM_S03_Rack_36p3x22p2x0p0
    ///   -executeMethod MapSkin.SampleCorner.Restore -obj S_03_Beam_009
    /// </summary>
    public static class SampleCorner
    {
        const string ScenePath = "Assets/Scenes/Level1.unity";
        static string Target = "S_01_Beam_038";
        static string TexName = "T_CUSTOM_S01_Entry_28p3x12p6x1p0";

        static string TexSrc => $"F:/VSCode/工业级渲染/exports/level1_custom/{TexName}";
        static string TexDst => $"Assets/Art/Images/HST_Custom/{TexName}";
        static string MatPath => $"Assets/Art/Materials/MapSkin/Custom/{TexName}.mat";
        static string BackupPath => $"Assets/Art/EnvSkins/SAMPLE_Backup_{Target}.asset";

        [MenuItem("MapSkin/工具/样板角：贴一组定制贴图")]
        public static void Run()
        {
            ParseArgs();
            EnsureScene();
            if (!CopyTextures()) return;
            var mat = EnsureMaterial();
            if (mat == null) { Debug.LogError("[样板角] 材质创建失败"); return; }

            GameObject go = null;
            foreach (var t in Object.FindObjectsOfType<Transform>(true))
                if (t != null && t.name == Target) { go = t.gameObject; break; }
            if (go == null) { Debug.LogError($"[样板角] 找不到 {Target}"); return; }

            var mf = go.GetComponent<MeshFilter>();
            var mr = go.GetComponent<MeshRenderer>();
            if (mf == null || mr == null || mf.sharedMesh == null)
            { Debug.LogError("[样板角] 目标没有 MeshFilter/MeshRenderer"); return; }

            var backup = AssetDatabase.LoadAssetAtPath<SampleBackup>(BackupPath);
            if (backup == null)
            {
                backup = ScriptableObject.CreateInstance<SampleBackup>();
                AssetDatabase.CreateAsset(backup, BackupPath);
            }
            if (backup.mesh == null && backup.material == null)
            {
                backup.objectName = Target;
                backup.mesh = mf.sharedMesh;
                backup.material = mr.sharedMaterial;
                EditorUtility.SetDirty(backup);
                AssetDatabase.SaveAssets();
            }

            var mesh = Object.Instantiate(mf.sharedMesh);
            mesh.name = $"SAMPLE_{Target}";
            BakeFaceUv(mesh);
            var meshPath = $"Assets/Art/EnvSkins/SAMPLE_{Target}.asset";
            if (AssetDatabase.LoadAssetAtPath<Mesh>(meshPath) != null) AssetDatabase.DeleteAsset(meshPath);
            AssetDatabase.CreateAsset(mesh, meshPath);
            mf.sharedMesh = mesh;
            mr.sharedMaterial = mat;

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            var uv = mesh.uv;
            float minU = 9, maxU = -9, minV = 9, maxV = -9;
            foreach (var u in uv) { minU = Mathf.Min(minU, u.x); maxU = Mathf.Max(maxU, u.x); minV = Mathf.Min(minV, u.y); maxV = Mathf.Max(maxV, u.y); }
            Debug.Log($"[样板角] {Target} ← {TexName}\n" +
                      $"        网格 {mesh.vertexCount}v / {mesh.triangles.Length / 3}tri；" +
                      $"UV0 = [{minU:F3}..{maxU:F3}] × [{minV:F3}..{maxV:F3}]；材质 {MatPath}");
        }

        public static void Restore()
        {
            ParseArgs();
            EnsureScene();
            var backup = AssetDatabase.LoadAssetAtPath<SampleBackup>(BackupPath);
            if (backup == null || backup.mesh == null) { Debug.LogWarning($"[样板角] 没有 {Target} 的备份"); return; }
            GameObject go = null;
            foreach (var t in Object.FindObjectsOfType<Transform>(true))
                if (t != null && t.name == backup.objectName) { go = t.gameObject; break; }
            if (go == null) { Debug.LogError("[样板角] 找不到原对象"); return; }
            go.GetComponent<MeshFilter>().sharedMesh = backup.mesh;
            go.GetComponent<MeshRenderer>().sharedMaterial = backup.material;
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            Debug.Log("[样板角] 已还原原网格/材质");
        }

        // ---------------------------------------------------------------- UV

        /// <summary>
        /// 盒体面贴合 UV：按顶点法线主轴把每个面投影到 0–1（用网格局部包围盒归一化），
        /// 负向面翻转 u 防镜像。灰盒墙/柱/板都是硬边盒，逐顶点即可，不拆点。
        /// </summary>
        static void BakeFaceUv(Mesh m)
        {
            var vs = m.vertices;
            var ns = m.normals;
            if (ns == null || ns.Length != vs.Length) { m.RecalculateNormals(); ns = m.normals; }
            var b = m.bounds;
            var uv = new Vector2[vs.Length];
            for (int i = 0; i < vs.Length; i++)
            {
                var n = ns[i];
                float ax = Mathf.Abs(n.x), ay = Mathf.Abs(n.y), az = Mathf.Abs(n.z);
                if (ay >= ax && ay >= az)
                    uv[i] = new Vector2((vs[i].x - b.min.x) / b.size.x, (vs[i].z - b.min.z) / b.size.z);
                else if (ax >= az)
                {
                    float u = (vs[i].z - b.min.z) / b.size.z;
                    uv[i] = new Vector2(n.x >= 0 ? u : 1f - u, (vs[i].y - b.min.y) / b.size.y);
                }
                else
                {
                    float u = (vs[i].x - b.min.x) / b.size.x;
                    uv[i] = new Vector2(n.z >= 0 ? u : 1f - u, (vs[i].y - b.min.y) / b.size.y);
                }
            }
            m.SetUVs(0, uv);
            m.RecalculateBounds();
        }

        // ---------------------------------------------------------------- 资产

        static bool CopyTextures()
        {
            if (!Directory.Exists(TexSrc)) { Debug.LogError($"[样板角] 贴图目录不存在 {TexSrc}"); return false; }
            if (!AssetDatabase.IsValidFolder("Assets/Art/Images")) AssetDatabase.CreateFolder("Assets/Art", "Images");
            if (!AssetDatabase.IsValidFolder("Assets/Art/Images/HST_Custom")) AssetDatabase.CreateFolder("Assets/Art/Images", "HST_Custom");
            if (!AssetDatabase.IsValidFolder(TexDst)) AssetDatabase.CreateFolder("Assets/Art/Images/HST_Custom", TexName);
            int n = 0;
            foreach (var suffix in new[] { "BC", "N", "MS", "ORM", "H", "E" })
            {
                var src = $"{TexSrc}/{TexName}_{suffix}.png";
                var dst = $"{TexDst}/{TexName}_{suffix}.png";
                if (!File.Exists(src)) { Debug.LogWarning($"[样板角] 缺通道 {src}"); continue; }
                File.Copy(src, dst, true);
                n++;
            }
            AssetDatabase.ImportAsset(TexDst, ImportAssetOptions.ImportRecursive);
            SetImporter($"{TexDst}/{TexName}_N.png", TextureImporterType.NormalMap, false);
            SetImporter($"{TexDst}/{TexName}_MS.png", TextureImporterType.Default, true);
            SetImporter($"{TexDst}/{TexName}_ORM.png", TextureImporterType.Default, true);
            SetImporter($"{TexDst}/{TexName}_H.png", TextureImporterType.Default, true);
            return n > 0;
        }

        static void SetImporter(string path, TextureImporterType type, bool linear)
        {
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null) return;
            ti.textureType = type;
            ti.sRGBTexture = !linear;
            ti.mipmapEnabled = true;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.maxTextureSize = 4096;
            ti.SaveAndReimport();
        }

        static Material EnsureMaterial()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Art/Materials/MapSkin/Custom"))
                AssetDatabase.CreateFolder("Assets/Art/Materials/MapSkin", "Custom");
            if (AssetDatabase.LoadAssetAtPath<Material>(MatPath) == null)
                AssetDatabase.CopyAsset("Assets/Art/Models/MikuGandam/materials/Material.014.mat", MatPath);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
            if (mat == null) return null;

            var bc = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDst}/{TexName}_BC.png");
            var nm = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDst}/{TexName}_N.png");
            var ms = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDst}/{TexName}_MS.png");
            var ao = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDst}/{TexName}_ORM.png");
            var em = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDst}/{TexName}_E.png");

            mat.SetTexture("_BaseMap", bc);
            mat.SetTexture("_BumpMap", nm);
            mat.SetFloat("_BumpScale", 1f);
            mat.SetTexture("_MetallicGlossMap", ms);
            mat.SetTexture("_OcclusionMap", ao);
            mat.SetFloat("_OcclusionStrength", 1f);
            mat.SetColor("_BaseColor", Color.white);
            mat.SetFloat("_Smoothness", 1f);
            mat.SetFloat("_Metallic", 0f);
            mat.SetVector("_BaseMap_ST", new Vector4(1, 1, 0, 0));
            mat.SetFloat("_EmissionEnabled", em != null ? 1f : 0f);
            mat.SetFloat("_EmissionUseMap", em != null ? 1f : 0f);
            if (em != null) mat.SetTexture("_EmissionMap", em);
            mat.SetColor("_EmissionColor", Color.white);
            mat.SetFloat("_EmissionIntensity", 1.2f);
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
            return mat;
        }

        static void EnsureScene()
        {
            var cur = EditorSceneManager.GetActiveScene();
            if (cur.IsValid() && cur.path == ScenePath) return;
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        static void ParseArgs()
        {
            var a = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] == "-obj" && i + 1 < a.Length) Target = a[i + 1];
                else if (a[i] == "-tex" && i + 1 < a.Length) TexName = a[i + 1];
            }
        }
    }

    public class SampleBackup : ScriptableObject
    {
        public string objectName;
        public Mesh mesh;
        public Material material;
    }
}
