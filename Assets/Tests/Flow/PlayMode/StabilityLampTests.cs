#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using GameJam.Flow;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace GameJam.FlowTests
{
    public sealed class StabilityLampTests
    {
        private const string PrefabPath = "Assets/Prefabs/Props/StabilityLamp.prefab";
        private RunFlowController _flow;

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_flow != null) yield return _flow.ReleaseRooms();
            SaveManager.CancelContinue(); LevelSelection.Clear(); TimeManager.SetPaused(false);
        }

        [Test]
        public void AllExistingDevicesUseReadableLampMeshesAndTheSharedCutEffect()
        {
            int count = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[]
                { "Assets/Settings/FormalFlow/Templates", "Assets/Settings/FlowExamples/Templates" }))
            {
                var root = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                foreach (StabilityDevice device in root.GetComponentsInChildren<StabilityDevice>(true))
                {
                    count++;
                    Assert.That(device.BreakEffect, Is.Not.Null, root.name);
                    Assert.That(device.BreakEffect.GetSetupError(), Is.Null, root.name);
                    Assert.That(device.GetComponent<EnemyBase>(), Is.Null, root.name);
                    Assert.That(device.GetComponent<CameraWallFadeIgnore>(), Is.Not.Null, root.name);
                    Assert.That(device.GetComponentsInChildren<SphereCollider>(true), Is.Empty, root.name);
                    Assert.That(device.Visual.GetComponentInChildren<MeshFilter>(true).sharedMesh.vertexCount, Is.GreaterThan(10000), root.name);
                }
            }
            Assert.That(count, Is.EqualTo(6));
        }

        [UnityTest]
        public IEnumerator StaticLampSlicesWithoutEnemyAndPiecesExpireUsingWorldTime()
        {
            var scene = SceneManager.CreateScene("LampSliceRegression");
            var root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath));
            SceneManager.MoveGameObjectToScene(root, scene);
            root.transform.SetPositionAndRotation(new Vector3(3000, 15, 3000), Quaternion.Euler(0, 37, 0));
            var gib = root.GetComponentInChildren<GibComponent>(true);
            try
            {
                TimeManager.SetPaused(false);
                Assert.That(root.GetComponentsInChildren<EnemyBase>(true), Is.Empty);
                Assert.That(gib.GetSetupError(), Is.Null);
                var settings = new SerializedObject(gib);
                settings.FindProperty("_deathChance").floatValue = 0;
                settings.FindProperty("_lifetime").floatValue = .2f;
                settings.ApplyModifiedPropertiesWithoutUndo();
                TimeManager.Apply(TimeManager.TimeLayer.World, 0f, owner: this);
                Assert.That(gib.TrySlice(Vector3.forward), Is.False);
                Assert.That(gib.TrySliceDestructible(Vector3.forward), Is.True);
                Assert.That(gib.TrySliceDestructible(Vector3.forward), Is.False);
                var pieces = scene.GetRootGameObjects().Select(o => o.GetComponent<GibPieceLifetime>()).Where(p => p != null).ToArray();
                Assert.That(pieces.Length, Is.EqualTo(2));
                Assert.That(pieces.SelectMany(p => p.GetComponentsInChildren<MeshRenderer>()).All(r => r.sharedMaterials.All(m => m.shader.name == "GameJam/EnemyWaistCutout")), Is.True);
                Assert.That(root.GetComponentsInChildren<Renderer>().All(r => !r.enabled), Is.True);
                yield return new WaitForSecondsRealtime(.25f);
                Assert.That(pieces.All(p => p != null), Is.True, "世界暂停时不能提前回收碎块");
                TimeManager.Release(TimeManager.TimeLayer.World, this);
                float deadline = Time.realtimeSinceStartup + 3f;
                while (pieces.Any(p => p != null) && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.That(pieces.All(p => p == null), Is.True);
                // 回收后仍启用原组件，验证它不会继续访问已经销毁的网格或 Transform。
                yield return new WaitForFixedUpdate(); yield return null;
                gib.ResetEffect();
                Assert.That(root.GetComponentsInChildren<Renderer>().All(r => r.enabled), Is.True);
            }
            finally
            {
                TimeManager.Release(TimeManager.TimeLayer.World, this);
                Object.DestroyImmediate(root);
                SceneManager.UnloadSceneAsync(scene);
            }
        }

        [UnityTest]
        public IEnumerator FlowLampRewardsOnceAndRecoveryDoesNotReplayTheCut()
        {
            var config = AssetDatabase.LoadAssetAtPath<RunFlowConfig>("Assets/Settings/FormalFlow/RunFlow.asset");
            RoomVariantDefinition original = config.InitialRoom;
            RunFlowController flow = null;
            try
            {
                SaveManager.CancelContinue(); LevelSelection.Clear(); TimeManager.SetPaused(false);
                config.InitialRoom = AssetDatabase.LoadAssetAtPath<RoomVariantDefinition>("Assets/Settings/FormalFlow/BOX_S_003.default.asset");
                yield return EditorSceneManager.LoadSceneAsyncInPlayMode("Assets/Scenes/FormalFlowGame.unity", new LoadSceneParameters(LoadSceneMode.Single));
                flow = RunFlowController.Current;
                _flow = flow;
                yield return Ready(flow);
                var device = flow.CurrentRoom.Root.GetComponentInChildren<StabilityDevice>();
                Assert.That(device, Is.Not.Null);
                device.BreakHealth = 2f; device.Bind(flow, flow.CurrentRoom);
                int before = flow.Chaos.Value;
                device.ReceiveAttack(new AttackDamageRequest(1, device.transform.position, Vector3.right, 1, 0));
                Assert.That(device.IsBroken, Is.False); Assert.That(device.BreakEffect.IsSliced, Is.False);
                Assert.That(flow.Chaos.Value, Is.EqualTo(before));
                device.ReceiveAttack(new AttackDamageRequest(1, device.transform.position, Vector3.right, 2, 0));
                Assert.That(device.IsBroken, Is.True); Assert.That(device.BreakEffect.IsSliced, Is.True);
                Assert.That(device.Visual.activeSelf, Is.False);
                Assert.That(device.GetComponentsInChildren<Collider>(true).All(c => !c.enabled), Is.True);
                Assert.That(device.GetComponentsInChildren<Light>(true).All(l => !l.enabled), Is.True);
                int expected = before + flow.CurrentRoom.Variant.StabilizerGain;
                Assert.That(flow.Chaos.Value, Is.EqualTo(expected));
                device.ReceiveAttack(new AttackDamageRequest(100, device.transform.position, Vector3.right, 3, 0));
                Assert.That(flow.Chaos.Value, Is.EqualTo(expected));
                yield return Ready(flow);
                Assert.That(flow.Recover(false), Is.True);
                yield return Ready(flow);
                device = flow.CurrentRoom.Root.GetComponentInChildren<StabilityDevice>(true);
                Assert.That(device.IsBroken, Is.True); Assert.That(device.Visual.activeSelf, Is.False);
                Assert.That(device.BreakEffect.IsSliced, Is.False, "恢复已破坏状态不能重新生成碎块");
                Assert.That(flow.Chaos.Value, Is.EqualTo(expected));
            }
            finally { config.InitialRoom = original; }
        }

        private static IEnumerator Ready(RunFlowController flow)
        {
            float deadline = Time.realtimeSinceStartup + 30;
            while (!flow.IsReady || flow.IsBusy || flow.IsPreparing)
            {
                Assert.That(Time.realtimeSinceStartup, Is.LessThan(deadline), flow.LastError);
                yield return null;
            }
        }
    }
}
#endif
