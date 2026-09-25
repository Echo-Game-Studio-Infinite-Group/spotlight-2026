using UnityEngine;

// 判定框 profile：站立/滑铲/闪避/攻击等形态共用的纯数据结构 + gizmo 预览
// 判定算法为 active-frame 窗口 + Layer 碰撞矩阵 + CapsuleCast（算法清单第三节），本体由战斗层生成
[CreateAssetMenu(fileName = "HitboxProfile", menuName = "超高速行者/HitboxProfile")]
public class HitboxProfile : ScriptableObject
{
    public enum Kind { Standing, Sliding, Dodge, Attack }

    [SerializeField] private Kind _kind;
    [Tooltip("本地空间中心；角色 pivot 约定在脚底，向上为正")]
    [SerializeField] private Vector3 _center = new Vector3(0f, 1f, 0f);
    [SerializeField] private Vector3 _size = new Vector3(1f, 2f, 1f);
    [Tooltip("攻击框是否带 parry 判定（策划案第 5-6 节：连斩无 parry，普攻/闪斩/推斩有）")]
    [SerializeField] private bool _parryable;
    [Tooltip("对投/飞行道具无敌（滑铲形态）")]
    [SerializeField] private bool _throwImmune;

    public Kind ProfileKind => _kind;
    public Vector3 Center => _center;
    public Vector3 Size => _size;
    public bool Parryable => _parryable;
    public bool ThrowImmune => _throwImmune;

    // 在拥有者本地空间画线框；由持有该 profile 的组件在 OnDrawGizmos 里调用
    public void DrawGizmos(Transform owner, Color color)
    {
        Gizmos.color = color;
        Matrix4x4 oldMatrix = Gizmos.matrix;
        Gizmos.matrix = owner.localToWorldMatrix;
        Gizmos.DrawWireCube(_center, _size);
        Gizmos.matrix = oldMatrix;
    }
}
