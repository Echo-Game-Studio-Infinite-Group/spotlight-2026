// HstKitBaker v3 —— 场景终态拆零件：组合体装配模式。
// 菜单: HST/Kit/拆分全部场景(幂等重建) | 生成 Catalog 场景 | 机位出图 | 结构分析 | 族包围盒分析
// 批处理: HST.KitBaker.BakeAll / BakeCatalog / RenderCamStills / AnalyzeStructure / AnalyzeFamilyBounds
// 规格来源：人工判读四场景机位图 + 族世界包围盒（v1 按 mesh 粉碎成 1317 件是错误口径，已废弃——
//   正确口径：一面墙/一组道具=一个组合体 prefab；同款单体归并出代表件；窗外造景不入册）。
//   组合体内的子件保留网格烘死的原始摆位，root 归零出厂，地编整体拖放。
// 口径权威：外层 docs/技术/地编Kit拆分与交付.md。分组规格改这里重跑，勿手改产物。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace HST
{
    public static class KitBaker
    {
        const string OutRoot = "Assets/Art/Environment/HST_Kit/Prefabs/HST_Kit";
        const string AuditPath = OutRoot + "/parts_audit.md";

        // 拆分范围：四个交付场景（答案场景本体不改动）
        static readonly string[] Scenes =
        {
            "Assets/Art/Environment/HST_Kit/Scenes/SC_Atrium_Military.unity",
            "Assets/Art/Environment/HST_Kit/Scenes/SC_Atrium_Concept.unity",
            "Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_Pragmata.unity",
            "Assets/Art/Environment/HST_Kit/Scenes/SC_CityFacade_LookDev.unity",
        };
        static readonly Dictionary<string, string> SceneDir = new Dictionary<string, string>
        {
            ["SC_Atrium_Military"] = "军用厅",
            ["SC_Atrium_Concept"] = "中庭",
            ["SC_Corridor_Pragmata"] = "走廊",
            ["SC_CityFacade_LookDev"] = "城市",
        };

        // 黑名单前缀：实灯/机位/后处理/辅助件/参考人形/标记点/ProBuilder内嵌/战斗侧预制体
        static readonly string[] SkipPrefix =
        {
            "L_", "CAM_", "HST_", "PP_", "cm", "Main Camera", "Virtual Camera", "Wwise",
            "man", "bdot", "pb_", "Enemy", "Player", "Bullet", "DamagePopup", "Audio",
            "SM_Human", "CAT_", "lbl_",
        };

        // ============ 分组规格（定稿理念：按建筑构件出件、附属随主体、同款留一、
        //   等距/聚类、排除唯一标准是窗外、道具按组、不要的点名剔除） ============
        // ClusterGap>0 = 距离聚类切多件；ModuleIdRegex = 先按名内模块 ID 聚成模块再聚类（桌系 _000..）；
        // SegmentLength>0 = 沿主轴切 N 米段取件数最多的一段（连贯的真实小样，走廊墙板/天花/地板用）。
        class Spec { public string Name; public string[] Families; public bool SplitBySection; public bool Single; public bool EvenSpacing; public float ClusterGap; public string ModuleIdRegex; public float SegmentLength; }

        static readonly Dictionary<string, Spec[]> SceneSpecs = new Dictionary<string, Spec[]>
        {
            // 军用厅：意义不明的件全部剔除（用户裁定：圆柱/门框/吊车轨/细柱/推车/枪架/靠板/地面标线/标牌/亭顶），
            //   幕墙只留幕墙本体（端墙另算已去掉）。
            ["SC_Atrium_Military"] = new[]
            {
                new Spec{ Name="玻璃幕墙", Families=new[]{"V6_CW"}},
                new Spec{ Name="桁架天花", Families=new[]{"V6_Brace","V6_LatA","V6_LatB","V6_Ceil","V6_Lens","V6_Disc"}}, // 灯板/光盘随天花
                new Spec{ Name="夹层", Families=new[]{"V6_Mezz"}},
                new Spec{ Name="集装箱垛", Families=new[]{"V6_Cont"}},
                new Spec{ Name="座位排", Families=new[]{"V6_Bench"}, EvenSpacing=true},
                new Spec{ Name="侧墙", Families=new[]{"V6_Wall"}, Single=true},
                new Spec{ Name="地坪", Families=new[]{"V6_Floor"}},
            },
            // 中庭：只收玩家封闭空间（室内盒=中心点判定，巨板蹭盒不再误收）。装饰随主体：
            //   橙条/镶嵌条并工位舱，警示条并栏杆，杂罐扔（用户裁定）；标牌按块拆（ClusterGap）。
            ["SC_Atrium_Concept"] = new[]
            {
                // 房壳五件（用户逐件点名，m1 一律排除）；附属随主体按 Blender 端设计源码
                //   （concept_atrium.py：inlay/orange 贴地板、rstrip 贴后墙、roomlight 贴天花板）：
                new Spec{ Name="室内地板", Families=new[]{"SM_panels_room_floor_m0","SM_panels_room_floor_m2","SM_panels_room_floor_m3","inlay_","orange_"}},
                new Spec{ Name="室内墙壁甲", Families=new[]{"SM_panels_room_side_000_m0"}},
                new Spec{ Name="室内墙壁乙", Families=new[]{"SM_panels_room_back_m0","SM_panels_room_back_m3","rstrip_"}}, // 后墙竖灯条随墙
                new Spec{ Name="室内墙壁丙", Families=new[]{"SM_panels_room_side_001_m0","SM_panels_room_side_001_m3"}},
                new Spec{ Name="室内天花板", Families=new[]{"SM_panels_room_ceil_m0","roomlight_","roomlamp_"}}, // 灯随天花
                // 楼梯按用户裁定删除（后续单独设计，stair_/mezz_stair 不入册）
                new Spec{ Name="工位舱", Families=new[]{"pod","gfbeam_"}},                // 玻璃房壳+顶梁
                new Spec{ Name="工位桌组", Families=new[]{"deskleg","desk_","deskleg2_","offG_","holodesk_","console"}, ClusterGap=5f, ModuleIdRegex=@"\d{3}"},
                new Spec{ Name="栏杆", Families=new[]{"mezz_rail","mezz","mezz_top","fhr","fgr","haz_"}},
                new Spec{ Name="标牌", Families=new[]{"SM_sign"}, ClusterGap=2f},
            },
            // 走廊：4 种件——墙板(单侧)/天花板(含灯条)/地板均切 6m 连贯段（设计小样，去 60m 长墙）；
            //   板条箱独立；后墙/附属/机架/计算机不入册（用户裁定）。
            ["SC_Corridor_Pragmata"] = new[]
            {
                new Spec{ Name="墙板", Families=new[]{"ARCH_Panel_-","ARCH_Panel_18","ARCH_Panel_19"}, SegmentLength=6f}, // 单侧(+z)+两块独特板
                new Spec{ Name="天花板", Families=new[]{"ARCH_CoveTop","ARCH_CeilBand"}, SegmentLength=6f}, // CoveTop 顶封板+CeilBand 灯带（顶在 y5.2，走廊加高后真实位置）
                new Spec{ Name="地板", Families=new[]{"ARCH_Floor"}, SegmentLength=6f},
                // 2026-10-14 裁掉："板条箱堆"（FORGE_Crate 导入件）不再入册 —— 用户裁定原板条箱全部删掉重做，
                //   可破坏箱子改由标准件库的塑料周转箱/能量罐承担。答案场景里的 FORGE 箱本体现状不动。
            },
            // 城市：楼型=一体件（楼体+日/暮窗面同厂），改组合体制
        };
        static readonly Dictionary<string, Spec[]> CityAssemblies =
            new Dictionary<string, Spec[]>
        {
            ["SC_CityFacade_LookDev"] = new[]
            {
                new Spec{ Name="楼_TowerBlade", Families=new[]{"SM_ME_TowerBlade"}},
                new Spec{ Name="楼_TowerCham", Families=new[]{"SM_ME_TowerCham"}},
                new Spec{ Name="楼_TowerCyl", Families=new[]{"SM_ME_TowerCyl"}},
                new Spec{ Name="楼_TowerDS", Families=new[]{"SM_ME_TowerDS"}},
                new Spec{ Name="楼_TowerEll", Families=new[]{"SM_ME_TowerEll"}},
                new Spec{ Name="楼_TowerStep", Families=new[]{"SM_ME_TowerStep"}},
                new Spec{ Name="楼_Podium", Families=new[]{"SM_ME_Podium"}},
                new Spec{ Name="楼_SlabRC", Families=new[]{"SM_ME_SlabRC"}},
                new Spec{ Name="楼_SkyTall", Families=new[]{"SM_ME_SkyTall"}},
                new Spec{ Name="楼_SkyStep", Families=new[]{"SM_ME_SkyStep"}},
            },
        };

        // 各场景单件族（签名归并出代表件）+ 中文名映射（prefab/图片名直接中文）
        static readonly Dictionary<string, string[]> SingletonFamilies = new Dictionary<string, string[]>
        {
            ["SC_Atrium_Military"] = new[]{ "V6_Bollard" },
            ["SC_Atrium_Concept"] = Array.Empty<string>(),
            ["SC_Corridor_Pragmata"] = Array.Empty<string>(),
            ["SC_CityFacade_LookDev"] = Array.Empty<string>(),
        };
        // 网格名黑名单：亭顶（KioskTop）等按用户裁定跳过
        static readonly string[] SkipMeshNames = { "V6_KioskTop" };
        static readonly (string prefix, string cn)[] CnNames =
        {
            ("V6_Bollard", "柱墩"),
        };
        static string CnNameOf(string meshName)
        {
            foreach (var (prefix, cn) in CnNames)
                if (meshName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return cn;
            return meshName;
        }

        class AsmEntry
        {
            public string Scene; public string Name; public string Section; public Spec Spec;
            public List<GameObject> Parts = new List<GameObject>();
            public long Tris; public HashSet<string> Mats = new HashSet<string>();
            public string Path; public bool Baked;
        }
        class SingEntry
        {
            public string Scene; public string Fam; public string Sig; public string MeshName;
            public GameObject Proto; public long Tris; public List<string> Mats;
            public Bounds Local; public Vector3 WorldSize; public int Instances;
            public HashSet<string> Scenes = new HashSet<string>();
            public string Path; public bool Baked;
        }

        [MenuItem("HST/Kit/拆分全部场景(幂等重建)")]
        public static void BakeAll()
        {
            // 幂等重建只清产物，保留手写的 IMPORT.md
            if (Directory.Exists(OutRoot))
                foreach (var d in Directory.GetDirectories(OutRoot))
                    AssetDatabase.DeleteAsset(d);
            if (AssetDatabase.DeleteAsset(AuditPath))
                Debug.Log($"[Kit] 清空旧产物 {OutRoot}");
            Directory.CreateDirectory(OutRoot);

            var asms = new Dictionary<string, AsmEntry>();
            var sings = new Dictionary<string, SingEntry>();
            var unassigned = new SortedDictionary<string, int>(); // 族: 件数 —— 造景/辅助判读清单
            var auditRows = new List<string>();

            foreach (var s in Scenes)
            {
                var scene = EditorSceneManager.OpenScene(s, OpenSceneMode.Single);
                var tag = Path.GetFileNameWithoutExtension(s);
                foreach (var root in scene.GetRootGameObjects())
                    Collect(root.transform, tag, asms, sings, unassigned);

                // 场景存活期内即时写盘（切场景即销毁对象）
                foreach (var e in asms.Values.Where(x => x.Scene == tag && !x.Baked))
                {
                    if (e.Spec != null && e.Spec.SegmentLength > 0f && e.Parts.Count > 1)
                    {
                        // 沿主轴切 N 米段，取件数最多的一段（连贯的真实小样）
                        e.Parts = BestSegment(e.Parts, e.Spec.SegmentLength);
                    }
                    if (e.Spec != null && e.Spec.ClusterGap > 0f && e.Parts.Count > 1)
                    {
                        var i = 0;
                        foreach (var cluster in SplitClusters(e.Parts, e.Spec.ClusterGap, e.Spec.ModuleIdRegex))
                        {
                            var ce = new AsmEntry
                            {
                                Scene = e.Scene, Name = $"{e.Name}_{++i}", Section = e.Section, Spec = e.Spec,
                                Parts = cluster, Tris = cluster.Sum(MeshTris),
                                Mats = new HashSet<string>(cluster.SelectMany(p =>
                                    p.GetComponentsInChildren<MeshRenderer>(true)
                                     .SelectMany(r2 => r2.sharedMaterials).Where(m => m).Select(m => m.name))),
                            };
                            ce.Path = WriteAssembly(ce);
                            ce.Baked = true;
                            auditRows.Add(AsmRow(ce));
                        }
                        e.Baked = true;
                        continue;
                    }
                    e.Path = WriteAssembly(e);
                    e.Baked = true;
                    auditRows.Add(AsmRow(e));
                }
                foreach (var e in sings.Values.Where(x => x.Scene == tag && !x.Baked))
                {
                    e.Path = WriteSingleton(e);
                    e.Baked = true; e.Proto = null;
                }
                Debug.Log($"[Kit] {tag}: 组合体 {asms.Values.Count(x => x.Scene == tag)} / 单件 {sings.Values.Count(x => x.Scene == tag)} / 未入册 {unassigned.Count}");
            }
            foreach (var e in sings.Values.OrderBy(x => x.Scene).ThenBy(x => x.Fam).ThenBy(x => x.Sig))
                auditRows.Add(SingRow(e));

            File.WriteAllText(AuditPath, AuditHeader(asms, sings, unassigned)
                + string.Join("\n", auditRows) + UnassignedBlock(unassigned), new UTF8Encoding(true));
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[Kit] 装配完成: 组合体 {asms.Count} + 单件 {sings.Count} -> {OutRoot}; 未入册族 {unassigned.Count}; 审计 {AuditPath}");
        }

        // ---------- 收集 ----------
        static Spec[] SpecsFor(string sceneTag) =>
            SceneSpecs.GetValueOrDefault(sceneTag) ?? CityAssemblies.GetValueOrDefault(sceneTag) ?? Array.Empty<Spec>();

        // 概念厅室内盒（InteriorDesk 机位周边，邻域分析定界）：盒内=室内，盒外=窗外造景
        static readonly Bounds IndoorBox = new Bounds(new Vector3(0f, -1.5f, 1.5f), new Vector3(33f, 26f, 27f));

        static void Collect(Transform t, string sceneTag, Dictionary<string, AsmEntry> asms,
            Dictionary<string, SingEntry> sings, SortedDictionary<string, int> unassigned)
        {
            var go = t.gameObject;
            var r = go.GetComponent<MeshRenderer>();
            if (r != null)
            {
                var mesh = go.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null || Skip(go, mesh)) return;

                // 概念厅：室内盒外=窗外造景，记账不入册（按包围盒中心点判定——巨板蹭盒不算室内）
                if (sceneTag == "SC_Atrium_Concept")
                {
                    var mrb = go.GetComponent<MeshRenderer>()?.bounds;
                    if (mrb.HasValue && !IndoorBox.Contains(mrb.Value.center))
                    {
                        var fk2 = Fam(go.name);
                        unassigned[fk2] = unassigned.GetValueOrDefault(fk2) + 1;
                        return;
                    }
                }

                var spec = SpecsFor(sceneTag).FirstOrDefault(sp => sp.Families.Any(f => go.name.StartsWith(f)));
                if (spec != null)
                {
                    var key = $"{sceneTag}|{spec.Name}|{(spec.SplitBySection ? SectionOf(t) : "")}";
                    if (!asms.TryGetValue(key, out var e))
                        asms[key] = e = new AsmEntry
                        {
                            Scene = sceneTag, Name = spec.Name,
                            Section = spec.SplitBySection ? SectionOf(t) : "", Spec = spec
                        };
                    if (e.Spec.Single && e.Parts.Count > 0) return; // Single：同款其余实例不入册也不记账
                    e.Parts.Add(go);
                    e.Tris += MeshTris(go);
                    foreach (var m in r.sharedMaterials) if (m) e.Mats.Add(m.name);
                    return;
                }
                var fam = SingletonFamilies.GetValueOrDefault(sceneTag)?.FirstOrDefault(f => go.name.StartsWith(f));                if (fam != null)
                {
                    var sig = $"{mesh.vertexCount}v/{mesh.triangles.Length / 3}t/{mesh.bounds.size.x:F2}x{mesh.bounds.size.y:F2}x{mesh.bounds.size.z:F2}";
                    var key = $"{sceneTag}|{fam}|{sig}";
                    if (!sings.TryGetValue(key, out var e2))
                    {
                        var wbv = r.bounds.size;
                        sings[key] = e2 = new SingEntry
                        {
                            Scene = sceneTag, Fam = fam, Sig = sig, Proto = go,
                            MeshName = mesh.name,
                            Tris = MeshTris(go), Local = LocalBounds(go),
                            WorldSize = wbv,
                            Mats = r.sharedMaterials.Where(m => m).Select(m => m.name).Distinct().ToList(),
                        };
                    }
                    e2.Instances++;
                    return;
                }
                var fk = Fam(go.name);
                unassigned[fk] = unassigned.GetValueOrDefault(fk) + 1;
            }
            for (var i = t.childCount - 1; i >= 0; i--) Collect(t.GetChild(i), sceneTag, asms, sings, unassigned);
        }


        static bool Skip(GameObject go, Mesh mesh)
        {
            if (go.GetComponent<Light>() != null || PrefabUtility.IsPartOfPrefabInstance(go)) return true;
            if (go.CompareTag("EditorOnly")) return true;
            var n = go.name;
            if (SkipPrefix.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return true;
            if (SkipMeshNames.Any(p => mesh.name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return true;
            if (new[] { "Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad" }.Contains(mesh.name)) return true;
            // 世界尺寸退化件（<5mm 的烘焙残渣）才跳过；UV2 网格是重烘工艺件，AABB 元数据过期但几何有效，不跳
            var wb = go.GetComponent<MeshRenderer>()?.bounds;
            if (wb.HasValue && Mathf.Max(wb.Value.size.x, wb.Value.size.y, wb.Value.size.z) < 0.005f) return true;
            return !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out string _, out long _); // 内嵌网格无法跨场景引用
        }

        static string SectionOf(Transform t)
        {
            while (t != null)
            {
                if (t.name.StartsWith("Section_")) return t.name.Substring("Section_".Length);
                t = t.parent;
            }
            return "";
        }

        // ---------- 写盘 ----------
        static string MeshKeyOf(GameObject go)
        {
            var m = go.GetComponent<MeshFilter>()?.sharedMesh;
            return m != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(m, out string g, out long l)
                ? $"{g}:{l}" : go.name;
        }

        // 贴合聚类(0.5m 单链)取最大连片（保证同一面墙）→ 剔离群 → 沿主轴切段长取最满段
        static List<GameObject> BestSegment(List<GameObject> parts, float segLen)
        {
            Vector3 C(GameObject p)
            {
                var b = new Bounds(); var f = true;
                foreach (var r in p.GetComponentsInChildren<Renderer>())
                { if (f) { b = r.bounds; f = false; } else b.Encapsulate(r.bounds); }
                return b.center;
            }
            var c = parts.ToDictionary(p => p, C);
            List<List<GameObject>> groups = new List<List<GameObject>>();
            foreach (var p in parts)
            {
                List<GameObject> best = null; var bd = 2.5f; // 板中心距~1.5m 需连上；异面墙 4m+ 断开
                foreach (var g in groups)
                    foreach (var q in g)
                    {
                        var d = Vector3.Distance(c[p], c[q]);
                        if (d <= bd) { bd = d; best = g; }
                    }
                if (best == null) { best = new List<GameObject>(); groups.Add(best); }
                best.Add(p);
            }
            var big = groups.OrderByDescending(g => g.Count).First();
            var cen = big.Aggregate(Vector3.zero, (a, p) => a + c[p]) / big.Count;
            var kept = big.Where(p => Vector3.Distance(c[p], cen) <= segLen * 1.5f).ToList();
            if (kept.Count < 2) kept = big;
            // 主轴切段，取最满段
            var spread = Vector3.zero;
            var f0 = C(kept[0]);
            foreach (var p in kept)
            {
                var cc = c[p];
                spread.x = Mathf.Max(spread.x, Mathf.Abs(cc.x - f0.x));
                spread.y = Mathf.Max(spread.y, Mathf.Abs(cc.y - f0.y));
                spread.z = Mathf.Max(spread.z, Mathf.Abs(cc.z - f0.z));
            }
            Func<GameObject, float> key = spread.x >= spread.z
                ? (Func<GameObject, float>)(p => c[p].x) : (p => c[p].z);
            List<GameObject> bestSeg = null; var bestN = -1;
            foreach (var seed in kept.OrderBy(key).ToList())
            {
                var bin = kept.Where(p => Mathf.Abs(key(p) - key(seed)) <= segLen).ToList();
                if (bin.Count > bestN) { bestN = bin.Count; bestSeg = bin; }
            }
            return bestSeg ?? kept;
        }

        // 先按模块 ID 捆单元（如桌系 _000..），再对单元做单链距离聚类（全 3D，离群另算一件）
        static List<List<GameObject>> SplitClusters(List<GameObject> parts, float gap, string moduleIdRegex)
        {
            var units = new List<List<GameObject>>();
            if (!string.IsNullOrEmpty(moduleIdRegex))
            {
                var mod = new Dictionary<string, List<GameObject>>();
                foreach (var p in parts)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(p.name, moduleIdRegex);
                    var id = m.Success ? m.Value : "";
                    if (!mod.TryGetValue(id, out var l)) mod[id] = l = new List<GameObject>();
                    l.Add(p);
                }
                units = mod.Values.ToList();
            }
            else
                units = parts.Select(p => new List<GameObject> { p }).ToList();

            var cs = units.ToDictionary(u => u, u =>
            {
                var b = new Bounds(); var f = true;
                foreach (var p in u)
                    foreach (var r in p.GetComponentsInChildren<Renderer>())
                    { if (f) { b = r.bounds; f = false; } else b.Encapsulate(r.bounds); }
                return b.center;
            });
            var merged = units.Select(u => new List<GameObject>(u)).ToList();
            var centers = merged.Select(u =>
            {
                var b = new Bounds(); var f = true;
                foreach (var r in u.SelectMany(p => p.GetComponentsInChildren<Renderer>()))
                { if (f) { b = r.bounds; f = false; } else b.Encapsulate(r.bounds); }
                return b.center;
            }).ToList();
            // 单链：任两簇中心距离 ≤ gap 即合并，直到稳定
            bool mergedAny = true;
            while (mergedAny)
            {
                mergedAny = false;
                for (var i = 0; i < merged.Count && !mergedAny; i++)
                    for (var j = i + 1; j < merged.Count && !mergedAny; j++)
                        if (Vector3.Distance(centers[i], centers[j]) <= gap)
                        {
                            merged[i].AddRange(merged[j]);
                            centers[i] = (centers[i] + centers[j]) / 2f;
                            merged.RemoveAt(j); centers.RemoveAt(j);
                            mergedAny = true;
                        }
            }
            return merged;
        }

        static string WriteAssembly(AsmEntry e)
        {
            var rootGo = new GameObject(PrefabName(e));
            var clones = new List<GameObject>();
            foreach (var part in e.Parts)
            {
                var inst = UnityEngine.Object.Instantiate(part);
                inst.transform.SetParent(rootGo.transform, true); // 保留烘死的原始摆位
                clones.Add(inst);
            }
            // 座位排类：主轴等距重排（间距=相邻间距中位数，取 5cm 步进），其余轴不动
            if (e.Spec != null && e.Spec.EvenSpacing && clones.Count > 2)
            {
                Vector3 Center(GameObject g)
                {
                    var b = new Bounds(); var f = true;
                    foreach (var rr in g.GetComponentsInChildren<Renderer>())
                    { if (f) { b = rr.bounds; f = false; } else b.Encapsulate(rr.bounds); }
                    return b.center;
                }
                var spread = new Vector3();
                var cs = clones.Select(Center).ToList();
                foreach (var c in cs) { spread.x = Mathf.Max(spread.x, Mathf.Abs(c.x - cs[0].x)); spread.y = Mathf.Max(spread.y, Mathf.Abs(c.y - cs[0].y)); spread.z = Mathf.Max(spread.z, Mathf.Abs(c.z - cs[0].z)); }
                var axis = spread.x >= spread.z ? Vector3.right : Vector3.forward;
                int Axis(Vector3 v) => axis.x > 0 ? Mathf.RoundToInt(v.x * 20) : Mathf.RoundToInt(v.z * 20);
                var ordered = clones.OrderBy(c => Axis(Center(c))).ToList(); // 按主轴键排序（Vector3 不可比较）
                var keys = ordered.Select(c => Axis(Center(c))).ToList();
                var diffs = new List<int>();
                for (var i = 1; i < keys.Count; i++) diffs.Add(keys[i] - keys[i - 1]);
                diffs.Sort();
                var pitch = diffs[diffs.Count / 2]; // 中位间距(5cm 步进)
                var origin = Center(ordered[0]);
                for (var i = 0; i < ordered.Count; i++)
                {
                    var p = ordered[i].transform.position;
                    var target = origin + axis * (pitch * i);
                    if (axis.x > 0) ordered[i].transform.position = new Vector3(target.x, p.y, p.z);
                    else ordered[i].transform.position = new Vector3(p.x, p.y, target.z);
                }
            }
            // 每个渲染子件补碰撞（有则不动）——组合体整体拖入即用
            foreach (var inst in clones)
                foreach (var mr in inst.GetComponentsInChildren<MeshRenderer>(true))
                    if (mr.GetComponentsInChildren<Collider>(true).Length == 0)
                    {
                        var b = mr.bounds;
                        var bc = mr.gameObject.AddComponent<BoxCollider>();
                        bc.center = mr.transform.InverseTransformPoint(b.center);
                        bc.size = mr.transform.InverseTransformVector(b.size);
                    }
            foreach (var inst in clones)
            {
                GameObjectUtility.SetStaticEditorFlags(inst, 0);
                inst.isStatic = false;
            }
            var dir = $"{OutRoot}/{SceneDir[e.Scene]}";
            Directory.CreateDirectory(dir);
            var path = $"{dir}/{rootGo.name}.prefab";
            for (var i = 2; File.Exists(path); i++) path = $"{dir}/{rootGo.name}_{i}.prefab";
            PrefabUtility.SaveAsPrefabAsset(rootGo, path);
            UnityEngine.Object.DestroyImmediate(rootGo);
            return path;
        }

        static string WriteSingleton(SingEntry e)
        {
            var clone = UnityEngine.Object.Instantiate(e.Proto);
            // 中文名直出（映射表）；同族不同设计靠签名分件，撞名加序号
            clone.name = $"P_{CnNameOf(e.MeshName)}";
            // 不归零 transform：军用厅等"小网格×大缩放"件靠 transform 携带尺寸，归零=几何缩水
            clone.isStatic = false;
            GameObjectUtility.SetStaticEditorFlags(clone, 0);
            if (clone.GetComponentsInChildren<Collider>(true).Length == 0)
            {
                var bc = clone.AddComponent<BoxCollider>();
                bc.center = e.Local.center; bc.size = e.Local.size; // 本地值，随 transform 缩放还原到世界尺寸
            }
            var dir = $"{OutRoot}/{SceneDir[e.Scene]}/单件";
            Directory.CreateDirectory(dir);
            var path = $"{dir}/{clone.name}.prefab";
            for (var i = 2; File.Exists(path); i++) path = $"{dir}/{clone.name}_{i}.prefab";
            PrefabUtility.SaveAsPrefabAsset(clone, path);
            UnityEngine.Object.DestroyImmediate(clone);
            return path;
        }

        static string PrefabName(AsmEntry e)
        {
            var s = $"P_{e.Name}{(string.IsNullOrEmpty(e.Section) ? "" : "_" + e.Section)}";
            return s;
        }

        // ---------- 审计 ----------
        static string AuditHeader(Dictionary<string, AsmEntry> asms, Dictionary<string, SingEntry> sings,
            SortedDictionary<string, int> unassigned)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# parts_audit（KitBaker v3 组合体装配模式生成，勿手改——重跑即重建）");
            sb.AppendLine();
            sb.AppendLine($"组合体 {asms.Count} + 单件代表 {sings.Count}；几何在网格里带原始烘位，组合体 root 归零出厂，整体拖放。");
            sb.AppendLine("未入册族 = 窗外/远景造景与辅助件，只存在于答案场景，不入零件库（规格异议改 KitBaker 分组表重跑）。");
            sb.AppendLine();
            sb.AppendLine("## 组合体");
            sb.AppendLine();
            sb.AppendLine("| prefab | 场景 | 内容(三角/材质) |");
            sb.AppendLine("| --- | --- | --- |");
            return sb.ToString();
        }

        static string AsmRow(AsmEntry e) =>
            $"| {e.Path.Replace(OutRoot + "/", "")} | {SceneDir[e.Scene]} | {e.Parts.Count}件/{e.Tris}tri/{string.Join(" + ", e.Mats.OrderBy(x => x))} |\n";

        static string SingRow(SingEntry e) =>
            $"| {e.Path.Replace(OutRoot + "/", "")} | {SceneDir[e.Scene]}单件 | {e.Fam} ×{e.Instances} {e.WorldSize.x:F1}x{e.WorldSize.y:F1}x{e.WorldSize.z:F1}m {e.Tris}tri {string.Join(" + ", e.Mats)} |\n";

        static string UnassignedBlock(SortedDictionary<string, int> unassigned)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("## 未入册族（造景/辅助判读清单）");
            sb.AppendLine();
            foreach (var kv in unassigned) sb.AppendLine($"- {kv.Key} ×{kv.Value}");
            return sb.ToString();
        }

        // ---------- Catalog ----------
        [MenuItem("HST/Kit/生成 Catalog 场景(平铺全部零件)")]
        public static void BakeCatalog()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var sun = new GameObject("Catalog_Sun");
            var l = sun.AddComponent<Light>();
            l.type = LightType.Directional; l.intensity = 1.1f;
            sun.transform.rotation = Quaternion.Euler(55f, -35f, 0f);

            float zCursor = 0f;
            var all = new List<GameObject>();
            foreach (var dir in Directory.GetDirectories(OutRoot).Select(Path.GetFileName).OrderBy(x => x))
            {
                var guids = AssetDatabase.FindAssets("P_", new[] { $"{OutRoot}/{dir}" });
                if (guids.Length == 0) continue;
                float x = 0f, rowDepth = 0f, zRow = zCursor;
                foreach (var guid in guids.OrderBy(g => AssetDatabase.GUIDToAssetPath(g)))
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                    if (prefab == null) continue;
                    var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                    inst.transform.position = new Vector3(x, 0f, zRow);
                    var b = ComputeBounds(inst);
                    inst.transform.position += Vector3.up * (-b.min.y);
                    b = ComputeBounds(inst);
                    var w = Mathf.Max(b.size.x, 0.5f);
                    Label(prefab.name, new Vector3(b.center.x, 0.4f, b.min.z - 0.4f),
                        Mathf.Clamp(w * 0.12f, 0.15f, 1.2f));
                    x += w + Mathf.Max(b.size.x * 0.4f, 0.8f);
                    rowDepth = Mathf.Max(rowDepth, b.size.z);
                    if (x > 120f) { x = 0f; zRow += rowDepth + 2f; rowDepth = 0f; }
                    all.Add(inst);
                }
                zCursor = zRow + rowDepth;
                var board = Label($"{dir} ({guids.Length})", new Vector3(0f, 4f, zCursor + 5f), 2.5f);
                board.name = "CAT_" + dir;
                zCursor += 12f;
            }
            EditorSceneManager.SaveScene(scene, "Assets/Art/Environment/HST_Kit/Scenes/SC_HST_Catalog.unity");
            Debug.Log($"[Kit] Catalog 装配完成: {all.Count} 件平铺 -> Assets/Art/Environment/HST_Kit/Scenes/SC_HST_Catalog.unity");
        }

        static Bounds ComputeBounds(GameObject go)
        {
            var b = new Bounds(); var first = true;
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                if (first) { b = r.bounds; first = false; } else b.Encapsulate(r.bounds);
            }
            return b;
        }

        static GameObject Label(string text, Vector3 pos, float size)
        {
            var go = new GameObject("lbl_" + text);
            go.transform.position = pos;
            go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            var tm = go.AddComponent<TextMesh>(); // TextMesh 自动携带 MeshRenderer
            tm.text = text; tm.fontSize = 48; tm.characterSize = size * 0.1f;
            tm.anchor = TextAnchor.MiddleCenter; tm.alignment = TextAlignment.Center;
            tm.color = new Color(0.9f, 0.9f, 0.2f, 1f);
            return go;
        }

        // 逐件棚拍：每个 prefab 一张图，按场景分文件夹 -> _render_kit/<场景>/<件名>.png
        [MenuItem("HST/Kit/逐件棚拍(四文件夹预览图)")]
        public static void RenderKitPreviews()
        {
            var outRoot = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "..", "_render_kit");
            Directory.CreateDirectory(outRoot);
            // 中性棚拍：平光环境+主补两盏灯，排除场景灯光干扰（同 SceneStillRender 棚拍口径）
            var studio = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.45f, 0.45f, 0.48f);
            RenderSettings.skybox = null;
            foreach (var (rot, intensity) in new[] { (45f, 1.15f), (30f, 0.35f) })
            {
                var g = new GameObject("Sun" + intensity);
                var li = g.AddComponent<Light>();
                li.type = LightType.Directional; li.intensity = intensity; li.shadows = LightShadows.None;
                g.transform.rotation = Quaternion.Euler(rot, intensity > 1f ? -35f : 145f, 0f);
            }

            var camGo = new GameObject("Cam");
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 40f; cam.nearClipPlane = 0.01f; cam.farClipPlane = 5000f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.16f, 0.17f, 0.19f, 1f);

            int n = 0;
            foreach (var dir in Directory.GetDirectories(OutRoot))
            {
                var sceneDir = Path.GetFileName(dir);
                var outDir = Path.Combine(outRoot, sceneDir);
                Directory.CreateDirectory(outDir);
                foreach (var guid in AssetDatabase.FindAssets("P_", new[] { dir }))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (prefab == null) continue;
                    var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                    var b = ComputeBounds(inst);
                    if (b.size == Vector3.zero) b = new Bounds(Vector3.zero, Vector3.one);
                    // 地面背景：给透明玻璃一个可见的反射/轮廓参照
                    var ground = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    ground.GetComponent<MeshRenderer>().sharedMaterial =
                        new Material(Shader.Find("Universal Render Pipeline/Lit"))
                        { color = new Color(0.32f, 0.33f, 0.36f) };
                    var maxDim = Mathf.Max(b.size.x, b.size.y, b.size.z);
                    ground.transform.position = new Vector3(b.center.x, b.min.y - 0.25f, b.center.z);
                    ground.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                    ground.transform.localScale = Vector3.one * maxDim * 6f;
                    // 取景：主维全覆盖（40° fov 水平覆盖=0.73×距离），再留 15% 边距
                    var dist = maxDim / 0.73f * 1.15f + 0.5f;
                    var dirV = new Vector3(-0.55f, 0.42f, -0.72f).normalized; // 左前上方
                    cam.transform.position = b.center + dirV * dist;
                    cam.transform.LookAt(b.center);
                    // 中文标签入画：件名+包围盒尺寸，画面下方
                    var label = new GameObject("lbl");
                    var tm = label.AddComponent<TextMesh>();
                    tm.text = $"{prefab.name}  {b.size.x:F1}x{b.size.y:F1}x{b.size.z:F1}m";
                    tm.fontSize = 48; tm.characterSize = dist * 0.012f;
                    tm.anchor = TextAnchor.MiddleCenter; tm.alignment = TextAlignment.Center;
                    tm.color = new Color(0.95f, 0.9f, 0.3f);
                    label.transform.position = cam.transform.position
                        + cam.transform.forward * (dist * 0.82f)
                        - cam.transform.up * (dist * 0.30f)
                        + cam.transform.right * (dist * 0.10f);
                    label.transform.rotation = cam.transform.rotation;
                    SnapSquare(cam, Path.Combine(outDir, prefab.name + ".png"));
                    UnityEngine.Object.DestroyImmediate(label);
                    UnityEngine.Object.DestroyImmediate(ground);
                    UnityEngine.Object.DestroyImmediate(inst);
                    n++;
                }
            }
            Debug.Log($"[Kit] 逐件棚拍完成: {n} 张 -> _render_kit/<军用厅|中庭|走廊|城市>/");
        }

        static void SnapSquare(Camera cam, string file)
        {
            const int s = 800;
            var rt = new RenderTexture(s, s, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt; cam.aspect = 1f; cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(s, s, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, s, s), 0, 0); tex.Apply();
            RenderTexture.active = null;
            File.WriteAllBytes(file, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            cam.targetTexture = null; rt.Release(); UnityEngine.Object.DestroyImmediate(rt);
        }

        static void SnapRes(Camera cam, string file, int w, int h)
        {
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt; cam.aspect = (float)w / h; cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0); tex.Apply();
            RenderTexture.active = null;
            File.WriteAllBytes(file, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            cam.targetTexture = null; rt.Release(); UnityEngine.Object.DestroyImmediate(rt);
        }

        // ---------- 标准件: Unity 侧目录场景 + 棚拍 ----------
        // 消费端是 Unity(URP 材质/真实 prefab 结构), 目检必须在 Unity 里做 ——
        // Blender 渲染只反映设计语言, 反映不了 prefab 缩放/材质/子件这些交付事实。
        const string StdRoot = "Assets/Art/Environment/HST_Kit/Prefabs/HST_Kit_Std";
        // 本轮新做(2026-10-14 二批): 塑料周转箱家族 + 能量罐 + 六边形浮雕墙 + 楼梯 + 集装箱垛
        // (走廊地板/平顶板虽也改了嵌缝与灯带, 但本位件属"既有件升级", 仍归既有)
        static readonly string[] NewPartNames =
        {
            "P_六边形浮雕墙", "P_楼梯_标准", "P_楼梯_宽段", "P_集装箱垛",
        };

        // 类目排布: 小件在前(近相机) → 大件在后
        // (2026-10-14: 墙类原偏航 180° 是给"整库躺倒 90°"打的补丁; 轴向纠正后它把
        //  贴板墙/六边形墙整个翻成背面(M_sub 近黑底) —— 所以类目图里那片大黑墙。改回 0)
        static readonly (string dir, float yaw)[] StdCatOrder =
        {
            ("楼梯", 0f), ("摆件组", 0f), ("摆件", 0f),
            ("地", 0f), ("顶", 0f), ("墙", 0f),
        };

        [MenuItem("HST/Kit/标准件目录场景+棚拍(Unity 侧目检)")]
        public static void BakeStdCatalogAndStills()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            // 环境光压低、主光改掠射、补一盏轮廓光: 浮雕/波纹/分板这些"厚度差异"要斜光才有立体感。
            // (2026-10-14 用户: 六边形墙在 Unity 图里"部分格子没光感"——原灯位近正面+高环境光把高差抹平了)
            RenderSettings.ambientLight = new Color(0.30f, 0.31f, 0.35f);
            RenderSettings.skybox = null;
            var keyGo = new GameObject("StdKey");
            var key = keyGo.AddComponent<Light>();
            key.type = LightType.Directional; key.intensity = 1.30f; key.shadows = LightShadows.Soft;
            keyGo.transform.rotation = Quaternion.Euler(33f, -58f, 0f);
            var fillGo = new GameObject("StdFill");
            var fill = fillGo.AddComponent<Light>();
            fill.type = LightType.Directional; fill.intensity = 0.28f; fill.shadows = LightShadows.None;
            fillGo.transform.rotation = Quaternion.Euler(24f, 132f, 0f);
            var rimGo = new GameObject("StdRim");
            var rim = rimGo.AddComponent<Light>();
            rim.type = LightType.Directional; rim.intensity = 0.55f; rim.shadows = LightShadows.None;
            rimGo.transform.rotation = Quaternion.Euler(62f, 40f, 0f);
            // 泛光: 自发光件(灯带/嵌缝/青格/全息)要出辉光才有"光感", 否则只是块亮色几何
            var volGo = new GameObject("StdPost");
            var vol = volGo.AddComponent<Volume>();
            vol.isGlobal = true;
            var prof = ScriptableObject.CreateInstance<VolumeProfile>();
            var bloom = prof.Add<Bloom>(true);
            bloom.intensity.value = 0.85f; bloom.threshold.value = 0.95f; bloom.scatter.value = 0.62f;
            vol.profile = prof;
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Catalog_Ground";
            ground.GetComponent<MeshRenderer>().sharedMaterial =
                new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = new Color(0.21f, 0.22f, 0.24f) };
            ground.transform.position = new Vector3(20f, -0.01f, 300f);
            ground.transform.localScale = Vector3.one * 400f;

            // ---- 1) 目录场景(真实比例, 供地编在编辑器里浏览) ----
            var labels = new List<GameObject>();
            var cats = new List<(string dir, List<(string path, string name)> items, Bounds b, List<GameObject> objs)>();
            float zCursor = 0f;
            foreach (var (dir, yaw) in StdCatOrder)
            {
                var abs = $"{StdRoot}/{dir}";
                if (!Directory.Exists(abs)) continue;
                var guids = AssetDatabase.FindAssets("P_", new[] { abs });
                if (guids.Length == 0) continue;
                float x = 0f, rowDepth = 0f, zRow = zCursor;
                var row = new List<(string path, string name)>();
                var rowObjs = new List<GameObject>();
                foreach (var guid in guids.OrderBy(g => AssetDatabase.GUIDToAssetPath(g)))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (prefab == null) continue;
                    var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                    if (yaw != 0f) inst.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                    inst.transform.position = new Vector3(x, 0f, zRow);
                    var b = ComputeBounds(inst);
                    inst.transform.position += Vector3.up * (-b.min.y);
                    b = ComputeBounds(inst);
                    var w = Mathf.Max(b.size.x, 0.6f);
                    labels.Add(Label(prefab.name, new Vector3(b.center.x, 0.35f, b.min.z - 0.5f), 1.1f));
                    row.Add((path, prefab.name));
                    rowObjs.Add(inst);
                    x += w + Mathf.Max(b.size.x * 0.25f, 1.2f);
                    rowDepth = Mathf.Max(rowDepth, b.size.z);
                    if (x > 46f) { x = 0f; zRow += rowDepth + 2.5f; rowDepth = 0f; }
                }
                zCursor = zRow + rowDepth;
                labels.Add(Label($"[{dir}] x{row.Count}", new Vector3(0f, 0.6f, zCursor + 3.5f), 2.2f));
                zCursor += 9f;
                cats.Add((dir, row, Encapsulate(rowObjs), rowObjs));
            }
            EditorSceneManager.SaveScene(scene, "Assets/Art/Environment/HST_Kit/Scenes/SC_HST_Kit_Std_Catalog.unity");

            var outDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "..", "_render_kit_std");
            Directory.CreateDirectory(outDir);
            var camGo = new GameObject("StdCam");
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 35f; cam.nearClipPlane = 0.05f; cam.farClipPlane = 6000f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.16f, 0.17f, 0.19f, 1f);
            var cdata = cam.GetUniversalAdditionalCameraData();
            if (cdata != null) cdata.renderPostProcessing = true;
            var sb = new StringBuilder("name\tsize\tcx\tcy\tcz\n");
            foreach (var lb in labels) { var r = lb.GetComponent<Renderer>(); if (r) r.enabled = false; }

            // ---- 2) 类目图(真实比例) ----
            foreach (var (dir, row, b, objs) in cats)
            {
                if (row.Count == 0) continue;
                // 只留本类目可见: 类目之间只隔 9m, 40x22 的大墙/28x14 的地台会窜进邻类目的取景框
                foreach (var (_, r2, _, o2) in cats)
                    foreach (var g in o2)
                        foreach (var r in g.GetComponentsInChildren<Renderer>(true))
                            r.enabled = ReferenceEquals(r2, row);
                // 墙类走低掠角: 浮雕/波纹/分板这些厚度差斜着看才立得住
                // (正对看 = 所有立面同一个光照, 一律压成平面 —— 六边形墙"没光感"的一半原因)
                var vd = dir == "墙" ? new Vector3(0.54f, 0.30f, -0.79f) : new Vector3(0.18f, 0.50f, -0.85f);
                FrameCameraFit(cam, b, vd, 1.04f, 1600, 900);
                AppendCam(sb, $"#CAM_{dir}", cam, 1600, 900);
                SnapRes(cam, Path.Combine(outDir, $"类目_{dir}.png"), 1600, 900);
            }
            foreach (var (_, _, _, o) in cats)
                foreach (var g in o)
                    foreach (var r in g.GetComponentsInChildren<Renderer>(true)) r.enabled = true;

            File.WriteAllText(Path.Combine(outDir, "类目.labels.tsv"), sb.ToString(), new UTF8Encoding(false));

            // ---- 3) 等视觉大小全景(全部 / 既有 / 新做 三张; 标注写真实尺寸) ----
            var allItems = new List<(string path, string name)>();
            foreach (var (_, row, _, _) in cats) allItems.AddRange(row);
            var sheets = new List<(string tag, List<(string path, string name)> items)>
            {
                ("ALL_全景", allItems),
                ("既有", allItems.Where(x => !NewPartNames.Contains(x.name)).ToList()),
                ("新做", allItems.Where(x => NewPartNames.Contains(x.name)).ToList()),
            };
            var zBase = 400f;
            foreach (var (tag, items) in sheets)
            {
                ShootUniform(cam, outDir, tag, items, zBase);
                zBase += 40f;
            }
            // ---- 4) 重点件特写(真实比例 + 掠射光): 等视觉大小全景把大件压小了,
            // 六边形浮雕/波纹箱壁这类"厚度差异"要近距离斜看才判得了立体感够不够。
            // 放在全景之后: 特写件摆在全景相机身后(z=600), 免得互相入镜。
            ShootHeroes(cam, outDir, StdRoot);
            Debug.Log($"[Kit] 标准件 Unity 棚拍完成: 全景 {sheets.Count} 张(全部/既有/新做) + {cats.Count} 类目 + {HeroShots.Length} 特写 -> _render_kit_std/");
        }

        // 重点件特写: 每件单独摆到远处空场, 真实比例 + 低掠角取景出图。
        // 用"清单"而不是"每件都出" —— 大件全集图里被压小的那些才需要特写目检。
        static readonly (string name, Vector3 dir)[] HeroShots =
        {
            ("P_六边形浮雕墙", new Vector3(0.66f, 0.18f, -0.72f)),
            ("P_集装箱垛", new Vector3(0.60f, 0.34f, -0.72f)),
            ("P_楼梯_标准", new Vector3(0.42f, 0.36f, -0.83f)),
        };

        static GameObject ShootHeroes(Camera cam, string outDir, string stdRoot)
        {
            var root = new GameObject("Heroes");
            float hx = 0f;
            foreach (var (name, dir) in HeroShots)
            {
                var guids = AssetDatabase.FindAssets($"{name} t:Prefab", new[] { stdRoot });
                if (guids.Length == 0) { Debug.LogWarning($"[Kit] 特写件未找到: {name}"); continue; }
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guids[0]));
                if (prefab == null) continue;
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                inst.transform.SetParent(root.transform, true);
                var b0 = ComputeBounds(inst);
                inst.transform.position += new Vector3(hx - b0.center.x, -b0.min.y, 600f - b0.center.z);
                var b = ComputeBounds(inst);
                FrameCameraFit(cam, b, dir, 1.22f, 1600, 900);
                SnapRes(cam, Path.Combine(outDir, $"特写_{name.Replace("P_", "")}.png"), 1600, 900);
                hx += Mathf.Max(b.size.x, 2f) + 24f;   // 大间距: 免得邻件入镜(相机是斜看的)
            }
            return root;
        }

        // 等视觉大小平铺 + 取景 + 出图 + 写该图的标注清单(件名/真实尺寸/世界中心/相机)
        static void ShootUniform(Camera cam, string outDir, string tag,
                                 List<(string path, string name)> items, float zBase)
        {
            const float S = 5.0f;
            var uni = new List<(string name, string size, Bounds b)>();
            var root = new GameObject("Uniform_" + tag);
            float ux = 0f, uz = zBase;
            var col = 0;
            foreach (var (path, nm) in items)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                inst.transform.SetParent(root.transform, true);
                var b0 = ComputeBounds(inst);
                var maxDim = Mathf.Max(b0.size.x, Mathf.Max(b0.size.y, b0.size.z));
                inst.transform.localScale *= maxDim > 0.01f ? S / maxDim : 1f;
                var b = ComputeBounds(inst);
                inst.transform.position += new Vector3(ux - b.center.x, -b.min.y, uz - b.center.z);
                b = ComputeBounds(inst);
                uni.Add((nm, $"{b0.size.x:F1}x{b0.size.y:F1}x{b0.size.z:F1}", b));
                ux += S * 1.35f;
                if (++col % 6 == 0) { ux = 0f; uz += S * 1.6f; }
            }
            if (uni.Count == 0) return;
            var ub = uni[0].b;
            foreach (var (_, _, b) in uni) ub.Encapsulate(b);
            FrameCameraFit(cam, ub, new Vector3(0.08f, 0.66f, -0.76f), 1.03f, 3200, 1800);
            var sb = new StringBuilder("name\tsize\tcx\tcy\tcz\n");
            foreach (var (nm, sz, b) in uni)
                sb.Append($"{nm}\t{sz}\t{(b.min.x + b.max.x) / 2f:F3}\t{(b.min.y + b.max.y) / 2f:F3}\t{(b.min.z + b.max.z) / 2f:F3}\n");
            AppendCam(sb, "#CAM", cam, 3200, 1800);
            SnapRes(cam, Path.Combine(outDir, tag + ".png"), 3200, 1800);
            File.WriteAllText(Path.Combine(outDir, tag + ".labels.tsv"), sb.ToString(), new UTF8Encoding(false));
        }

        static Bounds Encapsulate(List<GameObject> gos)
        {
            var b = new Bounds(); var first = true;
            foreach (var go in gos)
            {
                var rb = ComputeBounds(go);
                if (rb.size == Vector3.zero) continue;
                if (first) { b = rb; first = false; } else b.Encapsulate(rb);
            }
            return first ? new Bounds(Vector3.one * 3f, Vector3.one * 6f) : b;
        }

        static void AppendCam(StringBuilder sb, string tag, Camera cam, int w, int h)
        {
            var t = cam.transform;
            sb.Append($"{tag}\t{t.position.x:F4}\t{t.position.y:F4}\t{t.position.z:F4}\t" +
                      $"{t.rotation.x:F6}\t{t.rotation.y:F6}\t{t.rotation.z:F6}\t{t.rotation.w:F6}\t" +
                      $"{cam.fieldOfView:F3}\t{w}\t{h}\n");
        }

        // 取景: 先按包围球放到足够远, 再把包围盒 8 角投影进视口迭代收紧到刚好装下
        static void FrameCameraFit(Camera cam, Bounds b, Vector3 dir, float pad, int w, int h)
        {
            cam.aspect = (float)w / h;
            var d = dir.normalized;
            cam.transform.position = b.center + d * (b.extents.magnitude * 4f);
            cam.transform.LookAt(b.center);
            for (var it = 0; it < 6; it++)
            {
                var worst = 0f;
                for (var i = 0; i < 8; i++)
                {
                    var c = b.center + Vector3.Scale(b.extents,
                        new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                    var v = cam.WorldToViewportPoint(c);
                    if (v.z <= 0f) { worst = 99f; break; }
                    worst = Mathf.Max(worst, Mathf.Abs(v.x - 0.5f) * 2f);
                    worst = Mathf.Max(worst, Mathf.Abs(v.y - 0.5f) * 2f);
                }
                var target = 1f / Mathf.Max(1.01f, pad);   // 目标: 包围盒顶到视口 1/pad 处
                if (Mathf.Abs(worst - target) < 0.01f) break;
                var k = Mathf.Clamp(worst / Mathf.Max(target, 1e-3f), 0.2f, 5f);
                cam.transform.position = b.center + d * (Vector3.Distance(cam.transform.position, b.center) * k);
                cam.transform.LookAt(b.center);
            }
        }
        // 一次性诊断: FBX 网格顶点数 + prefab 子件材质(定位 Unity 侧丢件/丢材质)
        [MenuItem("HST/Kit/诊断:标准件网格自检")]
        public static void DiagStdMeshes()
        {
            const string fbx = "Assets/Art/Environment/HST_Kit/Models/HST_Kit_Std/SM_HST_Kit_Std_v1.fbx";
            var meshes = AssetDatabase.LoadAllAssetsAtPath(fbx).OfType<Mesh>().ToList();
            Debug.Log($"[DIAG] FBX 网格 {meshes.Count} 个, 零顶点 {meshes.Count(m => m.vertexCount == 0)}");
            foreach (var m in meshes.Where(m => m.vertexCount == 0).Take(10))
                Debug.LogWarning($"[DIAG] 零顶点: {m.name}");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Art/Environment/HST_Kit/Prefabs/HST_Kit_Std/楼梯/P_楼梯_标准.prefab");
            var rs = prefab.GetComponentsInChildren<MeshRenderer>();
            Debug.Log($"[DIAG] P_板条箱_标准 子件 {rs.Length}");
            foreach (var r in rs.OrderBy(r => r.gameObject.name))
            {
                var mf = r.GetComponent<MeshFilter>();
                Debug.Log($"[DIAG] {r.gameObject.name} verts={mf.sharedMesh.vertexCount} " +
                          $"mat={r.sharedMaterial.name} b={r.bounds.size.x:F2}x{r.bounds.size.y:F2}x{r.bounds.size.z:F2} " +
                          $"c={r.bounds.center.x:F2},{r.bounds.center.y:F2},{r.bounds.center.z:F2}");
            }
        }

        // 机位邻域分析：概念厅室内判读——InteriorDesk 相机周围各族的件数随距离分布
        [MenuItem("HST/Kit/机位邻域分析(室内族判读)")]
        public static void AnalyzeCamVicinity()
        {
            var sb = new StringBuilder("# 机位邻域分析（AnalyzeCamVicinity 生成）\n");
            EditorSceneManager.OpenScene("Assets/Art/Environment/HST_Kit/Scenes/SC_Atrium_Concept.unity", OpenSceneMode.Single);
            foreach (var cam in UnityEngine.Object.FindObjectsOfType<Camera>(true)
                         .Where(c => c.name.StartsWith("CAM_")).OrderBy(c => c.name))
            {
                var p = cam.transform.position;
                sb.Append($"\n## {cam.name} @ ({p.x:F0},{p.y:F0},{p.z:F0})\n| 族 | ≤5m | ≤10m | ≤20m | ≤40m | 全场景 |\n| --- | --- | --- | --- | --- | --- |\n");
                var all = new Dictionary<string, List<float>>();
                foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
                {
                    if (!(r is MeshRenderer) || !r.enabled || r.GetComponent<Light>() != null) continue;
                    if (PrefabUtility.IsPartOfPrefabInstance(r.gameObject)) continue;
                    var f = Fam(r.gameObject.name);
                    if (!all.TryGetValue(f, out var l)) all[f] = l = new List<float>();
                    l.Add(Vector3.Distance(r.bounds.center, p));
                }
                foreach (var kv in all.OrderBy(kv => kv.Value.Count(d => d <= 10f) == 0 ? 999f : kv.Value.Min()))
                {
                    int C(float r2) => kv.Value.Count(d => d <= r2);
                    if (C(5f) + C(10f) + C(20f) == 0) continue;
                    sb.Append($"| {kv.Key} | {C(5f)} | {C(10f)} | {C(20f)} | {C(40f)} | {kv.Value.Count} |\n");
                }
            }
            File.WriteAllText("Assets/Art/Environment/HST_Kit/Prefabs/HST_Kit/cam_vicinity_report.md", sb.ToString(), new UTF8Encoding(true));
            AssetDatabase.Refresh();
            Debug.Log("[Kit] 机位邻域分析完成 -> cam_vicinity_report.md");
        }

        // 标准件烘焙：Blender 端 kit_std.hst 生成的 FBX(+manifest) → 按零件切 prefab（材质按 manifest 重建，
        //   底面中心归零出厂），按类型分类 + 中文名。与场景拆分(BakeAll)互不影响。
        static readonly (string prefix, string dir, string cn)[] StdKitMap =
        {
            ("SM_panels_wall_atrium", "墙", "贴板墙_室内"), ("SM_panels_wall_mil", "墙", "贴板墙_大厅"),
            ("glass_curtain", "墙", "玻璃幕墙节"), ("glass_pod", "墙", "玻璃格构墙节"),
            ("floor_plates", "地", "板场地台"), ("corr_floor", "地", "走廊地板"),
            ("ceil_slab", "顶", "平顶板"),
            ("cont_stk", "摆件组", "集装箱垛"), ("cont_a", "摆件组", "集装箱"),
            ("can_a", "摆件", "罐体"), ("rail_a", "墙", "玻璃栏板"),
            ("pend_a", "摆件", "吊灯光岛"),
            ("SM_sub_wall_atrium", "墙", "贴板墙_室内"), ("SM_sub_wall_mil", "墙", "贴板墙_大厅"),
            ("SM_hex_hex_wall", "墙", "六边形浮雕墙"),
            ("stair_std", "楼梯", "楼梯_标准"), ("stair_wide", "楼梯", "楼梯_宽段"),
        };

        [MenuItem("HST/Kit/标准件烘焙(FBX→按零件 prefab)")]
        public static void BakeStandardKit()
        {
            const string fbxPath = "Assets/Art/Environment/HST_Kit/Models/HST_Kit_Std/SM_HST_Kit_Std_v1.fbx";
            const string manifestPath = "Assets/Art/Environment/HST_Kit/Models/HST_Kit_Std/manifest_kit_std.json";
            const string outRoot2 = "Assets/Art/Environment/HST_Kit/Prefabs/HST_Kit_Std";
            // 幂等重建只清产物子目录+audit, 保留手写的 IMPORT.md(与 BakeAll 同纪律;
            // 2026-10-14 实测整目录 DeleteAsset 曾把手写 IMPORT.md 连带吃掉)
            if (Directory.Exists(outRoot2))
                foreach (var d in Directory.GetDirectories(outRoot2))
                    AssetDatabase.DeleteAsset(d);
            if (AssetDatabase.DeleteAsset($"{outRoot2}/std_audit.md"))
                Debug.Log($"[Kit] 清空旧标准件 {outRoot2}");
            Directory.CreateDirectory(outRoot2);

            var manifestText = File.ReadAllText(manifestPath);

            // 1)+2) 材质与零件分组：读 TSV（python 预转，零转义）
            var matMap = new Dictionary<string, Material>();
            Directory.CreateDirectory($"{outRoot2}/材质");
            foreach (var ln in File.ReadAllLines(manifestPath.Replace("manifest_kit_std.json", "materials.tsv")))
            {
                if (string.IsNullOrWhiteSpace(ln)) continue;
                var p = ln.Split('	');
                // 贴图标准件(T_Floor/T_Wall/T_Ceil): 不建平色替身, 克隆走廊同款 HST_Prags
                // 贴图材质 —— Blender 侧 tex_override 已按世界三平面烘 UV0, URP Lit 直读
                var ti = System.Array.IndexOf(new[] { "T_Floor", "T_Wall", "T_Ceil" }, p[0]);
                if (ti >= 0)
                {
                    var srcName = p[0] == "T_Floor" ? "M_ARCH_FloorM" : p[0] == "T_Wall" ? "M_ARCH_WallM" : "M_ARCH_CeilM";
                    var srcMat = AssetDatabase.LoadAssetAtPath<Material>($"Assets/Art/Environment/HST_Kit/Materials/HST_Prags/{srcName}.mat");
                    if (srcMat != null)
                    {
                        var texed = new Material(srcMat);
                        matMap[p[0]] = texed;
                        AssetDatabase.CreateAsset(texed, $"{outRoot2}/材质/M_{p[0]}.mat");
                        continue;
                    }
                    Debug.LogWarning($"[Kit] 贴图源材质缺失, 退化平色: {srcName}");
                }
                var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                var rgb = p[3].Split(',').Select(v => float.Parse(v)).ToList();
                var col = new Color(rgb[0], rgb[1], rgb[2]);
                var alpha = float.Parse(p[4]);
                var rough = float.Parse(p[1]);
                if (alpha < 0.999f)
                {
                    mat.SetFloat("_Surface", 1f); mat.SetFloat("_Blend", 0f);
                    mat.SetOverrideTag("RenderType", "Transparent");
                    mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    mat.renderQueue = 3000;
                    col.a = alpha;
                }
                mat.color = col;
                mat.SetFloat("_Smoothness", 1f - rough);
                mat.SetFloat("_Metallic", float.Parse(p[2]));
                // 自发光: manifest 一直带着 emit/emit_str, 老 TSV 没这两列 → 全库发光件
                // (M_cyan/M_lamp/M_band/M_holo/M_blue_dot/M_hazard)在 Unity 里被烧成不发光的
                // 平色几何。这就是"发光条不亮、六边形墙在 Unity 里没光感"的根因。
                if (p.Length > 6 && float.TryParse(p[6], out var es) && es > 0f)
                {
                    var e = p[5].Split(',').Select(v => float.Parse(v)).ToList();
                    mat.EnableKeyword("_EMISSION");
                    mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                    mat.SetColor("_EmissionColor", new Color(e[0], e[1], e[2]) * es);
                }
                matMap[p[0]] = mat;
                AssetDatabase.CreateAsset(mat, $"{outRoot2}/材质/M_{p[0]}.mat");
            }
            var objMat = new Dictionary<string, string>();
            var partOf = new Dictionary<string, (string dir, string cn)>();
            foreach (var ln in File.ReadAllLines(manifestPath.Replace("manifest_kit_std.json", "objects.tsv")))
            {
                if (string.IsNullOrWhiteSpace(ln)) continue;
                var p = ln.Split('	');
                objMat[p[0]] = p[1];
                foreach (var (prefix, dir, cn) in StdKitMap)
                    if (p[0].StartsWith(prefix))
                    {
                        partOf[p[0]] = (dir, cn);
                        break;
                    }
            }
            var meshes = AssetDatabase.LoadAllAssetsAtPath(fbxPath).OfType<Mesh>().ToList();
            // FBX 的 Z-up→Y-up 轴向转换与单位换算都编码在导入层级的**节点变换**上, 不烘进顶点。
            // 整层级实例化的路径(场景拆分 BakeAll)自然正确; 只取裸 mesh 会连旋转一起丢 ——
            // 件会整体躺倒(2026-10-14 实测: 罐体/平顶板/走廊地板/板条箱在 Unity 里全躺着)。
            // 这里按网格名索引源节点, 逐件复制其**相对根**的 TRS, 与实例化 FBX 完全等价。
            var fbxRoot = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            var nodeTr = new Dictionary<string, (Vector3 p, Quaternion r, Vector3 s)>();
            if (fbxRoot != null)
            {
                var srcRoot = (GameObject)PrefabUtility.InstantiatePrefab(fbxRoot);
                foreach (var t in srcRoot.GetComponentsInChildren<Transform>(true))
                {
                    var mf = t.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null || nodeTr.ContainsKey(mf.sharedMesh.name))
                        continue;
                    var m = srcRoot.transform.worldToLocalMatrix * t.localToWorldMatrix;
                    nodeTr[mf.sharedMesh.name] = (m.GetColumn(3), m.rotation, m.lossyScale);
                }
                UnityEngine.Object.DestroyImmediate(srcRoot);
                Debug.Log($"[Kit] FBX 源节点索引 {nodeTr.Count} 个(轴向转换+单位换算随节点 TRS 逐件复制)");
            }
            var groups = new Dictionary<string, List<Mesh>>();
            var dirOf = new Dictionary<string, string>();
            foreach (var mesh in meshes)
            {
                if (mesh.name.StartsWith("SM_human") || mesh.name.StartsWith("ref_a")) continue; // 比例尺非交付件
                if (!partOf.TryGetValue(mesh.name, out var info))
                {
                    Debug.LogWarning($"[Kit] 标准件网格无归属: {mesh.name}");
                    continue;
                }
                if (!groups.TryGetValue(info.cn, out var l))
                    groups[info.cn] = l = new List<Mesh>();
                l.Add(mesh);
                dirOf[info.cn] = info.dir;
            }

            // 3) 每零件一个 prefab：子件网格烘的是原场景绝对位——按零件包围盒把底面中心归零出厂
            int made = 0;
            var audit = new StringBuilder("# 标准件 audit（BakeStandardKit 生成）\n\n| prefab | 类目 | 子件 | 三角 | 尺寸m |\n| --- | --- | --- | --- | --- |\n");
            foreach (var kv in groups.OrderBy(x => x.Key))
            {
                var dir = dirOf[kv.Key];
                var rootGo = new GameObject($"P_{kv.Key}");
                var bb = new Bounds(); var first = true;
                var objMats = new Dictionary<string, Material>();
                foreach (var mesh in kv.Value)
                {
                    var sub = new GameObject(mesh.name);
                    sub.transform.parent = rootGo.transform;
                    if (nodeTr.TryGetValue(mesh.name, out var tr))
                    {   // 源节点 TRS(含轴向转换与单位换算) —— 与实例化 FBX 等价
                        sub.transform.localPosition = tr.p;
                        sub.transform.localRotation = tr.r;
                        sub.transform.localScale = tr.s;
                    }
                    else Debug.LogWarning($"[Kit] 网格无源节点(按单位阵放置): {mesh.name}");
                    var mf = sub.AddComponent<MeshFilter>(); mf.sharedMesh = mesh;
                    var mr = sub.AddComponent<MeshRenderer>();
                    var matName = objMat.TryGetValue(mesh.name, out var mn) ? mn : "M_white";
                    var mat = matMap.TryGetValue(matName, out var mm) ? mm : matMap.Values.First();
                    mr.sharedMaterial = mat; objMats[mesh.name] = mat;
                    foreach (var corner in Corners(mr.bounds)) // 世界包围盒(含 scale), 米制
                    {
                        if (first) { bb.center = corner; bb.extents = Vector3.zero; first = false; }
                        else bb.Encapsulate(corner);
                    }
                }
                // 底面中心归零：整体平移子件（烘位 → 出厂原点）
                var off = new Vector3(bb.center.x, bb.min.y, bb.center.z);
                foreach (Transform t in rootGo.transform) t.position -= off;
                var bc = rootGo.AddComponent<BoxCollider>();
                bc.center = bb.center - off; bc.size = bb.size;
                var dirPath = $"{outRoot2}/{dir}";
                Directory.CreateDirectory(dirPath);
                var path = $"{dirPath}/{rootGo.name}.prefab";
                for (var i = 2; File.Exists(path); i++) path = $"{dirPath}/{rootGo.name}_{i}.prefab";
                PrefabUtility.SaveAsPrefabAsset(rootGo, path);
                UnityEngine.Object.DestroyImmediate(rootGo);
                var tri = kv.Value.Sum(mm => mm.triangles.Length / 3);
                var matNames = string.Join("+", objMats.Values.Select(mm => mm.name).Distinct());
                audit.Append($"| {path.Replace(outRoot2 + "/", "")} | {dir} | {kv.Value.Count} | {tri} | {bb.size.x:F1}x{bb.size.y:F1}x{bb.size.z:F1} | {matNames} |\n");
                made++;
            }
            Directory.CreateDirectory(outRoot2);
            File.WriteAllText($"{outRoot2}/std_audit.md", audit.ToString(), new UTF8Encoding(true));
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[Kit] 标准件烘焙完成: {made} 件 -> {outRoot2}");
        }

        // ---------- 机位出图 ----------
        [MenuItem("HST/Kit/机位出图(四场景CAM全渲)")]
        public static void RenderCamStills()
        {
            var outDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "..", "_render_cams");
            Directory.CreateDirectory(outDir);
            foreach (var path in Scenes)
            {
                EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                try { var ls = UnityEditor.Lightmapping.lightingSettings; if (ls != null) ls.autoGenerate = false; }
                catch (System.Exception) { }
                var tag = Path.GetFileNameWithoutExtension(path);
                foreach (var src in UnityEngine.Object.FindObjectsOfType<Camera>(true)
                             .Where(c => c.name.StartsWith("CAM_")).OrderBy(c => c.name))
                {
                    var go = new GameObject("V_" + src.name);
                    var cam = go.AddComponent<Camera>();
                    cam.CopyFrom(src);
                    cam.nearClipPlane = 0.05f; cam.farClipPlane = 3000f;
                    var cd = go.AddComponent<UniversalAdditionalCameraData>(); cd.renderPostProcessing = true;
                    go.transform.position = src.transform.position;
                    go.transform.rotation = src.transform.rotation;
                    Snap1080(cam, Path.Combine(outDir, $"{tag}_{src.name}.png"));
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }
            Debug.Log("[Kit] 机位出图完成 -> _render_cams/");
        }

        static void Snap1080(Camera cam, string file)
        {
            const int w = 1920, h = 1080;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt; cam.aspect = (float)w / h; cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0); tex.Apply();
            RenderTexture.active = null;
            File.WriteAllBytes(file, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            cam.targetTexture = null; rt.Release(); UnityEngine.Object.DestroyImmediate(rt);
        }

        // ---------- 分析 ----------
        [MenuItem("HST/Kit/族包围盒分析(室内外判读)")]
        public static void AnalyzeFamilyBounds()
        {
            var sb = new StringBuilder("# 族世界包围盒（AnalyzeFamilyBounds 生成）\n");
            foreach (var s in Scenes)
            {
                var scene = EditorSceneManager.OpenScene(s, OpenSceneMode.Single);
                var tag = Path.GetFileNameWithoutExtension(s);
                sb.Append($"\n## {tag}\n| 族 | 件数 | 世界AABB(min~max) | 中心偏移 | 判读 |\n| --- | --- | --- | --- | --- |\n");
                var fams = new Dictionary<string, (int n, Bounds b)>();
                foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
                {
                    if (!(r is MeshRenderer) || !r.enabled || r.GetComponent<Light>() != null) continue;
                    if (PrefabUtility.IsPartOfPrefabInstance(r.gameObject)) continue;
                    var f = Fam(r.gameObject.name);
                    if (!fams.TryGetValue(f, out var e)) e = (0, new Bounds());
                    e.b.Encapsulate(r.bounds); e.n++;
                    fams[f] = e;
                }
                var all = new Bounds();
                foreach (var kv in fams) all.Encapsulate(kv.Value.b);
                foreach (var kv in fams.OrderBy(k => k.Value.b.min.y))
                {
                    var b = kv.Value.b;
                    var c = b.center - all.center;
                    var note = b.min.y < all.min.y + 1f ? "低于主地坪(疑造景)" :
                               c.magnitude > 30f ? "远离主体(疑造景)" : "主体范围";
                    sb.Append($"| {kv.Key} | {kv.Value.n} | {b.min.x:F0},{b.min.y:F0},{b.min.z:F0} ~ {b.max.x:F0},{b.max.y:F0},{b.max.z:F0} | {c.x:F0},{c.y:F0},{c.z:F0} | {note} |\n");
                }
            }
            File.WriteAllText("Assets/Art/Environment/HST_Kit/Prefabs/HST_Kit/bounds_report.md", sb.ToString(), new UTF8Encoding(true));
            AssetDatabase.Refresh();
            Debug.Log("[Kit] 族包围盒分析完成 -> bounds_report.md");
        }

        // mesh 几何签名归并统计——同款单体判读依据
        [MenuItem("HST/Kit/结构分析(签名归并报告)")]
        public static void AnalyzeStructure()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Kit 结构分析（AnalyzeStructure 生成）");
            foreach (var s in Scenes)
            {
                var scene = EditorSceneManager.OpenScene(s, OpenSceneMode.Single);
                var shortName = Path.GetFileNameWithoutExtension(s);
                sb.AppendLine($"\n## {shortName}");
                var sig = new Dictionary<string, List<GameObject>>();
                foreach (var root in scene.GetRootGameObjects())
                foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
                {
                    var m = mf.sharedMesh;
                    if (m == null || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(m, out string _, out long _)) continue;
                    var go = mf.gameObject;
                    if (go.GetComponent<Light>() != null || PrefabUtility.IsPartOfPrefabInstance(go)) continue;
                    var key = $"{m.vertexCount}v/{m.triangles.Length / 3}t/{m.bounds.size.x:F2}x{m.bounds.size.y:F2}x{m.bounds.size.z:F2}";
                    if (!sig.TryGetValue(key, out var list)) sig[key] = list = new List<GameObject>();
                    list.Add(go);
                }
                sb.AppendLine($"渲染件按几何签名归并: {sig.Count} 种签名 / {sig.Values.Sum(v => v.Count)} 件");
                foreach (var kv in sig.OrderByDescending(kv => kv.Value.Count).Take(40))
                {
                    var fams = string.Join(",", kv.Value.Select(g => Fam(g.name)).Distinct().Take(4));
                    sb.AppendLine($"- {kv.Value.Count,4} 件  [{kv.Key}]  族:{fams}  例:{kv.Value[0].name}");
                }
            }
            File.WriteAllText("Assets/Art/Environment/HST_Kit/Prefabs/HST_Kit/structure_report.md", sb.ToString(), new UTF8Encoding(true));
            AssetDatabase.Refresh();
            Debug.Log("[Kit] 结构分析完成 -> structure_report.md");
        }

        // ---------- 工具 ----------
        static string Fam(string n)
        {
            var m = System.Text.RegularExpressions.Regex.Match(n, @"^[A-Za-z]+[0-9]*_?[A-Za-z]*");
            return m.Success ? m.Value : n;
        }

        static long MeshTris(GameObject go) => go.GetComponentsInChildren<MeshFilter>(true)
            .Where(mf => mf.sharedMesh != null).Sum(mf => mf.sharedMesh.triangles.Length / 3);

        // 根本地空间的子树包围盒——用 Renderer 世界包围盒（网格 AABB 元数据在重烘件上已过期不可信）
        static Bounds LocalBounds(GameObject proto)
        {
            var root = proto.transform;
            var rootW = Matrix4x4.TRS(root.position, root.rotation, root.lossyScale);
            var b = new Bounds(); var first = true;
            foreach (var mr in proto.GetComponentsInChildren<MeshRenderer>(true))
            {
                var wb = mr.bounds;
                var mx = rootW.inverse * Matrix4x4.TRS(wb.center, Quaternion.identity, wb.size);
                foreach (var c in Corners(new Bounds(Vector3.zero, Vector3.one)))
                {
                    var p = mx.MultiplyPoint3x4(c);
                    if (first) { b.center = p; b.extents = Vector3.zero; first = false; }
                    else b.Encapsulate(p);
                }
            }
            return b;
        }

        static IEnumerable<Vector3> Corners(Bounds b)
        {
            var mn = b.min; var mx = b.max;
            for (var i = 0; i < 8; i++)
                yield return new Vector3((i & 1) == 0 ? mn.x : mx.x, (i & 2) == 0 ? mn.y : mx.y,
                    (i & 4) == 0 ? mn.z : mx.z);
        }
    }
}
