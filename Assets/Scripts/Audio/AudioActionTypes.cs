using System;
using System.Collections.Generic;
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
/// 动态动作上报给音频系统的阶段。
/// 动作代码只描述“开始 / 更新 / 请求停止”，不直接操作 Wwise 或 PCM。
/// </summary>
public enum DynamicAudioActionPhase
{
    Start,
    Update,
    Stop
}

/// <summary>
/// 特殊停止方式。动作代码可以在停止请求里指定，音频侧负责解释。
/// </summary>
public enum DynamicAudioActionStopMode
{
    /// <summary>走当前定义的 Release 逻辑。</summary>
    Release,

    /// <summary>立即停止，不等待循环或尾音。</summary>
    Immediate,

    /// <summary>走完当前这一遍循环，再进入 Release。</summary>
    FinishCurrentLoop,

    /// <summary>从头或当前位置完整播放一遍，不循环。</summary>
    PlayFullOnce
}

/// <summary>
/// 动态动作接口的数据帧。动作代码只生产这个结构，不依赖 Wwise 类型。
/// </summary>
public struct DynamicAudioActionRequest
{
    public string ActionId;
    public DynamicAudioActionPhase Phase;
    public DynamicAudioActionStopMode StopMode;
    public float NormalizedSpeed;
    public float ContactIntensity;
    public float Direction;
    public string SurfaceId;
    public int Seed;

    public static DynamicAudioActionRequest Start(
        string actionId,
        float normalizedSpeed = 0f,
        float contactIntensity = 0f,
        float direction = 0f,
        string surfaceId = "",
        int seed = 0)
    {
        return Build(
            actionId,
            DynamicAudioActionPhase.Start,
            DynamicAudioActionStopMode.Release,
            normalizedSpeed,
            contactIntensity,
            direction,
            surfaceId,
            seed);
    }

    public static DynamicAudioActionRequest Update(
        string actionId,
        float normalizedSpeed,
        float contactIntensity,
        float direction = 0f,
        string surfaceId = "")
    {
        return Build(
            actionId,
            DynamicAudioActionPhase.Update,
            DynamicAudioActionStopMode.Release,
            normalizedSpeed,
            contactIntensity,
            direction,
            surfaceId,
            0);
    }

    public static DynamicAudioActionRequest Stop(
        string actionId,
        DynamicAudioActionStopMode stopMode = DynamicAudioActionStopMode.Release)
    {
        return Build(
            actionId,
            DynamicAudioActionPhase.Stop,
            stopMode,
            0f,
            0f,
            0f,
            string.Empty,
            0);
    }

    private static DynamicAudioActionRequest Build(
        string actionId,
        DynamicAudioActionPhase phase,
        DynamicAudioActionStopMode stopMode,
        float normalizedSpeed,
        float contactIntensity,
        float direction,
        string surfaceId,
        int seed)
    {
        return new DynamicAudioActionRequest
        {
            ActionId = actionId ?? string.Empty,
            Phase = phase,
            StopMode = stopMode,
            NormalizedSpeed = Mathf.Clamp01(normalizedSpeed),
            ContactIntensity = Mathf.Clamp01(contactIntensity),
            Direction = Mathf.Clamp(direction, -1f, 1f),
            SurfaceId = surfaceId ?? string.Empty,
            Seed = seed
        };
    }
}

/// <summary>
/// 动作代码实现这个接口，音频系统按帧收集动态动作请求。
/// 一个组件可以同时上报多个动作，例如滑铲和墙滑。
/// </summary>
public interface IDynamicAudioActionSource
{
    void CollectDynamicAudioActions(
        List<DynamicAudioActionRequest> output);
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
