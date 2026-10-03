using UnityEngine;

public enum CampType { Player, Enemy }

// 与参考工程一致：动画开关触发器，触发器按阵营调用受伤方法。
[DisallowMultipleComponent]
[RequireComponent(typeof(BoxCollider))]
[RequireComponent(typeof(Rigidbody))]
public sealed class Hitbox : MonoBehaviour
{
    public Enemy enemyBase;
    public PlayerCombat player;
    public CampType campType;
    private BoxCollider _collider;
    public CampType Camp => campType;

    private void Awake()
    {
        _collider = GetComponent<BoxCollider>();
        // Trigger 至少一方需要刚体；运动仍由角色控制器负责。
        Rigidbody body = GetComponent<Rigidbody>();
        body.isKinematic = true;
        body.useGravity = false;
        _collider.enabled = false;
        if (player == null) player = GetComponentInParent<PlayerCombat>();
        if (enemyBase == null) enemyBase = GetComponentInParent<Enemy>();
    }

    public void Configure(CampType camp, MonoBehaviour owner)
    {
        campType = camp;
        player = owner as PlayerCombat;
        enemyBase = owner as Enemy;
    }

    public void EnableHitbox() => GetComponent<BoxCollider>().enabled = true;
    public void DisableHitbox() => GetComponent<BoxCollider>().enabled = false;

    private void OnTriggerEnter(Collider other)
    {
        if (other.isTrigger) return;
        if (campType == CampType.Player)
        {
            Enemy target = other.GetComponentInParent<Enemy>();
            if (target == null || player == null) return;
            Vector3 point = other.ClosestPoint(transform.position);
            Vector3 direction = target.transform.position - transform.position;
            float applied = target.TakeDamage(player.AttackDamage, point, direction);
            if (applied > 0f) player.OnLandedHit(target, point, direction, applied);
        }
        else if (enemyBase != null && other.GetComponentInParent<PlayerMotor>() != null)
        {
            GameManager.Instance.Player.TakeDamage(enemyBase.AttackDamage);
        }
    }
}
