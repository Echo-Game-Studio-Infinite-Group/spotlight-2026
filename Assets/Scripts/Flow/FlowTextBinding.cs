using System;
using UnityEngine;

namespace GameJam.Flow
{
    public enum FlowTextTrigger { [InspectorName("进入镜头")] Visible, [InspectorName("进入当前房间")] RoomEntered }
    public enum FlowTextDisplay { [InspectorName("播放后消失")] Timed, [InspectorName("出现后保持")] Persistent }

    [Serializable]
    public sealed class FlowTextBinding
    {
        [InspectorName("文字点编号")] public string PointId;
        [InspectorName("启用")] public bool Enabled;
        [InspectorName("文字内容"), TextArea(3, 8)] public string Content;
        [InspectorName("字号"), Min(.1f)] public float FontSize = 36f;
        [InspectorName("颜色")] public Color Color = Color.white;
        [InspectorName("触发方式")] public FlowTextTrigger Trigger;
        [InspectorName("最大触发距离"), Tooltip("单位为米，0 表示不限制距离。"), Min(0f)] public float MaximumDistance;
        [InspectorName("显示方式")] public FlowTextDisplay Display;
        [InspectorName("停留时间"), Min(0f)] public float HoldSeconds = 2f;
        [InspectorName("出现效果"), Tooltip("留空使用文字点的默认效果；效果资产可从现有动画重新读取曲线。")] public FlowTextAnimation Appearance;
        [InspectorName("消失效果")] public FlowTextAnimation Disappearance;
        [InspectorName("出现速度"), Min(.01f)] public float AppearanceSpeed = 1f;
        [InspectorName("消失速度"), Min(.01f)] public float DisappearanceSpeed = 1f;
    }
}
