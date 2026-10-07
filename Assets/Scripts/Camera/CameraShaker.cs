using Cinemachine;
using UnityEngine;

// 玩家相机的震屏接收端：保证 vcam 上挂着与发射端同频道的 CinemachineImpulseListener，
// 并把「伤害 → 幅度」的换算收在这里，各处发射端直接取用，避免各算一份。
// 不直接写相机 Transform：位置与朝向归 CinemachineBrain / vcam 所有，写了也会被下一帧覆盖。
[DisallowMultipleComponent]
public sealed class CameraShaker : MonoBehaviour
{
    // 和发射端的Channel要保持一致
    public const int ImpulseChannel = 1;

    [HideInInspector, SerializeField] private CinemachineVirtualCamera _virtualCamera;
    [Tooltip("基准伤害")]
    [SerializeField, Min(0.001f)] private float _refDmg = 20f;
    [Tooltip("基准伤害对应的抖动幅度")]
    [SerializeField, Min(0f)] private float _refAmp = 0.5f;

    private static CameraShaker _instance;

    public static float Normalize(float dmg)
    {
        if (_instance == null) return 0f;
        return Mathf.Max(0f, dmg) / _instance._refDmg * _instance._refAmp;
    }

    // 震屏方法，直接包装CM官方震屏，以后全局调用CameraShaker.Emit()即可
    public static void Emit(CinemachineImpulseSource source, Vector3 direction, float dmg)
    {
        if (source == null) return;
        float amplitude = Normalize(dmg);
        if (amplitude <= 0f) return;
        // 防止浮点数精度问题，故跟0.0001f比较代替!=0
        source.m_DefaultVelocity = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.back;
        source.GenerateImpulseWithForce(amplitude);
    }

    private void Awake()
    {
        _instance = this;
        // 震屏组件按归属规则一定挂在 vcam 物体上，自取即可，不需要兜底判断。
        _virtualCamera = GetComponent<CinemachineVirtualCamera>();
        EnsureListener(_virtualCamera);
    }

    private void OnDestroy()
    {
        if (_instance == this) _instance = null;
    }

    // 给Editor自动装配用的
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
