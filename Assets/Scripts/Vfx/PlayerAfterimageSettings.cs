using UnityEngine;

[CreateAssetMenu(fileName = "PlayerAfterimages", menuName = "超高速行者/玩家残影参数")]
public sealed class PlayerAfterimageSettings : ScriptableObject
{
    public const int MaximumPoolSize = 8;

    [Header("轨迹采样（玩家逻辑时间）")]
    [Tooltip("同时保留的人形上限；网格预分配并循环复用。")]
    [Range(1, MaximumPoolSize)] public int MaxSnapshots = 6;
    [Tooltip("每个人形从出现到消失的秒数。")]
    [Min(0.01f)] public float Lifetime = 0.6f;
    [Tooltip("两次姿态快照的最短间隔。")]
    [Min(0.01f)] public float SampleInterval = 0.05f;
    [Tooltip("两次采样间至少移动的路径距离；避免贴墙或静止时堆叠。")]
    [Min(0f)] public float MinDistance = 0.5f;
    [Header("蓝色半透明人形")]
    public Material Material;
    [ColorUsage(true, true)] public Color BodyColor = new Color(0.14435479f, 0.71129894f, 1.3115677f, 0.32f);
    [ColorUsage(true, true)] public Color EdgeColor = new Color(0.08f, 0.85f, 3f, 0.65f);
    [Range(0f, 1f)] public float Opacity = 0.843f;
    [Range(0.1f, 8f)] public float EdgePower = 0.96f;
    // 保留当前配表的尾部平台；寿命到期由快照回收负责，不在曲线末尾补零。
    public AnimationCurve Fade = new AnimationCurve(
        new Keyframe(0f, 1f, 0f, 0f),
        new Keyframe(0.69383293f, 0.5308183f, 0f, 0f));
}
