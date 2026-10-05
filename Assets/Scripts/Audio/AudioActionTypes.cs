using System;
using UnityEngine;

public enum AudioActionState
{
    Idle,
    Starting,
    Sustaining,
    Releasing,
    Finished
}

/// <summary>
/// 采样循环模式，语义对齐 SFZ 的 loop_mode。
/// </summary>
public enum AudioActionLoopMode
{
    /// <summary>no_loop：从起点播到素材结尾，或先收到松手。</summary>
    NoLoop,

    /// <summary>one_shot：整段播完，忽略松手。</summary>
    OneShot,

    /// <summary>loop_sustain：按住时在循环点之间循环，松手后不再循环，播放头继续向素材结尾推进。</summary>
    SustainLoop,

    /// <summary>loop_continuous：一直循环，包含 Release 阶段。</summary>
    ContinuousLoop,

    /// <summary>
    /// 颗粒持续段（本工程扩展，思路来自 GameSynth 的 GranularPlayer）：
    /// 不整段循环，而是在循环区里取一串带淡入淡出窗、随机起点的颗粒重叠播放，
    /// 从根上避免接缝。循环片段是离线渲染好的，仍然交给 AudioSource.loop。
    /// </summary>
    Granular
}

/// <summary>
/// 起段（还没进循环）时松手的处理方式。
/// </summary>
public enum AudioIntroReleaseMode
{
    /// <summary>直接停止音效。</summary>
    StopImmediately,

    /// <summary>不循环，把这一遍音效完整播完再停。</summary>
    PlayFullOnce
}

public enum AudioCurveMapping
{
    Linear,
    Slow,
    Fast,
    SCurve
}

[Serializable]
public struct AudioRange
{
    public float Min;
    public float Max;

    public AudioRange(float min, float max)
    {
        Min = min;
        Max = max;
    }

    public float Value(System.Random random)
    {
        if (random == null) return Min;
        return Min + (Max - Min) * (float)random.NextDouble();
    }

    public float Lerp(float t)
    {
        return Mathf.LerpUnclamped(Min, Max, Mathf.Clamp01(t));
    }
}

public struct ActionControlFrame
{
    public float NormalizedSpeed;
    public float ContactIntensity;
    public float Direction;
    public float Release;
    public float ActionElapsed;
    public string SurfaceId;
    public int Seed;

    public static ActionControlFrame Default => new ActionControlFrame
    {
        NormalizedSpeed = 0f,
        ContactIntensity = 0f,
        Direction = 0f,
        Release = 0f,
        ActionElapsed = 0f,
        SurfaceId = string.Empty,
        Seed = 0
    };
}

public static class AudioCurveUtility
{
    public static float Map(float value, AudioCurveMapping mapping)
    {
        float x = Mathf.Clamp01(value);
        switch (mapping)
        {
            case AudioCurveMapping.Slow:
                return x * x * x;
            case AudioCurveMapping.Fast:
                return 1f - Mathf.Pow(1f - x, 3f);
            case AudioCurveMapping.SCurve:
                return 0.5f + 0.5f * Mathf.Sin(Mathf.PI * x - Mathf.PI * 0.5f);
            default:
                return x;
        }
    }

    public static float Evaluate(AnimationCurve curve, float time, AudioCurveMapping mapping)
    {
        if (curve == null) return Map(time, mapping);
        return Map(curve.Evaluate(Mathf.Clamp01(time)), mapping);
    }

    public static float LerpFrequency(float minHz, float maxHz, float t)
    {
        float min = Mathf.Max(10f, minHz);
        float max = Mathf.Max(min, maxHz);
        return min * Mathf.Pow(max / min, Mathf.Clamp01(t));
    }

    public static float DbToLinear(float db)
    {
        if (db <= -80f) return 0f;
        return Mathf.Pow(10f, db / 20f);
    }

    public static float SemitoneToRatio(float semitones)
    {
        return Mathf.Pow(2f, semitones / 12f);
    }

    public static float SmoothTowards(float current, float target, float response, float deltaTime)
    {
        if (deltaTime <= 0f) return current;
        float alpha = 1f - Mathf.Exp(-Mathf.Max(0f, response) * deltaTime);
        return Mathf.LerpUnclamped(current, target, alpha);
    }
}
