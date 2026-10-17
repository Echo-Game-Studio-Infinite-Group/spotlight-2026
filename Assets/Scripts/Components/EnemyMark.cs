using System;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(HealthComponent))]
public sealed class EnemyMark : MonoBehaviour
{
    private HealthComponent _health;
    public bool IsMarked { get; private set; }
    public event Action<bool> Changed;
    private void Awake() => _health = GetComponent<HealthComponent>();
    private void OnEnable() => _health.Died += OnDied;
    private void OnDisable() { _health.Died -= OnDied; SetMarked(false); }
    private void OnDied(HealthComponent health) => SetMarked(false);
    public void Toggle()
    {
        if (isActiveAndEnabled && _health.IsAlive) SetMarked(!IsMarked);
    }
    private void SetMarked(bool value)
    {
        if (IsMarked == value) return;
        IsMarked = value;
        Changed?.Invoke(value);
    }
}
