using UnityEngine;

// 受击契约：任何能被玩家打中的东西都实现它，攻击检测只认这个接口，不关心具体是谁。
// 约束：实现方必须自身或同一个 GameObject 上有 Collider，否则攻击检测的 Overlap 找不到它。
public interface IDamageable
{
    bool IsAlive { get; }

    // 返回实际生效的伤害，便于调用方做打击感判定（免疫/已死时返回 0，不触发减速与震屏）
    float TakeDamage(float amount, Vector3 hitPoint, Vector3 hitDirection);
}
