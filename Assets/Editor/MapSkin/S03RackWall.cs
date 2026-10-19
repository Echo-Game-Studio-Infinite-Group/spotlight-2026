// S03RackWall.cs — S_03 机架墙「按真实尺寸分段贴图」工具（草案，未运行）
//
// 背景
//   S_03_Beam_009/010/011/012 是 4 面 44.4m 高的整块巨板（局部网格 80×2×2，
//   scale 为 (0.0192, 22.224, 36.2665) 等），真实尺寸 72.5/85.1/71.1/88.2 × 44.4m。
//   旧做法是一张 36.3×22.2m 的贴图铺满整面，texel density 只有 17.7mm/px。
//   新贴图由 scripts/gen_rackwall_real.py 按 18–24m 一段、每段 4096px 出图
//   （≈4.4mm/px），本工具负责把每段贴到墙的可见面上。
//
// 硬约束（与生成器一致）
//   · 不写 prefab；
//   · 不 new Material（复用现有 T_CUSTOM_S03_Rack_*.mat，逐段用 MaterialPropertyBlock 换贴图）；
//   · 不改原墙的 BoxCollider / MeshCollider / Transform / 父子关系；
//   · 原 MeshRenderer 只在“应用”时禁用，Clear 可恢复。
//
// 贴图路径约定（与 gen_rackwall_real.py 的 manifest 一致）：
//   Assets/Art/Images/HST_Custom/<setName>/<setName>_BC.png / _N / _R / _M / _AO / _H / _MS / _ORM / _E
//   setName = T_CUSTOM_S03_RackWall_<Beam_009>_seg00
//
// 金属口径（2026-10-19 修正）：机架是金属，不是哑光黑。MS 的 R=Metallic、A=Smoothness，
// 基材质 _Metallic/_Smoothness 已置 1（贴图驱动）；逐段 MPB 仍显式写 1，避免材质被别处改回。
// 用法
//   菜单：MapSkin/S_03 机架墙/① 干跑（只打印）
//         MapSkin/S_03 机架墙/② 应用分段贴图
//         MapSkin/S_03 机架墙/③ 清除分段
//   批处理：
//     -executeMethod MapSkin.S03RackWall.DryRun
//     -executeMethod MapSkin.S03RackWall.Run
//     -executeMethod MapSkin.S03RackWall.Clear
//
// 注意：贴图的导入设置（BC sRGB、N 为 Normal Map、MS 为 Metallic/Smoothness、
//       E sRGB + 开 Alpha Is Transparency 否）由导入脚本/主控负责；本工具只读 Texture2D。

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MapSkin
{
    public static class S03RackWall
    {
        const string ScenePath = "Assets/Scenes/Level1.unity";
        const string TextureRoot = "Assets/Art/Images/HST_Custom";
        const string BaseMaterialPath =
            "Assets/Art/Materials/MapSkin/Custom/T_CUSTOM_S03_Rack_36p3x22p2x0p0.mat";
        const string ContainerName = "S03_RackWall_Segments";
        const string SegmentPrefix = "S03_RackWall_Seg_";
        const float FaceOffset = 0.012f;      // 可见面外移 12mm，避开 z-fighting
        const float TargetSegmentM = 20.5f;   // 与生成器 plan_segments 同口径
        const float MinSegmentM = 18f;
        const float MaxSegmentM = 24f;

        static readonly string[] WallNames =
        {
            "S_03_Beam_009", "S_03_Beam_010", "S_03_Beam_011", "S_03_Beam_012",
        };

        // 只读缓存，不写资产
        static readonly Dictionary<string, Texture2D> _texCache = new Dictionary<string, Texture2D>();

        // ------------------------------------------------------------------ 菜单
        [MenuItem("MapSkin/S_03 机架墙/① 干跑（只打印）")]
        public static void DryRunMenu() => DryRun();

        [MenuItem("MapSkin/S_03 机架墙/② 应用分段贴图")]
        public static void RunMenu() => Run();

        [MenuItem("MapSkin/S_03 机架墙/③ 清除分段")]
        public static void ClearMenu() => Clear();

        public static void DryRun() => RunCore(true);
        public static void Run() => RunCore(false);

        // ------------------------------------------------------------------ 主流程
        static void RunCore(bool dry)
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (!scene.IsValid() || scene.path != ScenePath)
            {
                if (scene.IsValid() && scene.isDirty)
                    Debug.LogWarning("[S03RackWall] 当前场景有未保存改动；仍切到 Level1（不替你保存原场景）");
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            }

            var baseMat = AssetDatabase.LoadAssetAtPath<Material>(BaseMaterialPath);
            if (baseMat == null)
            {
                Debug.LogError($"[S03RackWall] 找不到基材质 {BaseMaterialPath}");
                return;
            }

            var rep = new StringBuilder();
            rep.AppendLine("[S03RackWall] 分段贴图工具（" + (dry ? "干跑" : "应用") + "）");
            int made = 0, skipped = 0;
            Transform container = dry ? null : EnsureContainer();

            foreach (var wallName in WallNames)
            {
                var wall = FindWall(wallName);
                if (wall == null)
                {
                    rep.AppendLine($"  {wallName}: 场景里没找到带 MeshRenderer 的墙");
                    skipped++;
                    continue;
                }

                var b = wall.renderer.bounds;
                var (longAxis, thickAxis) = AxesOf(b.size);
                float length = b.size[longAxis];
                int n = SegmentCount(length);
                float segW = length / n;
                float height = b.size.y;
                float faceCoord = FaceCoord(b, thickAxis);
                int nSign = faceCoord < b.center[thickAxis] ? -1 : 1;

                rep.AppendLine($"  {wallName}: {length:F3}×{height:F3}m, 长轴 {(char)('X' + longAxis)}, " +
                               $"厚轴 {(char)('X' + thickAxis)}, 可见面 {(nSign < 0 ? "-" : "+")}{(char)('X' + thickAxis)}, " +
                               $"{n} 段 × {segW:F3}m");

                for (int i = 0; i < n; i++)
                {
                    string set = $"T_CUSTOM_S03_RackWall_{ShortName(wallName)}_seg{i:00}";
                    var paths = TexPaths(set);
                    var missing = paths.Values.Where(p => AssetDatabase.LoadAssetAtPath<Texture2D>(p) == null).ToList();
                    if (missing.Count > 0)
                    {
                        rep.AppendLine($"    seg{i:00}: 贴图未齐，跳过（缺 {Path.GetFileName(missing[0])} 等 {missing.Count} 张）");
                        skipped++;
                        continue;
                    }
                    rep.AppendLine($"    seg{i:00}: {set}  BC={Path.GetFileName(paths["BC"])}");
                    if (dry) { made++; continue; }

                    var go = BuildQuad(wall.go.transform, container, set, i, n, segW, height,
                                       b, longAxis, thickAxis, faceCoord, baseMat);
                    if (go != null) made++;
                    else skipped++;
                }
            }

            if (!dry)
            {
                // 只改现有材质的 keyword / emission color，不新建材质
                if (baseMat.HasProperty("_EmissionColor"))
                {
                    baseMat.EnableKeyword("_EMISSION");
                    baseMat.SetColor("_EmissionColor", Color.white);
                    EditorUtility.SetDirty(baseMat);
                }
                AssetDatabase.SaveAssets();
                EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            }

            rep.AppendLine($"[S03RackWall] {(dry ? "将创建" : "已创建")} {made} 段，跳过 {skipped}；" +
                           (dry ? "零写盘" : $"场景已保存；贴图根 {TextureRoot}"));
            if (dry) Debug.Log(rep.ToString());
            else Debug.Log(rep.ToString());
        }

        // ------------------------------------------------------------------ 场景查找
        class WallRef
        {
            public GameObject go;
            public MeshRenderer renderer;
        }

        static WallRef FindWall(string name)
        {
            foreach (var mf in Object.FindObjectsOfType<MeshFilter>(true))
            {
                if (mf == null || mf.sharedMesh == null) continue;
                if (mf.gameObject.name != name) continue;
                var r = mf.GetComponent<MeshRenderer>();
                if (r == null) continue;
                if (r.bounds.size.y < 10f) continue;   // 排除同名小块
                return new WallRef { go = mf.gameObject, renderer = r };
            }
            return null;
        }

        static (int longAxis, int thickAxis) AxesOf(Vector3 size)
        {
            int longAxis = size.z >= size.x ? 2 : 0;
            int thickAxis = 3 - longAxis - 1;       // 0/1/2 中剩下的水平轴
            return (longAxis, thickAxis);
        }

        static int SegmentCount(float length)
        {
            int n = Mathf.Max(1, Mathf.RoundToInt(length / TargetSegmentM));
            float w = length / n;
            if (n > 1 && (w < MinSegmentM - 1e-4f || w > MaxSegmentM + 1e-4f))
                Debug.LogWarning($"[S03RackWall] 段宽 {w:F3}m 超出 {MinSegmentM}–{MaxSegmentM}m 目标");
            return n;
        }

        static float FaceCoord(Bounds b, int thickAxis)
        {
            // 走廊中心 x≈52（S03 装饰口径）；朝走廊的一面就是可见面
            float cx = b.center.x;
            bool rightSide = cx > 52f;
            return rightSide ? b.min[thickAxis] - FaceOffset : b.max[thickAxis] + FaceOffset;
        }

        static string ShortName(string wallName) =>
            Regex.Replace(wallName, "^S_03_", "");

        static Dictionary<string, string> TexPaths(string set)
        {
            const string root = TextureRoot;
            string dir = $"{root}/{set}";
            return new Dictionary<string, string>
            {
                ["BC"] = $"{dir}/{set}_BC.png",
                ["N"] = $"{dir}/{set}_N.png",
                ["MS"] = $"{dir}/{set}_MS.png",
                ["AO"] = $"{dir}/{set}_AO.png",
                ["E"] = $"{dir}/{set}_E.png",
            };
        }

        // ------------------------------------------------------------------ 建 quad
        static Transform EnsureContainer()
        {
            foreach (var t in Object.FindObjectsOfType<Transform>(true))
                if (t != null && t.name == ContainerName) return t;
            var go = new GameObject(ContainerName);
            return go.transform;
        }

        static GameObject BuildQuad(Transform parent, Transform container, string set, int index, int count,
                                     float segW, float height, Bounds b,
                                     int longAxis, int thickAxis, float faceCoord,
                                     Material baseMat)
        {
            // 段中心沿长轴均分；高度取墙包围盒的 Y 中心
            float start = b.min[longAxis];
            float center = start + segW * (index + 0.5f);
            var centerPos = b.center;
            centerPos[longAxis] = center;
            centerPos[thickAxis] = faceCoord;

            // 可见面法线（厚度轴方向）
            int nSign = faceCoord < b.center[thickAxis] ? -1 : 1;
            Vector3 normal = Vector3.zero;
            normal[thickAxis] = nSign;
            Vector3 axis = Vector3.zero;
            axis[longAxis] = 1f;
            var rot = Quaternion.LookRotation(normal, Vector3.up);
            // 让贴图 U 始终沿长轴正方向（+X 墙的 local X 会反过来，UV 需要镜像）
            bool flipU = Vector3.Dot(rot * Vector3.right, axis) < 0f;

            var go = new GameObject($"{SegmentPrefix}{ShortName(parent.name)}_{index:00}");
            go.transform.SetParent(container != null ? container : parent.parent, false);
            go.transform.position = centerPos;
            go.transform.rotation = rot;
            go.transform.localScale = Vector3.one;

            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = baseMat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            mr.receiveShadows = true;

            var mesh = new Mesh { name = set };
            float hw = segW * 0.5f, hh = height * 0.5f;
            mesh.SetVertices(new List<Vector3>
            {
                new Vector3(-hw, -hh, 0f),
                new Vector3( hw, -hh, 0f),
                new Vector3(-hw,  hh, 0f),
                new Vector3( hw,  hh, 0f),
            });
            mesh.SetUVs(0, flipU ? new List<Vector2>
            {
                new Vector2(1f, 0f), new Vector2(0f, 0f),
                new Vector2(1f, 1f), new Vector2(0f, 1f),
            } : new List<Vector2>
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(0f, 1f), new Vector2(1f, 1f),
            });
            mesh.SetNormals(new List<Vector3> { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward });
            mesh.SetTangents(new List<Vector4>
            {
                new Vector4(1f, 0f, 0f, -1f), new Vector4(1f, 0f, 0f, -1f),
                new Vector4(1f, 0f, 0f, -1f), new Vector4(1f, 0f, 0f, -1f),
            });
            // Unity 顺时针为正面：BL, TR, BR / TR, TL, BR
            mesh.SetTriangles(new[] { 0, 2, 1, 2, 3, 1 }, 0);
            mesh.RecalculateBounds();
            mf.sharedMesh = mesh;

            var paths = TexPaths(set);
            var bc = LoadTex(paths["BC"]);
            var nrm = LoadTex(paths["N"]);
            var ms = LoadTex(paths["MS"]);
            var ao = LoadTex(paths["AO"]);
            var em = LoadTex(paths["E"]);
            if (bc == null || nrm == null || ms == null || ao == null)
            {
                Debug.LogWarning($"[S03RackWall] {set}: 贴图加载失败，跳过");
                Object.DestroyImmediate(go);
                return null;
            }

            // MaterialPropertyBlock 不序列化，存盘后会丢 → 每段用 CopyAsset 建一份真材质
            const string matRoot = "Assets/Art/Materials/MapSkin/S03RackWall";
            if (!AssetDatabase.IsValidFolder("Assets/Art/Materials/MapSkin"))
                AssetDatabase.CreateFolder("Assets/Art/Materials", "MapSkin");
            if (!AssetDatabase.IsValidFolder(matRoot))
                AssetDatabase.CreateFolder("Assets/Art/Materials/MapSkin", "S03RackWall");
            string matPath = $"{matRoot}/{set}.mat";
            if (AssetDatabase.LoadAssetAtPath<Material>(matPath) == null)
                AssetDatabase.CopyAsset(BaseMaterialPath, matPath);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null) { Debug.LogWarning($"[S03RackWall] {set}: 材质创建失败"); Object.DestroyImmediate(go); return null; }
            mat.SetTexture("_BaseMap", bc);
            mat.SetTexture("_BumpMap", nrm);
            mat.SetTexture("_MetallicGlossMap", ms);
            mat.SetTexture("_OcclusionMap", ao);
            if (em != null) mat.SetTexture("_EmissionMap", em);
            mat.SetColor("_EmissionColor", Color.white);
            mat.SetFloat("_BumpScale", 1f);
            mat.SetFloat("_Metallic", 1f);
            mat.SetFloat("_Smoothness", 1f);
            mat.SetFloat("_OcclusionStrength", 1f);
            mat.SetFloat("_Cull", 0f);   // 背面也可见：分段 quad 的朝向在不同墙上可能相反
            mat.SetVector("_BaseMap_ST", new Vector4(1f, 1f, 0f, 0f));
            if (em != null) { mat.EnableKeyword("_EMISSION"); mat.SetFloat("_EmissionEnabled", 1f); }
            EditorUtility.SetDirty(mat);
            mr.sharedMaterial = mat;
            AssetDatabase.SaveAssets();

            // 原墙渲染器留作碰撞/背面；禁用可见面避免与分段 quad 重叠
            var orig = parent.GetComponent<MeshRenderer>();
            if (orig != null) orig.enabled = false;
            return go;
        }

        static Texture2D LoadTex(string path)
        {
            if (_texCache.TryGetValue(path, out var t)) return t;
            t = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            _texCache[path] = t;
            return t;
        }

        // ------------------------------------------------------------------ 清除
        public static void Clear()
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (!scene.IsValid() || scene.path != ScenePath)
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            int n = 0;
            foreach (var t in Object.FindObjectsOfType<Transform>(true))
            {
                if (t == null) continue;
                if (t.name == ContainerName || t.name.StartsWith(SegmentPrefix))
                {
                    n += t.GetComponentsInChildren<Transform>(true).Length;
                    Object.DestroyImmediate(t.gameObject);
                }
            }
            // 恢复原墙渲染器
            foreach (var name in WallNames)
            {
                var wall = FindWall(name);
                if (wall != null && wall.renderer != null) wall.renderer.enabled = true;
            }
            _texCache.Clear();
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            Debug.Log($"[S03RackWall] 清除 {n} 个分段节点；原墙 MeshRenderer 已恢复");
        }
    }
}
