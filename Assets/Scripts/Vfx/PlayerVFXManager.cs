using UnityEngine;
using Cinemachine;

// 与 Animator 挂在同一节点，直接接收 UpdateAttack 动画事件，不经过战斗组件。
// 使用配置好的粒子，不用程序生成替代特效。
[DisallowMultipleComponent]
public sealed class PlayerVFXManager : MonoBehaviour
{
    [SerializeField] private Transform _origin;
    public ParticleSystem attack1;
    public ParticleSystem attack2;
    public ParticleSystem attack3;
    public bool IsSequenceDriven { get; private set; }
    public void SetSequenceDriven(bool driven) => IsSequenceDriven = driven;

    private CinemachineImpulseSource _impulseSource;
    private PlayerCombat _player => GetComponentInParent<PlayerCombat>();

    public void Configure(Transform origin) => _origin = origin;

    private void Awake()
    {
        if (_impulseSource == null) 
        {
            _impulseSource = GetComponent<CinemachineImpulseSource>();
        }
    }

    private void OnDestroy()
    {
        // 播放后粒子已脱离玩家层级，销毁玩家时也要回收。
        if (attack1 != null && attack1.transform.parent == null) Destroy(attack1.gameObject);
        if (attack2 != null && attack2.transform.parent == null) Destroy(attack2.gameObject);
        if (attack3 != null && attack3.transform.parent == null) Destroy(attack3.gameObject);
    }

    public void UpdateAttack(int cnt = 1)
    {
        if (!IsSequenceDriven) PlayAttack(cnt);
    }
    public void PlayAttack(int cnt = 1)
    {
        ParticleSystem effect = cnt == 1 ? attack1 : cnt == 2 ? attack2 : cnt == 3 ? attack3 : null;
        if (effect == null) return;
        PlayShake();
        Transform origin = _origin != null ? _origin : transform;
        effect.transform.SetPositionAndRotation(origin.position, origin.rotation * Quaternion.Euler(0f, 180f, 0f));
        //effect.transform.SetParent(null, true);
        effect.Play();
    }

    // 震屏必须挂在这里：两个入口都汇到 PlayAttack——
    // 非序列走动画事件 UpdateAttack，动作序列走帧事件 vfx.attack → ActionAnimatorBridge.PlayAttackEffect。
    // 挂在 UpdateAttack 里两条都够不着：序列接入时 ActionAnimatorBridge 会关掉 Animator.fireEvents，
    // 动画事件根本不触发；就算触发，IsSequenceDriven 也会把分支挡掉。
    private void PlayShake()
    {
        PlayerCombat combat = _player;
        if (combat == null) return;
        CameraShaker.Emit(_impulseSource, new Vector3(0.5f, 0.0f, 0.0f), combat.AttackDamage);
    }
    public void StopAttacks()
    {
        if (attack1 != null) attack1.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        if (attack2 != null) attack2.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        if (attack3 != null) attack3.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
    }
}
