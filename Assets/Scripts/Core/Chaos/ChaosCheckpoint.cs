using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(BoxCollider), typeof(Rigidbody))]
public sealed class ChaosCheckpoint : MonoBehaviour
{
    [SerializeField] private string _checkpointId;
    [SerializeField] private int _priority;
    [SerializeField] private bool _canOverrideHigherPriority;
    [SerializeField] private bool _allowNegativeDeath;
    public string CheckpointId => _checkpointId;
    public int Priority => _priority;
    public bool CanOverrideHigherPriority => _canOverrideHigherPriority;
    public bool AllowNegativeDeath => _allowNegativeDeath;
    public void Configure(string id, int priority, bool canOverride = false, bool allowNegativeDeath = false)
    {
        _checkpointId = id; _priority = priority;
        _canOverrideHigherPriority = canOverride; _allowNegativeDeath = allowNegativeDeath;
        GetComponent<BoxCollider>().isTrigger = true;
        var body = GetComponent<Rigidbody>();
        body.isKinematic = true; body.useGravity = false;
    }
    private void Awake()
    {
        GetComponent<BoxCollider>().isTrigger = true;
        var body = GetComponent<Rigidbody>();
        body.isKinematic = true; body.useGravity = false;
    }
    private void OnTriggerEnter(Collider other) => TryActivate(other);
    private void OnTriggerStay(Collider other) => TryActivate(other);
    private void TryActivate(Collider other)
    {
        var player = other.GetComponentInParent<PlayerMotor>();
        if (player != null) player.GetComponent<ChaosRunController>()?.TrySetCheckpoint(this);
    }
}
