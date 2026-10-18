using UnityEngine;

/// <summary>
/// Bridges player movement and combat events to one-shot Wwise events.
/// </summary>
[DefaultExecutionOrder(-10)]
[DisallowMultipleComponent]
public sealed class PlayerGameplayAudioSource : MonoBehaviour
{
    [SerializeField, Min(0f)] private float _minFootstepSpeed = 0.5f;
    [SerializeField, Range(0.1f, 1f)] private float _footstepPlantWeight = 0.65f;
    [SerializeField, Min(0f)] private float _minimumFootstepInterval = 0.08f;
    [SerializeField, Min(0.05f)] private float _wallFootstepInterval = 0.15f;
    [SerializeField, Range(0f, 1f)] private float _leftFootPhase = 0.15f;
    [SerializeField, Range(0f, 1f)] private float _rightFootPhase = 0.6f;

    private PlayerMotor _motor;
    private PlayerCombat _combat;
    private PlayerDynamicAudioActionSource _dynamicActions;
    private PlayerGroundPose _groundPose;
    private PlayerAnimation _locomotion;
    private Animator _animator;
    private GameObject _footstepEmitter;
    private int _lastWallJumpCount;
    private bool _leftFootPlanted;
    private bool _rightFootPlanted;
    private bool _wallFootLeft;
    private float _wallFootstepTimer;
    private float _lastStepPhase = -1f;
    private int _lastStepStateHash;
    private bool _subscribed;
    private float _lastFootstepTime = float.NegativeInfinity;

    private void Awake()
    {
        CacheReferences();
    }

    private void OnEnable()
    {
        Subscribe();
    }

    private void OnDisable()
    {
        Unsubscribe();
    }

    private void Update()
    {
        if (_motor == null) return;
        TickWallJump();
    }

    private void LateUpdate()
    {
        TickFootsteps();
    }

    private void CacheReferences()
    {
        _motor = GetComponent<PlayerMotor>();
        _combat = GetComponent<PlayerCombat>();
        _dynamicActions = GetComponent<PlayerDynamicAudioActionSource>();
        _groundPose = GetComponent<PlayerGroundPose>();
        _locomotion = GetComponent<PlayerAnimation>();
        _animator = GetComponentInChildren<Animator>();
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        CacheReferences();
        if (_motor != null)
        {
            _lastWallJumpCount = _motor.WallJumpCount;
            _motor.Landed += OnLanded;
        }
        if (_combat != null) _combat.SequenceHitLanded += OnSequenceHitLanded;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        if (_motor != null) _motor.Landed -= OnLanded;
        if (_combat != null) _combat.SequenceHitLanded -= OnSequenceHitLanded;
        _leftFootPlanted = false;
        _rightFootPlanted = false;
        _wallFootstepTimer = 0f;
        _lastStepPhase = -1f;
        _subscribed = false;
    }

    private void TickWallJump()
    {
        int wallJumpCount = _motor.WallJumpCount;
        if (wallJumpCount == _lastWallJumpCount) return;
        _lastWallJumpCount = wallJumpCount;
        _dynamicActions?.RequestImmediateStops();
        WwiseEventBridge.Play("player_jump", gameObject);
    }

    private void TickFootsteps()
    {
        if (_motor == null) return;
        if (_motor.IsWallSliding)
        {
            _wallFootstepTimer += TimeManager.UnscaledDeltaTime;
            if (_wallFootstepTimer >= _wallFootstepInterval)
            {
                _wallFootstepTimer -= _wallFootstepInterval;
                PlayFootstep(_wallFootLeft
                    ? "footstep_normal_left_2"
                    : "footstep_normal_right_2");
                _wallFootLeft = !_wallFootLeft;
            }
            return;
        }

        _wallFootstepTimer = 0f;
        if (_animator != null && _locomotion != null &&
            !_animator.GetBool("ActionPlaying") &&
            (_locomotion.CurrentMotionState == PlayerAnimation.MotionStateWalk ||
             _locomotion.CurrentMotionState == PlayerAnimation.MotionStateMove))
        {
            TickAnimatedFootsteps();
            return;
        }
        TickContactFootsteps();
    }

    private void TickAnimatedFootsteps()
    {
        AnimatorStateInfo state = _animator.GetCurrentAnimatorStateInfo(0);
        if (state.shortNameHash != _lastStepStateHash)
        {
            _lastStepStateHash = state.shortNameHash;
            _lastStepPhase = -1f;
        }

        float phase = Mathf.Repeat(state.normalizedTime, 1f);
        if (_lastStepPhase >= 0f)
        {
            if (_lastStepPhase < _leftFootPhase && phase >= _leftFootPhase)
                PlayFootstep("footstep_normal_left_2");
            if (_lastStepPhase < _rightFootPhase && phase >= _rightFootPhase)
                PlayFootstep("footstep_normal_right_2");
        }
        _lastStepPhase = phase;
    }

    private void TickContactFootsteps()
    {
        if (_groundPose == null) return;

        float leftWeight = _groundPose.LeftFootWeight;
        float rightWeight = _groundPose.RightFootWeight;
        bool leftPlanted = leftWeight >= _footstepPlantWeight;
        bool rightPlanted = rightWeight >= _footstepPlantWeight;
        bool moving = _motor.IsGrounded &&
                      !_motor.IsSliding &&
                      !_motor.IsWallSliding &&
                      _motor.HorizontalSpeed > _minFootstepSpeed;

        if (moving && leftPlanted && !_leftFootPlanted && leftWeight >= rightWeight)
            PlayFootstep("footstep_normal_left_2");
        if (moving && rightPlanted && !_rightFootPlanted && rightWeight > leftWeight)
            PlayFootstep("footstep_normal_right_2");

        _leftFootPlanted = leftPlanted;
        _rightFootPlanted = rightPlanted;
    }

    private void PlayFootstep(string eventId)
    {
        if (TimeManager.UnscaledTime - _lastFootstepTime < _minimumFootstepInterval)
            return;

        GameObject emitter = EnsureFootstepEmitter();
        if (emitter == null) return;
        WwiseEventBridge.Play(eventId, emitter);
        _lastFootstepTime = TimeManager.UnscaledTime;
    }

    private GameObject EnsureFootstepEmitter()
    {
        if (_footstepEmitter != null) return _footstepEmitter;
        if (!AkUnitySoundEngine.IsInitialized()) return null;

        _footstepEmitter = new GameObject("FootstepEmitter");
        _footstepEmitter.transform.SetParent(transform, false);
        _footstepEmitter.AddComponent<AkGameObj>();
        return _footstepEmitter;
    }

    private void OnLanded(GroundSupport support, float impactSpeed)
    {
        _leftFootPlanted = true;
        _rightFootPlanted = true;
        _wallFootstepTimer = 0f;
        _lastFootstepTime = TimeManager.UnscaledTime;
        WwiseEventBridge.Play("player_land", gameObject);
    }

    private void OnSequenceHitLanded(
        IAttackDamageReceiver target,
        AttackDamageRequest request,
        AttackDamageResult result)
    {
        if (result.AppliedDamage <= 0f) return;
        WwiseEventBridge.Play("metal_hit", gameObject);
    }
}
