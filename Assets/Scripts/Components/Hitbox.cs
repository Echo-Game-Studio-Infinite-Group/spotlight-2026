using UnityEngine;
using System;
using System.Collections.Generic;

public enum CampType { Player, Enemy }

// 判定盒只回答一件事：「我现在碰到了谁」。
// 扣谁的血、扣多少、扣完播什么表现，全部交给持有者 CombatComponent——
// 这里不认识 Enemy / PlayerCombat，因此玩家和敌人可以共用同一个判定盒。
//
// 序列角色使用骨骼姿态查询（OverlapBox）；动画事件路径走触发器。
// 两条路径互斥，避免一次命中被重复结算。
[DisallowMultipleComponent]
[RequireComponent(typeof(BoxCollider))]
[RequireComponent(typeof(Rigidbody))]
public sealed class Hitbox : MonoBehaviour
{
    /// <summary>碰到一个带 HealthComponent 的目标。参数：本判定盒、目标碰撞体、目标血量组件。</summary>
    public event Action<Hitbox, Collider, HealthComponent> Hit;

    private BoxCollider _collider;
    private CombatComponent _owner;
    private long _sequenceInstance;
    private int _hitGroup;
    private int _hitMask;
    private bool _windowOpen;
    private Collider[] _overlaps = new Collider[16];
    private readonly Dictionary<int, HashSet<HealthComponent>> _hitTargets = new Dictionary<int, HashSet<HealthComponent>>();

    public bool SequenceWindowOpen => _sequenceInstance != 0 && _windowOpen;
    public CampType Camp => _owner != null ? _owner.Camp : CampType.Player;

    private void Awake()
    {
        _collider = GetComponent<BoxCollider>();
        // Trigger 至少一方需要刚体；运动仍由角色控制器负责。
        Rigidbody body = GetComponent<Rigidbody>();
        body.isKinematic = true;
        body.useGravity = false;
        _collider.enabled = false;
        if (_owner == null) _owner = GetComponentInParent<CombatComponent>();
    }

    /// <summary>由 CombatComponent 在装配时回填，判定盒靠它知道自己属于谁。</summary>
    public void SetOwner(CombatComponent owner) => _owner = owner;

    public void EnableHitbox() { if (_sequenceInstance == 0) GetComponent<BoxCollider>().enabled = true; }
    public void DisableHitbox() => GetComponent<BoxCollider>().enabled = false;

    private void OnTriggerEnter(Collider other)
    {
        // 序列判定走 OverlapBox，触发器路径必须让位，否则同一次挥砍会结算两遍。
        if (_sequenceInstance != 0) return;
        if (other.isTrigger) return;
        Report(other);
    }

    // 只报「带血量的目标」：没有 HealthComponent 的东西（地形、装饰物）根本不参与战斗。
    private void Report(Collider other)
    {
        HealthComponent target = other.GetComponentInParent<HealthComponent>();
        if (target == null) return;
        Hit?.Invoke(this, other, target);
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
        if (!_hitTargets.ContainsKey(hitGroup)) _hitTargets.Add(hitGroup, new HashSet<HealthComponent>());
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
        if (!SequenceWindowOpen || _owner == null || !_owner.isActiveAndEnabled || !isActiveAndEnabled) return;
        Vector3 scale = transform.lossyScale;
        Vector3 extents = Vector3.Scale(_collider.size * 0.5f, new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
        Vector3 center = transform.TransformPoint(_collider.center);
        Physics.SyncTransforms();
        int count;
        // 拥挤场景中扩容后重查，避免固定容量悄悄漏掉目标。
        // 姿态必须传 transform.rotation：判定盒通常挂在挥砍的骨骼上，用单位旋转会扫错方向。
        while ((count = Physics.OverlapBoxNonAlloc(center, extents, _overlaps, transform.rotation, _hitMask, QueryTriggerInteraction.Ignore)) == _overlaps.Length)
            Array.Resize(ref _overlaps, _overlaps.Length * 2);
        HashSet<HealthComponent> hit = _hitTargets[_hitGroup];
        for (int i = 0; i < count; i++)
        {
            Collider other = _overlaps[i];
            HealthComponent target = other.GetComponentInParent<HealthComponent>();
            // 一次开窗内每个目标只报一次：同一个目标挨两下不该是「多段命中」的意思。
            if (target == null || !hit.Add(target)) continue;
            Hit?.Invoke(this, other, target);
        }
    }
}
