using System;
using Cinemachine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// 玩家相机的震屏接线：vcam 上没有 CinemachineImpulseListener 时，敌人发的 impulse 没有接收端，
// 表现是「脚本跑了但相机纹丝不动」。这里把监听与 CameraShaker 一起落盘，
// 免得只靠运行时 AddComponent——那样 Inspector 里看不见，也没法调参。
// 幂等，可重复执行；执行前会退出 Play 模式。
public static class CameraShakeSetup
{
    private const string PlayerPrefabPath = "Assets/Prefabs/Player.prefab";
    private const string CombatScenePath = "Assets/Scenes/CombatTestScene.unity";

    [MenuItem("超高速行者/相机/接入震屏")]
    public static void Wire()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorApplication.playModeStateChanged -= AfterPlay;
            EditorApplication.playModeStateChanged += AfterPlay;
            EditorApplication.isPlaying = false;
            return;
        }
        if (!EditorSceneManager.SaveOpenScenes()) throw new InvalidOperationException("当前场景保存失败");
        var setup = EditorSceneManager.GetSceneManagerSetup();
        try
        {
            WirePlayerPrefab();
            WireScene(CombatScenePath);
        }
        finally { EditorSceneManager.RestoreSceneManagerSetup(setup); }
        AssetDatabase.SaveAssets();
        Debug.Log("[CameraShakeSetup] 玩家相机已接入震屏：ImpulseListener 就位，CameraShaker 已落盘");
    }

    private static void AfterPlay(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredEditMode) return;
        EditorApplication.playModeStateChanged -= AfterPlay;
        EditorApplication.delayCall += Wire;
    }

    private static void WirePlayerPrefab()
    {
        GameObject contents = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);
        try
        {
            if (!WireRigs(contents)) throw new InvalidOperationException("Player 预制体缺少接好 _virtualCamera 的 PlayerCameraRig");
            PrefabUtility.SaveAsPrefabAsset(contents, PlayerPrefabPath);
        }
        finally { PrefabUtility.UnloadPrefabContents(contents); }
    }

    private static void WireScene(string path)
    {
        Scene scene = SceneManager.GetSceneByPath(path);
        if (!scene.IsValid() || !scene.isLoaded) scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
        foreach (GameObject root in scene.GetRootGameObjects()) WireRigs(root);
        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException(path + " 保存失败");
    }

    // 按组件找而不是按预制体来源找：CombatTestScene 的玩家是手工搭的，不是 Player 预制体实例。
    private static bool WireRigs(GameObject scope)
    {
        bool wired = false;
        foreach (PlayerCameraRig rig in scope.GetComponentsInChildren<PlayerCameraRig>(true))
        {
            CinemachineVirtualCamera vcam = ReadVirtualCamera(rig);
            if (vcam == null) continue;
            CameraShaker shaker = vcam.GetComponent<CameraShaker>();
            if (shaker == null) shaker = vcam.gameObject.AddComponent<CameraShaker>();
            SerializedObject serialized = new SerializedObject(shaker);
            serialized.FindProperty("_virtualCamera").objectReferenceValue = vcam;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            CameraShaker.EnsureListener(vcam);
            // 改完必须 SetDirty 才会落盘；漏掉时内存值正确、磁盘是空的。
            EditorUtility.SetDirty(shaker);
            EditorUtility.SetDirty(vcam);
            foreach (Component component in vcam.GetComponents<Component>())
                if (component != null && PrefabUtility.IsPartOfPrefabInstance(component))
                    PrefabUtility.RecordPrefabInstancePropertyModifications(component);
            wired = true;
        }
        return wired;
    }

    // 沿用 PlayerCameraRig 已有的接线，不另找一台相机；它是私有序列化字段，只能走 SerializedObject。
    private static CinemachineVirtualCamera ReadVirtualCamera(PlayerCameraRig rig)
    {
        SerializedObject serialized = new SerializedObject(rig);
        return serialized.FindProperty("_virtualCamera").objectReferenceValue as CinemachineVirtualCamera;
    }
}
