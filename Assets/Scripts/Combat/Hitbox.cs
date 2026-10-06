using UnityEngine;
using System;
using System.Collections.Generic;
using GameJam.Actions;

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
    private bool _configuredWindowOpen;
    private Collider[] _overlaps = new Collider[16];
    private readonly Dictionary<int, HashSet<Enemy>> _hitTargets = new Dictionary<int, HashSet<Enemy>>();
    private struct VolumePose
    {
        public Vector3 Center, Extents, Root;
        public Quaternion Rotation;
    }
    private readonly Dictionary<int, VolumePose> _volumeHistory = new Dictionary<int, VolumePose>();
    private readonly Dictionary<int, HashSet<IAttackDamageReceiver>> _receiverHits = new Dictionary<int, HashSet<IAttackDamageReceiver>>();
    private RaycastHit[] _obstacles = new RaycastHit[16];
    private PlayerMotor _motor;
    private CharacterController _controller;
    public bool SequenceWindowOpen => _sequenceInstance != 0 && (_windowOpen || _configuredWindowOpen);
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
        else if (enemyBase != null && other.GetComponentInParent<PlayerMotor>() != null)
        {
            GameManager.Instance.Player.TakeDamage(enemyBase.AttackDamage);
        }
    }

    public void BeginSequence(long instanceId, int hitMask)
    {
        EndSequence();
        _sequenceInstance = instanceId;
        _hitMask = hitMask;
        if (_collider == null) _collider = GetComponent<BoxCollider>();
        _collider.enabled = false;
        _motor = player != null ? player.GetComponent<PlayerMotor>() : null;
        _controller = player != null ? player.GetComponent<CharacterController>() : null;
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
        _configuredWindowOpen = false;
        _sequenceInstance = 0;
        _hitTargets.Clear();
        _volumeHistory.Clear();
        _receiverHits.Clear();
        DisableHitbox();
    }
    public void SampleSequenceWindow()
    {
        if (_sequenceInstance == 0 || !_windowOpen || player == null || !player.enabled || !isActiveAndEnabled) return;
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
            float applied = target.TakeDamage(player.SequenceDamage(_hitGroup), point, direction);
            if (applied <= 0f) continue;
            hit.Add(target);
            player.OnLandedHit(target, point, direction, applied);
        }
    }

    public void SampleConfiguredVolumes(ActionDefinition action, double fromFrame, double toFrame, IReadOnlyList<Vector3> path)
    {
        if (_sequenceInstance == 0 || player == null || !player.enabled || !isActiveAndEnabled) return;
        Physics.SyncTransforms();
        List<ActionHitVolume> volumes = action.Combat.HitVolumes;
        _configuredWindowOpen = false;
        for (int i = 0; i < volumes.Count; i++)
        {
            ActionHitVolume volume = volumes[i];
            bool wasActive = volume.IsActive(action, fromFrame);
            bool active = volume.IsActive(action, toFrame);
            _configuredWindowOpen |= active;
            if ((!wasActive && !active) || !volume.TryGetPose(player.transform, out Vector3 center, out Vector3 extents, out Quaternion rotation))
            { _volumeHistory.Remove(i); continue; }
            var current = new VolumePose { Center = center, Extents = extents, Rotation = rotation, Root = player.transform.position };
            player.SequenceDamage(volume.HitGroup);
            if (!_receiverHits.ContainsKey(volume.HitGroup)) _receiverHits.Add(volume.HitGroup, new HashSet<IAttackDamageReceiver>());
            if (wasActive && _volumeHistory.TryGetValue(i, out VolumePose previous))
            {
                // 根节点沿真实 Move 路径走，骨骼的相对姿态在两次逻辑采样之间插值。
                // 取消与开窗都会清空历史，不能把两刀之间的动画跳变扫成伤害。
                VolumePose begin = previous;
                if (path != null && path.Count > 0)
                {
                    float length = 0f; Vector3 root = previous.Root;
                    foreach (Vector3 point in path) { length += Vector3.Distance(root, point); root = point; }
                    float elapsed = 0f; root = previous.Root;
                    for (int p = 0; p < path.Count; p++)
                    {
                        elapsed += Vector3.Distance(root, path[p]); root = path[p];
                        float t = length > 0f ? elapsed / length : (p + 1f) / path.Count;
                        var end = new VolumePose {
                            Root = root,
                            Center = root + Vector3.Lerp(previous.Center - previous.Root, current.Center - current.Root, t),
                            Extents = Vector3.Lerp(previous.Extents, current.Extents, t),
                            Rotation = Quaternion.Slerp(previous.Rotation, current.Rotation, t)
                        };
                        SweepVolume(begin, end, volume); begin = end;
                    }
                }
                else SweepVolume(previous, current, volume);
            }
            else QueryVolume(current, volume.HitGroup);
            if (active) _volumeHistory[i] = current;
            else _volumeHistory.Remove(i);
        }
    }

    private void SweepVolume(VolumePose from, VolumePose to, ActionHitVolume volume)
    {
        float radius = Mathf.Max(from.Extents.magnitude, to.Extents.magnitude);
        float travel = Vector3.Distance(from.Center, to.Center) + Quaternion.Angle(from.Rotation, to.Rotation) * Mathf.Deg2Rad * radius;
        float smallest = Mathf.Min(to.Extents.x, Mathf.Min(to.Extents.y, to.Extents.z));
        float spacing = Mathf.Max(0.01f, Mathf.Min(volume.SweepSpacing, smallest));
        int steps = Mathf.Max(1, Mathf.CeilToInt(travel / spacing));
        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            QueryVolume(new VolumePose { Center = Vector3.Lerp(from.Center, to.Center, t),
                Extents = Vector3.Lerp(from.Extents, to.Extents, t), Rotation = Quaternion.Slerp(from.Rotation, to.Rotation, t),
                Root = Vector3.Lerp(from.Root, to.Root, t) }, volume.HitGroup);
        }
    }

    private void QueryVolume(VolumePose pose, int group)
    {
        int count;
        while ((count = Physics.OverlapBoxNonAlloc(pose.Center, pose.Extents, _overlaps, pose.Rotation, _hitMask, QueryTriggerInteraction.Ignore)) == _overlaps.Length)
            Array.Resize(ref _overlaps, _overlaps.Length * 2);
        HashSet<IAttackDamageReceiver> hit = _receiverHits[group];
        for (int i = 0; i < count; i++)
        {
            Collider other = _overlaps[i];
            IAttackDamageReceiver target = other.GetComponentInParent<IAttackDamageReceiver>();
            if (target == null || hit.Contains(target) || other.transform.IsChildOf(player.transform)) continue;
            Vector3 point = other.ClosestPoint(pose.Center);
            Vector3 origin = pose.Root + Vector3.up * _controller.height * 0.5f;
            Vector3 ray = point - origin;
            bool blocked = false;
            // 判定盒可伸到墙外；环境遮挡仍需检查，避免大刀框隔墙造成伤害。
            int obstacles;
            while ((obstacles = Physics.RaycastNonAlloc(origin, ray.normalized, _obstacles, ray.magnitude,
                _motor.Params.CollisionMask, QueryTriggerInteraction.Ignore)) == _obstacles.Length) Array.Resize(ref _obstacles, _obstacles.Length * 2);
            for (int j = 0; j < obstacles; j++)
                if (!_obstacles[j].transform.IsChildOf(player.transform) && _obstacles[j].collider.GetComponentInParent<IAttackDamageReceiver>() == null)
                { blocked = true; break; }
            if (blocked) continue;
            var request = new AttackDamageRequest(player.SequenceDamage(group), point, other.bounds.center - pose.Center, _sequenceInstance, group);
            AttackDamageResult result = target.ReceiveAttack(request);
            if (result.AppliedDamage <= 0f) continue;
            hit.Add(target);
            player.OnSequenceHit(target, request, result);
        }
    }
}
