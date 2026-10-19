using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MapSkin
{
    /// <summary>
    /// 灰盒 UV 重投影：世界三平面（triplanar）投影烘进对象专属 mesh 副本的 UV0。
    ///
    /// 为什么需要：灰盒件全是"图元 Cube + 拉伸 localScale"（比如 1×9×78 的巨墙是 scale 拉出来的），
    /// 原始 UV 是 Cube 的 0..1，直接上贴图 → 整面拉伸成一个巨大像素。重投影后每个对象都拿到
    /// "世界坐标 / 基准尺度"性质的 UV，贴图按真实米数循环，与对象拉伸无关。
    ///
    /// 口径：
    ///   · UV0 = 世界坐标 / 基准尺度；投影平面按**顶点法线主轴**选（±X → ZY 面、±Y → XZ 面、±Z → XY 面）
    ///   · 基准：墙/柱 3.6m、地 4.8m（竖向面用 wallBase，水平面用 floorBase）；菜单/命令行可覆盖
    ///   · 原 UV0 备份进 UV1（mesh.uv2）；**UV1 已被占用时跳过该对象**并记原因（不覆盖可能已存在的烘焙 UV）
    ///   · 每个对象一份 mesh 副本 → `Assets/Art/EnvSkins/UV_<对象名>.asset`（同名加序号），赋回 MeshFilter
    ///   · 不碰 MeshCollider / 场景对象结构；副本顶点法线与源逐位一致 → 光照观感不变
    ///
    /// 与"按三角形主法线"的取舍：本实现按**顶点法线**选平面（不拆点）。理由：灰盒件是硬边体块，
    /// 每个面的顶点法线就等于该三角形法线，两种口径结果**逐位相同**；而按三角形拆点会让顶点数 ×3。
    /// 日志会打印"顶点法线与三角形主法线不一致的三角形数"作为这次等价性的证据。
    ///
    /// 菜单：MapSkin/③ UV 重投影·干跑(只报数)  /  MapSkin/③ UV 重投影·执行(确认后写盘)
    /// 批处理：-executeMethod MapSkin.MapSkinUvBake.DryRun  /  MapSkin.MapSkinUvBake.Run
    ///        可选 -uvbase 3.6,4.8（墙,地）
    /// </summary>
    public static class MapSkinUvBake
    {
        const string ScenePath = "Assets/Scenes/Level1.unity";
        const string OutDir = "Assets/Art/EnvSkins";
        const string BakedPrefix = "UV_";      // 已是本工具产物 → 幂等跳过

        static float WallBase = 3.6f;
        static float FloorBase = 4.8f;
        static bool Rebuild = false;      // -uvrebake 1：覆盖首轮产物（用于调整贴图尺度/口径）

        class Item
        {
            public MeshFilter mf;
            public Mesh src;
            public string name;
            public int verts, tris;
            public int backupChannel;   // 1 = mesh.uv2（UV1）；2 = mesh.uv3（UV2，UV1 被占时的退路）
            public bool rebuild;        // 源网格已是本工具产物 → 沿用已有备份通道，只重写 UV0
            public int tileScale = 1;   // 本对象的贴图格放大倍数（按尺寸自适应）
            public string skip;      // 非空 = 跳过原因
        }

        /// <summary>三角形数上限：超过就别烘（灰色盒件都是几十面；上万的只可能是换皮浮雕网格，
        /// 逐对象复制会把仓库塞成几百 MB）。</summary>
        const int MaxTris = 20000;

        // ---------------------------------------------------------------- 菜单/入口

        [MenuItem("MapSkin/③ UV 重投影·干跑(只报数)")]
        public static void DryRunMenu() => DryRun();

        [MenuItem("MapSkin/③ UV 重投影·执行(确认后写盘)")]
        public static void RunMenu()
        {
            var plan = Plan();
            int n = plan.Count(i => i.skip == null);
            long tris = plan.Where(i => i.skip == null).Sum(i => (long)i.tris);
            if (!EditorUtility.DisplayDialog("UV 重投影",
                    $"将处理 {n} 个 MeshFilter（共 {tris:N0} 三角形），每对象生成一份 mesh 副本写入 {OutDir}/\n" +
                    $"基准：墙/柱 {WallBase}m、地 {FloorBase}m\n\n继续？", "执行", "取消"))
            {
                Debug.Log("[UVBake] 用户取消");
                return;
            }
            Run();
        }

        /// <summary>干跑：只统计与列跳过项，不写任何资产。 -executeMethod MapSkin.MapSkinUvBake.DryRun</summary>
        public static void DryRun()
        {
            ParseArgs();
            var plan = Plan();
            Report(plan, executed: false);
        }

        /// <summary>执行：生成 mesh 副本并赋回。 -executeMethod MapSkin.MapSkinUvBake.Run</summary>
        public static void Run()
        {
            ParseArgs();
            var plan = Plan();
            Report(plan, executed: false);

            if (!AssetDatabase.IsValidFolder(OutDir)) AssetDatabase.CreateFolder("Assets/Art", "EnvSkins");
            var scene = EditorSceneManager.GetActiveScene();
            int done = 0;
            var used = new HashSet<string>();
            var rep = new StringBuilder();

            foreach (var it in plan)
            {
                if (it.skip != null) continue;
                Vector2[] backupOverride = it.rebuild
                    ? (it.backupChannel == 2 ? it.src.uv3 : it.src.uv2)
                    : null;
                var baked = Bake(it.src, it.mf.transform, it.backupChannel, it.tileScale, backupOverride);
                if (baked == null) { rep.AppendLine($"  [失败] {it.name}"); continue; }

                // 文件名必须**逐对象唯一**：同名件（Cube(105) 这种满地重名）如果共用一份资产，
                // 后者会把前者 upsert 掉 —— 前者的 MeshFilter 就指向被删的对象（悬空引用）。
                // 名字里带上世界坐标（UV 本身也是世界坐标的函数，位置稳定 → 名字稳定 → 幂等）。
                var p = it.mf.transform.position;
                string stem = $"{BakedPrefix}{Sanitize(it.name)}_p{Mathf.RoundToInt(p.x)}_{Mathf.RoundToInt(p.y)}_{Mathf.RoundToInt(p.z)}";
                baked.name = stem;
                var path = UniquePath(stem, used);
                if (it.rebuild && AssetDatabase.LoadAssetAtPath<Mesh>(path) != null)
                    AssetDatabase.DeleteAsset(path);
                AssetDatabase.CreateAsset(baked, path);
                it.mf.sharedMesh = baked;
                done++;
                rep.AppendLine($"  {it.name}: {it.verts}v/{it.tris}t 备份→UV{it.backupChannel} 格×{it.tileScale} → {path}");
            }

            AssetDatabase.SaveAssets();
            if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
            var scene2 = EditorSceneManager.GetActiveScene();
            if (scene2.IsValid() && scene2.path == ScenePath) EditorSceneManager.SaveScene(scene2);

            Debug.Log($"[UVBake] 完成 {done} 个对象（基准 墙/柱 {WallBase}m、地 {FloorBase}m），场景已写盘\n{rep}");
        }

        // ---------------------------------------------------------------- 计划

        /// <summary>要统计/改的 MeshFilter 全在场景里，Level1 没开就先开（有未保存改动时不强开）。</summary>
        static void EnsureScene()
        {
            var cur = EditorSceneManager.GetActiveScene();
            if (cur.IsValid() && cur.path == ScenePath) return;
            if (cur.IsValid() && cur.isDirty)
            {
                Debug.LogWarning("[UVBake] 当前场景有未保存改动，不替你切换场景；请在 Level1 打开时跑本工具");
                return;
            }
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        static List<Item> Plan()
        {
            EnsureScene();
            // 只挑"吃到语义贴图的件"：材质槽里有语义表材质（基底或 variant）才需要重投影；
            // 换皮浮雕网格（M_panel_*/M_dark_*，Blender 出件自带 UV）不在语义体系里，重投影只会白烧几百万面
            var skinIds = new HashSet<int>();
            foreach (var n in MapSkinApply.SkinMatBaseNames)
            {
                var p = $"Assets/Art/Models/MikuGandam/materials/{n}.mat";
                var m = AssetDatabase.LoadAssetAtPath<Material>(p);
                if (m != null) skinIds.Add(m.GetInstanceID());
            }
            // 搜索范围要覆盖刻线族/强调件材质（Assets/Art/Materials/MapSkin 下），
            // 否则线分派之后对象材质不再是基底 variant，会被误判"无需重投影"。
            foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets/Art/Materials" }))
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                var file = Path.GetFileNameWithoutExtension(p);
                foreach (var n in MapSkinApply.SkinMatBaseNames)
                    if (file == n || file.StartsWith(n + "_"))
                    { var m2 = AssetDatabase.LoadAssetAtPath<Material>(p); if (m2 != null) skinIds.Add(m2.GetInstanceID()); break; }
            }
            if (skinIds.Count == 0)
                Debug.LogWarning("[UVBake] 语义表材质一个都没加载到，按 0 件处理（先跑 ② 材质换装）");

            var list = new List<Item>();
            foreach (var mf in Object.FindObjectsOfType<MeshFilter>())
            {
                if (mf == null || mf.sharedMesh == null) continue;
                var r = mf.GetComponent<Renderer>();
                if (r == null || !r.enabled) continue;
                var src = mf.sharedMesh;
                var it = new Item
                {
                    mf = mf, src = src, name = mf.gameObject.name,
                    verts = src.vertexCount, tris = src.triangles.Length / 3,
                };

                bool usesSkin = false;
                foreach (var mm in r.sharedMaterials)
                    if (mm != null && skinIds.Contains(mm.GetInstanceID())) { usesSkin = true; break; }
                if (!usesSkin) it.skip = "材质槽里没有语义表材质（不是换装对象，无需重投影）";
                else if (src.name.StartsWith(BakedPrefix) && !Rebuild) it.skip = "已是本工具产物（幂等跳过）";
                else if (it.tris > MaxTris)
                    it.skip = $"三角形 {it.tris:N0} > 上限 {MaxTris:N0}（逐对象复制会撑爆仓库；这类件应自带 UV）";
                else if (src.name.StartsWith(BakedPrefix))
                {
                    // 重建：首轮已把原 UV 备份进 UV1/UV2，沿用原备份通道，不要二次搬家
                    var u3 = src.uv3;
                    it.rebuild = true;
                    it.backupChannel = (u3 != null && u3.Length == src.vertexCount && u3.Length > 0) ? 2 : 1;
                }
                else
                {
                    // 原 UV 备份通道：优先 UV1(uv2)；被占则退到 UV2(uv3)（烘焙已停，uv2 是死数据，但不覆盖它）
                    var uv2 = src.uv2; var uv3 = src.uv3;
                    bool uv1Taken = uv2 != null && uv2.Length == src.vertexCount && uv2.Length > 0;
                    bool uv2Taken = uv3 != null && uv3.Length == src.vertexCount && uv3.Length > 0;
                    if (!uv1Taken) it.backupChannel = 1;
                    else if (!uv2Taken) it.backupChannel = 2;
                    else it.skip = "UV1 与 UV2 都已被占用（源网格带 uv2+uv3），无处备份原 UV → 需人工拍板";
                }
                it.tileScale = TileScale(src, mf.transform);
                list.Add(it);
            }
            list.Sort((a, b) => b.tris.CompareTo(a.tris));
            return list;
        }

        static void Report(List<Item> plan, bool executed)
        {
            int ok = plan.Count(i => i.skip == null);
            long tris = plan.Where(i => i.skip == null).Sum(i => (long)i.tris);
            long verts = plan.Where(i => i.skip == null).Sum(i => (long)i.verts);
            int c1 = plan.Count(i => i.skip == null && i.backupChannel == 1);
            int c2 = plan.Count(i => i.skip == null && i.backupChannel == 2);
            var rep = new StringBuilder();
            rep.AppendLine($"[UVBake·{(executed ? "执行" : "干跑")}{(Rebuild ? "·重建" : "")}] 待处理 MeshFilter {ok} 个；" +
                           $"总三角形 {tris:N0}、总顶点 {verts:N0}；基准 墙/柱 {WallBase}m、地 {FloorBase}m；" +
                           $"原 UV 备份：UV1 {c1} 件、UV1 被占改走 UV2 {c2} 件");
            var kHist = plan.Where(i => i.skip == null).GroupBy(i => i.tileScale)
                            .OrderBy(g => g.Key).Select(g => $"×{g.Key}: {g.Count()} 件");
            rep.AppendLine($"  贴图格自适应：{string.Join("    ", kHist)}（巨件按 6×基准 取 1–4 倍格距）");
            rep.AppendLine($"  待处理清单（全部 {ok} 件，按三角形降序）：");
            foreach (var it in plan.Where(i => i.skip == null))
                rep.AppendLine($"    {it.name}: {it.verts}v/{it.tris}t 备份→UV{it.backupChannel}");
            var skipped = plan.Where(i => i.skip != null).ToList();
            rep.AppendLine($"  跳过 {skipped.Count} 个：");
            foreach (var g in skipped.GroupBy(i => i.skip))
                rep.AppendLine($"    [{g.Count()} 个] {g.Key}   例: {string.Join(", ", g.Take(3).Select(i => i.name))}");
            Debug.Log(rep.ToString());
        }

        // ---------------------------------------------------------------- 重投影本体

        static Mesh Bake(Mesh src, Transform t, int backupChannel, int tileScale, Vector2[] backupUvOverride)
        {
            var vs = src.vertices;
            var ns = src.normals;
            var uvs = src.uv;
            var tangents = src.tangents;
            bool hasN = ns != null && ns.Length == vs.Length;
            bool hasUV = uvs != null && uvs.Length == vs.Length;
            bool hasTan = tangents != null && tangents.Length == vs.Length;

            var m = t.localToWorldMatrix;
            var nm = m.inverse.transpose;
            var newUV = new Vector2[vs.Length];
            int mismatch = 0;
            // 贴图格按对象尺寸自适应：巨墙（100m+）用 3.6m 格会平铺成"铁丝网"，
            // 这里按 max(尺寸)/(6×基准) 取 1–4 倍格距 —— 线条世界宽度随之变粗，读作建筑尺度。
            float wallBase = WallBase * Mathf.Max(1, tileScale);
            float floorBase = FloorBase * Mathf.Max(1, tileScale);

            for (int i = 0; i < vs.Length; i++)
            {
                var wp = m.MultiplyPoint3x4(vs[i]);
                Vector3 n;
                if (hasN)
                {
                    n = nm.MultiplyVector(ns[i]);
                    if (n.sqrMagnitude < 1e-12f) n = Vector3.up; else n.Normalize();
                }
                else n = Vector3.up;

                float ax = Mathf.Abs(n.x), ay = Mathf.Abs(n.y), az = Mathf.Abs(n.z);
                if (ay >= ax && ay >= az)      // 水平面 → 投影到 XZ，地基准
                    newUV[i] = new Vector2(wp.x / floorBase, wp.z / floorBase);
                else if (ax >= az)             // 竖面 ±X → 投影到 ZY，墙基准
                    newUV[i] = new Vector2(wp.z / wallBase, wp.y / wallBase);
                else                           // 竖面 ±Z → 投影到 XY，墙基准
                    newUV[i] = new Vector2(wp.x / wallBase, wp.y / wallBase);
            }

            // 等价性证据：顶点法线主轴 vs 三角形主法线的分歧计数（灰盒硬边体块应为 0）
            var tris = src.triangles;
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                var a = m.MultiplyPoint3x4(vs[tris[i]]);
                var b = m.MultiplyPoint3x4(vs[tris[i + 1]]);
                var c = m.MultiplyPoint3x4(vs[tris[i + 2]]);
                var tn = Vector3.Cross(b - a, c - a);
                if (tn.sqrMagnitude < 1e-12f) continue;
                tn.Normalize();
                int axisTri = DominantAxis(tn);
                int A = DominantAxis(m.MultiplyVector(hasN ? ns[tris[i]] : Vector3.up).normalized);
                int B = DominantAxis(m.MultiplyVector(hasN ? ns[tris[i + 1]] : Vector3.up).normalized);
                int C = DominantAxis(m.MultiplyVector(hasN ? ns[tris[i + 2]] : Vector3.up).normalized);
                bool triPlaneSame = (A == B && B == C && A == axisTri);
                // 顶点法线口径：三点用各自平面 —— 只要三点平面都与三角形平面同类就算等价
                if (!triPlaneSame) mismatch++;
            }

            var dst = Object.Instantiate(src);
            dst.name = BakedPrefix + Sanitize(src.name);
            dst.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;   // 长墙/巨件防 65k 溢出
            dst.SetVertices(vs);
            if (hasN) dst.SetNormals(ns);
            if (hasTan) dst.SetTangents(tangents);
            dst.SetUVs(0, newUV);
            var backupSrc = backupUvOverride != null && backupUvOverride.Length == vs.Length ? backupUvOverride : uvs;
            if (backupSrc != null && backupSrc.Length == vs.Length)
                dst.SetUVs(backupChannel, backupSrc);   // 原 UV 备份进 UV1（UV1 被占时退到 UV2）
            dst.SetTriangles(tris, 0);
            dst.RecalculateBounds();
            if (mismatch > 0)
                Debug.LogWarning($"[UVBake] {src.name}: {mismatch} 个三角形的顶点法线平面与三角形主法线不同类 " +
                                 "（顶点法线近 45° 对角）——这些面按顶点法线取平面，UV 会有斜向拉伸");
            return dst;
        }

        static int DominantAxis(Vector3 v)
        {
            float ax = Mathf.Abs(v.x), ay = Mathf.Abs(v.y), az = Mathf.Abs(v.z);
            if (ay >= ax && ay >= az) return 1;
            return ax >= az ? 0 : 2;
        }

        /// <summary>
        /// 贴图格放大倍数：按对象最大世界尺寸 / (6×基准) 取整，夹 1–4。
        /// 132m 巨墙 → ×4（14.4m 一格）≈9 次平铺；40m 墙 → ×2；常规 3–8m 件 → ×1。
        /// </summary>
        static int TileScale(Mesh src, Transform t)
        {
            var b = src.bounds.size;
            var ls = t.lossyScale;
            float maxDim = Mathf.Max(Mathf.Abs(b.x * ls.x),
                            Mathf.Max(Mathf.Abs(b.y * ls.y), Mathf.Abs(b.z * ls.z)));
            int k = Mathf.RoundToInt(maxDim / (WallBase * 6f));
            return Mathf.Clamp(k, 1, 4);
        }

        /// <summary>同一路径重跑时避让（序号只在本轮内分配给不同对象；已烘对象由 src.name 前缀检查跳过）。</summary>
        static string UniquePath(string stem, HashSet<string> used)
        {
            var path = $"{OutDir}/{stem}.asset";
            int n = 2;
            while (used.Contains(path)) path = $"{OutDir}/{stem}_{n++}.asset";
            used.Add(path);
            return path;
        }

        static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unnamed";
            var sb = new StringBuilder(s.Length);
            foreach (var c in s) sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' ? c : '_');
            return sb.ToString();
        }

        static void ParseArgs()
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-uvrebake" && i + 1 < args.Length)
                    Rebuild = args[i + 1] == "1";
                if (args[i] == "-uvbase" && i + 1 < args.Length)
                {
                    var p = args[i + 1].Split(',');
                    if (p.Length >= 1 && float.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w) && w > 0.01f)
                        WallBase = w;
                    if (p.Length >= 2 && float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fl) && fl > 0.01f)
                        FloorBase = fl;
                }
            }
        }
    }
}
