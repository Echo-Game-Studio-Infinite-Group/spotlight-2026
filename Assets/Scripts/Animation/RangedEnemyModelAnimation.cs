using UnityEngine;

[DisallowMultipleComponent, RequireComponent(typeof(Animator))]
public sealed class RangedEnemyModelAnimation : MonoBehaviour
{
    [SerializeField] private AnimationClip _aimClip;
    [SerializeField] private AnimationClip _shootClip;
    private Animator _animator;
    private RangeComponent _ranged;

    private void Awake()
    {
        _animator = GetComponent<Animator>();
        _ranged = GetComponentInParent<RangeComponent>();
    }

    private void OnEnable() => _ranged.Fired += OnFired;
    private void Update()
    {
        _animator.SetBool("Charging", _ranged.IsCharging);
        // 动作适配现有蓄力和冷却，不让素材片段的长度改变战斗节奏。
        _animator.SetFloat("AimSpeed", _aimClip.length / Mathf.Max(.01f, _ranged.TelegraphSeconds));
        _animator.SetFloat("ShootSpeed", _shootClip.length / Mathf.Max(.1f, _ranged.Cooldown - _ranged.TelegraphSeconds));
    }

    // 由实际出弹通知表现，动画不反向决定伤害和冷却。
    private void OnFired() => _animator.SetTrigger("Shoot");

    private void OnDisable()
    {
        _ranged.Fired -= OnFired;
        _animator.ResetTrigger("Shoot");
        _animator.SetBool("Charging", false);
    }
}
