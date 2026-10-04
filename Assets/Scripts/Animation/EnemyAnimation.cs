using UnityEngine;

// 与 Animator 同节点接收动画事件；Enemy 只负责决策、血量与移动。
[RequireComponent(typeof(Animator))]
public sealed class EnemyAnimation : MonoBehaviour
{
    private Enemy _enemy;
    private Animator _animator;
    private bool _enteredAttack;
    // 直接读状态，不另存等待标记，避免标签漏配或过渡被打断后永久锁住 AI。
    public bool IsHurting => _animator != null && _animator.isActiveAndEnabled
        && _animator.runtimeAnimatorController != null
        && (_animator.GetCurrentAnimatorStateInfo(0).IsName("Hurt")
            || (_animator.IsInTransition(0) && _animator.GetNextAnimatorStateInfo(0).IsName("Hurt")));
    [SerializeField, Min(0f)] private float _speedDampTime = 0.1f;
    private Vector3 _modelOffset;
    private Quaternion _modelRotation;
    public bool CanAttack => _animator != null && _animator.isActiveAndEnabled
        && _animator.runtimeAnimatorController != null && !IsHurting && !_animator.IsInTransition(0)
        && !_animator.GetCurrentAnimatorStateInfo(0).IsTag("Attack");

    private void Awake()
    {
        _enemy = GetComponentInParent<Enemy>();
        _animator = GetComponent<Animator>();
        _animator.applyRootMotion = false;
        _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        _animator.updateMode = AnimatorUpdateMode.UnscaledTime;
        if (_enemy != null)
        {
            _modelOffset = _enemy.transform.InverseTransformPoint(transform.position);
            _modelRotation = Quaternion.Inverse(_enemy.transform.rotation) * transform.rotation;
        }
    }

    private void Update()
    {
        if (_enemy == null) return;
        _animator.speed = TimeManager.WorldRate * TimeManager.GameRate;
        _animator.SetFloat("Hp", _enemy.Health);
        _animator.SetFloat("MaxHp", _enemy.MaxHealth);
        _animator.SetFloat("Speed", _enemy.Speed, _speedDampTime, TimeManager.WorldDeltaTime);
        _animator.SetFloat("Injured", _enemy.IsInjured ? 1f : 0f);
        _animator.SetBool("Dead", !_enemy.IsAlive);
    }

    private void LateUpdate()
    {
        // 只插值模型，碰撞体和攻击判定仍跟随固定逻辑帧，不改物理位置。
        if (_enemy != null && transform != _enemy.transform)
        {
            float alpha = Mathf.Clamp01((Time.unscaledTime - Time.fixedUnscaledTime) / Time.fixedUnscaledDeltaTime);
            Quaternion rotation = Quaternion.Slerp(_enemy.PreviousRotation, _enemy.transform.rotation, alpha);
            Vector3 position = Vector3.Lerp(_enemy.PreviousPosition, _enemy.transform.position, alpha);
            transform.SetPositionAndRotation(position + rotation * Vector3.Scale(_modelOffset, _enemy.transform.lossyScale),
                rotation * _modelRotation);
        }
        if (_enemy == null || !_enemy.IsAttacking) return;
        AnimatorStateInfo state = _animator.GetCurrentAnimatorStateInfo(0);
        if (state.IsTag("Attack")) _enteredAttack = true;
        // 状态被打断或末帧事件未送达时也要关闭判定，不能把敌人锁在攻击里。
        if (_enteredAttack && ((!state.IsTag("Attack") && !_animator.IsInTransition(0))
            || (state.IsTag("Attack") && state.normalizedTime >= 1f))) FinishAttack();
    }

    public void PlayAttack()
    {
        _enteredAttack = false;
        _animator.SetTrigger("Attack");
    }
    public void PlayDeath()
    {
        _animator.ResetTrigger("Hurt");
        _animator.ResetTrigger("Attack");
        _animator.SetBool("Dead", true);
    }

    public void PlayHurt()
    {
        if (_animator.runtimeAnimatorController == null || !_animator.isActiveAndEnabled) return;
        FinishAttack();
        _animator.ResetTrigger("Attack");
        _animator.SetTrigger("Hurt");
    }

    public void EnableHitbox() { if (_enemy != null) _enemy.EnableHitbox(); }
    public void DisableHitbox() { if (_enemy != null) _enemy.DisableHitbox(); }
    public void FinishAttack() { if (_enemy != null) _enemy.FinishAttack(); }
    private void OnDisable() => FinishAttack();
}
