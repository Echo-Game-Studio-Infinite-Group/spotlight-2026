using Cinemachine;
using UnityEngine;

// 命中震屏：走 Cinemachine Impulse，而不是直接改相机 Transform。
// 相机的位置与朝向由 CinemachineBrain / vcam 与 PlayerCameraRig 掌管，直接写 Transform 会被下一帧覆盖；
// 而且 SpeedCameraFeedback 已经在改同一台 vcam 的 Lens，震屏必须走独立的叠加通道。
[DisallowMultipleComponent]
public sealed class CameraShaker : MonoBehaviour
{
    private static CameraShaker _instance;

    [SerializeField] private CinemachineImpulseSource _source;
    [SerializeField, Min(0f)] private float _defaultAmplitude = 0.6f;

    public static CameraShaker Instance => _instance;

    public void Configure(CinemachineImpulseSource source, float defaultAmplitude)
    {
        _source = source;
        _defaultAmplitude = Mathf.Max(0f, defaultAmplitude);
    }

    private void Awake()
    {
        _instance = this;
        if (_source == null) _source = GetComponent<CinemachineImpulseSource>();
        if (_source == null) _source = gameObject.AddComponent<CinemachineImpulseSource>();
    }

    private void OnDestroy()
    {
        if (_instance == this) _instance = null;
    }

    // 静态入口：命中发生在 PlayerCombat / Enemy 里，让它们免于持有场景引用
    public static void Shake(float amplitude)
    {
        if (_instance == null) return;
        _instance.Generate(amplitude);
    }

    public void Generate(float amplitude)
    {
        if (_source == null || amplitude <= 0f) return;
        // 用相机位置发信号：Impulse Listener 按距离衰减，原点必须落在听者附近
        Camera camera = Camera.main;
        Vector3 origin = camera != null ? camera.transform.position : transform.position;
        _source.GenerateImpulseAt(origin, Vector3.one * amplitude);
    }
}
