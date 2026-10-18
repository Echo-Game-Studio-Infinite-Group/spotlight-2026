using GameJam.Actions;
using UnityEngine;

[DefaultExecutionOrder(-80)]
[DisallowMultipleComponent]
[RequireComponent(typeof(Animator), typeof(HealthComponent), typeof(PlayerMotor))]
public sealed class PlayerDeathAnimation : MonoBehaviour
{
    public const string LayerName = "Death";
    public const string StateName = "Death.Death";

    [SerializeField] private AnimationClip _clip;
    [SerializeField, Min(0.01f)] private float _playbackSpeed = 1f;

    private Animator _animator;
    private HealthComponent _health;
    private PlayerMotor _motor;
    private PlayerInputReader _input;
    private PlayerRagdollHitStun _ragdoll;
    private Behaviour[] _controls;
    private bool[] _controlsWereEnabled;
    private float[] _layerWeights;
    private int _layer;
    private bool _dead, _inputWasEnabled, _animatorWasEnabled;
    private float _elapsed, _previousSpeed;
    private AnimatorCullingMode _previousCulling;
    private PlayerRootSkinFollow _rootSkin;

    public bool IsPlayingDeath => _dead && _clip != null && _elapsed < _clip.length;
    public bool IsDead => _dead;
    public float Duration => _clip != null ? _clip.length / Mathf.Max(0.01f, _playbackSpeed) : 0f;

    private void Awake()
    {
        _animator = GetComponent<Animator>();
        _health = GetComponent<HealthComponent>();
        _motor = GetComponent<PlayerMotor>();
        _input = GetComponent<PlayerInputReader>();
        _ragdoll = GetComponent<PlayerRagdollHitStun>();
        // 动作执行器先释放 Animator；恢复时先恢复移动与动画，最后重新接入执行器。
        _controls = new Behaviour[]
        {
            _motor, GetComponent<PlayerRespawn>(), GetComponent<PlayerCombat>(),
            GetComponent<PlayerAnimation>(), GetComponent<PlayerGroundPose>(),
            GetComponent<ActionAnimatorBridge>(), GetComponent<PlayerActionRunner>()
        };
        _controlsWereEnabled = new bool[_controls.Length];
        _layerWeights = new float[_animator.layerCount];
        _layer = _animator.GetLayerIndex(LayerName);
        _rootSkin = new PlayerRootSkinFollow(_animator);
    }

    private void OnEnable()
    {
        _health.Died += OnDied;
        _health.Revived += OnRevived;
    }

    private void OnDied(HealthComponent source)
    {
        if (_dead || source.IsAlive) return;
        if (_clip == null || _layer < 0 || !_animator.HasState(_layer, Animator.StringToHash(StateName)))
        {
            Debug.LogWarning("[PlayerDeathAnimation] 缺少死亡片段或死亡状态，保留原死亡界面流程。", this);
            return;
        }

        _ragdoll?.ExitHitStun();
        _dead = true;
        _elapsed = 0f;
        _inputWasEnabled = _input != null && _input.GameplayEnabled;
        for (int i = 0; i < _controls.Length; i++)
            _controlsWereEnabled[i] = _controls[i] != null && _controls[i].enabled;
        for (int i = _controls.Length - 1; i >= 0; i--)
            if (_controls[i] != null) _controls[i].enabled = false;

        // Release 会还原动画器，必须在它结束后记录自动播放状态。
        _animatorWasEnabled = _animator.enabled;
        _previousSpeed = _animator.speed;
        _previousCulling = _animator.cullingMode;
        for (int i = 0; i < _layerWeights.Length; i++)
        {
            _layerWeights[i] = _animator.GetLayerWeight(i);
            if (i > 0) _animator.SetLayerWeight(i, 0f);
        }
        _motor.SetHorizontalSpeed(0f);
        _input?.SetGameplayEnabled(false);
        _animator.enabled = false;
        _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        _animator.speed = Mathf.Max(0.01f, _playbackSpeed);
        _rootSkin.Begin();
        _animator.SetLayerWeight(_layer, 1f);
        _animator.Play(StateName, _layer, 0f);
        _animator.Update(0f);
        _rootSkin.UpdatePose();
    }

    private void Update()
    {
        if (!IsPlayingDeath) return;
        // 与公共玩家时间一致；暂停和时缓仍然有效，不单独修改全局时间。
        float speed = Mathf.Max(0.01f, _playbackSpeed);
        _animator.speed = speed;
        float dt = Mathf.Min(TimeManager.PlayerDeltaTime, (_clip.length - _elapsed) / speed);
        if (dt <= 0f) return;
        _animator.Update(dt);
        _rootSkin.UpdatePose();
        _elapsed = Mathf.Min(_clip.length, _elapsed + dt * speed);
    }

    private void OnRevived(HealthComponent source) => RestorePresentation();

    private void RestorePresentation()
    {
        if (!_dead) return;
        _dead = false;
        _elapsed = 0f;
        _rootSkin.Restore();
        _animator.Rebind();
        for (int i = 1; i < _layerWeights.Length; i++)
            _animator.SetLayerWeight(i, _layerWeights[i]);
        _animator.speed = _previousSpeed;
        _animator.cullingMode = _previousCulling;
        _animator.enabled = _animatorWasEnabled;
        // 死亡期间 Motor.Active 为空，读档可能直接改 Transform；在恢复前同步胶囊与速度。
        _motor.Teleport(transform.position);
        for (int i = 0; i < _controls.Length; i++)
            if (_controls[i] != null) _controls[i].enabled = _controlsWereEnabled[i];
        _input?.SetGameplayEnabled(_inputWasEnabled);
    }

    private void OnDisable()
    {
        _health.Died -= OnDied;
        _health.Revived -= OnRevived;
        if (Application.isPlaying && gameObject.activeInHierarchy) RestorePresentation();
    }
}
