using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// 刀光拖尾的**自证工具**：在编辑器里（不用进 Play）把玩家预制体实例化、摆动剑骨、
// 手动推进粒子仿真并渲染成 PNG。用来回答「到底是没发射、还是看不见」——
// 控制台会打出粒子数与全部关键参数，图片落在 Library/AttackTrailDebug/（不进版本库）。
[InitializeOnLoad]
public static class AttackTrailDebug
{
    private const string PlayerPrefabPath = "Assets/Prefabs/Player.prefab";

    // 绝对路径：相对路径在编辑器进程里可能落到别的工作目录（之前出图就是这么丢的）
    private static string OutDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "AttackTrailDebug"));
    // 剑骨候选，与 AttackTrailSetup 保持一致
    private static readonly string[] Anchors = { "joint10", "joint9", "CC_Base_R_Hand" };

    static AttackTrailDebug() { }

    [MenuItem("超高速行者/战斗特效/调试：试放玩家刀光并出图")]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogWarning("[AttackTrailDebug] 先退出 Play 模式");
            return;
        }
        Directory.CreateDirectory(OutDir);

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
        if (prefab == null) { Debug.LogError("[AttackTrailDebug] 读不到 " + PlayerPrefabPath); return; }

        GameObject player = null;
        GameObject rig = null;
        try
        {
            player = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            player.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            AttackTrailVfx trail = player.GetComponentInChildren<AttackTrailVfx>(true);
            if (trail == null) { Debug.LogError("[AttackTrailDebug] 预制体里没有 AttackTrailVfx"); return; }
            ParticleSystem system = trail.System;
            if (system == null) { Debug.LogError("[AttackTrailDebug] AttackTrail 上没有 ParticleSystem"); return; }

            Transform anchor = null;
            foreach (string name in Anchors)
            {
                foreach (Transform candidate in player.GetComponentsInChildren<Transform>(true))
                    if (candidate.name == name) { anchor = candidate; break; }
                if (anchor != null) break;
            }
            if (anchor == null) { Debug.LogError("[AttackTrailDebug] 找不到剑骨"); return; }

            // 相机 + 灯：挂在独立的 rig 上，照右手扫过的区域
            rig = new GameObject("AttackTrailDebugRig");
            var cam = new GameObject("DebugCam").AddComponent<Camera>();
            cam.transform.SetParent(rig.transform, false);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.06f, 0.07f, 0.09f, 1f);
            cam.fieldOfView = 40f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 50f;

            var lightGo = new GameObject("DebugLight");
            lightGo.transform.SetParent(rig.transform, false);
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.5f;
            lightGo.transform.rotation = Quaternion.Euler(35f, -40f, 0f);
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.4f, 0.42f, 0.46f);

            // 摆动剑骨 + 推进仿真：手怎么挥，弧就怎么弯
            trail.Play(1.5f);
            Quaternion baseRotation = anchor.localRotation;
            const int steps = 36;
            for (int i = 0; i < steps; i++)
            {
                anchor.localRotation = baseRotation * Quaternion.Euler(0f, 0f, -6f * i);
                system.Simulate(1f / 60f, true, i == 0);
            }

            var psRenderer = system.GetComponent<ParticleSystemRenderer>();
            string matInfo = psRenderer != null && psRenderer.sharedMaterial != null
                ? $"{psRenderer.sharedMaterial.name} (shader={psRenderer.sharedMaterial.shader.name})"
                : "null";
            Debug.Log($"[AttackTrailDebug] 材质={matInfo} 出图目录={OutDir}");
            Debug.Log($"[AttackTrailDebug] 剑骨={anchor.name} 实例位置={anchor.position} "
                + $"particleCount={system.particleCount} isPlaying={system.isPlaying} "
                + AttackTrailVfx.Describe(system));

            // 三张图：剑骨近景 / 整条弧 / 俯视
            Shoot(cam, "trail_close", anchor.position + new Vector3(0.9f, 0.25f, -1.1f), anchor.position, 32f);
            Shoot(cam, "trail_wide", anchor.position + new Vector3(1.7f, 0.6f, -2.2f), anchor.position + new Vector3(-0.2f, 0.3f, 0f), 40f);
            Shoot(cam, "trail_top", anchor.position + new Vector3(0.05f, 1.8f, -0.6f), anchor.position, 40f);

            Debug.Log("[AttackTrailDebug] 出图目录 -> " + Path.GetFullPath(OutDir));
        }
        catch (System.Exception error)
        {
            Debug.LogError("[AttackTrailDebug] 失败: " + error);
        }
        finally
        {
            if (rig != null) Object.DestroyImmediate(rig);
            if (player != null) Object.DestroyImmediate(player);
        }
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
