using UnityEngine;

/// <summary>
/// Emits enemy footsteps from locomotion animation phase without changing enemy AI.
/// </summary>
[DisallowMultipleComponent]
public sealed class EnemyFootstepAudioSource : MonoBehaviour
{
    [SerializeField, Min(0f)] private float _minimumSpeed = 0.15f;
    [SerializeField, Min(0f)] private float _minimumInterval = 0.08f;
    [SerializeField, Range(0f, 1f)] private float _leftFootPhase = 0.15f;
    [SerializeField, Range(0f, 1f)] private float _rightFootPhase = 0.6f;

    private EnemyBase _enemy;
    private Animator _animator;
    private string _leftEventId;
    private string _rightEventId;
    private int _lastStateHash;
    private float _lastPhase = -1f;
    private float _lastFootstepTime = float.NegativeInfinity;

    private void Awake()
    {
        _enemy = GetComponent<EnemyBase>();
        _animator = GetComponentInChildren<Animator>();
        bool heavy = GetComponent<EnemyHeavy>() != null;
        _leftEventId = heavy ? "enemy_footstep_tech_left" : "enemy_footstep_normal_left";
        _rightEventId = heavy ? "enemy_footstep_tech_right" : "enemy_footstep_normal_right";
    }

    private void LateUpdate()
    {
        if (_enemy == null || _animator == null || !_enemy.IsAlive ||
            _enemy.Speed < _minimumSpeed || _enemy.IsAttacking || _animator.IsInTransition(0))
        {
            _lastPhase = -1f;
            return;
        }

        AnimatorStateInfo state = _animator.GetCurrentAnimatorStateInfo(0);
        if (state.shortNameHash != _lastStateHash)
        {
            _lastStateHash = state.shortNameHash;
            _lastPhase = -1f;
        }

        float phase = Mathf.Repeat(state.normalizedTime, 1f);
        if (_lastPhase >= 0f)
        {
            if (_lastPhase < _leftFootPhase && phase >= _leftFootPhase)
                PlayFootstep(_leftEventId);
            if (_lastPhase < _rightFootPhase && phase >= _rightFootPhase)
                PlayFootstep(_rightEventId);
        }
        _lastPhase = phase;
    }

    private void PlayFootstep(string eventId)
    {
        if (TimeManager.UnscaledTime - _lastFootstepTime < _minimumInterval) return;
        if (!AkUnitySoundEngine.IsInitialized()) return;
        if (GetComponent<AkGameObj>() == null) gameObject.AddComponent<AkGameObj>();
        WwiseEventBridge.Play(eventId, gameObject);
        _lastFootstepTime = TimeManager.UnscaledTime;
    }

    private void OnDisable()
    {
        _lastStateHash = 0;
        _lastPhase = -1f;
        _lastFootstepTime = float.NegativeInfinity;
    }
}
