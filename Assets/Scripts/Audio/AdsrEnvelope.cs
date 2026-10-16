using System;
using UnityEngine;

/// <summary>
/// 标准 ADSR 音量包络。
/// A / D / R 是时间（秒），S 是电平（0..1）而不是时长。
/// Sustain 段没有固定长度：只要还按着就一直是 SustainLevel，按住多久就保持多久。
/// 语义与 SFZ 的 ampeg_attack / ampeg_decay / ampeg_sustain / ampeg_release 一致。
/// </summary>
[Serializable]
public sealed class AdsrEnvelope
{
    [Tooltip("Attack：从 0 爬升到 1 的时间（秒）。")]
    [Min(0f)] public float AttackSeconds = 0.01f;

    [Tooltip("Decay：从 1 下降到 SustainLevel 的时间（秒）。")]
    [Min(0f)] public float DecaySeconds = 0.05f;

    [Tooltip("Sustain：按住期间保持的电平（0..1），不是时长。")]
    [Range(0f, 1f)] public float SustainLevel = 1f;

    public AudioCurveMapping AttackCurve = AudioCurveMapping.SCurve;
    public AudioCurveMapping DecayCurve = AudioCurveMapping.Fast;
    public AudioCurveMapping ReleaseCurve = AudioCurveMapping.Fast;

    /// <summary>按住期间的电平。timeSincePress 为从按下算起的秒数。</summary>
    public float EvaluateOn(float timeSincePress)
    {
        float t = Mathf.Max(0f, timeSincePress);
        if (AttackSeconds > 0f && t < AttackSeconds)
        {
            return Mathf.LerpUnclamped(0f, 1f,
                AudioCurveUtility.Map(t / AttackSeconds, AttackCurve));
        }

        float decayTime = t - AttackSeconds;
        if (DecaySeconds > 0f && decayTime < DecaySeconds)
        {
            return Mathf.LerpUnclamped(1f, SustainLevel,
                AudioCurveUtility.Map(decayTime / DecaySeconds, DecayCurve));
        }

        return SustainLevel;
    }

    /// <summary>
    /// 松手后的电平。timeSinceRelease 为从松手算起的秒数，startLevel 是松手瞬间的电平。
    /// duration 由尾音区间长度决定（不是独立字段），所以 release 时长 = 绿色区间长度。
    /// </summary>
    public float EvaluateRelease(float timeSinceRelease, float startLevel, float duration)
    {
        if (duration <= 0f) return 0f;
        float u = Mathf.Clamp01(Mathf.Max(0f, timeSinceRelease) / duration);
        return Mathf.LerpUnclamped(Mathf.Clamp01(startLevel), 0f,
            AudioCurveUtility.Map(u, ReleaseCurve));
    }

    /// <summary>按下后到进入 Sustain 的总时间，仅用于显示。</summary>
    public float AttackDecaySeconds => AttackSeconds + DecaySeconds;

    public void Clamp()
    {
        AttackSeconds = Mathf.Max(0f, AttackSeconds);
        DecaySeconds = Mathf.Max(0f, DecaySeconds);
        SustainLevel = Mathf.Clamp01(SustainLevel);
    }
}
