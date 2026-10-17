using System;
using UnityEngine;

[RequireComponent(typeof(PlayerMotor))]
public sealed class PlayerRespawn : MonoBehaviour
{
    [SerializeField] private float _killY = -10f;
    private Vector3 _spawn;
    private PlayerMotor _motor;
    private Func<bool> _fallHandler;
    public Vector3 RespawnPosition => _spawn;
    public float KillHeight => _killY;
    public void SetCheckpoint(Vector3 position, float killHeight)
    {
        _spawn = position;
        _killY = killHeight;
    }
    private void Awake() { _spawn = transform.position; _motor = GetComponent<PlayerMotor>(); }
    public void SetFallHandler(Func<bool> handler) => _fallHandler = handler;
    private void FixedUpdate()
    {
        if (transform.position.y >= _killY) return;
        if (_fallHandler != null) { _fallHandler(); return; }
        _motor.Teleport(_spawn);
    }
}
