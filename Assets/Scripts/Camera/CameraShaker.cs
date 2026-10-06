using Cinemachine;
using UnityEngine;

// 玩家相机的震屏接收端：保证 vcam 上挂着与发射端同频道的 CinemachineImpulseListener，
// 并把「伤害 → 幅度」的换算收在这里，各处发射端直接取用，避免各算一份。
// 不直接写相机 Transform：位置与朝向归 CinemachineBrain / vcam 所有，写了也会被下一帧覆盖。
[DisallowMultipleComponent]
public sealed class CameraShaker : MonoBehaviour
{
    // 必须与发射端 CinemachineImpulseSource 的 m_ImpulseChannel 对齐；两边错开时 impulse 会被静默丢弃。
    public const int ImpulseChannel = 1;

    // 震屏作用在 vcam 的最终输出上，本组件必须与 CinemachineImpulseListener 同挂在 vcam 物体，
    // 所以这个引用只有自取一种可能——不暴露到 Inspector，免得被指到别处后静默失效。
    [HideInInspector, SerializeField] private CinemachineVirtualCamera _virtualCamera;
    [Tooltip("基准伤害，对应下面那个幅度")]
    [SerializeField, Min(0.001f)] private float _referenceDamage = 20f;
    [Tooltip("基准伤害对应的抖动幅度")]
    [SerializeField, Min(0f)] private float _referenceAmplitude = 0.5f;

    // Impulse Duration 落到 0 时 Cinemachine 照样建事件，但包络只在 0.1ms 内非零；
    // 相机每 16ms 才采样一次，表现是「代码跑了、相机纹丝不动」且不报任何错。
    private const float MinImpulseDuration = 0.2f;

    private static CameraShaker _instance;

    // 20 点伤害 → 0.5，其余按倍率线性缩放。
    // 没有实例时返回 0，调用方不必判空——震屏属于表现层，缺了不该打断战斗结算。
    public static float AmplitudeFor(float damage)
    {
        if (_instance == null) return 0f;
        return Mathf.Max(0f, damage) / _instance._referenceDamage * _instance._referenceAmplitude;
    }

    // 全局震屏入口：攻击方带着自己的 ImpulseSource 调用，方向取世界空间，伤害决定力度。
    public static void Emit(CinemachineImpulseSource source, Vector3 direction, float damage)
    {
        if (source == null) return;
        float amplitude = AmplitudeFor(damage);
        if (amplitude <= 0f) return;
        // 只抬不降：Inspector 里填 0 或漏填时把时长兜回可见范围，正常配置不受影响。
        if (source.m_ImpulseDefinition != null)
            source.m_ImpulseDefinition.m_ImpulseDuration =
                Mathf.Max(source.m_ImpulseDefinition.m_ImpulseDuration, MinImpulseDuration);
        // Default Invocation 只承载方向，力度交给 force，避免两处相乘把幅度放大成平方。
        source.m_DefaultVelocity = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.back;
        source.GenerateImpulseWithForce(amplitude);
    }

    private void Awake()
    {
        _instance = this;
        if (_virtualCamera == null) _virtualCamera = GetComponent<CinemachineVirtualCamera>();
        EnsureListener(_virtualCamera);
    }

    private void OnDestroy()
    {
        if (_instance == this) _instance = null;
    }

    // 编辑器装配工具与运行时共用这一份配置，避免两处各写一套参数。
    public static CinemachineImpulseListener EnsureListener(CinemachineVirtualCamera vcam)
    {
        if (vcam == null) return null;
        CinemachineImpulseListener listener = vcam.GetComponent<CinemachineImpulseListener>();
        if (listener == null) listener = vcam.gameObject.AddComponent<CinemachineImpulseListener>();
        // 默认值只在 Inspector 手动添加时由 Reset() 写入，代码添加必须显式补齐。
        listener.m_ApplyAfter = CinemachineCore.Stage.Noise;
        listener.m_ChannelMask = ImpulseChannel;
        listener.m_Gain = 1f;
        listener.m_Use2DDistance = false;
        // 发射端写的是世界方向；开相机空间会把方向当成镜头局部向量，各方向的攻击会塌缩成同一个晃动。
        listener.m_UseCameraSpace = false;
        return listener;
    }
}
