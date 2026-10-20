using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// 刀光拖尾的**自证工具**：在编辑器里（不用进 Play）实例化预制体、摆动剑骨、
// 每帧手动推进 Trail 采样并渲染成 PNG。用来回答「到底是没画出来、还是看不见」——
// 控制台打出点数与全部关键参数，图片落在 Library/AttackTrailDebug/（不进版本库）。
// batchmode 入口 RunFromCLI 兼做编译门禁：能跑完 = 三个脚本都编译通过。
[InitializeOnLoad]
public static class AttackTrailDebug
{
    private const string PlayerPrefabPath = "Assets/Prefabs/Player.prefab";
    private const string EnemyTestPrefabPath = "Assets/Prefabs/EnemyTest.prefab";
    private const string EnemyHeavyPrefabPath = "Assets/Prefabs/EnemyHeavy.prefab";

    // 绝对路径：相对路径在编辑器进程里可能落到别的工作目录（之前出图就是这么丢的）
    private static string OutDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "AttackTrailDebug"));

    static AttackTrailDebug() { }

    [MenuItem("超高速行者/战斗特效/调试：试放玩家刀光并出图")]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) { Debug.LogWarning("[AttackTrailDebug] 先退出 Play 模式"); return; }
        Directory.CreateDirectory(OutDir);
        AttackTrailSetup.Setup();
        SwingAndShoot(PlayerPrefabPath, "player", "joint10", "joint9", "CC_Base_R_Hand");
        SwingAndShoot(EnemyTestPrefabPath, "enemy_test", "arm right wrist", "arm right forearm");
        SwingAndShoot(EnemyHeavyPrefabPath, "enemy_heavy", "hand_R", "gun_R");
        ProbePlayerBladeAxis();
        Debug.Log("[AttackTrailDebug] 出图目录 -> " + OutDir);
    }

    // CI / 命令行自证入口：Unity.exe -batchmode -executeMethod AttackTrailDebug.RunFromCLI -quit
    public static void RunFromCLI()
    {
        Directory.CreateDirectory(OutDir);
        AttackTrailSetup.Setup();
        MeasureBladeAxis(PlayerPrefabPath, "player", "joint10", "joint9", "CC_Base_R_Hand");
        MeasureBladeAxis(EnemyTestPrefabPath, "enemy_test", "arm right wrist", "arm right forearm");
        MeasureBladeAxis(EnemyHeavyPrefabPath, "enemy_heavy", "hand_R", "gun_R");
        SwingAndShoot(PlayerPrefabPath, "player", "joint10", "joint9", "CC_Base_R_Hand");
        SwingAndShoot(EnemyTestPrefabPath, "enemy_test", "arm right wrist", "arm right forearm");
        SwingAndShoot(EnemyHeavyPrefabPath, "enemy_heavy", "hand_R", "gun_R");
        ProbePlayerBladeAxis();
        Debug.Log("[AttackTrailDebug][CLI] 出图目录 -> " + OutDir);
    }

    // 实例化一个预制体，绕腕摆 180° 拖出刀光，三机位出图。
    private static void SwingAndShoot(string prefabPath, string label, params string[] anchors)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (prefab == null) { Debug.LogError($"[AttackTrailDebug] {label}: 读不到 {prefabPath}"); return; }

        GameObject root = null;
        GameObject rig = null;
        try
        {
            root = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            AttackTrailVfx trail = root.GetComponentInChildren<AttackTrailVfx>(true);
            if (trail == null) { Debug.LogError($"[AttackTrailDebug] {label}: 没有 AttackTrailVfx"); return; }
            Transform anchor = FindByName(root.transform, anchors);
            if (anchor == null) { Debug.LogError($"[AttackTrailDebug] {label}: 找不到骨骼 {string.Join("/", anchors)}"); return; }

            var meshFilter = trail.GetComponent<MeshFilter>();
            string matInfo = "null";
            var meshRenderer = trail.GetComponent<MeshRenderer>();
            if (meshRenderer != null && meshRenderer.sharedMaterial != null)
                matInfo = $"{meshRenderer.sharedMaterial.name} (shader={meshRenderer.sharedMaterial.shader.name})";
            Debug.Log($"[AttackTrailDebug] {label}: 骨骼={anchor.name} tipOffset={trail.TipOffset} 材质={matInfo} follow={trail.Follow != null}");

            rig = BuildCameraRig();
            Camera cam = rig.GetComponentInChildren<Camera>();

            trail.Play(2f);
            Quaternion baseRotation = anchor.localRotation;
            Vector3 swingAxis = SwingAxisAvoiding(trail.TipOffset);
            Debug.Log($"[AttackTrailDebug] {label}: 摆动轴(局部)={swingAxis}");
            const int steps = 36;
            for (int i = 0; i < steps; i++)
            {
                anchor.localRotation = baseRotation * Quaternion.AngleAxis(-5f * i, swingAxis);
                trail.Tick(1f / 60f);
            }
            int verts = meshFilter != null && meshFilter.sharedMesh != null ? meshFilter.sharedMesh.vertexCount : 0;
            Debug.Log($"[AttackTrailDebug] {label}: 顶点={verts}");

            Vector3 center = anchor.position + anchor.up * 0.3f;
            // 相机偏移按角色体型缩放（玩家 1.8m 为基准）：同一组偏移对 6m 机甲才不会怼到脸上
            float s = CharacterHeight(root) / 1.8f;
            Shoot(cam, $"{label}_close", center + new Vector3(0.9f, 0.25f, -1.1f) * s, center, 32f);
            Shoot(cam, $"{label}_wide", center + new Vector3(1.7f, 0.6f, -2.2f) * s, center + new Vector3(-0.2f, 0.3f, 0f) * s, 40f);
            Shoot(cam, $"{label}_top", center + new Vector3(0.05f, 1.8f, -0.6f) * s, center, 40f);
        }
        catch (System.Exception error)
        {
            Debug.LogError($"[AttackTrailDebug] {label} 失败: {error}");
        }
        finally
        {
            if (rig != null) Object.DestroyImmediate(rig);
            if (root != null) Object.DestroyImmediate(root);
        }
    }

    // 轴向探测，两层：
    //   1. 数值法（定论）：把每个 SkinnedMeshRenderer 烘焙到当前姿势，找离跟随骨骼最远的顶点，
    //      它在骨骼局部空间的归一化方向就是刀尖轴向（武器离握持骨最远的一端必然是尖端）。
    //   2. 出图法（目检）：同一摆动分别用 X/Y/Z 当刀尖轴出三张图，弧最像刀光的那根轴即答案。
    private static void ProbePlayerBladeAxis()
    {
        MeasureBladeAxis(PlayerPrefabPath, "player", "joint10", "joint9", "CC_Base_R_Hand");
        MeasureBladeAxis(EnemyTestPrefabPath, "enemy_test", "arm right wrist", "arm right forearm");
        MeasureBladeAxis(EnemyHeavyPrefabPath, "enemy_heavy", "hand_R", "gun_R");

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
        if (prefab == null) return;
        foreach (string axis in new[] { "x", "y", "z" })
        {
            GameObject root = null;
            GameObject rig = null;
            try
            {
                root = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                AttackTrailVfx trail = root.GetComponentInChildren<AttackTrailVfx>(true);
                Transform anchor = FindByName(root.transform, "joint10", "joint9", "CC_Base_R_Hand");
                if (trail == null || anchor == null) return;

                Vector3 tip = axis == "x" ? new Vector3(0.5f, 0f, 0f)
                    : axis == "y" ? new Vector3(0f, 0.5f, 0f) : new Vector3(0f, 0f, 0.5f);
                trail.SetTipOffset(tip);

                rig = BuildCameraRig();
                Camera cam = rig.GetComponentInChildren<Camera>();
                trail.Play(2f);
                Quaternion baseRotation = anchor.localRotation;
                // 绕局部 X 摆：+Z/+Y 刀尖都能扫出弧，只有 +X 刀尖退化为直线，配合数值法看图足够
                for (int i = 0; i < 36; i++)
                {
                    anchor.localRotation = baseRotation * Quaternion.Euler(-5f * i, 0f, 0f);
                    trail.Tick(1f / 60f);
                }
                Vector3 center = anchor.position + anchor.up * 0.3f;
                Shoot(cam, $"player_axis_{axis}", center + new Vector3(0.9f, 0.25f, -1.1f), center, 32f);
            }
            finally
            {
                if (rig != null) Object.DestroyImmediate(rig);
                if (root != null) Object.DestroyImmediate(root);
            }
        }
    }

    // 角色包围盒高度（取所有蒙皮网格的世界 AABB 并集），给相机取景做体型缩放。
    private static float CharacterHeight(GameObject root)
    {
        bool any = false;
        Bounds bounds = default;
        foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (!any) { bounds = renderer.bounds; any = true; }
            else bounds.Encapsulate(renderer.bounds);
        }
        return any ? Mathf.Max(0.5f, bounds.size.y) : 1.8f;
    }

    // 摆动轴必须与刀尖轴垂直（平行时刀尖画不出弧，采样全被"没位移"过滤）。
    // 取与刀尖方向夹角最大的局部坐标轴。
    private static Vector3 SwingAxisAvoiding(Vector3 tipOffset)
    {
        Vector3 tip = tipOffset.sqrMagnitude > 0.0001f ? tipOffset.normalized : Vector3.forward;
        Vector3 best = Vector3.right;
        float bestDot = 2f;
        foreach (Vector3 axis in new[] { Vector3.right, Vector3.up, Vector3.forward })
        {
            float d = Mathf.Abs(Vector3.Dot(tip, axis));
            if (d < bestDot) { bestDot = d; best = axis; }
        }
        return best;
    }

    // 数值法测刀尖轴向：每个蒙皮网格的世界 AABB 上离跟随骨骼最远的角 ≈ 武器尖端。
    // 全程世界空间 API + InverseTransformPoint（与 TransformPoint 严格互逆），不经过 BakeMesh，
    // 天然没有 FBX 缩放坑；TipOffset候选 可直接填进 AttackTrailSetup 的 *TipOffset。
    private static void MeasureBladeAxis(string prefabPath, string label, params string[] anchors)
    {
        GameObject root = null;
        try
        {
            root = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath));
            Transform anchor = FindByName(root.transform, anchors);
            if (anchor == null) { Debug.LogError($"[AttackTrailDebug] {label}: 探测找不到骨骼"); return; }

            Debug.Log($"[AttackTrailDebug] {label}: 骨骼={anchor.name} lossyScale={anchor.lossyScale:F4}");
            foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Bounds bounds = renderer.bounds;
                float best = -1f;
                Vector3 bestLocal = Vector3.zero;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = new Vector3(
                        (i & 1) != 0 ? bounds.max.x : bounds.min.x,
                        (i & 2) != 0 ? bounds.max.y : bounds.min.y,
                        (i & 4) != 0 ? bounds.max.z : bounds.min.z);
                    Vector3 local = anchor.InverseTransformPoint(corner);
                    float d = local.sqrMagnitude;
                    if (d > best) { best = d; bestLocal = local; }
                }
                Debug.Log($"[AttackTrailDebug] {label}: renderer={renderer.name} scale={renderer.transform.lossyScale:F3} "
                    + $"包围盒尺寸={bounds.size:F3} 中心距骨骼={Vector3.Distance(bounds.center, anchor.position):F3} "
                    + $"TipOffset候选(局部)={bestLocal:F3}");
            }
        }
        catch (System.Exception error)
        {
            Debug.LogError($"[AttackTrailDebug] {label} 轴向探测失败: {error.Message}");
        }
        finally
        {
            if (root != null) Object.DestroyImmediate(root);
        }
    }

    // 独立的相机 + 灯 rig：刀光是自发光材质，环境给个 Flat 底光避免全黑
    private static GameObject BuildCameraRig()
    {
        var rig = new GameObject("AttackTrailDebugRig");
        var camGo = new GameObject("DebugCam");
        camGo.transform.SetParent(rig.transform, false);
        Camera cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.06f, 0.07f, 0.09f, 1f);
        cam.fieldOfView = 40f;
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 50f;

        var lightGo = new GameObject("DebugLight");
        lightGo.transform.SetParent(rig.transform, false);
        Light light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.5f;
        lightGo.transform.rotation = Quaternion.Euler(35f, -40f, 0f);
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.4f, 0.42f, 0.46f);
        return rig;
    }

    private static Transform FindByName(Transform root, params string[] names)
    {
        foreach (string name in names)
            foreach (Transform candidate in root.GetComponentsInChildren<Transform>(true))
                if (candidate.name == name) return candidate;
        return null;
    }

    private static void Shoot(Camera cam, string name, Vector3 eye, Vector3 target, float fov)
    {
        const int W = 720, H = 720;
        cam.fieldOfView = fov;
        cam.transform.position = eye;
        cam.transform.rotation = Quaternion.LookRotation(target - eye);
        var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        RenderTexture previous = cam.targetTexture;
        RenderTexture active = RenderTexture.active;
        try
        {
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            File.WriteAllBytes(Path.Combine(OutDir, name + ".png"), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            Debug.Log($"[AttackTrailDebug] 出图 {name}.png");
        }
        finally
        {
            cam.targetTexture = previous;
            RenderTexture.active = active;
            rt.Release();
            Object.DestroyImmediate(rt);
        }
    }
}
