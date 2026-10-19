using System;
using UnityEngine;

[RequireComponent(typeof(PlayerMotor))]
public sealed class PlayerRespawn : MonoBehaviour
{
    [SerializeField] private float _killY = -10f;
    private Vector3 _spawn;
    private PlayerMotor _motor;
    private PlayerRagdollHitStun _ragdoll;
    private Func<bool> _fallHandler;
    public Vector3 RespawnPosition => _spawn;
    public float KillHeight => _killY;
    public Vector3 FallCheckPosition => _ragdoll != null ? _ragdoll.FallCheckPosition : transform.position;
    public void SetCheckpoint(Vector3 position, float killHeight)
    {
        _spawn = position;
        _killY = killHeight;
    }
    private void Awake()
    {
        _spawn = transform.position;
        _motor = GetComponent<PlayerMotor>();
        _ragdoll = GetComponent<PlayerRagdollHitStun>();
    }
    public void SetFallHandler(Func<bool> handler) => _fallHandler = handler;
    // 必须先恢复布娃娃进入前的组件状态，流程才能正确记录并释放重建期间的控制锁。
    public void PrepareForRecovery() => _ragdoll?.ExitHitStun();
    private void FixedUpdate()
    {
        if (FallCheckPosition.y >= _killY) return;
        if (_fallHandler != null) { _fallHandler(); return; }
        PrepareForRecovery();
        _motor.Teleport(_spawn);
    }
}
