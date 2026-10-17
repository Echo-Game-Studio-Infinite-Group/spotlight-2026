using UnityEngine;

// RuinsFireFlicker —— 火光闪烁（只给"驻足档"或近处少数几堆火用）
//
// 调研结论（docs/技术/游戏特效与火焰调研.md §6.4）：
//   · 火光不能"一堆火一个实时光"——灯数会爆（本工程附加光 PerObject 上限 24）。
//     所以：**恒定照明贡献烘进光照贴图**（正式做法），动态闪烁灯只给近处/驻足档。
//   · 闪烁必须**低频、幅度小、不规则**，绝不每帧随机跳（那是最典型的"廉价感"来源）。
//     这里用两层整数频率正弦 + 一层更慢的"呼吸"叠出确定但看起来不规则的光强曲线。
//
// 可复现：Apply(time) 是纯函数，编辑器出图脚本可以指定任意 t 取到同一结果。
[ExecuteAlways]
[DisallowMultipleComponent]
[RequireComponent(typeof(Light))]
public sealed class RuinsFireFlicker : MonoBehaviour
{
    [Header("Base")]
    public float baseIntensity = 2.2f;
    public float baseRange = 6.5f;

    [Tooltip("强度起伏幅度（占基准的比例）")]
    [Range(0f, 1f)] public float intensityAmplitude = 0.22f;

    [Tooltip("照射半径起伏幅度（比强度更小，半径抖动过大会让人眼觉得灯在跑）")]
    [Range(0f, 1f)] public float rangeAmplitude = 0.08f;

    [Tooltip("闪烁主频（Hz）")]
    [Range(0.02f, 8f)] public float flickerSpeed = 1.15f;

    [Tooltip("每堆火的相位错开（0 = 全部同步；建议 >0，避免整排火一起呼吸）")]
    public float phaseOffset = 0f;

    Light _light;
    Vector3 _initialPosition;

    void OnEnable()
    {
        _light = GetComponent<Light>();
        _initialPosition = transform.localPosition;
        if (baseIntensity <= 0f) baseIntensity = _light.intensity;
        if (baseRange <= 0f) baseRange = _light.range;
        Apply(Time.time);
    }

    void Update() => Apply(Time.time);

    public void Apply(float time)
    {
        if (_light == null) _light = GetComponent<Light>();
        if (_light == null) return;

        float t = time * flickerSpeed * 6.2831853f + phaseOffset;

        // 三层：快抖 + 慢呼吸 + 极慢漂移。都用整数比频率 → 循环感弱、不"打拍子"。
        float n = Mathf.Sin(t) * 0.45f
                + Mathf.Sin(t * 1.61803f + 0.7f) * 0.33f
                + Mathf.Sin(time * 0.31f + phaseOffset * 3.1f) * 0.22f;

        _light.intensity = baseIntensity * (1f + intensityAmplitude * n);
        _light.range = baseRange * (1f + rangeAmplitude * n * 0.8f);

        // 火苗会让光源中心有极轻微漂移（人眼读作"火在动"）
        transform.localPosition = _initialPosition + new Vector3(
            Mathf.Sin(t * 0.53f) * 0.045f,
            Mathf.Sin(t * 0.79f + 1.3f) * 0.03f,
            Mathf.Sin(t * 0.41f + 2.1f) * 0.045f);
    }
}
