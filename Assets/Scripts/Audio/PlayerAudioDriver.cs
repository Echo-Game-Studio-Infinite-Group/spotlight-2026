using UnityEngine;

/// <summary>
/// 把玩家动作状态桥接成动态连续动作音效。
///
/// 目前只负责滑铲和墙滑：读 <see cref="PlayerMotor"/> 的公开状态，上升沿开始播放、
/// 每帧喂速度和接触强度、下降沿请求 Release（走完这一遍循环再播尾音）。
///
/// 脚步 / 跳跃 / 攻击 / 命中 / 故障 / 音乐等已移除，后续交给 Wwise。
/// </summary>
[DisallowMultipleComponent]
public sealed class PlayerAudioDriver : MonoBehaviour
{
    [Header("连续动作音效（直接绑定）")]
    [Tooltip("滑铲用的 AudioActionDefinition，拖进来即可。")]
    [SerializeField] private AudioActionDefinition _slideAction;
    [Tooltip("墙滑用的 AudioActionDefinition，可不填。")]
    [SerializeField] private AudioActionDefinition _wallSlideAction;

    private AudioSystem _audio;
    private PlayerMotor _motor;
    private AudioActionHandle _slideHandle;
    private AudioActionHandle _wallHandle;
    private float _slideTime;
    private float _wallTime;
    private bool _lastSliding;
    private bool _lastWallSliding;

    public AudioActionDefinition SlideAction => _slideAction;
    public AudioActionDefinition WallSlideAction => _wallSlideAction;

    private void Awake()
    {
        _motor = GetComponent<PlayerMotor>();
    }

    private void Update()
    {
        if (_audio == null) _audio = AudioSystem.Instance;
        if (_audio == null || _motor == null) return;
        if (GetComponent<WwiseActionDriver>() != null) return;
        if (_slideAction == null && _wallSlideAction == null) return;

        float dt = TimeManager.UnscaledDeltaTime;
        UpdateSlide(dt);
        UpdateWallSlide(dt);
    }

    private void UpdateSlide(float deltaTime)
    {
        bool sliding = _motor.IsSliding;
        if (sliding && !_lastSliding && _slideAction != null)
        {
            _slideTime = 0f;
            _slideHandle = _audio.PlayAction(_slideAction, transform, string.Empty);
        }
        else if (!sliding && _lastSliding)
        {
            if (_slideHandle != null) _slideHandle.Stop();
            _slideHandle = null;
        }

        if (sliding && _slideHandle != null)
        {
            _slideTime += deltaTime;
            _slideHandle.SetControl(BuildFrame(_slideTime, 1f));
        }
        _lastSliding = sliding;
    }

    private void UpdateWallSlide(float deltaTime)
    {
        bool sliding = _motor.IsWallSliding;
        if (sliding && !_lastWallSliding && _wallSlideAction != null)
        {
            _wallTime = 0f;
            _wallHandle = _audio.PlayAction(_wallSlideAction, transform, string.Empty);
        }
        else if (!sliding && _lastWallSliding)
        {
            if (_wallHandle != null) _wallHandle.Stop();
            _wallHandle = null;
        }

        if (sliding && _wallHandle != null)
        {
            _wallTime += deltaTime;
            _wallHandle.SetControl(BuildFrame(_wallTime, _motor.WallApproachAngle / 90f));
        }
        _lastWallSliding = sliding;
    }

    /// <summary>把当前动作状态打包成曲线映射用的控制帧。</summary>
    private ActionControlFrame BuildFrame(float elapsed, float contact)
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
            SurfaceId = string.Empty,
            Seed = GetInstanceID()
        };
    }
}
