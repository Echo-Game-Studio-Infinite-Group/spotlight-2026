using UnityEngine;

[RequireComponent(typeof(PlayerMotor))]
public sealed class PlayerRespawn : MonoBehaviour
{
    [SerializeField] private float _killY = -10f;
    private Vector3 _spawn;
    private PlayerMotor _motor;
    private void Awake() { _spawn = transform.position; _motor = GetComponent<PlayerMotor>(); }
    private void FixedUpdate() { if (transform.position.y < _killY) _motor.Teleport(_spawn); }
}
