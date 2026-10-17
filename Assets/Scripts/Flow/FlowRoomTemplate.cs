using UnityEngine;
using UnityEngine.AI;

namespace GameJam.Flow
{
    public sealed class FlowRoomTemplate : MonoBehaviour
    {
        [InspectorName("入口（朝房内）")] public Transform Entry;
        [InspectorName("出口（朝房外）")] public Transform Exit;
        [InspectorName("入口门板")] public GameObject EntryDoor;
        [InspectorName("出口门板")] public GameObject ExitDoor;
        [InspectorName("入口净尺寸")] public Vector2 EntrySize = new Vector2(6f, 5f);
        [InspectorName("出口净尺寸")] public Vector2 ExitSize = new Vector2(6f, 5f);
        [InspectorName("局部几何包围盒")] public Bounds GeometryBounds;
        [InspectorName("局部掉落高度")] public float FallHeight = -10f;
        [InspectorName("导航数据")] public NavMeshData Navigation;
        [InspectorName("叙事文字")] public TextMesh[] Narratives = new TextMesh[0];
        [InspectorName("终点半径"), Min(0.1f)] public float FinishRadius = 3f;
        public FlowCheckpoint[] Checkpoints { get; private set; }
        public RoomVariantController Variants { get; private set; }
        private NavMeshDataInstance _navigationInstance;

        public NavMeshDataInstance RegisterNavigation()
        {
            if (_navigationInstance.valid) _navigationInstance.Remove();
            if (Navigation != null) _navigationInstance = NavMesh.AddNavMeshData(Navigation, transform.position, transform.rotation);
            return _navigationInstance;
        }
        private void OnDisable() { if (_navigationInstance.valid) _navigationInstance.Remove(); }

        public void Bind(RunFlowController flow, FlowRoomInstance instance)
        {
            Checkpoints = GetComponentsInChildren<FlowCheckpoint>(true);
            Variants = new RoomVariantController(this);
            foreach (StabilityDevice device in GetComponentsInChildren<StabilityDevice>(true)) device.Bind(flow, instance);
            Variants.Apply(instance.Variant, false);
            Vector3 entryScale = EntryDoor.transform.localScale, exitScale = ExitDoor.transform.localScale;
            entryScale.z = exitScale.z = flow.Config.GateThickness;
            EntryDoor.transform.localScale = entryScale; ExitDoor.transform.localScale = exitScale;
            SetEntryOpen(true); SetExitOpen(false);
        }
        public void SetEntryOpen(bool open) => EntryDoor.SetActive(!open);
        public void SetExitOpen(bool open) => ExitDoor.SetActive(!open);
        public FlowCheckpoint FindCheckpoint(string id)
        {
            foreach (FlowCheckpoint checkpoint in Checkpoints) if (checkpoint.Id == id) return checkpoint;
            return null;
        }
    }
}
