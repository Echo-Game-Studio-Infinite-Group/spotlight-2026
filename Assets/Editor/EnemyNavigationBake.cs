using System;
using System.IO;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

public static class EnemyNavigationBake
{
    [MenuItem("超高速行者/导航/烘焙两个测试场景")]
    public static void BakeBoth()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorApplication.playModeStateChanged -= OnStopped;
            EditorApplication.playModeStateChanged += OnStopped;
            EditorApplication.isPlaying = false;
            return;
        }
        // 切场景前保存当前工作，避免丢失尚未落盘的布局。
        if (!EditorSceneManager.SaveOpenScenes()) throw new InvalidOperationException("当前场景保存失败");
        var setup = EditorSceneManager.GetSceneManagerSetup();
        try
        {
            Bake("Assets/Scenes/AnimTestScene.unity");
            Bake("Assets/Scenes/TestScene.unity");
        }
        finally { EditorSceneManager.RestoreSceneManagerSetup(setup); }
    }

    private static void OnStopped(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredEditMode) return;
        EditorApplication.playModeStateChanged -= OnStopped;
        EditorApplication.delayCall += BakeBoth;
    }

    private static void Bake(string path)
    {
        Scene scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
        var surface = UnityEngine.Object.FindObjectOfType<NavMeshSurface>();
        if (surface == null) surface = new GameObject("NavMesh").AddComponent<NavMeshSurface>();
        surface.collectObjects = CollectObjects.All;
        surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
        surface.ignoreNavMeshAgent = true;
        surface.ignoreNavMeshObstacle = true;
        foreach (var player in UnityEngine.Object.FindObjectsOfType<PlayerMotor>())
        {
            var modifier = player.GetComponent<NavMeshModifier>();
            if (modifier == null) modifier = player.gameObject.AddComponent<NavMeshModifier>();
            modifier.ignoreFromBuild = true;
            EditorUtility.SetDirty(modifier);
            PrefabUtility.RecordPrefabInstancePropertyModifications(modifier);
        }
        var enemies = UnityEngine.Object.FindObjectsOfType<Enemy>();
        foreach (var enemy in enemies)
        {
            var agent = enemy.GetComponent<NavMeshAgent>();
            if (agent == null)
            {
                agent = enemy.gameObject.AddComponent<NavMeshAgent>();
                var collider = enemy.GetComponent<CharacterController>();
                if (collider != null)
                {
                    agent.height = collider.height;
                    agent.radius = collider.radius;
                    agent.baseOffset = collider.height * .5f - collider.center.y;
                }
            }
            agent.agentTypeID = surface.agentTypeID;
            agent.enabled = true;
            agent.speed = enemy.WalkSpeed;
            agent.stoppingDistance = Mathf.Max(0f, enemy.AttackRange - agent.radius);
            var settings = new SerializedObject(enemy);
            settings.FindProperty("_agent").objectReferenceValue = agent;
            settings.FindProperty("_chasePlayer").boolValue = true;
            settings.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(enemy);
            EditorUtility.SetDirty(agent);
            PrefabUtility.RecordPrefabInstancePropertyModifications(enemy);
            PrefabUtility.RecordPrefabInstancePropertyModifications(agent);
        }
        var previousData = surface.navMeshData;
        surface.BuildNavMesh();
        var data = surface.navMeshData;
        if (data == null) throw new InvalidOperationException(path + " 烘焙失败");
        data.name = "NavMesh-NavMesh";
        surface.RemoveData();
        if (previousData != null && AssetDatabase.Contains(previousData))
        {
            EditorUtility.CopySerialized(data, previousData);
            UnityEngine.Object.DestroyImmediate(data);
            data = previousData;
            EditorUtility.SetDirty(data);
        }
        else
        {
            string folder = Path.ChangeExtension(path, null).Replace('\\', '/');
            if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets/Scenes", scene.name);
            AssetDatabase.CreateAsset(data, folder + "/NavMesh-NavMesh.asset");
        }
        surface.navMeshData = data;
        surface.AddData();
        foreach (var enemy in enemies)
        {
            var agent = enemy.GetComponent<NavMeshAgent>();
            var filter = new NavMeshQueryFilter { agentTypeID = agent.agentTypeID, areaMask = agent.areaMask };
            Vector3 feet = enemy.transform.position - Vector3.up * agent.baseOffset;
            if (!NavMesh.SamplePosition(feet, out var hit, agent.height, filter))
                throw new InvalidOperationException(scene.name + "/" + enemy.name + " 出生点附近无导航网格");
            enemy.transform.position = hit.position + Vector3.up * agent.baseOffset;
            PrefabUtility.RecordPrefabInstancePropertyModifications(enemy.transform);
        }
        EditorUtility.SetDirty(surface);
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException(path + " 保存失败");
        Debug.Log($"[EnemyNavigationBake] {scene.name}: 已保存导航，敌人={enemies.Length}，所有出生点在网格上");
    }
}
