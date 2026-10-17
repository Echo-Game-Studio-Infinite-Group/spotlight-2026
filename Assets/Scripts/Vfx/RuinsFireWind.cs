using UnityEngine;

// RuinsFireWind —— 全局风源（废墟氛围火的统一驱动）
//
// 为什么要有它：调研里社区反复强调"风是氛围火的灵魂"（火苗与余烬要随不规则的风来回摆），
// 而且**必须全局一致**——雾、烟、余烬、火苗朝向若各吹各的，一眼就假。所以：
//   · 全场景一个风向向量，写进 shader 全局 `_RuinsFireWind`（shader 里声明在 CBUFFER 之外，
//     Shader.SetGlobalVector 才生效），火焰卡片在顶点按它顺风弯曲；
//   · 风的强度带"阵风"起伏（两层整数频率正弦叠加 → 平滑且确定，可复现）。
//
// 编辑器批处理出图时，由出图脚本直接调 Set()/Sample() 设值（批处理跑不到 Update）。
[ExecuteAlways]
[DisallowMultipleComponent]
public sealed class RuinsFireWind : MonoBehaviour
{
    [Tooltip("风向（世界空间，只看 XZ；Y 分量会被忽略）")]
    public Vector2 direction = new Vector2(1f, 0.15f);

    [Tooltip("基准风速（对应 shader 的 _WindStrength 倍率前的量级）")]
    [Range(0f, 3f)] public float baseStrength = 0.55f;

    [Tooltip("阵风起伏幅度（0 = 恒定风）")]
    [Range(0f, 1f)] public float gustAmplitude = 0.35f;

    [Tooltip("阵风频率（Hz 量级的观感参数）")]
    [Range(0f, 1f)] public float gustFrequency = 0.11f;

    [Tooltip("风向缓慢摆动的幅度（弧度，模拟风在飘）")]
    [Range(0f, 1.5f)] public float directionWander = 0.45f;

    static readonly int WindId = Shader.PropertyToID("_RuinsFireWind");

    public static RuinsFireWind Current { get; private set; }

    void OnEnable()
    {
        Current = this;
        Apply(Time.time);
    }

    void OnDisable()
    {
        if (Current == this) Current = null;
        Shader.SetGlobalVector(WindId, new Vector4(1f, 0f, 0.1f, 0f) * 0.35f);
    }

    void Update() => Apply(Time.time);

    public void Apply(float time) => Set(direction, baseStrength, gustAmplitude, gustFrequency, directionWander, time);

    // 纯计算版本：给出"某一时刻的风向量"，批处理出图与运行时共用同一条公式。
    public static Vector3 Evaluate(Vector2 dir, float baseStrength, float gustAmp,
                                   float gustFreq, float wander, float time)
    {
        Vector2 d = dir.sqrMagnitude < 1e-6f ? new Vector2(1f, 0f) : dir.normalized;

        // 阵风：两层整数频率正弦叠加（确定、平滑、无随机跳变）
        float gust = 1f + gustAmp * (Mathf.Sin(time * gustFreq * 6.2831853f) * 0.65f
                                   + Mathf.Sin(time * gustFreq * 2.0f * 6.2831853f + 1.7f) * 0.35f);

        // 风向缓慢摆动：两条不同频率的摇摆叠出"风在飘"
        float ang = wander * (Mathf.Sin(time * 0.23f) * 0.6f + Mathf.Sin(time * 0.07f + 0.9f) * 0.4f);
        float cs = Mathf.Cos(ang), sn = Mathf.Sin(ang);
        Vector2 rd = new Vector2(d.x * cs - d.y * sn, d.x * sn + d.y * cs);

        float s = Mathf.Max(0f, baseStrength) * gust;
        return new Vector3(rd.x * s, 0f, rd.y * s);
    }

    public void Set(Vector2 dir, float baseStrength, float gustAmp, float gustFreq, float wander, float time)
    {
        Vector3 w = Evaluate(dir, baseStrength, gustAmp, gustFreq, wander, time);
        Shader.SetGlobalVector(WindId, new Vector4(w.x, w.y, w.z, 0f));
    }

    // 给批处理/工具用的静态入口（不依赖场景里有实例）
    public static void SetGlobal(Vector3 wind)
        => Shader.SetGlobalVector(WindId, new Vector4(wind.x, wind.y, wind.z, 0f));
}
