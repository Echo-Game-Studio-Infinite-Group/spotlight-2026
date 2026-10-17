using UnityEngine;

// parry 闪光用的短命点光：亮一下、按秒数衰减到灭、然后隐藏待复用。
// 全场景共用一盏（parry 是低频事件），不闪的时候零开销。
// 衰减按 UnscaledTime——parry 触发的时缓不能把「闪光」拖成常亮。
[RequireComponent(typeof(Light))]
public sealed class SparkLightFader : MonoBehaviour
{
    private static SparkLightFader _shared;

    private Light _light;
    private float _intensity;
    private float _remain;
    private float _seconds;

    /// <summary>在 point 处亮一盏点光，seconds 秒内衰减到灭。</summary>
    public static void Flash(Vector3 point, Color color, float intensity, float range, float seconds)
    {
        if (_shared == null)
        {
            GameObject host = new GameObject("CombatSparks(FlashLight)");
            if (Application.isPlaying) DontDestroyOnLoad(host);
            _shared = host.AddComponent<SparkLightFader>();
        }
        // 上一次闪还没结束就被下一次复用：直接从新值开始，不用等
        _shared.gameObject.SetActive(true);
        _shared.transform.position = point;
        _shared.Begin(color, intensity, range, seconds);
    }

    private void Awake()
    {
        _light = GetComponent<Light>();
        _light.shadows = LightShadows.None; // 一闪而过的光，不值得付出阴影开销
    }

    private void Begin(Color color, float intensity, float range, float seconds)
    {
        if (_light == null) _light = GetComponent<Light>();
        _light.color = color;
        _light.range = range;
        _light.intensity = _intensity = intensity;
        _seconds = Mathf.Max(0.01f, seconds);
        _remain = _seconds;
    }

    private void Update()
    {
        _remain -= TimeManager.UnscaledDeltaTime;
        if (_remain <= 0f)
        {
            _light.intensity = 0f;
            gameObject.SetActive(false);
            return;
        }
        // 二次方缓出：闪得快、暗得柔，比线性衰减更像「光爆」
        float t = _remain / _seconds;
        _light.intensity = _intensity * t * t;
    }
}
