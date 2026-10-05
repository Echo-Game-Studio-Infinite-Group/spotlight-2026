using System;
using UnityEngine;

/// <summary>
/// 一个连续动作的播放实例。
///
/// 两条互相独立的时间线：
/// - 音量：标准 ADSR（按下走 A→D→S，松手走 R）。
/// - 采样回放：播放头从 StartPosition 前进。
///   循环段用 <see cref="ActionClipRenderer"/> 预渲染好的“交叠淡化循环片段”，
///   由 AudioSource.loop 在音频线程里无缝循环；起段用 SetScheduledEndTime 精确交班，
///   所以接缝既不靠硬拼，也不靠每帧改音量。
///   loop_sustain 松手后停止循环，回到原始素材从当前位置继续向结尾播，即 Release 尾巴。
/// </summary>
public sealed class ActionAudioVoice : MonoBehaviour
{
    private const float ContactGainResponse = 12f;
    private const double ScheduleLeadSeconds = 0.05;

    private AudioSource _sourceA;
    private AudioSource _sourceB;
    private AudioLowPassFilter _lowpass;
    private AudioHighPassFilter _highpass;
    private AudioActionDefinition _definition;
    private ActionControlFrame _control;
    private System.Random _random;
    private Transform _follow;
    private AudioClip _clip;
    private AudioClip _loopClip;
    private AudioActionState _state;

    private int _startSample;
    private int _loopStartSample;
    private int _loopEndSample;
    private int _crossfadeSamples;
    private int _releaseSample;
    private bool _hasScheduledLoop;
    private double _loopStartDspTime = double.MaxValue;
    private int _loopSeed;

    private float _heldTime;
    private float _releaseTime;
    private float _releaseStartLevel;
    private float _releaseDuration = 0.3f;
    private bool _released;         // 已经进入 Release 段
    private bool _releasePending;   // 持续段松手：等这一遍 loop 走完
    private bool _playFullOnce;     // 起段松手：不循环，把这一遍播完
    private bool _loopEntered;
    private int _lastLoopSample = -1;
    private float _silentTime;

    private float _gainOffsetDb;
    private float _startPitch = 1f;
    private float _sustainPitch = 1f;
    private float _releasePitch = 1f;
    private float _lowpassOffset;
    private float _lowpassHz;
    private float _contactGain = 1f;
    private float _speedPitch = 1f;
    private float _driftTimer;
    private float _driftGainValue;
    private float _driftGainTarget;
    private float _driftCutoffValue;
    private float _driftCutoffTarget;

    public AudioActionState State => _state;
    public ActionControlFrame Control => _control;
    public float LowpassHz => _lowpassHz;
    public float EnvelopeGain { get; private set; }
    public float PitchEnvelopeValue { get; private set; }
    public float LowpassEnvelopeValue { get; private set; }
    public bool LoopEntered => _loopEntered;
    public bool UsesRenderedLoop => _loopClip != null;
    public bool IsActive => _state != AudioActionState.Idle && _state != AudioActionState.Finished;

    private bool IsAnyPlaying =>
        (_sourceA != null && _sourceA.isPlaying) || (_sourceB != null && _sourceB.isPlaying);

    public void Initialize(AudioActionDefinition definition, Transform follow, string surfaceId, int seed)
    {
        EnsureSource();
        _definition = definition;
        _follow = follow;
        _random = new System.Random(seed == 0 ? Environment.TickCount ^ GetInstanceID() : seed);
        _state = AudioActionState.Idle;
        _heldTime = 0f;
        _releaseTime = 0f;
        _released = false;
        _releasePending = false;
        _playFullOnce = false;
        _loopEntered = false;
        _lastLoopSample = -1;
        _silentTime = 0f;
        _contactGain = 1f;
        _control = ActionControlFrame.Default;
        _control.Seed = seed;
        _control.SurfaceId = surfaceId;
        transform.position = follow != null ? follow.position : Vector3.zero;
        if (_definition != null) _definition.EnsureDefaults();
    }

    public void Begin()
    {
        if (_definition == null || _definition.Clip == null)
        {
            Finish();
            return;
        }

        _clip = _definition.Clip;
        _gainOffsetDb = _definition.GainDbVariation.Value(_random);
        _startPitch = AudioCurveUtility.SemitoneToRatio(_definition.StartPitchSemitones.Value(_random));
        _sustainPitch = AudioCurveUtility.SemitoneToRatio(_definition.SustainPitchSemitones.Value(_random));
        _releasePitch = AudioCurveUtility.SemitoneToRatio(_definition.ReleasePitchSemitones.Value(_random));
        _lowpassOffset = _definition.LowpassVariation.Value(_random);
        _startSample = ToSample(_definition.StartPosition01);
        _loopStartSample = ToSample(_definition.LoopStart01);
        _loopEndSample = Mathf.Max(_loopStartSample + 2, ToSample(_definition.LoopEnd01));
        ApplyLoopRegionJitter();
        _crossfadeSamples = Mathf.Max(0,
            Mathf.RoundToInt(_definition.LoopSeamFadeSeconds * _clip.frequency));
        _releaseSample = ToSample(_definition.ReleasePosition01);
        _loopSeed = _random.Next();
        _loopClip = _definition.IsGranular
            ? ActionClipRenderer.GetGranularLoop(
                _clip, _loopStartSample, _loopEndSample,
                _definition.GrainSeconds, _definition.GrainSpacingSeconds,
                _definition.GrainRandomStart01, _definition.GrainTuneCents,
                _definition.GrainFadeCurve, _loopSeed)
            : ActionClipRenderer.GetCrossfadeLoop(
                _clip, _loopStartSample, _loopEndSample, _crossfadeSamples);
        _hasScheduledLoop = _definition.LoopsWhileHeld && _loopClip != null;
        _loopStartDspTime = double.MaxValue;

        _heldTime = 0f;
        _releaseTime = 0f;
        _released = false;
        _releasePending = false;
        _playFullOnce = false;
        _loopEntered = false;
        _lastLoopSample = -1;
        _silentTime = 0f;
        _contactGain = 1f;
        _driftTimer = 0f;
        _driftGainValue = 0f;
        _driftGainTarget = 0f;
        _driftCutoffValue = 0f;
        _driftCutoffTarget = 0f;
        EnvelopeGain = 0f;

        _sourceA.Stop();
        _sourceB.Stop();
        ApplySpatial(_sourceA);
        ApplySpatial(_sourceB);
        _sourceA.clip = _clip;
        _sourceB.clip = _hasScheduledLoop ? _loopClip : _clip;
        _sourceA.loop = false;
        _sourceB.loop = _hasScheduledLoop;
        _sourceA.volume = 0f;
        _sourceB.volume = 0f;

        double startTime = AudioSettings.dspTime + ScheduleLeadSeconds;
        _sourceA.timeSamples = _startSample;
        _sourceA.pitch = _startPitch;
        _sourceA.PlayScheduled(startTime);

        if (_hasScheduledLoop)
        {
            // 启动段 = StartPosition → LoopStart，播完正好接上循环片段的第一个采样。
            int introFrames = Mathf.Max(1, _loopStartSample - _startSample);
            double introSeconds = introFrames / (double)(_clip.frequency * Mathf.Max(0.01f, _startPitch));
            _sourceA.SetScheduledEndTime(startTime + introSeconds);
            _sourceB.timeSamples = 0;
            _sourceB.pitch = _startPitch;
            _sourceB.PlayScheduled(startTime + introSeconds);
            _loopStartDspTime = startTime + introSeconds;
        }

        _state = AudioActionState.Starting;
    }

    public void SetControl(in ActionControlFrame frame)
    {
        _control = frame;
    }

    public void RequestRelease(bool immediate = false)
    {
        if (!IsActive) return;
        if (immediate)
        {
            Finish();
            return;
        }
        if (_definition.LoopMode == AudioActionLoopMode.OneShot) return;
        if (_released || _releasePending || _playFullOnce) return;

        // 相位边界 = 有没有进入循环：
        //   启动段 = StartPosition01 → LoopStart01（只播一次）
        //   保持段 = LoopStart01 → LoopEnd01（循环）
        // 启动段长度由 LoopStart01 决定，所以想让"短按直接停/完整播一遍"生效，
        // 只要把 LoopStart01 设成和短按时长相当即可。
        if (!_loopEntered)
        {
            if (_definition.IntroReleaseMode == AudioIntroReleaseMode.StopImmediately)
            {
                Finish();
                return;
            }
            // 不循环，把这一遍完整播完。
            _playFullOnce = true;
            int position = _sourceA != null && _sourceA.isPlaying ? _sourceA.timeSamples : _startSample;
            _sourceB.Stop();
            _sourceA.Stop();
            _sourceA.clip = _clip;
            _sourceA.loop = false;
            _sourceA.timeSamples = Mathf.Clamp(position, 0, Mathf.Max(0, _clip.samples - 1));
            _sourceA.pitch = _sustainPitch;
            _sourceA.Play();
            return;
        }

        // 颗粒模式：保持段松手直接进尾音。
        // 颗粒 buffer 是 2.5s 量级，若还按"走完这一遍 buffer"去等，
        // 松手后会多循环好几秒，听感上就像"loop 次数暴增"。
        // 颗粒本身是密集纹理，不需要凑到一个周期边界，直接切不会有半句问题。
        if (_definition.IsGranular)
        {
            BeginRelease(_loopEndSample);
            return;
        }

        // 保持段松手：先走完当前这一遍 loop，再进 Release（播放尾音区间）。
        _releasePending = true;
    }

    private void BeginRelease(int resumeSample)
    {
        _releasePending = false;
        _released = true;
        _releaseTime = 0f;
        _releaseStartLevel = Mathf.Clamp01(EnvelopeGain);

        if (_definition.LoopsDuringRelease && _loopClip != null)
        {
            // loop_continuous：继续循环，只走 ADSR 的 R。
            _sourceA.Stop();
        }
        else
        {
            // 回到原始素材，边播尾巴边走 R。
            if (_definition.SeekToReleaseRegion) resumeSample = _releaseSample;
            _sourceB.Stop();
            _sourceA.Stop();
            _sourceA.clip = _clip;
            _sourceA.loop = false;
            _sourceA.timeSamples = Mathf.Clamp(resumeSample, 0, Mathf.Max(0, _clip.samples - 1));
            _sourceA.pitch = _releasePitch;
            _sourceA.Play();
        }
        // release 时长 = 尾音区间长度（从播放头当前位置到素材结尾）。
        int from = _sourceA != null ? _sourceA.timeSamples : 0;
        _releaseDuration = Mathf.Max(0.01f, (_clip.samples - from) / (float)_clip.frequency);
        _state = AudioActionState.Releasing;
    }

    public bool Tick(float deltaTime)
    {
        if (!IsActive) return false;
        if (_follow != null) transform.position = _follow.position;
        if (_clip == null || _sourceA == null)
        {
            Finish();
            return false;
        }

        float dt = Mathf.Max(0f, deltaTime);
        UpdateTone(dt);
        if (_playFullOnce) UpdateFullOnce(dt);
        else if (!_released) UpdateHeld(dt);
        else UpdateRelease(dt);
        return IsActive;
    }

    /// <summary>起段松手后的“完整播一遍”：不循环，ADSR 保持按住时的电平，播到素材结尾为止。</summary>
    private void UpdateFullOnce(float deltaTime)
    {
        _heldTime += deltaTime;
        float amplitude = _definition.Envelope.EvaluateOn(_heldTime);
        EnvelopeGain = Mathf.Clamp01(amplitude);
        ApplyOutput(amplitude, _sustainPitch);
        if (!IsAnyPlaying) Finish();
    }

    private void UpdateTone(float deltaTime)
    {
        float speed = Mathf.Clamp01(_control.NormalizedSpeed);
        PitchEnvelopeValue = _definition.PitchFollow.Evaluate(speed);
        LowpassEnvelopeValue = _definition.LowpassFollow.Evaluate(speed);
        _speedPitch = AudioCurveUtility.SemitoneToRatio(
            _definition.PitchFollowMaxSemitones * PitchEnvelopeValue);
        float cutoff = AudioCurveUtility.LerpFrequency(
            _definition.LowpassIdleHz, _definition.LowpassFastHz, LowpassEnvelopeValue);
        cutoff = Mathf.Lerp(cutoff, _definition.LowpassIdleHz,
            Mathf.Clamp01(_control.ContactIntensity) * 0.25f);
        UpdateDrift(deltaTime);
        SetLowpass(cutoff + _lowpassOffset + _driftCutoffValue * _definition.SustainDriftCutoffHz);
        SetDirectionPan(_control.Direction);

        float contactTarget = Mathf.Lerp(0.82f, 1f, Mathf.Clamp01(_control.ContactIntensity));
        _contactGain = AudioCurveUtility.SmoothTowards(
            _contactGain, contactTarget, ContactGainResponse, deltaTime);
    }

    /// <summary>
    /// 持续段的慢速随机漂移：每 0.3 s 抽一个新目标再平滑过去。
    /// 参考 GameSynth 的颗粒/漂移思路，避免短循环听起来像机关枪。
    /// </summary>
    private void UpdateDrift(float deltaTime)
    {
        _driftTimer -= deltaTime;
        if (_driftTimer <= 0f)
        {
            _driftTimer = 0.3f;
            _driftGainTarget = (float)(_random.NextDouble() * 2.0 - 1.0);
            _driftCutoffTarget = (float)(_random.NextDouble() * 2.0 - 1.0);
        }
        _driftGainValue = AudioCurveUtility.SmoothTowards(
            _driftGainValue, _driftGainTarget, 3f, deltaTime);
        _driftCutoffValue = AudioCurveUtility.SmoothTowards(
            _driftCutoffValue, _driftCutoffTarget, 3f, deltaTime);
    }

    /// <summary>每次触发把循环窗口整体挪一点点（长度不变），避免每遍滑铲完全一样。</summary>
    private void ApplyLoopRegionJitter()
    {
        int loopLength = _loopEndSample - _loopStartSample;
        int range = Mathf.RoundToInt(loopLength * _definition.LoopRegionRandom01);
        if (range <= 0) return;
        // 量化成 16 档，既有多样性又不会把预渲染缓存撑爆。
        int step = Mathf.Max(1, range * 2 / 16);
        int raw = _random.Next(-range, range + 1);
        int offset = Mathf.RoundToInt(raw / (float)step) * step;
        int maxStart = Mathf.Max(0, _clip.samples - loopLength - 1);
        int minStart = Mathf.Clamp(_startSample, 0, maxStart);
        _loopStartSample = Mathf.Clamp(_loopStartSample + offset, minStart, maxStart);
        _loopEndSample = _loopStartSample + loopLength;
    }

    private void UpdateHeld(float deltaTime)
    {
        _heldTime += deltaTime;
        float amplitude = _definition.Envelope.EvaluateOn(_heldTime);
        EnvelopeGain = Mathf.Clamp01(amplitude);

        // PlayScheduled 之后 isPlaying 会立刻变 true，所以不能用它判断“已经进循环”，
        // 必须等排程的 dsp 时间真正到了。
        if (_hasScheduledLoop && AudioSettings.dspTime >= _loopStartDspTime)
        {
            _loopEntered = true;
        }

        // 持续段松手后，等这一遍 loop 走完（播放头回绕）再进 Release。
        if (_loopEntered && _sourceB.isPlaying)
        {
            int position = _sourceB.timeSamples;
            if (_releasePending && _lastLoopSample >= 0 && position < _lastLoopSample)
            {
                // 这一遍循环走完了，播放头正好在 LoopEnd —— 从这里往素材结尾播就是尾音。
                // 注意不能回到 LoopStart，那样等于把循环区又播了一遍，听不出尾音。
                BeginRelease(_loopEndSample);
                return;
            }
            _lastLoopSample = position;
        }

        ApplyOutput(amplitude, _loopEntered ? _sustainPitch : _startPitch);
        _state = _loopEntered ? AudioActionState.Sustaining : AudioActionState.Starting;

        // 起段交班时两个源可能有一瞬间都不在播放，容忍几十毫秒再判定结束。
        if (IsAnyPlaying) _silentTime = 0f;
        else
        {
            _silentTime += deltaTime;
            if (_silentTime > 0.05f) Finish();
        }
    }

    private void UpdateRelease(float deltaTime)
    {
        _releaseTime += deltaTime;
        float amplitude = _definition.Envelope.EvaluateRelease(
            _releaseTime, _releaseStartLevel, _releaseDuration);
        EnvelopeGain = Mathf.Clamp01(amplitude);
        ApplyOutput(amplitude, _releasePitch);
        if (_releaseTime >= _releaseDuration) { Finish(); return; }
        if (IsAnyPlaying) _silentTime = 0f;
        else
        {
            _silentTime += deltaTime;
            if (_silentTime > 0.05f) Finish();
        }
    }

    private void ApplyOutput(float amplitude, float basePitch)
    {
        float master = AudioCurveUtility.DbToLinear(_gainOffsetDb)
                       * Mathf.Clamp01(amplitude)
                       * _contactGain
                       * AudioCurveUtility.DbToLinear(_driftGainValue * _definition.SustainDriftDb);
        // 保持段循环期间额外减益，用来压住 sustain 的响度。
        if (_loopEntered && !_released)
        {
            master *= AudioCurveUtility.DbToLinear(_definition.SustainGainDb);
        }
        float pitch = Mathf.Clamp(basePitch * _speedPitch, 0.35f, 2.5f);
        if (_sourceA != null)
        {
            _sourceA.pitch = pitch;
            _sourceA.volume = master;
        }
        if (_sourceB != null)
        {
            _sourceB.pitch = pitch;
            _sourceB.volume = master;
        }
    }

    public void Finish()
    {
        if (_sourceA != null) _sourceA.Stop();
        if (_sourceB != null) _sourceB.Stop();
        _loopClip = null;
        _hasScheduledLoop = false;
        _state = AudioActionState.Finished;
        _heldTime = 0f;
        _releaseTime = 0f;
    }

    private int ToSample(float normalized)
    {
        if (_clip == null || _clip.samples <= 0) return 0;
        return Mathf.Clamp(
            Mathf.RoundToInt(_clip.samples * Mathf.Clamp01(normalized)), 0, _clip.samples - 1);
    }

    private void SetLowpass(float hertz)
    {
        _lowpassHz = Mathf.Clamp(hertz, 120f, 22000f);
        if (_lowpass != null) _lowpass.cutoffFrequency = _lowpassHz;
    }

    private void SetDirectionPan(float direction)
    {
        float pan = Mathf.Clamp(direction, -1f, 1f) * _definition.DirectionPan;
        if (_sourceA != null) _sourceA.panStereo = pan;
        if (_sourceB != null) _sourceB.panStereo = pan;
    }

    private void ApplySpatial(AudioSource source)
    {
        if (source == null) return;
        source.spatialBlend = _definition.SpatialBlend;
        source.minDistance = _definition.MinDistance;
        source.maxDistance = _definition.MaxDistance;
        source.priority = _definition.Priority;
    }

    private void EnsureSource()
    {
        if (_sourceA != null) return;
        _sourceA = gameObject.AddComponent<AudioSource>();
        _sourceB = gameObject.AddComponent<AudioSource>();
        foreach (AudioSource source in new[] { _sourceA, _sourceB })
        {
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 1f;
            source.dopplerLevel = 0f;
            source.rolloffMode = AudioRolloffMode.Linear;
        }
        // 滤镜挂在同一个 GameObject 上，两个 AudioSource 共用同一条滤镜链。
        _lowpass = gameObject.AddComponent<AudioLowPassFilter>();
        _lowpass.cutoffFrequency = 22000f;
        _highpass = gameObject.AddComponent<AudioHighPassFilter>();
        _highpass.cutoffFrequency = 20f;
    }

    private void OnDisable()
    {
        Finish();
    }
}
