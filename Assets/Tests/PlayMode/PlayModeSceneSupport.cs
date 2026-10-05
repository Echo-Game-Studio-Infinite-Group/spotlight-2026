using UnityEngine;

// PlayMode 集成测试的场景约定。
//
// TestScene 里躺着两个敌人，但两者并不等价：
//   · MikuGandam 挂了 humanoid avatar（mixamo），GibComponent 也接了腰斩骨骼
//   · Miku 的 Animator 没有 avatar，GibComponent 的 _waist 是空的
// FindObjectOfType 返回哪一个取决于场景内部顺序，随机拿到 Miku 就会红。
// 所以凡是要验动画/腰斩的用例，都必须显式挑装配完整的那个，而不是碰运气。
internal static class PlayModeSceneSupport
{
    /// <summary>挑场景里挂了 humanoid avatar 的敌人；一个都没有时返回 null。</summary>
    internal static Enemy FindRiggedEnemy()
    {
        foreach (Enemy candidate in Object.FindObjectsOfType<Enemy>())
        {
            Animator animator = candidate.GetComponentInChildren<Animator>();
            if (animator != null && animator.isHuman) return candidate;
        }
        return null;
    }
}
