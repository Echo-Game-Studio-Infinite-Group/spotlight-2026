using UnityEngine;

namespace GameJam.Flow
{
    [CreateAssetMenu(menuName = "超高速行者/流程/差分", fileName = "Variant")]
    public sealed class RoomVariantDefinition : ScriptableObject
    {
        [InspectorName("差分编号")] public string Id;
        [InspectorName("所属箱庭")] public RoomDefinition Room;
        [InspectorName("混乱度下限")] public int MinimumChaos;
        [InspectorName("混乱度上限")] public int MaximumChaos = 100;
        [InspectorName("启用内容组")] public string[] ContentGroups = new string[0];
        [InspectorName("叙事文本"), TextArea] public string Narrative;
        [InspectorName("应用灯光颜色")] public bool OverrideLightColor;
        [InspectorName("灯光颜色")] public Color LightColor = Color.white;
        [InspectorName("灯光强度倍率"), Min(0f)] public float LightIntensityMultiplier = 1f;
        [InspectorName("稳定装置增量"), Min(0)] public int StabilizerGain;
        public bool Supports(int chaos) => chaos >= MinimumChaos && chaos <= MaximumChaos;
    }
}
