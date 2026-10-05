using UnityEngine;

/// <summary>
/// 单音效连续动作定义。
///
/// 模型对齐通用采样器（SFZ / SoundFont）的两条独立主线：
/// 1. 音量包络：标准 ADSR，A/D/R 是秒，S 是电平。按住多久 Sustain 就保持多久。
/// 2. 采样回放：播放头从 StartPosition01 前进，越过 LoopEnd01 时回跳到 LoopStart01。
///    循环只在按住期间发生；松手后播放头继续向素材结尾推进，那就是天然的 Release 尾巴。
/// </summary>
[CreateAssetMenu(fileName = "AudioActionDefinition", menuName = "超高速行者/音频/单音效连续动作")]
public sealed class AudioActionDefinition : ScriptableObject
{
    public string Id;

    [Header("采样源")]
    public AudioClip Clip;

    [Header("采样回放位置（0..1 相对整段长度）")]
    [Tooltip("播放头起点。")]
    [Range(0f, 1f)] public float StartPosition01;
    [Tooltip("循环回跳点：播放头越过 LoopEnd01 后跳回这里。")]
    [Range(0f, 1f)] public float LoopStart01 = 0.2f;
    [Tooltip("循环结束点。")]
    [Range(0f, 1f)] public float LoopEnd01 = 0.9f;
    [Tooltip("松手后是否把播放头跳到 ReleasePosition01 再往素材结尾播；关掉则从当前播放头继续。")]
    public bool SeekToReleaseRegion;
    [Range(0f, 1f)] public float ReleasePosition01 = 0.9f;
    public AudioActionLoopMode LoopMode = AudioActionLoopMode.SustainLoop;

    [Header("循环接缝")]
    [Tooltip("循环接缝的交叠淡化时长。会与循环区尾部重叠，做淡出/淡入，再交给 AudioSource.loop 无缝循环。")]
    [Range(0f, 0.25f)] public float LoopSeamFadeSeconds = 0.02f;

    [Header("持续段质感")]
    [Tooltip("颗粒模式的单颗时长（秒）。仅 LoopMode = Granular 时生效。")]
    [Min(0.01f)] public float GrainSeconds = 0.18f;
    [Tooltip("颗粒模式的相邻颗粒间隔（秒）。小于 GrainSeconds 就是重叠。")]
    [Min(0.005f)] public float GrainSpacingSeconds = 0.07f;
    [Tooltip("颗粒读取起点的随机范围（占循环区长度的比例），0 = 每颗都一样。")]
    [Range(0f, 1f)] public float GrainRandomStart01 = 1f;
    [Tooltip("每颗颗粒的随机失谐（音分，±）。同一段素材以不同偏移重叠会产生梳状滤波，" +
             "听感是金属/电子味；加一点随机失谐把相位打散就能消掉。0 = 不苟谐（容易出金属音）。")]
    [Range(0f, 500f)] public float GrainTuneCents = 25f;
    [Tooltip("颗粒淡入淡出曲线。")]
    public AudioCurveMapping GrainFadeCurve = AudioCurveMapping.SCurve;
    [Tooltip("每次触发在循环区长度内做多少比例的随机偏移，避免每一遍滑铲听起来一模一样。0 表示不随机。")]
    [Range(0f, 0.25f)] public float LoopRegionRandom01 = 0.05f;
    [Tooltip("持续段的慢速随机音量漂移（dB），让循环不显得死板。")]
    [Range(0f, 6f)] public float SustainDriftDb = 0.8f;
    [Tooltip("持续段的慢速随机低通漂移（Hz）。")]
    [Min(0f)] public float SustainDriftCutoffHz = 600f;
    [Tooltip("保持段循环期间的音量减益（dB）。负值 = 压小声，" +
             "用来让 sustain 的响度和启动段 / 尾音衔接上。0 = 不处理。")]
    [Range(-24f, 6f)] public float SustainGainDb = 0f;

    [Header("音量包络 ADSR")]
    public AdsrEnvelope Envelope = new AdsrEnvelope();

    [Header("松手行为")]
    [Tooltip("启动段（StartPosition01 → LoopStart01，还没进循环）时松手：\n" +
             "  StopImmediately = 直接停；PlayFullOnce = 不循环、把这一遍完整播完。\n" +
             "保持段（已经进循环）松手：先走完当前这一遍循环，再播尾音区间（LoopEnd01→素材结尾）。\n" +
             "Release 时长就等于这段尾音区间的长度，没有单独的 Release 时间字段。")]
    public AudioIntroReleaseMode IntroReleaseMode = AudioIntroReleaseMode.PlayFullOnce;

    [Header("动作驱动音色曲线（映射速度，不是时间）")]
    public AudioEnvelope PitchFollow = AudioEnvelope.Linear();
    public AudioEnvelope LowpassFollow = AudioEnvelope.Linear();

    [Header("随机差分")]
    public AudioRange StartPitchSemitones = new AudioRange(-0.35f, 0.35f);
    public AudioRange SustainPitchSemitones = new AudioRange(-0.2f, 0.2f);
    public AudioRange ReleasePitchSemitones = new AudioRange(-1.2f, 1.2f);
    public AudioRange GainDbVariation = new AudioRange(-2f, 1.5f);
    public AudioRange LowpassVariation = new AudioRange(-1800f, 1800f);

    [Header("动作驱动音色")]
    [Min(0f)] public float PitchFollowMaxSemitones = 0.9f;
    [Min(10f)] public float LowpassIdleHz = 18000f;
    [Min(10f)] public float LowpassFastHz = 8500f;
    [Range(0f, 1f)] public float DirectionPan = 0.35f;

    [Header("空间")]
    [Range(0f, 1f)] public float SpatialBlend = 1f;
    [Min(0.01f)] public float MinDistance = 1.5f;
    [Min(0.02f)] public float MaxDistance = 55f;
    [Range(0, 256)] public int Priority = 128;

    public bool LoopsWhileHeld =>
        LoopMode == AudioActionLoopMode.SustainLoop ||
        LoopMode == AudioActionLoopMode.ContinuousLoop ||
        LoopMode == AudioActionLoopMode.Granular;

    /// <summary>只有 loop_continuous 松手后继续循环；颗粒模式松手后同样去播尾音区。</summary>
    public bool LoopsDuringRelease => LoopMode == AudioActionLoopMode.ContinuousLoop;

    public bool IsGranular => LoopMode == AudioActionLoopMode.Granular;

    public void EnsureDefaults()
    {
        if (Envelope == null) Envelope = new AdsrEnvelope();
        Envelope.Clamp();
        if (PitchFollow == null) PitchFollow = AudioEnvelope.Linear();
        if (LowpassFollow == null) LowpassFollow = AudioEnvelope.Linear();
        PitchFollow.EnsureDefaults();
        LowpassFollow.EnsureDefaults();
        StartPosition01 = Mathf.Clamp01(StartPosition01);
        LoopStart01 = Mathf.Clamp01(LoopStart01);
        LoopEnd01 = Mathf.Clamp(LoopEnd01, LoopStart01 + 0.001f, 1f);
        ReleasePosition01 = Mathf.Clamp01(ReleasePosition01);
        LoopSeamFadeSeconds = Mathf.Clamp(LoopSeamFadeSeconds, 0f, 0.25f);
        LoopRegionRandom01 = Mathf.Clamp(LoopRegionRandom01, 0f, 0.25f);
        SustainDriftDb = Mathf.Clamp(SustainDriftDb, 0f, 6f);
        SustainDriftCutoffHz = Mathf.Max(0f, SustainDriftCutoffHz);
        SustainGainDb = Mathf.Clamp(SustainGainDb, -24f, 6f);
        GrainSeconds = Mathf.Max(0.01f, GrainSeconds);
        GrainSpacingSeconds = Mathf.Clamp(GrainSpacingSeconds, 0.005f, GrainSeconds);
        GrainRandomStart01 = Mathf.Clamp01(GrainRandomStart01);
        GrainTuneCents = Mathf.Clamp(GrainTuneCents, 0f, 500f);
    }

    /// <summary>恢复出厂默认值。保留 Clip 和 Id，避免一键重置把素材也清掉。</summary>
    public void ResetToDefaults()
    {
        StartPosition01 = 0f;
        LoopStart01 = 0.2f;
        LoopEnd01 = 0.9f;
        ReleasePosition01 = 0.9f;
        SeekToReleaseRegion = false;
        LoopMode = AudioActionLoopMode.SustainLoop;
        LoopSeamFadeSeconds = 0.02f;
        LoopRegionRandom01 = 0.05f;
        SustainDriftDb = 0.8f;
        SustainDriftCutoffHz = 600f;
        SustainGainDb = 0f;
        GrainSeconds = 0.18f;
        GrainSpacingSeconds = 0.07f;
        GrainRandomStart01 = 1f;
        GrainTuneCents = 25f;
        GrainFadeCurve = AudioCurveMapping.SCurve;
        Envelope = new AdsrEnvelope();
        IntroReleaseMode = AudioIntroReleaseMode.PlayFullOnce;
        PitchFollow = AudioEnvelope.Linear();
        LowpassFollow = AudioEnvelope.Linear();
        StartPitchSemitones = new AudioRange(-0.35f, 0.35f);
        SustainPitchSemitones = new AudioRange(-0.2f, 0.2f);
        ReleasePitchSemitones = new AudioRange(-1.2f, 1.2f);
        GainDbVariation = new AudioRange(-2f, 1.5f);
        LowpassVariation = new AudioRange(-1800f, 1800f);
        PitchFollowMaxSemitones = 0.9f;
        LowpassIdleHz = 18000f;
        LowpassFastHz = 8500f;
        DirectionPan = 0.35f;
        SpatialBlend = 1f;
        MinDistance = 1.5f;
        MaxDistance = 55f;
        Priority = 128;
        EnsureDefaults();
    }
}
