using System;
using UnityEngine;

namespace GameJam.Flow
{
    // 把原动画的材质与变换曲线保存为数据，避免 Animator 把共享字体材质重新写回渲染器。
    [CreateAssetMenu(menuName = "超高速行者/流程/文字动画效果", fileName = "TextEffect")]
    public sealed class FlowTextAnimation : ScriptableObject
    {
        [InspectorName("来源动画")] public AnimationClip Source;
        [InspectorName("动画时长"), Min(.001f)] public float Duration = 1f;
        [HideInInspector] public FloatTrack[] Tracks = new FloatTrack[0];
        [HideInInspector] public MaterialKey[] Materials = new MaterialKey[0];
        [HideInInspector] public float CutoffMinimum = -2f;
        [HideInInspector] public float CutoffMaximum = 2f;

        [Serializable]
        public sealed class FloatTrack
        {
            public string Property;
            public AnimationCurve Curve;
        }
        [Serializable]
        public sealed class MaterialKey
        {
            public float Time;
            public Material Material;
        }
    }
}
