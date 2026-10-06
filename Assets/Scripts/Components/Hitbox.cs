using System;
using UnityEngine;

public enum CampType { Player, Enemy }

// 判定组件只认识世界空间的盒与战斗持有者；动作窗口和骨骼路径由适配层翻译。
[DisallowMultipleComponent]
[RequireComponent(typeof(BoxCollider))]
[RequireComponent(typeof(Rigidbody))]
public sealed class Hitbox : MonoBehaviour
{
    // 仅通知成功命中；统一结算已经完成，订阅者不能再次扣血。
    public event Action<Hitbox, Collider, HealthComponent> Hit;
    private BoxCollider _collider;
    private CombatComponent _owner;
    private long _sequenceInstance;
    private int _hitGroup, _hitMask;
    private bool _windowOpen, _configuredWindowOpen;
    private Collider[] _overlaps = new Collider[16];
    private RaycastHit[] _obstacles = new RaycastHit[16];
    public bool SequenceWindowOpen => _sequenceInstance != 0 && (_windowOpen || _configuredWindowOpen);
    public CampType Camp => _owner != null ? _owner.Camp : CampType.Player;

    private void Awake()
    {
        _collider = GetComponent<BoxCollider>();
        Rigidbody body = GetComponent<Rigidbody>();
        body.isKinematic = true;
        body.useGravity = false;
        _collider.enabled = false;
        if (_owner == null) _owner = GetComponentInParent<CombatComponent>();
    }
    public void SetOwner(CombatComponent owner) => _owner = owner;
    public void EnableHitbox() { if (_sequenceInstance == 0) GetComponent<BoxCollider>().enabled = true; }
    public void DisableHitbox() => GetComponent<BoxCollider>().enabled = false;
    private void OnTriggerEnter(Collider other)
    {
        if (_sequenceInstance != 0 || other.isTrigger || _owner == null) return;
        Report(other, _owner.transform.position, 0, 0);
    }
    private void Report(Collider other, Vector3 center, long instance, int group)
    {
        if (_owner == null || !_owner.isActiveAndEnabled) return;
        // 血量组件是实体的唯一受击身份，多碰撞体必须解析成同一个目标。
        HealthComponent health = other.GetComponentInParent<HealthComponent>();
        IAttackDamageReceiver receiver = health != null ? health : other.GetComponentInParent<IAttackDamageReceiver>();
        if (receiver == null) return;
        var request = new AttackDamageRequest(instance == 0 ? _owner.Damage : _owner.SequenceDamage(group),
            other.ClosestPoint(center), other.bounds.center - center, instance, group);
        AttackDamageResult result = _owner.TryHit(other, receiver, request);
        if (result.AppliedDamage > 0f && health != null) Hit?.Invoke(this, other, health);
    }
    public void BeginSequence(long instanceId, int hitMask)
    {
        EndSequence();
        _sequenceInstance = instanceId; _hitMask = hitMask;
    }
    public void OpenSequenceWindow(int hitGroup)
    {
        if (_sequenceInstance == 0) return;
        _hitGroup = hitGroup; _windowOpen = true;
    }
    public void CloseSequenceWindow() => _windowOpen = false;
    public void SetConfiguredWindow(long instanceId, bool open)
    {
        if (_sequenceInstance == instanceId) _configuredWindowOpen = open;
    }
    public void EndSequence()
    {
        _windowOpen = _configuredWindowOpen = false; _sequenceInstance = 0;
        DisableHitbox();
    }
    public void SampleSequenceWindow()
    {
        if (_sequenceInstance == 0 || !_windowOpen || _owner == null) return;
        if (_collider == null) _collider = GetComponent<BoxCollider>();
        Vector3 scale = transform.lossyScale;
        var pose = new AttackVolumePose(transform.TransformPoint(_collider.center),
            Vector3.Scale(_collider.size * .5f, new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z))),
            transform.rotation, _owner.transform.position);
        Physics.SyncTransforms();
        SampleBox(_sequenceInstance, _hitGroup, pose, Vector3.zero, 0, false);
    }
    public void SampleBox(long instanceId, int group, AttackVolumePose pose, Vector3 obstructionOrigin, int environmentMask, bool checkObstacles = true)
    {
        if (instanceId == 0 || instanceId != _sequenceInstance || !isActiveAndEnabled || _owner == null || !_owner.isActiveAndEnabled) return;
        int count;
        while ((count = Physics.OverlapBoxNonAlloc(pose.Center, pose.Extents, _overlaps, pose.Rotation, _hitMask, QueryTriggerInteraction.Ignore)) == _overlaps.Length)
            Array.Resize(ref _overlaps, _overlaps.Length * 2);
        for (int i = 0; i < count; i++)
        {
            // 伤害事件可能取消动作，余下候选必须立即停掉。
            if (_sequenceInstance != instanceId) break;
            Collider other = _overlaps[i];
            if (other.transform.IsChildOf(_owner.transform)) continue;
            if (checkObstacles && Blocked(obstructionOrigin, other.ClosestPoint(pose.Center), environmentMask)) continue;
            Report(other, pose.Center, instanceId, group);
        }
    }
    private bool Blocked(Vector3 origin, Vector3 point, int environmentMask)
    {
        Vector3 ray = point - origin;
        int count;
        while ((count = Physics.RaycastNonAlloc(origin, ray.normalized, _obstacles, ray.magnitude, environmentMask, QueryTriggerInteraction.Ignore)) == _obstacles.Length)
            Array.Resize(ref _obstacles, _obstacles.Length * 2);
        for (int i = 0; i < count; i++)
        {
            Collider obstacle = _obstacles[i].collider;
            if (!obstacle.transform.IsChildOf(_owner.transform) && obstacle.GetComponentInParent<IAttackDamageReceiver>() == null)
                return true;
        }
        return false;
    }
}
