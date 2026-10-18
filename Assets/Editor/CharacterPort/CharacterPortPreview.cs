// 迁移后的可视化预览：把 Player.prefab 的模型拍成 PNG，供人眼核验（材质/贴图/布料骨）。
// 只在编辑器里用，验收后可随 CharacterPortTool 一起删除。
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class CharacterPortPreview
{
    const string PlayerPrefabPath = "Assets/Prefabs/Player.prefab";
    const string OutDir = "Library/CharacterPort";

    [MenuItem("超高速行者/移植/4. 渲染预览 PNG")]
    public static void Render()
    {
        Directory.CreateDirectory(OutDir);
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
        if (prefab == null) { Debug.LogError("[CharacterPort] 找不到 Player.prefab"); return; }

        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        GameObject rig = null;
        try
        {
            go.transform.position = Vector3.zero;
            go.transform.rotation = Quaternion.identity;
            // 关掉旧模型残留的渲染器，只留新骨架的
            foreach (var r in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (r.gameObject.name.StartsWith("Alpha_")) r.enabled = false;
            var animator = go.GetComponent<Animator>();
            if (animator != null && animator.avatar != null) animator.Rebind();

            rig = new GameObject("PreviewRig");
            var cam = new GameObject("PreviewCamera").AddComponent<Camera>();
            cam.transform.SetParent(rig.transform, false);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.08f, 0.09f, 0.11f, 1f);
            cam.fieldOfView = 32f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 50f;
            cam.allowHDR = false;

            var lightGo = new GameObject("PreviewLight");
            lightGo.transform.SetParent(rig.transform, false);
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.6f;
            light.color = new Color(1f, 0.96f, 0.9f);
            lightGo.transform.rotation = Quaternion.Euler(38f, -35f, 0f);

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.35f, 0.37f, 0.42f);

            Shots(cam, "front_full", new Vector3(0f, 1.05f, -3.2f), new Vector3(0f, 0.95f, 0f), 32f);
            Shots(cam, "front_head", new Vector3(0.25f, 1.60f, -1.0f), new Vector3(0f, 1.52f, 0f), 30f);
            Shots(cam, "back_full", new Vector3(0f, 1.05f, 3.2f), new Vector3(0f, 0.95f, 0f), 32f);
            Shots(cam, "hips_cloth", new Vector3(0.9f, 1.05f, -1.4f), new Vector3(0f, 0.95f, 0f), 35f);
            Shots(cam, "hand_sword", new Vector3(-0.8f, 1.15f, -1.0f), new Vector3(0.1f, 1.05f, 0f), 40f);
        }
        catch (Exception e) { Debug.LogError("[CharacterPort] 预览渲染失败: " + e); }
        finally
        {
            if (rig != null) UnityEngine.Object.DestroyImmediate(rig);
            UnityEngine.Object.DestroyImmediate(go);
        }
        Debug.Log("[CharacterPort] 预览完成 -> " + OutDir);
    }

    static void Shots(Camera cam, string name, Vector3 eye, Vector3 target, float fov)
    {
        const int W = 720, H = 900;
        cam.fieldOfView = fov;
        cam.transform.position = eye;
        cam.transform.rotation = Quaternion.LookRotation(target - eye);
        var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        var prev = cam.targetTexture;
        var prevActive = RenderTexture.active;
        try
        {
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            File.WriteAllBytes(Path.Combine(OutDir, name + ".png"), tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }
        finally
        {
            cam.targetTexture = prev;
            RenderTexture.active = prevActive;
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
        }
        Debug.Log("[CharacterPort] 出图 " + name + ".png");
    }
}
