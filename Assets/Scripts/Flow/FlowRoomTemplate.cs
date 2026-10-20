using UnityEngine;
using UnityEngine.AI;

namespace GameJam.Flow
{
    public sealed class FlowRoomTemplate : MonoBehaviour
    {
        [Tooltip("入口锚点，必须是模板根节点的直接子节点，朝向房间内部，位于门口地板处。")]
        [InspectorName("入口（朝房内）")] public Transform Entry;
        [Tooltip("出口锚点，必须是模板根节点的直接子节点，朝向房间外部，位于门口地板处。")]
        [InspectorName("出口（朝房外）")] public Transform Exit;
        [Tooltip("入口保护门板，由流程控制开关。")]
        [InspectorName("入口门板")] public GameObject EntryDoor;
        [Tooltip("出口保护门板，后继房间装配完成后才开放。")]
        [InspectorName("出口门板")] public GameObject ExitDoor;
        [Tooltip("入口门洞净宽与净高，单位为米。")]
        [InspectorName("入口净尺寸")] public Vector2 EntrySize = new Vector2(6f, 5f);
        [Tooltip("出口门洞净宽与净高，单位为米。")]
        [InspectorName("出口净尺寸")] public Vector2 ExitSize = new Vector2(6f, 5f);
        [Tooltip("模板局部坐标中的几何范围，用于后继空间装配和重叠检查。")]
        [InspectorName("局部几何包围盒")] public Bounds GeometryBounds;
        [Tooltip("相对房间原点的局部高度；玩家低于此高度时触发裂隙掉落处理。")]
        [InspectorName("局部掉落高度")] public float FallHeight = -10f;
        [Tooltip("按模板原点和姿态烘焙的导航数据，运行时随房间位置与旋转注册。")]
        [InspectorName("导航数据")] public NavMeshData Navigation;
        [Tooltip("接收当前差分叙事文本的场景文字组件。")]
        [InspectorName("叙事文字")] public TextMesh[] Narratives = new TextMesh[0];
        [Tooltip("最终房出口的终点判定区域，摆放在出口门洞里；留空表示该模板没有终点。")]
        [InspectorName("终点判定区域")] public FlowFinishArea FinishArea;
        public FlowCheckpoint[] Checkpoints { get; private set; }
        public RoomVariantController Variants { get; private set; }
        public FlowRoomEnemies Enemies { get; private set; }
        public FlowRoomTexts Texts { get; private set; }
        private NavMeshDataInstance _navigationInstance;

        public NavMeshDataInstance RegisterNavigation()
        {
            if (_navigationInstance.valid) _navigationInstance.Remove();
            if (Navigation != null) _navigationInstance = NavMesh.AddNavMeshData(Navigation, transform.position, transform.rotation);
            return _navigationInstance;
        }
        private void OnDisable() { Enemies?.SetCurrent(false); Texts?.SetCurrent(false); if (_navigationInstance.valid) _navigationInstance.Remove(); }

        public void Bind(RunFlowController flow, FlowRoomInstance instance)
        {
            Checkpoints = GetComponentsInChildren<FlowCheckpoint>(true);
            Variants = new RoomVariantController(this);
            Enemies = new FlowRoomEnemies(this, instance.Variant);
            Texts = new FlowRoomTexts(this, instance.Variant);
            foreach (StabilityDevice device in GetComponentsInChildren<StabilityDevice>(true)) device.Bind(flow, instance);
            Variants.Apply(instance.Variant, false);
            Vector3 entryScale = EntryDoor.transform.localScale, exitScale = ExitDoor.transform.localScale;
            entryScale.z = exitScale.z = flow.Config.GateThickness;
            EntryDoor.transform.localScale = entryScale; ExitDoor.transform.localScale = exitScale;
            SetEntryOpen(true); SetExitOpen(false);
        }
        public void SetEntryOpen(bool open) => EntryDoor.SetActive(!open);
        public void SetCurrent(bool current) { Variants.SetCurrent(current); Enemies.SetCurrent(current); Texts.SetCurrent(current); }
        public void SetExitOpen(bool open) => ExitDoor.SetActive(!open);
        public FlowCheckpoint FindCheckpoint(string id)
        {
            foreach (FlowCheckpoint checkpoint in Checkpoints) if (checkpoint.Id == id) return checkpoint;
            return null;
        }
    }
}
