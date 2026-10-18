using UnityEngine;

namespace GameJam.Flow
{
    [CreateAssetMenu(menuName = "超高速行者/流程/差分", fileName = "Variant")]
    public sealed class RoomVariantDefinition : ScriptableObject
    {
        [Tooltip("差分的唯一编号，用于抽房、访问记录及表格导入匹配。")]
        [InspectorName("差分编号")] public string Id;
        [Tooltip("此差分所属的箱庭；同时需要把本差分登记到该箱庭的差分列表。")]
        [InspectorName("所属箱庭")] public RoomDefinition Room;
        [Tooltip("允许使用此差分的最低混乱度，包含此值。")]
        [InspectorName("混乱度下限")] public int MinimumChaos;
        [Tooltip("允许使用此差分的最高混乱度，包含此值。")]
        [InspectorName("混乱度上限")] public int MaximumChaos = 100;
        [Tooltip("填写模板中内容组的编号；只有列出的组会启用，仅当前房逻辑还会受房间激活状态限制。")]
        [InspectorName("启用内容组")] public string[] ContentGroups = new string[0];
        [Tooltip("应用到模板所登记叙事文字上的文本。")]
        [InspectorName("叙事文本"), TextArea] public string Narrative;
        [Tooltip("勾选后用下方颜色替换模板内灯光颜色。")]
        [InspectorName("应用灯光颜色")] public bool OverrideLightColor;
        [Tooltip("启用灯光颜色覆盖后使用的颜色。")]
        [InspectorName("灯光颜色")] public Color LightColor = Color.white;
        [Tooltip("模板内灯光原始强度乘以此值；1 表示原始强度。")]
        [InspectorName("灯光强度倍率"), Min(0f)] public float LightIntensityMultiplier = 1f;
        [Tooltip("本次访问击碎稳定装置后增加的混乱度。同一次访问只结算一次；普通池填 0，变化池填正整数。")]
        [InspectorName("稳定装置增量"), Min(0)] public int StabilizerGain;
        [Tooltip("按地图模板中的出生点编号指定敌人。新增点位默认关闭，每次访问在启用点位生成一只敌人。")]
        [InspectorName("出生点敌人配置")] public FlowEnemySpawnBinding[] EnemySpawns = new FlowEnemySpawnBinding[0];
        public bool Supports(int chaos) => chaos >= MinimumChaos && chaos <= MaximumChaos;
    }
}
