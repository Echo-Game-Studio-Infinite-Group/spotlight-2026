using UnityEngine;
using UnityEngine.Rendering;

// 所有几何只创建一次；动画仅消费攻击快照，不参与瞄准、命中或伤害。
[DisallowMultipleComponent]
[RequireComponent(typeof(LaserAttack))]
public sealed class LaserAttackVisuals : MonoBehaviour
{
    [Header("共享材质与原有光束")]
    [SerializeField] private Material _material;
    [SerializeField] private LineRenderer _core;

    [Header("色彩（HDR，不依赖 Bloom 才能看清）")]
    [SerializeField, ColorUsage(true, true)] private Color _warningColor = new Color(1.5f, .12f, .025f, .8f);
    [SerializeField, ColorUsage(true, true)] private Color _fireColor = new Color(3f, .38f, .06f, .8f);
    [SerializeField, ColorUsage(true, true)] private Color _coreColor = new Color(4f, 2.8f, 1.6f, 1f);

    [Header("尺寸（米）")]
    [SerializeField, Min(.001f)] private float _aimWidth = .025f;
    [SerializeField, Min(.001f)] private float _beamWidth = .12f;
    [SerializeField, Min(1f)] private float _haloScale = 3.5f;
    [SerializeField, Min(.001f)] private float _ringWidth = .035f;
    [SerializeField, Min(.01f)] private float _lockRadius = .65f;
    [SerializeField, Min(.01f)] private float _lockedRadius = .18f;
    [SerializeField, Min(.01f)] private float _muzzleRadius = .32f;
    [SerializeField, Min(.01f)] private float _impactRadius = .38f;
    [SerializeField, Min(0f)] private float _surfaceOffset = .025f;
    [SerializeField, Range(12, 96)] private int _ringSegments = 48;
    [SerializeField, Range(3, 16)] private int _sparkCount = 8;

    [Header("动画（世界时间）")]
    [SerializeField, Min(0f)] private float _rotationSpeed = 110f;
    [SerializeField, Min(0f)] private float _pulseFrequency = 9f;
    [SerializeField, Range(0f, .5f)] private float _pulseStrength = .12f;
    [SerializeField, Min(.01f)] private float _flashSeconds = .12f;
    [SerializeField, Min(.01f)] private float _sparkCycleSeconds = .16f;
    [SerializeField, Min(.01f)] private float _sparkLength = .24f;

    private LaserAttack _attack;
    private LineRenderer _aim, _halo, _lockRing, _muzzleRing, _shockRing, _impactRing;
    private LineRenderer[] _sparks;
    private LineRenderer[] _lines;
    private LaserAttack.AttackPhase _lastPhase;
    private float _clock, _phaseTime;
    private bool _ready;

    private void Awake()
    {
        _attack = GetComponent<LaserAttack>();
    }

    private void Start()
    {
        if (_material == null)
        {
            Debug.LogError("激光特效需要指定程序化激光材质。", this);
            enabled = false;
            return;
        }
        if (_core == null) _core = CreateLine("Laser Core", 2);
        Configure(_core);
        _aim = CreateLine("Laser Aim", 2);
        _halo = CreateLine("Laser Halo", 2);
        _lockRing = CreateLine("Laser Lock", _ringSegments + 1);
        _muzzleRing = CreateLine("Laser Charge", _ringSegments + 1);
        _shockRing = CreateLine("Laser Muzzle Shock", _ringSegments + 1);
        _impactRing = CreateLine("Laser Impact", _ringSegments + 1);
        _sparks = new LineRenderer[_sparkCount];
        _lines = new LineRenderer[7 + _sparkCount];
        _lines[0] = _core;
        _lines[1] = _aim;
        _lines[2] = _halo;
        _lines[3] = _lockRing;
        _lines[4] = _muzzleRing;
        _lines[5] = _shockRing;
        _lines[6] = _impactRing;
        for (int i = 0; i < _sparkCount; i++)
        {
            _sparks[i] = CreateLine("Laser Spark " + i, 2);
            _lines[7 + i] = _sparks[i];
        }
        _ready = true;
        Hide();
    }

    private LineRenderer CreateLine(string label, int points)
    {
        var child = new GameObject(label);
        child.layer = gameObject.layer;
        child.transform.SetParent(transform, false);
        var line = child.AddComponent<LineRenderer>();
        line.positionCount = points;
        Configure(line);
        return line;
    }

    private void Configure(LineRenderer line)
    {
        line.sharedMaterial = _material;
        line.useWorldSpace = true;
        line.loop = false;
        line.alignment = LineAlignment.View;
        line.textureMode = LineTextureMode.Stretch;
        line.numCapVertices = 4;
        line.shadowCastingMode = ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.lightProbeUsage = LightProbeUsage.Off;
        line.reflectionProbeUsage = ReflectionProbeUsage.Off;
        line.enabled = false;
    }

    private void LateUpdate()
    {
        if (!_ready) return;
        LaserAttack.AttackPhase phase = _attack.Phase;
        if (phase != _lastPhase)
        {
            _phaseTime = 0f;
            _lastPhase = phase;
        }
        float dt = TimeManager.IsPaused ? 0f : TimeManager.WorldDeltaTime;
        _clock += dt;
        _phaseTime += dt;
        Hide();
        if (!_attack.isActiveAndEnabled || (!_attack.IsCharging && !_attack.IsFiring)) return;

        Vector3 start = _attack.BeamStart;
        Vector3 end = _attack.BeamEnd;
        Vector3 forward = _attack.BeamDirection;
        float pulse = 1f + Mathf.Sin(_clock * _pulseFrequency * Mathf.PI * 2f) * _pulseStrength;
        float angle = _clock * _rotationSpeed * Mathf.Deg2Rad;
        float charge = _attack.ChargeProgress;
        bool firing = _attack.IsFiring;
        Color color = firing ? _fireColor : _warningColor;

        if (!firing)
        {
            Segment(_aim, start, end, _aimWidth * Mathf.Lerp(.6f, 1.4f, charge) * pulse, color);
            // 锁定标记始终位于真实截断点；命中时贴合法线，空射时垂直于瞄准方向。
            Vector3 normal = _attack.HasBeamHit ? _attack.ImpactNormal : -forward;
            Vector3 center = _attack.HasBeamHit ? _attack.ImpactPoint + normal * _surfaceOffset : end;
            Ring(_lockRing, center, normal, Mathf.Lerp(_lockRadius, _lockedRadius, charge),
                _ringWidth, color, angle, .82f);
        }
        else
        {
            float envelope = Mathf.Clamp01((1f - _attack.FireProgress) * 5f);
            float width = _beamWidth * pulse * Mathf.Lerp(.35f, 1f, envelope);
            Segment(_core, start, end, width, _coreColor);
            Segment(_halo, start, end, width * _haloScale, _fireColor);
            float flash = Mathf.Clamp01(_phaseTime / _flashSeconds);
            if (flash < 1f)
                Ring(_shockRing, start, forward, _muzzleRadius * (1f + flash * 3f),
                    _ringWidth * 2f * (1f - flash), color, 0f, 1f);
            if (_attack.HasBeamHit) DrawImpact(angle, pulse);
        }

        float radius = firing ? _muzzleRadius : Mathf.Lerp(_muzzleRadius, _lockedRadius * .5f, charge);
        Ring(_muzzleRing, start, forward, radius * pulse, _ringWidth * (firing ? 2f : 1f), color, -angle, .9f);
    }

    private void DrawImpact(float angle, float pulse)
    {
        Vector3 normal = _attack.ImpactNormal;
        Vector3 center = _attack.ImpactPoint + normal * _surfaceOffset;
        Basis(normal, out Vector3 right, out Vector3 up);
        Ring(_impactRing, center, normal, _impactRadius * .45f * pulse, _ringWidth, _coreColor, angle, 1f);
        for (int i = 0; i < _sparks.Length; i++)
        {
            float age = Mathf.Repeat(_phaseTime / _sparkCycleSeconds + (float)i / _sparks.Length, 1f);
            float theta = angle + i * Mathf.PI * 2f / _sparks.Length;
            Vector3 radial = right * Mathf.Cos(theta) + up * Mathf.Sin(theta);
            Vector3 tail = center + radial * (_impactRadius * age) + normal * (_impactRadius * age * (1f - age));
            Segment(_sparks[i], tail, tail + (radial + normal * .5f) * (_sparkLength * (1f - age)),
                _ringWidth * (1f - age), _fireColor);
        }
    }

    private static void Segment(LineRenderer line, Vector3 start, Vector3 end, float width, Color color)
    {
        line.startWidth = line.endWidth = width;
        line.startColor = line.endColor = color;
        line.SetPosition(0, start);
        line.SetPosition(1, end);
        line.enabled = true;
    }

    private static void Basis(Vector3 normal, out Vector3 right, out Vector3 up)
    {
        right = Vector3.Cross(normal, Mathf.Abs(normal.y) < .95f ? Vector3.up : Vector3.right).normalized;
        up = Vector3.Cross(normal, right).normalized;
    }

    private static void Ring(LineRenderer line, Vector3 center, Vector3 normal, float radius,
        float width, Color color, float angle, float arc)
    {
        Basis(normal, out Vector3 right, out Vector3 up);
        int segments = line.positionCount - 1;
        for (int i = 0; i <= segments; i++)
        {
            float theta = angle + (float)i / segments * Mathf.PI * 2f * arc;
            line.SetPosition(i, center + (right * Mathf.Cos(theta) + up * Mathf.Sin(theta)) * radius);
        }
        line.startWidth = line.endWidth = width;
        line.startColor = line.endColor = color;
        line.enabled = true;
    }

    private void Hide()
    {
        if (!_ready) return;
        foreach (LineRenderer line in _lines) line.enabled = false;
    }

    private void OnDisable()
    {
        Hide();
        _clock = _phaseTime = 0f;
        _lastPhase = LaserAttack.AttackPhase.Idle;
    }
}
