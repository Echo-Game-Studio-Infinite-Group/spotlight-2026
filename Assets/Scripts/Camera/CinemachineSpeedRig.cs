using Cinemachine;
using UnityEngine;

// 速度感相机转速器：把玩家水平速度映射成镜头 FOV，并可选驱动跟随预判
// 挂在 Virtual Camera 上；FOV 在 Update 写入，早于 CinemachineBrain 的 LateUpdate 取用
// 预判量以「米」为单位下发（时间 = 米 / 速度）：源码里 LookaheadTime 是秒数且无上限，
// 在本工程 10~120 的速度区间内固定秒数必然在一端失控
[RequireComponent(typeof(CinemachineVirtualCamera))]
public class CinemachineSpeedRig : MonoBehaviour
{
    [Header("速度源")]
    [Tooltip("玩家身份组件；速度真值在它持有的 PlayerMotor 上")]
    [SerializeField] private Player _player;

    [Header("速度归一化")]
    [Tooltip("达到该水平速度视为满速（量级参考 MovementParams 的地速阈值~软上限）")]
    [SerializeField] private float _fullSpeed = 40f;

    [Tooltip("低于该速度不产生速度感，避免走路时镜头呼吸")]
    [SerializeField] private float _minSpeed = 10f;

    [Header("FOV 速度感")]
    [Tooltip("满速时的 FOV；基准 FOV 直接读 Virtual Camera 的 Lens，不另存一份")]
    [SerializeField] private float _maxFov = 85f;

    [Tooltip("响应锐度（每秒指数逼近）：越大越跟手，越小越绵")]
    [SerializeField] private float _sharpness = 4f;

    [Header("跟随预判")]
    [Tooltip("满速时的前瞻距离（米）；0 = 关闭预判")]
    [SerializeField] private float _lookaheadMeters = 0f;

    [Tooltip("预判平滑：压住速度抖动带来的画面摇摆")]
    [SerializeField] private float _lookaheadSmoothing = 6f;

    private CinemachineVirtualCamera _vcam;
    private CinemachineFramingTransposer _body;
    private float _baseFov;
    private float _fov;

    private void Awake()
    {
        _vcam = GetComponent<CinemachineVirtualCamera>();
        _body = GetComponentInChildren<CinemachineFramingTransposer>();

        _baseFov = _vcam.m_Lens.FieldOfView;
        _fov = _baseFov;

        if (_body != null)
        {
            _body.m_LookaheadSmoothing = _lookaheadSmoothing;
        }

        if (_player == null)
        {
            Debug.LogError("[CinemachineSpeedRig] _player 未赋值，速度感不会生效", this);
            enabled = false;
        }
    }

    private void Update()
    {
        // 用 Time.deltaTime 与 Brain 的 m_IgnoreTimeScale=0 保持同一时间轴；TimeManager 只缩放自己的
        // WorldDeltaTime，不写 Time.timeScale，所以对本组件而言它本来就是真实时间
        float deltaTime = Time.deltaTime;
        float speed = _player.Speed;
        float ratio = Mathf.Clamp01(Mathf.InverseLerp(_minSpeed, _fullSpeed, speed));

        _fov = Mathf.Lerp(_fov, Mathf.Lerp(_baseFov, _maxFov, ratio), 1f - Mathf.Exp(-_sharpness * deltaTime));
        ApplyFov(_fov);

        if (_body != null)
        {
            float meters = _lookaheadMeters * ratio;
            _body.m_LookaheadTime = speed > 0.01f ? meters / speed : 0f;
        }
    }

    private void ApplyFov(float fov)
    {
        LensSettings lens = _vcam.m_Lens;
        lens.FieldOfView = fov;
        _vcam.m_Lens = lens;
    }
}
