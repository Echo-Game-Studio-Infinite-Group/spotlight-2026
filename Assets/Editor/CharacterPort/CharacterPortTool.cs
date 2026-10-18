// 一次性迁移工具：美工 MagicaCloth 版 Character 模型 -> 主工程 Player.prefab。
// Diagnose：只读诊断，dump 新旧 FBX 骨架/网格/材质 + 参考场景 cloth 参数。
// Apply   ：把美工模型的网格、骨骼、材质、Avatar 接到 Player.prefab，并按名字重建 MagicaCloth + 胶囊碰撞体。
// Verify  ：重新加载 Player.prefab 做完整性断言。
// 迁移完成后请删除本目录。
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using MagicaCloth2;

public static class CharacterPortTool
{
    const string CharFbxPath = "Assets/Art/Models/Character/Character.fbx";
    const string PortFbxPath = "Assets/Art/Models/Character/_port/Character_source.fbx";
    const string PortScenePath = "Assets/Art/Models/Character/_port/ClothReference.unity";
    const string PlayerPrefabPath = "Assets/Prefabs/Player.prefab";
    const string OutDir = "Library/CharacterPort";

    static StringBuilder _sb;
    static void Log(string s) { _sb.AppendLine(s); Debug.Log("[CharacterPort] " + s); }
    static void Step(string title, Action body)
    {
        Log("\n---------- " + title + " ----------");
        try { body(); }
        catch (Exception e) { Log("[异常] " + e); }
    }
    static void Require(bool cond, string message)
    {
        if (!cond) throw new InvalidOperationException(message);
    }

    // ================================================================ 入口
    [MenuItem("超高速行者/移植/1. 诊断 Character 迁移")]
    public static void DiagnoseMenu() => Diagnose();
    [MenuItem("超高速行者/移植/2. 应用 Character 迁移")]
    public static void ApplyMenu() => Apply();
    [MenuItem("超高速行者/移植/3. 校验 Character 迁移")]
    public static void VerifyMenu() => Verify();

    public static void Diagnose()
    {
        Begin("diagnose");
        Step("Importer 设置", DumpImporter);
        Step("人形 Avatar", DumpAvatar);
        Step("新 FBX 网格与渲染器", () => DumpFbxHierarchy(CharFbxPath, "NEW"));
        Step("参考 FBX 网格与渲染器", () => DumpFbxHierarchy(PortFbxPath, "REF"));
        Step("玩家预制体渲染器", DumpPlayerPrefab);
        Step("参考场景 MagicaCloth", DumpClothReference);
        End("diagnose");
    }

    static void Begin(string tag) { Directory.CreateDirectory(OutDir); _sb = new StringBuilder(); Log($"=== {tag} {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==="); }
    static void End(string tag)
    {
        File.WriteAllText(Path.Combine(OutDir, tag + ".txt"), _sb.ToString());
        Debug.Log($"[CharacterPort] {tag} 完成 -> {Path.Combine(OutDir, tag + ".txt")}");
    }

    // ================================================================ Apply
    public static void Apply()
    {
        Begin("apply");
        var player = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);
        GameObject refGo = null;
        Scene refScene = default;
        bool sceneOpened = false;
        try
        {
            // ---- 1. 导入设置：必须是 Humanoid（Animator 的 Clip 是 humanMotion）
            var avatar = EnsureHumanoidAvatar();
            Log($"Avatar = {avatar.name} isValid={avatar.isValid} isHuman={avatar.isHuman}");

            // ---- 2. 参考模型：美工的 FBX（额外骨骼 + 材质映射的唯一事实来源）
            var refPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PortFbxPath);
            Require(refPrefab != null, "参考 FBX 加载失败：" + PortFbxPath);
            refGo = (GameObject)PrefabUtility.InstantiatePrefab(refPrefab);
            refGo.transform.position = Vector3.zero;
            var refRoot = refGo.transform;
            var refRenderers = new Dictionary<string, SkinnedMeshRenderer>();
            foreach (var r in refGo.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                refRenderers[r.gameObject.name] = r;
            Log($"参考模型渲染器: {string.Join(", ", refRenderers.Keys)}");

            // ---- 3. 网格 / 骨骼 / 材质重接（以美工模型为准，多出来的网格自动补建渲染节点）
            int rewired = 0;
            var playerRenderers = player.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .GroupBy(r => r.gameObject.name)
                .ToDictionary(g => g.Key, g => g.First());
            foreach (var kv in refRenderers)
            {
                var name = kv.Key;
                var rr = kv.Value;
                SkinnedMeshRenderer r;
                if (playerRenderers.TryGetValue(name, out var existing))
                {
                    r = existing;
                    Log($"重接 {NamePath(r.transform, player.transform)}");
                }
                else
                {
                    var relPath = RelPath(rr.transform, refRoot);
                    Require(relPath != null, "新渲染器定位失败: " + name);
                    var parentPath = relPath.Contains("/") ? relPath.Substring(0, relPath.LastIndexOf('/')) : "";
                    var parent = FindOrCreate(player.transform, refRoot, parentPath, rr.transform.parent);
                    Require(parent != null, "新渲染器的父节点映射失败: " + parentPath);
                    var go = new GameObject(name);
                    go.transform.SetParent(parent, false);
                    go.transform.localPosition = rr.transform.localPosition;
                    go.transform.localRotation = rr.transform.localRotation;
                    go.transform.localScale = rr.transform.localScale;
                    r = go.AddComponent<SkinnedMeshRenderer>();
                    Log($"[新增渲染器] {relPath}（白模里没有这个网格）");
                }
                var bones = rr.bones.Select(b => FindOrCreate(player.transform, refRoot, RelPath(b, refRoot), b)).ToArray();
                Require(bones.All(b => b != null), $"{name} 骨骼映射失败");
                r.sharedMesh = rr.sharedMesh;
                r.bones = bones;
                r.rootBone = rr.rootBone == null ? null : FindOrCreate(player.transform, refRoot, RelPath(rr.rootBone, refRoot), rr.rootBone);
                r.sharedMaterials = rr.sharedMaterials;
                r.localBounds = rr.localBounds;
                r.updateWhenOffscreen = rr.updateWhenOffscreen;
                Log($"   mesh={r.sharedMesh?.name} bounds={r.sharedMesh?.bounds} bones={bones.Length} " +
                    $"root={(r.rootBone == null ? "null" : r.rootBone.name)} " +
                    $"materials={string.Join(",", r.sharedMaterials.Select(m => m == null ? "null" : m.name))}");
                if (r.sharedMaterials.Any(m => m == null)) Log("[警告] 有材质槽为空");
                rewired++;
            }
            Require(rewired > 0, "没有任何渲染器被重接");
            foreach (var stale in playerRenderers.Where(kv => !refRenderers.ContainsKey(kv.Key)))
                Log($"[保留未动] {stale.Key}（参考模型里没有，属旧模型残留）");

            // 静止姿态偏差体检：蒙皮 bindpose 来自美工模型，若两边骨架静止姿态差太多说明套皮会歪
            float maxDelta = 0f; Transform worst = null; int compared = 0;
            foreach (var b in refRenderers.Values.SelectMany(r => r.bones).Where(b => b != null).Distinct())
            {
                var dst = FindOrCreate(player.transform, refRoot, RelPath(b, refRoot), b);
                if (dst == null) continue;
                compared++;
                float d = Vector3.Distance(dst.localPosition, b.localPosition);
                if (d > maxDelta) { maxDelta = d; worst = dst; }
            }
            Log($"静止姿态对比 {compared} 根骨骼，局部位置最大偏差 {maxDelta:F4} ({(worst == null ? "-" : NamePath(worst, player.transform))})");

            // ---- 4. Avatar
            var animator = player.GetComponent<Animator>();
            Require(animator != null, "Player.prefab 根节点没有 Animator");
            animator.avatar = avatar;
            animator.applyRootMotion = false;
            animator.Rebind();

            // ---- 5. MagicaCloth：从参考场景搬迁参数 + 绘制权重
            refScene = EditorSceneManager.OpenScene(PortScenePath, OpenSceneMode.Additive);
            sceneOpened = true;
            var srcCloth = refScene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<MagicaCloth>(true)).FirstOrDefault();
            Require(srcCloth != null, "参考场景里没有 MagicaCloth");
            var srcRoot = srcCloth.transform.root;
            Log($"参考 clothType={srcCloth.SerializeData.clothType} rootBones={srcCloth.SerializeData.rootBones.Count}");

            // 移除上一次迁移可能留下的同名节点，保证幂等
            foreach (var stale in player.GetComponentsInChildren<Transform>(true)
                         .Where(t => t != player.transform && (t.name == "Magica Cloth" || t.name.StartsWith("Magica Capsule Collider"))))
            {
                Log($"[幂等] 删除旧节点 {NamePath(stale, player.transform)}");
                UnityEngine.Object.DestroyImmediate(stale.gameObject);
            }

            var clothGo = new GameObject("Magica Cloth");
            clothGo.transform.SetParent(player.transform, false);
            var dstCloth = clothGo.AddComponent<MagicaCloth>();
            Transform MapTransform(Transform t) => t == null ? null : FindOrCreate(player.transform, srcRoot, RelPath(t, srcRoot), t);
            CopyObjectFields(srcCloth.SerializeData, dstCloth.SerializeData, MapTransform, "SerializeData", 0);
            Log($"  拷贝后 rootBones={dstCloth.SerializeData.rootBones.Count}: " +
                string.Join(", ", dstCloth.SerializeData.rootBones.Select(b => b == null ? "null" : NamePath(b, player.transform))));

            // selectionData（美工手绘权重）与 initData（构建缓存，清掉让 MC2 现场重建）
            var srcData2 = GetSerializeData2(srcCloth);
            var dstData2 = GetSerializeData2(dstCloth);
            Require(srcData2 != null && dstData2 != null, "拿不到 serializeData2");
            object srcSel = srcData2.GetType().GetField("selectionData").GetValue(srcData2);
            object dstSel = dstData2.GetType().GetField("selectionData").GetValue(dstData2);
            CopyObjectFields(srcSel, dstSel, MapTransform, "selectionData", 0);
            var selCount = GetMember(srcSel, "positions") is Array selPositions ? selPositions.Length : -1;
            Log($"  selectionData count={selCount}");
            var initData = dstData2.GetType().GetField("initData")?.GetValue(dstData2);
            initData?.GetType().GetMethod("Clear")?.Invoke(initData, null);
            Log("  initData 已清空（运行时由 MC2 重建）");

            // 胶囊碰撞体
            var createdColliders = new List<ColliderComponent>();
            foreach (var srcCol in refScene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<MagicaCapsuleCollider>(true)))
            {
                var parentPath = RelPath(srcCol.transform.parent, srcRoot);
                var parent = FindOrCreate(player.transform, srcRoot, parentPath, srcCol.transform.parent);
                Require(parent != null, "碰撞体父骨骼映射失败: " + parentPath);
                var go = new GameObject(srcCol.gameObject.name);
                go.transform.SetParent(parent, false);
                go.transform.localPosition = srcCol.transform.localPosition;
                go.transform.localRotation = srcCol.transform.localRotation;
                go.transform.localScale = srcCol.transform.localScale;
                var dstCol = go.AddComponent<MagicaCapsuleCollider>();
                CopyObjectFields(srcCol, dstCol, MapTransform, "CapsuleCollider", 0);
                EditorUtility.SetDirty(dstCol);
                createdColliders.Add(dstCol);
                Log($"  碰撞体 {srcCol.gameObject.name} 挂在 {NamePath(parent, player.transform)}");
            }
            // 碰撞体列表搬迁下来时指向的是参考场景里的组件，关场景后会变空，这里改指新建的实例
            if (createdColliders.Count > 0)
            {
                var list = dstCloth.SerializeData.colliderCollisionConstraint.colliderList;
                Log($"  重指向碰撞体列表：{list.Count} -> {createdColliders.Count}");
                list.Clear();
                list.AddRange(createdColliders);
                EditorUtility.SetDirty(dstCloth);
            }

            EditorSceneManager.CloseScene(refScene, true);
            sceneOpened = false;

            EditorUtility.SetDirty(animator);
            PrefabUtility.SaveAsPrefabAsset(player, PlayerPrefabPath);
            Log("\n[保存] " + PlayerPrefabPath);
            AssetDatabase.SaveAssets();
        }
        catch (Exception e)
        {
            Log("[失败] 未保存预制体：" + e);
            throw;
        }
        finally
        {
            if (sceneOpened) EditorSceneManager.CloseScene(refScene, true);
            if (refGo != null) UnityEngine.Object.DestroyImmediate(refGo);
            PrefabUtility.UnloadPrefabContents(player);
        }
        Step("保存后校验", VerifyCore);
        End("apply");
    }

    static Avatar EnsureHumanoidAvatar()
    {
        var imp = (ModelImporter)AssetImporter.GetAtPath(CharFbxPath);
        Require(imp != null, "找不到 ModelImporter: " + CharFbxPath);
        bool changed = false;
        if (imp.animationType != ModelImporterAnimationType.Human) { imp.animationType = ModelImporterAnimationType.Human; changed = true; }
        if (imp.avatarSetup != ModelImporterAvatarSetup.CreateFromThisModel) { imp.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel; changed = true; }
        if (changed) { Log("更新导入设置为 Humanoid，重新导入…"); imp.SaveAndReimport(); }
        var avatar = AssetDatabase.LoadAllAssetsAtPath(CharFbxPath).OfType<Avatar>().FirstOrDefault();
        Require(avatar != null, "Character.fbx 没有 Avatar 子资产");
        Require(avatar.isValid && avatar.isHuman, $"Avatar 无效 isValid={avatar.isValid} isHuman={avatar.isHuman}");
        return avatar;
    }

    // ================================================================ Verify
    public static void Verify() { Begin("verify"); Step("完整性断言", VerifyCore); End("verify"); }

    static void VerifyCore()
    {
        var go = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);
        int fail = 0;
        void Check(bool ok, string what) { if (!ok) { fail++; Log("  [FAIL] " + what); } else Log("  [ok] " + what); }
        try
        {
            var animator = go.GetComponent<Animator>();
            Check(animator != null && animator.avatar != null && animator.avatar.isValid && animator.avatar.isHuman,
                $"Animator Avatar 有效且为人形 ({(animator?.avatar == null ? "null" : animator.avatar.name)})");
            if (animator != null && animator.runtimeAnimatorController != null)
            {
                var clips = animator.runtimeAnimatorController.animationClips.Where(c => c != null).Distinct().ToArray();
                int human = clips.Count(c => c.humanMotion);
                Check(clips.Length > 0 && human == clips.Length, $"动画 Clip 全为 Humanoid ({human}/{clips.Length})");
            }

            var renderers = go.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.enabled).ToArray();
            Log($"  启用的渲染器 {renderers.Length} 个:");
            foreach (var r in renderers)
            {
                Check(r.sharedMesh != null, $"{NamePath(r.transform, go.transform)} mesh != null");
                Check(r.sharedMaterials.Length > 0 && r.sharedMaterials.All(m => m != null),
                    $"{NamePath(r.transform, go.transform)} 材质齐全 ({string.Join(",", r.sharedMaterials.Select(m => m?.name))})");
                Check(r.bones.Length > 0 && r.bones.All(b => b != null),
                    $"{NamePath(r.transform, go.transform)} 骨骼齐全 ({r.bones.Length})");
                if (r.sharedMesh != null)
                    Check(r.sharedMesh.bindposes.Length == 0 || r.bones.Length == r.sharedMesh.bindposes.Length,
                        $"{NamePath(r.transform, go.transform)} 骨骼数 == bindposes 数 ({r.bones.Length}/{r.sharedMesh.bindposes.Length})");
            }

            var cloth = go.GetComponentInChildren<MagicaCloth>(true);
            Check(cloth != null, "存在 MagicaCloth 组件");
            if (cloth != null)
            {
                var sd = cloth.SerializeData;
                Check(sd.clothType == ClothProcess.ClothType.BoneCloth, $"clothType = BoneCloth ({sd.clothType})");
                Check(sd.rootBones.Count > 0 && sd.rootBones.All(b => b != null),
                    $"rootBones 齐全 ({sd.rootBones.Count}): {string.Join(",", sd.rootBones.Select(b => b?.name))}");
                Check(sd.colliderCollisionConstraint.colliderList.Count > 0 &&
                      sd.colliderCollisionConstraint.colliderList.All(c => c != null),
                    $"碰撞体列表 ({sd.colliderCollisionConstraint.colliderList.Count})");
                Check(sd.gravity == 5f && Mathf.Approximately(sd.radius.value, 0.02f) && Mathf.Approximately(sd.damping.value, 0.05f),
                    $"参数已搬迁 gravity={sd.gravity} radius={sd.radius.value} damping={sd.damping.value}");

                var data2 = GetSerializeData2(cloth);
                object sel = data2?.GetType().GetField("selectionData")?.GetValue(data2);
                var positions = GetMember(sel, "positions") as Array;
                var attributes = GetMember(sel, "attributes") as Array;
                Check(positions != null && positions.Length > 0, $"绘制权重已搬迁 positions={positions?.Length}");
                Check(attributes != null && positions != null && attributes.Length == positions.Length, "权重属性数一致");
                if (attributes != null)
                    Log("  attributes=" + string.Join(",", attributes.Cast<object>()));
                object initData = data2?.GetType().GetField("initData")?.GetValue(data2);
                bool hasData = initData != null && (bool)initData.GetType().GetMethod("HasData").Invoke(initData, null);
                Check(!hasData, "initData 为空（MC2 会重建）");
            }

            var capsule = go.GetComponentsInChildren<MagicaCapsuleCollider>(true).FirstOrDefault();
            Check(capsule != null, "存在胶囊碰撞体");
            if (capsule != null)
            {
                Require(capsule.transform.parent != null, "碰撞体没有父节点");
                var sizeField = capsule.GetType().GetField("size", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                Log($"  碰撞体父骨骼={NamePath(capsule.transform.parent, go.transform)} " +
                    $"center={capsule.center} size={(sizeField == null ? "n/a" : sizeField.GetValue(capsule))} " +
                    $"direction={capsule.direction} alignedOnCenter={capsule.alignedOnCenter}");
            }

            Check(go.GetComponentsInChildren<MonoBehaviour>(true).All(c => c != null), "没有丢失脚本");
        }
        finally { PrefabUtility.UnloadPrefabContents(go); }
        Log(fail == 0 ? "\n===== 校验通过 =====" : $"\n===== 校验失败 {fail} 项 =====");
        Require(fail == 0, $"校验失败 {fail} 项");
    }

    // ================================================================ 反射深拷贝
    static object GetSerializeData2(MagicaCloth cloth)
        => typeof(MagicaCloth).GetField("serializeData2", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(cloth);

    static object GetMember(object obj, string name)
        => obj?.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(obj);

    // 只遍历 MagicaCloth2 自己声明的字段：一旦走到 UnityEngine 程序集（MonoBehaviour/Component/Object）
    // 就停下，否则会把 m_GameObject / m_CachedPtr 这类引擎内部字段也反射进去。
    static IEnumerable<FieldInfo> AllFields(Type t)
    {
        var engineAssembly = typeof(UnityEngine.Object).Assembly;
        for (var cur = t; cur != null && cur != typeof(object) && cur.Assembly != engineAssembly; cur = cur.BaseType)
            foreach (var f in cur.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                yield return f;
    }

    static bool ValueOnly(Type t)
    {
        if (t.IsPrimitive || t.IsEnum) return true;
        if (!t.IsValueType) return false;
        if (t == typeof(decimal)) return true;
        return t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).All(f => ValueOnly(f.FieldType));
    }

    static void CopyObjectFields(object src, object dst, Func<Transform, Transform> map, string label, int depth)
    {
        if (src == null || dst == null) return;
        foreach (var f in AllFields(src.GetType()))
        {
            if (f.IsNotSerialized || f.IsLiteral || f.IsInitOnly) continue;
            object value;
            try { value = f.GetValue(src); } catch { continue; }
            object copy = CopyValue(value, f.FieldType, map, label + "." + f.Name, depth);
            try { f.SetValue(dst, copy); }
            catch (Exception e) { Log($"[warn] 写不进 {label}.{f.Name}: {e.Message}"); }
        }
    }

    static object CopyValue(object v, Type t, Func<Transform, Transform> map, string label, int depth)
    {
        if (v == null) return null;
        if (t == typeof(Transform)) return map((Transform)v);
        if (t == typeof(AnimationCurve)) return CloneCurve((AnimationCurve)v);
        if (typeof(UnityEngine.Object).IsAssignableFrom(t)) return v;
        if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal)) return v;

        if (t.IsArray)
        {
            var src = (Array)v;
            var dst = Array.CreateInstance(t.GetElementType(), src.Length);
            for (int i = 0; i < src.Length; i++)
                dst.SetValue(CopyValue(src.GetValue(i), t.GetElementType(), map, label + "[" + i + "]", depth + 1), i);
            return dst;
        }
        if (v is IList list)
        {
            var dst = (IList)Activator.CreateInstance(t, true);
            var elemType = t.IsGenericType ? t.GetGenericArguments()[0] : typeof(object);
            foreach (var item in list) dst.Add(CopyValue(item, elemType, map, label + "[]", depth + 1));
            return dst;
        }
        if (depth > 6) return v;
        // 纯值类型（float3 / Vector3 / 只含基元的 struct）直接按值复制
        if (ValueOnly(t)) return v;
        // 只对 MagicaCloth2 自己的类做深拷贝；System 集合、字典等直接共用引用
        var ns = t.Namespace ?? string.Empty;
        if (!t.IsValueType && !ns.StartsWith("MagicaCloth2")) return v;

        var inst = Activator.CreateInstance(t, true);
        CopyObjectFields(v, inst, map, label, depth + 1);
        return inst;
    }

    static AnimationCurve CloneCurve(AnimationCurve c)
        => new AnimationCurve(c.keys.Select(k => new Keyframe(k.time, k.value, k.inTangent, k.outTangent, k.inWeight, k.outWeight) { weightedMode = k.weightedMode, tangentMode = k.tangentMode }).ToArray())
        { preWrapMode = c.preWrapMode, postWrapMode = c.postWrapMode };

    // ================================================================ 层级工具
    static string RelPath(Transform t, Transform root)
    {
        if (t == null) return null;
        if (t == root) return "";
        var parts = new List<string>();
        Transform cur = t;
        while (cur != null && cur != root) { parts.Add(cur.name); cur = cur.parent; }
        if (cur != root)
        {
            Log($"[warn] {t.name} 不在 {root.name} 的层级下，无法按路径映射");
            return null;
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    static Transform FindOrCreate(Transform dstRoot, Transform srcRoot, string path, Transform srcNode)
    {
        if (dstRoot == null || path == null) return null;
        if (string.IsNullOrEmpty(path)) return dstRoot;
        var dst = dstRoot;
        var src = srcRoot;
        foreach (var seg in path.Split('/'))
        {
            var nextSrc = src != null ? src.Find(seg) : null;
            var nextDst = dst.Find(seg);
            if (nextDst == null)
            {
                var go = new GameObject(seg);
                nextDst = go.transform;
                nextDst.SetParent(dst, false);
                if (nextSrc != null)
                {
                    nextDst.localPosition = nextSrc.localPosition;
                    nextDst.localRotation = nextSrc.localRotation;
                    nextDst.localScale = nextSrc.localScale;
                }
                Log($"  [新建节点] {seg}  (源侧{(nextSrc == null ? "缺失，用默认变换" : "已复制局部变换")})");
            }
            dst = nextDst;
            src = nextSrc;
        }
        return dst;
    }

    static string NamePath(Transform t, Transform root)
    {
        var p = RelPath(t, root);
        return string.IsNullOrEmpty(p) ? t.name : p;
    }

    // ================================================================ Diagnose 辅助
    static void DumpImporter()
    {
        foreach (var path in new[] { CharFbxPath, PortFbxPath })
        {
            var imp = AssetImporter.GetAtPath(path) as ModelImporter;
            if (imp == null) { Log(path + " 没有 ModelImporter"); continue; }
            Log($"{path}");
            Log($"  animationType={imp.animationType} avatarSetup={imp.avatarSetup} isReadable={imp.isReadable}");
            Log($"  importAnimation={imp.importAnimation} importBlendShapes={imp.importBlendShapes} optimizeBones={imp.optimizeBones}");
            Log($"  materialImportMode={imp.materialImportMode} materialLocation={imp.materialLocation}");
            var so = new SerializedObject(imp);
            var ext = so.FindProperty("externalObjects");
            Log($"  externalObjects={(ext == null ? "n/a" : ext.arraySize + " 项")}");
            if (ext != null)
                for (int i = 0; i < ext.arraySize; i++)
                {
                    var e = ext.GetArrayElementAtIndex(i);
                    var name = e.FindPropertyRelative("first")?.FindPropertyRelative("name")?.stringValue;
                    var obj = e.FindPropertyRelative("second")?.objectReferenceValue;
                    Log($"    {name} -> {(obj == null ? "null" : obj.name + " (" + AssetDatabase.GetAssetPath(obj) + ")")}");
                }
        }
    }

    static void DumpAvatar()
    {
        foreach (var path in new[] { CharFbxPath, PortFbxPath })
        {
            var avatar = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Avatar>().FirstOrDefault();
            if (avatar == null) { Log(path + " -> 无 Avatar 子资产"); continue; }
            Log($"{path} -> avatar='{avatar.name}' isValid={avatar.isValid} isHuman={avatar.isHuman} humanBones={avatar.humanDescription.human.Length}");
        }
    }

    static void DumpFbxHierarchy(string fbxPath, string tag)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
        if (prefab == null) { Log(fbxPath + " 加载失败"); return; }
        Log($"[{tag}] 网格子资产:");
        foreach (var mesh in AssetDatabase.LoadAllAssetsAtPath(fbxPath).OfType<Mesh>())
            Log($"    mesh '{mesh.name}' verts={mesh.vertexCount} readable={mesh.isReadable} bindposes={mesh.bindposes.Length}");
        Log($"[{tag}] 材质子资产: " + string.Join(", ", AssetDatabase.LoadAllAssetsAtPath(fbxPath).OfType<Material>().Select(m => m.name)));

        var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        try
        {
            inst.transform.position = Vector3.zero;
            Log($"[{tag}] 渲染器:");
            foreach (var r in inst.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Log($"    {NamePath(r.transform, inst.transform)}");
                Log($"      mesh={r.sharedMesh?.name} bones={r.bones.Length} root={r.rootBone?.name} subMeshes={r.sharedMesh?.subMeshCount}");
                Log("      materials=" + string.Join(", ", r.sharedMaterials.Select(m => m == null ? "null" : m.name)));
                Log("      boneNames=" + string.Join(",", r.bones.Select(b => b == null ? "null" : b.name)));
            }
            Log($"[{tag}] 层级 ({inst.GetComponentsInChildren<Transform>(true).Length} 节点):");
            foreach (var t in inst.GetComponentsInChildren<Transform>(true))
                Log("    " + (t == inst.transform ? t.name : NamePath(t, inst.transform)) + (t == inst.transform ? "" : $" children={t.childCount}"));
        }
        finally { UnityEngine.Object.DestroyImmediate(inst); }
    }

    static void DumpPlayerPrefab()
    {
        var go = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);
        try
        {
            var animator = go.GetComponent<Animator>();
            Log($"Animator avatar={animator?.avatar?.name} valid={animator?.avatar?.isValid} human={animator?.avatar?.isHuman} " +
                $"controller={animator?.runtimeAnimatorController?.name}");
            if (animator?.runtimeAnimatorController != null)
            {
                var clips = animator.runtimeAnimatorController.animationClips.Distinct().Where(c => c != null).ToArray();
                Log($"clips={clips.Length} humanMotion={clips.Count(c => c.humanMotion)}");
            }
            foreach (var r in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                Log($"SMR {NamePath(r.transform, go.transform)} mesh={r.sharedMesh?.name} bones={r.bones.Length} " +
                    $"root={r.rootBone?.name} enabled={r.enabled} mats={string.Join(",", r.sharedMaterials.Select(m => m == null ? "null" : m.name))}");
            Log($"丢失脚本组件={go.GetComponentsInChildren<MonoBehaviour>(true).Count(c => c == null)}");
        }
        finally { PrefabUtility.UnloadPrefabContents(go); }
    }

    static void DumpClothReference()
    {
        var scene = EditorSceneManager.OpenScene(PortScenePath, OpenSceneMode.Additive);
        try
        {
            var roots = scene.GetRootGameObjects();
            var cloth = roots.SelectMany(r => r.GetComponentsInChildren<MagicaCloth>(true)).FirstOrDefault();
            if (cloth == null) { Log("参考场景里没找到 MagicaCloth"); return; }
            var root = cloth.transform.root;
            Log($"cloth 路径={NamePath(cloth.transform, root)} clothType={cloth.SerializeData.clothType}");
            Log($"rootBones={cloth.SerializeData.rootBones.Count}:");
            foreach (var b in cloth.SerializeData.rootBones)
                Log($"   {(b == null ? "null" : NamePath(b, root))}");
            Log("---- SerializeData 全字段 ----");
            DumpObject(cloth.SerializeData, "  ", 0);
            var data2 = GetSerializeData2(cloth);
            object sel = data2?.GetType().GetField("selectionData")?.GetValue(data2);
            var positions = GetMember(sel, "positions") as Array;
            var attributes = GetMember(sel, "attributes") as Array;
            Log($"selectionData positions={positions?.Length} attributes={attributes?.Length} userEdit={GetMember(sel, "userEdit")}");
            for (int i = 0; i < (attributes?.Length ?? 0); i++)
                Log($"   [{i}] pos={positions.GetValue(i)} attr={attributes.GetValue(i)}");
            object initData = data2?.GetType().GetField("initData")?.GetValue(data2);
            Log($"initData.HasData={initData?.GetType().GetMethod("HasData")?.Invoke(initData, null)}");

            foreach (var c in roots.SelectMany(g => g.GetComponentsInChildren<MagicaCapsuleCollider>(true)))
            {
                Log($"胶囊体 {c.gameObject.name} 父={NamePath(c.transform.parent, root)} localPos={c.transform.localPosition} localScale={c.transform.localScale}");
                DumpObject(c, "  ", 0);
            }
        }
        finally { EditorSceneManager.CloseScene(scene, true); }
    }

    static void DumpObject(object obj, string indent, int depth)
    {
        if (obj == null) { Log(indent + "null"); return; }
        if (depth > 5) { Log(indent + "..."); return; }
        foreach (var f in AllFields(obj.GetType()))
        {
            if (f.IsNotSerialized) continue;
            object v;
            try { v = f.GetValue(obj); } catch { continue; }
            string label = indent + f.Name + " = ";
            if (v == null) { Log(label + "null"); continue; }
            if (v is UnityEngine.Object uo) { Log(label + (uo == null ? "null" : uo.name + " <" + uo.GetType().Name + ">")); continue; }
            if (v is AnimationCurve curve) { Log(label + $"Curve(keys={curve.length})"); continue; }
            if (f.FieldType.IsPrimitive || v is string || f.FieldType.IsEnum) { Log(label + v); continue; }
            if (v is IEnumerable en)
            {
                var list = en.Cast<object>().Take(24).ToList();
                Log(label + "[" + list.Count + "]");
                foreach (var item in list) DumpObject(item, indent + "  ", depth + 1);
                continue;
            }
            Log(label + "{");
            DumpObject(v, indent + "  ", depth + 1);
            Log(indent + "}");
        }
    }
}
