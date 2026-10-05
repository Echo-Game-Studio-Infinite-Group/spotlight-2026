using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class PlayerAudioDriver : MonoBehaviour
{
    [Header("直接绑定（没有 PlayerAudioProfile 时用这里）")]
    [Tooltip("把滑铲用的 AudioActionDefinition 拖进来即可，不需要 Catalog / Profile。")]
    [SerializeField] private AudioActionDefinition _slideAction;
    [SerializeField] private AudioActionDefinition _wallSlideAction;

    private AudioSystem _audio;
    private PlayerAudioProfile _fallbackProfile;
    private PlayerMotor _motor;
    private PlayerCombat _combat;
    private PlayerHealth _health;
    private AudioActionHandle _slideHandle;
    private AudioActionHandle _wallHandle;
    private readonly HashSet<Damageable> _trackedDamageables = new HashSet<Damageable>();
    private float _slideTime;
    private float _wallTime;
    private float _stepDistance;
    private float _damageScanTimer;
    private float _lastHitTime = float.NegativeInfinity;
    private float _lastHealth;
    private bool _lastSliding;
    private bool _lastWallSliding;
    private bool _lastGrounded;
    private bool _lastAttacking;
    private bool _lastHitStop;
    private bool _drewSword;

    /// <summary>
    /// 优先用 AudioSystem.Configure 进来的 Profile；
    /// 没有的话（场景里没放 GameAudioInstaller）就用一条运行时兜底 Profile，
    /// 里面只填这两个直接绑定的动作音效。
    /// </summary>
    private PlayerAudioProfile Profile
    {
        get
        {
            if (_audio == null) return null;
            if (_audio.Profile != null) return _audio.Profile;
            if (_fallbackProfile == null)
            {
                _fallbackProfile = ScriptableObject.CreateInstance<PlayerAudioProfile>();
                _fallbackProfile.hideFlags = HideFlags.HideAndDontSave;
                _fallbackProfile.SlideAction = _slideAction;
                _fallbackProfile.WallSlideAction = _wallSlideAction;
            }
            return _fallbackProfile;
        }
    }

    private void Awake()
    {
        _motor = GetComponent<PlayerMotor>();
        _combat = GetComponent<PlayerCombat>();
        _health = GetComponent<PlayerHealth>();
        if (_health != null) _lastHealth = _health.Health;
        if (_motor != null)
        {
            _lastGrounded = _motor.IsGrounded;
            _motor.Teleported += OnTeleported;
            _motor.WallCollision += OnWallCollision;
        }
    }

    private void OnDestroy()
    {
        if (_motor != null)
        {
            _motor.Teleported -= OnTeleported;
            _motor.WallCollision -= OnWallCollision;
        }
        foreach (Damageable damageable in _trackedDamageables)
            if (damageable != null) damageable.Damaged -= OnDamageableDamaged;
        _trackedDamageables.Clear();
        if (_fallbackProfile != null)
        {
            Destroy(_fallbackProfile);
            _fallbackProfile = null;
        }
    }

    private void Update()
    {
        if (_audio == null) _audio = AudioSystem.Instance;
        if (_audio == null || _motor == null) return;
        // 有直接绑定动作时，允许没有 Profile。
        bool hasDirectBinding = _slideAction != null || _wallSlideAction != null;
        if (_audio.Profile == null && !hasDirectBinding) return;

        float dt = TimeManager.UnscaledDeltaTime;
        UpdateSpeed();
        UpdateSlide(dt);
        UpdateWallSlide(dt);
        UpdateAttack();
        UpdateHitStopGlitch();
        UpdateFootsteps(dt);
        UpdateHealth();
        RefreshDamageables(dt);
    }

    private void UpdateSpeed()
    {
        float threshold = _motor.Params != null && _motor.Params.GroundSpeedThreshold > 0f
            ? _motor.Params.GroundSpeedThreshold
            : 10f;
        _audio.SetSpeedRatio(_motor.HorizontalSpeed / threshold);
    }

    private void UpdateSlide(float deltaTime)
    {
        bool sliding = _motor.IsSliding;
        // Profile 里没填就用直连字段兜底，避免"有了 Profile 反而直连失效"。
        AudioActionDefinition definition = Profile != null && Profile.SlideAction != null
            ? Profile.SlideAction
            : _slideAction;
        if (sliding && !_lastSliding && definition != null)
        {
            _slideTime = 0f;
            AudioCueDefinition surfaceCue = ResolveGroundCue();
            _slideHandle = _audio.PlayAction(definition, transform, surfaceCue != null ? surfaceCue.Id : string.Empty);
        }
        else if (!sliding && _lastSliding)
        {
            if (_slideHandle != null) _slideHandle.Stop();
            _slideHandle = null;
        }

        if (sliding && _slideHandle != null)
        {
            _slideTime += deltaTime;
            _slideHandle.SetControl(BuildFrame(_slideTime, 1f, ResolveGroundCue()));
        }
        _lastSliding = sliding;
    }

    private void UpdateWallSlide(float deltaTime)
    {
        bool sliding = _motor.IsWallSliding;
        AudioActionDefinition definition = Profile != null && Profile.WallSlideAction != null
            ? Profile.WallSlideAction
            : _wallSlideAction;
        if (sliding && !_lastWallSliding && definition != null)
        {
            _wallTime = 0f;
            _wallHandle = _audio.PlayAction(definition, transform, string.Empty);
        }
        else if (!sliding && _lastWallSliding)
        {
            if (_wallHandle != null) _wallHandle.Stop();
            _wallHandle = null;
        }

        if (sliding && _wallHandle != null)
        {
            _wallTime += deltaTime;
            _wallHandle.SetControl(BuildFrame(_wallTime, _motor.WallApproachAngle / 90f, null));
        }
        _lastWallSliding = sliding;
    }

    private void UpdateAttack()
    {
        bool attacking = _combat != null && _combat.IsAttacking;
        if (attacking && !_lastAttacking)
        {
            _audio.PlaySfx(Profile.SwordSwing, transform, -2f);
            if (!_drewSword)
            {
            _audio.PlaySfx(Profile.DrawSword, transform, -3f);
                _drewSword = true;
            }
        }
        _lastAttacking = attacking;
    }

    private void UpdateHitStopGlitch()
    {
        bool hitStop = TimeManager.InHitStop;
        if (hitStop && !_lastHitStop) _audio.TriggerGlitch(transform.position, 0.85f, 0.065f);
        _lastHitStop = hitStop;
    }

    private void UpdateFootsteps(float deltaTime)
    {
        bool grounded = _motor.IsGrounded;
        float vertical = _motor.Velocity.y;
        if (!_lastGrounded && grounded)
        {
            PlayFootstep(true);
        }
        else if (_lastGrounded && !grounded && vertical > 0.5f)
        {
            _audio.PlaySfx(Profile.Jump, transform, -2f);
        }
        _lastGrounded = grounded;

        if (!grounded || _motor.IsSliding || _motor.IsWallSliding) return;
        float speed = _motor.HorizontalSpeed;
        if (speed < 0.35f) return;
        float maxSpeed = _motor.Params != null ? Mathf.Max(1f, _motor.Params.MaxSpeed) : 120f;
        float stepLength = Mathf.Lerp(1.8f, 3.6f, Mathf.InverseLerp(0f, maxSpeed, speed));
        _stepDistance += speed * deltaTime;
        if (_stepDistance < stepLength) return;
        _stepDistance = 0f;
        PlayFootstep(false);
    }

    private void PlayFootstep(bool landing)
    {
        AudioCueDefinition cue = ResolveGroundCue();
        if (cue == null) cue = Profile.DefaultFootstep;
        if (cue == null || !cue.HasClips) return;
        float volume = landing ? -1.5f : -4f;
        _audio.PlaySfx(cue, transform, volume);
    }

    private void UpdateHealth()
    {
        if (_health == null) return;
        if (_health.Health + 0.001f < _lastHealth)
        {
            _audio.PlaySfx(Profile.Hurt, transform, -2f);
            _audio.TriggerGlitch(transform.position, 0.55f, 0.06f);
        }
        _lastHealth = _health.Health;
    }

    private void RefreshDamageables(float deltaTime)
    {
        _damageScanTimer -= deltaTime;
        if (_damageScanTimer > 0f) return;
        _damageScanTimer = 1f;
        Damageable[] found = FindObjectsOfType<Damageable>(false);
        for (int i = 0; i < found.Length; i++)
        {
            Damageable damageable = found[i];
            if (damageable == null || _trackedDamageables.Contains(damageable)) continue;
            _trackedDamageables.Add(damageable);
            damageable.Damaged += OnDamageableDamaged;
        }
    }

    private void OnDamageableDamaged(Damageable damageable, float amount)
    {
        if (TimeManager.UnscaledTime - _lastHitTime < 0.045f) return;
        _lastHitTime = TimeManager.UnscaledTime;
        Vector3 position = damageable != null ? damageable.transform.position : transform.position;
            _audio.PlaySfx(Profile.Hit, position, -3f);
        if (amount >= 20f) _audio.TriggerGlitch(position, 0.75f, 0.08f);
    }

    private void OnWallCollision(WallContact contact)
    {
        if (_audio == null) return;
            _audio.PlaySfx(Profile.Hit, contact.Point, -4f);
    }

    private void OnTeleported(Vector3 delta)
    {
        if (_audio == null) return;
        _audio.TriggerGlitch(transform.position, 0.6f, 0.07f);
    }

    private ActionControlFrame BuildFrame(float elapsed, float contact, AudioCueDefinition surfaceCue)
    {
        float threshold = _motor.Params != null && _motor.Params.GroundSpeedThreshold > 0f
            ? _motor.Params.GroundSpeedThreshold
            : 10f;
        Vector3 horizontal = new Vector3(_motor.Velocity.x, 0f, _motor.Velocity.z);
        float direction = horizontal.sqrMagnitude > 0.0001f
            ? Vector3.Dot(horizontal.normalized, transform.right)
            : 0f;
        return new ActionControlFrame
        {
            NormalizedSpeed = _motor.HorizontalSpeed / Mathf.Max(1f, threshold * 3f),
            ContactIntensity = contact,
            Direction = direction,
            Release = 0f,
            ActionElapsed = elapsed,
            SurfaceId = surfaceCue != null ? surfaceCue.Id : string.Empty,
            Seed = GetInstanceID()
        };
    }

    private AudioCueDefinition ResolveGroundCue()
    {
        Vector3 origin = transform.position + Vector3.up * 0.15f;
        if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 1.6f, ~0, QueryTriggerInteraction.Ignore))
            return AudioSurface.ResolveCue(hit);
        return null;
    }
}
