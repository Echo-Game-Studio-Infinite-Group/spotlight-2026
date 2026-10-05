using UnityEngine;
using System;
using System.Collections.Generic;

public enum CampType { Player, Enemy }

// 序列玩家使用骨骼姿态查询；敌方与旧玩家保留触发器路径，避免一次命中被两条路径重复结算。
[DisallowMultipleComponent]
[RequireComponent(typeof(BoxCollider))]
[RequireComponent(typeof(Rigidbody))]
public sealed class Hitbox : MonoBehaviour
{
    public Enemy enemyBase;
    public PlayerCombat player;
    public CampType campType;
    private BoxCollider _collider;
    private long _sequenceInstance;
    private int _hitGroup;
    private int _hitMask;
    private bool _windowOpen;
    private Collider[] _overlaps = new Collider[16];
    private readonly Dictionary<int, HashSet<Enemy>> _hitTargets = new Dictionary<int, HashSet<Enemy>>();
    public bool SequenceWindowOpen => _sequenceInstance != 0 && _windowOpen;
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

    public void EnableHitbox() { if (_sequenceInstance == 0) GetComponent<BoxCollider>().enabled = true; }
    public void DisableHitbox() => GetComponent<BoxCollider>().enabled = false;

    private void OnTriggerEnter(Collider other)
    {
        if (_sequenceInstance != 0 && campType == CampType.Player) return;
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
        else if (enemyBase != null)
        {
            PlayerMotor victim = other.GetComponentInParent<PlayerMotor>();
            if (victim == null) return;
            HealthComponent health = Player.Current != null ? Player.Current.Health : null;
            if (health == null) return;
            // 结算与表现分开：伤害走血量组件，震屏方向由命中位置决定。
            health.TakeDamage(enemyBase.AttackDamage);
            enemyBase.PlayAttackImpulse(victim.transform.position);
        }
    }

    public void BeginSequence(long instanceId, int hitMask)
    {
        EndSequence();
        _sequenceInstance = instanceId;
        _hitMask = hitMask;
        if (_collider == null) _collider = GetComponent<BoxCollider>();
        _collider.enabled = false;
    }
    public void OpenSequenceWindow(int hitGroup)
    {
        if (_sequenceInstance == 0) return;
        _hitGroup = hitGroup;
        _windowOpen = true;
        if (!_hitTargets.ContainsKey(hitGroup)) _hitTargets.Add(hitGroup, new HashSet<Enemy>());
    }
    public void CloseSequenceWindow() => _windowOpen = false;
    public void EndSequence()
    {
        _windowOpen = false;
        _sequenceInstance = 0;
        _hitTargets.Clear();
        DisableHitbox();
    }
    public void SampleSequenceWindow()
    {
        if (!SequenceWindowOpen || player == null || !player.enabled || !isActiveAndEnabled) return;
        Vector3 scale = transform.lossyScale;
        Vector3 extents = Vector3.Scale(_collider.size * 0.5f, new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
        Vector3 center = transform.TransformPoint(_collider.center);
        Physics.SyncTransforms();
        int count;
        // 拥挤场景中扩容后重查，避免固定容量悄悄漏掉目标。
        while ((count = Physics.OverlapBoxNonAlloc(center, extents, _overlaps, transform.rotation, _hitMask, QueryTriggerInteraction.Ignore)) == _overlaps.Length)
            Array.Resize(ref _overlaps, _overlaps.Length * 2);
        HashSet<Enemy> hit = _hitTargets[_hitGroup];
        for (int i = 0; i < count; i++)
        {
            Collider other = _overlaps[i];
            Enemy target = other.GetComponentInParent<Enemy>();
            if (target == null || hit.Contains(target) || target.transform.IsChildOf(player.transform)) continue;
            Vector3 point = other.ClosestPoint(center);
            Vector3 direction = target.transform.position - center;
            float applied = target.TakeDamage(player.AttackDamage, point, direction);
            if (applied <= 0f) continue;
            hit.Add(target);
            player.OnLandedHit(target, point, direction, applied);
        }
    }
}
