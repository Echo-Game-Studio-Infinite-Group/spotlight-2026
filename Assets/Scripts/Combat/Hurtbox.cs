using System.Collections.Generic;
using UnityEngine;

// 受击框（设计 §4.4）：受击形状 + 所属血量 + 类型免疫；与移动碰撞胶囊物理分离——
// 伤害查询只认本组件 GO 上的 Collider（静态注册表精确映射），移动胶囊不会被误判为受击
[DisallowMultipleComponent]
public class Hurtbox : MonoBehaviour
{
    [Tooltip("所属血量组件（通常同物体；留空自动向上找）")]
    public HealthComponent Health;

    [Tooltip("形态免疫（静态常驻）：如某形态对投技免疫。滑铲/闪避的动态无敌走 Health.GrantImmunity")]
    public DamageType ImmuneTypes = DamageType.None;

    private static readonly Dictionary<Collider, Hurtbox> _colliderMap = new Dictionary<Collider, Hurtbox>();

    private IKnockbackReceiver _knockbackReceiver;
    private Collider[] _ownColliders;

    private void Awake()
    {
        if (Health == null) Health = GetComponentInParent<HealthComponent>();
        _knockbackReceiver = GetComponentInParent<IKnockbackReceiver>();
        // 只认自有碰撞体，且排除 CharacterController——移动胶囊是阻挡形状不是受击形状（设计 §4.4「物理分离」），
        // 误把它标成 trigger 会直接毁掉移动碰撞
        _ownColliders = System.Array.FindAll(GetComponents<Collider>(), c => !(c is CharacterController));
        if (_ownColliders.Length == 0)
        {
            Debug.LogWarning("[Hurtbox] 本物体上没有 Collider——受击形状与移动胶囊分离，需要自备碰撞体", this);
            return;
        }
        for (int i = 0; i < _ownColliders.Length; i++)
        {
            _ownColliders[i].isTrigger = true; // 受击框不参与物理阻挡
            _colliderMap[_ownColliders[i]] = this;
        }
    }

    private void OnDestroy()
    {
        if (_ownColliders == null) return;
        for (int i = 0; i < _ownColliders.Length; i++) _colliderMap.Remove(_ownColliders[i]);
    }

    /// <summary>Collider → Hurtbox 精确映射（只认本组件 GO 上的碰撞体；CharacterController 胶囊查不到）</summary>
    public static Hurtbox FromCollider(Collider collider) =>
        collider != null && _colliderMap.TryGetValue(collider, out Hurtbox hb) ? hb : null;

    /// <summary>该受击框是否属于攻击者本人（防自伤）</summary>
    public bool IsOwnedBy(GameObject attacker) =>
        attacker != null && (transform == attacker.transform || transform.IsChildOf(attacker.transform));

    public bool IsImmuneTo(DamageType type, float layerNow) =>
        (ImmuneTypes & type) != 0 || (Health != null && Health.IsImmuneTo(type, layerNow));

    /// <summary>击退命令转发（结算③）：目标 Motor/投射物实现 IKnockbackReceiver；无接收方静默跳过</summary>
    public void NotifyKnockback(in DamageInfo info)
    {
        if (info.Knockback <= 0f || _knockbackReceiver == null) return;
        _knockbackReceiver.OnKnockback(info.KnockbackDir * info.Knockback);
    }

    // 可视化（设计 §五）：受击框绿
    private void OnDrawGizmos()
    {
        if (!CombatDebug.DrawHitboxes) return;
        Gizmos.color = Color.green;
        Collider[] colliders = GetComponents<Collider>();
        for (int i = 0; i < colliders.Length; i++)
        {
            if (colliders[i] is BoxCollider box)
            {
                Gizmos.matrix = Matrix4x4.TRS(box.transform.position, box.transform.rotation, Vector3.one);
                Gizmos.DrawWireCube(box.center, box.size);
                Gizmos.matrix = Matrix4x4.identity;
            }
            else
            {
                Bounds bounds = colliders[i].bounds;
                Gizmos.DrawWireCube(bounds.center, bounds.size);
            }
        }
    }
}
