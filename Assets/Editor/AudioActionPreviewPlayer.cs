using UnityEditor;
using UnityEngine;
using System.Reflection;

/// <summary>
/// Inspector 里的实时试听。模型和运行期 ActionAudioVoice 完全一致：
/// ADSR 管音量；循环段用预渲染好的交叠淡化片段交给 AudioSource.loop 无缝循环。
/// </summary>
public static class AudioActionPreviewPlayer
{
    private enum PreviewState
    {
        Idle,
        Held,
        Releasing
    }

    private const double ScheduleLeadSeconds = 0.05;

    private static AudioSource _sourceA;
    private static AudioSource _sourceB;
    private static AudioLowPassFilter _lowpass;
    private static GameObject _host;
    private static AudioActionDefinition _definition;
    private static AudioClip _clip;
    private static AudioClip _loopClip;
    private static PreviewState _state;
    private static double _pressTime;
    private static double _releaseTime;
    private static bool _startedPlaying;
    private static bool _released;
    private static bool _releasePending;
    private static bool _playFullOnce;
    private static bool _loopEntered;
    private static int _lastLoopSample = -1;
    private static int _silentFrames;
    private static float _releaseStartLevel;
    private static float _releaseDuration = 0.3f;
    private static float _normalizedSpeed;
    private static float _contactIntensity;
    private static float _gainOffsetDb;
    private static float _startPitch = 1f;
    private static float _sustainPitch = 1f;
    private static float _releasePitch = 1f;
    private static float _lowpassOffset;
    private static int _startSample;
    private static int _loopStartSample;
    private static int _loopEndSample;
    private static int _crossfadeSamples;
    private static int _releaseSample;
    private static bool _hasScheduledLoop;
    private static double _loopStartDspTime = double.MaxValue;
    private static float _driftTimer;
    private static float _driftGainValue;
    private static float _driftGainTarget;
    private static float _driftCutoffValue;
    private static float _driftCutoffTarget;
    private static double _lastDriftTime;
    private static MethodInfo _updateAudio;

    public static bool IsActive => _state != PreviewState.Idle;
    public static string Status { get; private set; } = string.Empty;
    public static float CurrentGain { get; private set; }
    public static bool LoopEntered => _loopEntered;
    public static bool UsesRenderedLoop => _loopClip != null;

    [InitializeOnLoadMethod]
    private static void Initialize()
    {
        EditorApplication.update -= Tick;
        EditorApplication.update += Tick;
        AssemblyReloadEvents.beforeAssemblyReload -= Stop;
        AssemblyReloadEvents.beforeAssemblyReload += Stop;
    }

    /// <summary>按住期间的 ADSR 电平，供 Inspector 显示。</summary>
    public static float EvaluateGain(AudioActionDefinition definition, float holdSeconds)
    {
        if (definition == null) return 0f;
        definition.EnsureDefaults();
        return definition.Envelope.EvaluateOn(holdSeconds);
    }

    public static string DescribeStage(AudioActionDefinition definition, float holdSeconds)
    {
        if (definition == null || definition.Envelope == null) return "Idle";
        var e = definition.Envelope;
        if (e.AttackSeconds > 0f && holdSeconds < e.AttackSeconds) return "Attack";
        if (e.DecaySeconds > 0f && holdSeconds < e.AttackSeconds + e.DecaySeconds) return "Decay";
        return "Sustain";
    }

    public static bool Begin(
        AudioActionDefinition definition,
        float normalizedSpeed,
        float contactIntensity,
        out string error)
    {
        error = string.Empty;
        if (definition == null || definition.Clip == null)
        {
            error = "缺少 AudioClip";
            return false;
        }

        EnsureHost();
        _definition = definition;
        _definition.EnsureDefaults();
        _clip = definition.Clip;
        _normalizedSpeed = Mathf.Clamp01(normalizedSpeed);
        _contactIntensity = Mathf.Clamp01(contactIntensity);
        _gainOffsetDb = _definition.GainDbVariation.Lerp(0.5f);
        _startPitch = AudioCurveUtility.SemitoneToRatio(_definition.StartPitchSemitones.Lerp(0.5f));
        _sustainPitch = AudioCurveUtility.SemitoneToRatio(_definition.SustainPitchSemitones.Lerp(0.5f));
        _releasePitch = AudioCurveUtility.SemitoneToRatio(_definition.ReleasePitchSemitones.Lerp(0.5f));
        _lowpassOffset = _definition.LowpassVariation.Lerp(0.5f);
        _startSample = ToSample(_clip, _definition.StartPosition01);
        _loopStartSample = ToSample(_clip, _definition.LoopStart01);
        _loopEndSample = Mathf.Max(_loopStartSample + 2, ToSample(_clip, _definition.LoopEnd01));
        ApplyLoopRegionJitter();
        _crossfadeSamples = Mathf.Max(0,
            Mathf.RoundToInt(_definition.LoopSeamFadeSeconds * _clip.frequency));
        _releaseSample = ToSample(_clip, _definition.ReleasePosition01);
        int loopSeed = Random.Range(int.MinValue, int.MaxValue);
        _loopClip = _definition.IsGranular
            ? ActionClipRenderer.GetGranularLoop(
                _clip, _loopStartSample, _loopEndSample,
                _definition.GrainSeconds, _definition.GrainSpacingSeconds,
                _definition.GrainRandomStart01, _definition.GrainTuneCents,
                _definition.GrainFadeCurve, loopSeed)
            : ActionClipRenderer.GetCrossfadeLoop(
                _clip, _loopStartSample, _loopEndSample, _crossfadeSamples);
        _hasScheduledLoop = _definition.LoopsWhileHeld && _loopClip != null;
        _loopStartDspTime = double.MaxValue;

        _sourceA.Stop();
        _sourceB.Stop();
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
        InvokeUpdateAudio();

        _pressTime = EditorApplication.timeSinceStartup;
        _released = false;
        _releasePending = false;
        _playFullOnce = false;
        _loopEntered = false;
        _lastLoopSample = -1;
        _silentFrames = 0;
        _driftTimer = 0f;
        _driftGainValue = 0f;
        _driftGainTarget = 0f;
        _driftCutoffValue = 0f;
        _driftCutoffTarget = 0f;
        _lastDriftTime = 0.0;
        _startedPlaying = false;
        _releaseStartLevel = 1f;
        CurrentGain = 0f;
        _state = PreviewState.Held;
        Status = "Attack";
        EditorApplication.QueuePlayerLoopUpdate();
        return true;
    }

    public static void Release()
    {
        if (_state == PreviewState.Idle) return;
        if (_definition == null) return;
        if (_released || _releasePending || _playFullOnce) return;
        if (_definition.LoopMode == AudioActionLoopMode.OneShot) return;

        // 相位边界 = 有没有进入循环（启动段 = Start → LoopStart，保持段 = 循环）。
        if (!_loopEntered)
        {
            if (_definition.IntroReleaseMode == AudioIntroReleaseMode.StopImmediately)
            {
                Stop();
                return;
            }
            _playFullOnce = true;
            int position = _sourceA != null && _sourceA.isPlaying ? _sourceA.timeSamples : _startSample;
            _sourceB.Stop();
            _sourceA.Stop();
            _sourceA.clip = _clip;
            _sourceA.loop = false;
            _sourceA.timeSamples = Mathf.Clamp(position, 0, Mathf.Max(0, _clip.samples - 1));
            _sourceA.pitch = _sustainPitch;
            _sourceA.Play();
            Status = "起段松手 · 完整播一遍";
            return;
        }

        // 颗粒模式保持段松手：直接进尾音，不等 buffer 周期（理由同 ActionAudioVoice）。
        if (_definition.IsGranular)
        {
            BeginRelease(_loopEndSample);
            return;
        }

        // 保持段松手：先走完当前这一遍 loop，再进 Release（播放尾音区间）。
        _releasePending = true;
        Status = "持续段松手 · 等这一遍循环走完";
    }

    private static void BeginRelease(int resumeSample)
    {
        _releasePending = false;
        _released = true;
        _releaseStartLevel = CurrentGain;
        _releaseTime = EditorApplication.timeSinceStartup;

        if (_definition.LoopsDuringRelease && _loopClip != null)
        {
            _sourceA.Stop();
        }
        else
        {
            if (_definition.SeekToReleaseRegion) resumeSample = _releaseSample;
            _sourceB.Stop();
            _sourceA.Stop();
            _sourceA.clip = _clip;
            _sourceA.loop = false;
            _sourceA.timeSamples = Mathf.Clamp(resumeSample, 0, Mathf.Max(0, _clip.samples - 1));
            _sourceA.pitch = _releasePitch;
            _sourceA.Play();
        }
        _state = PreviewState.Releasing;
        // release 时长 = 尾音区间长度（从播放头当前位置到素材结尾）。
        int from = _sourceA != null ? _sourceA.timeSamples : 0;
        _releaseDuration = Mathf.Max(0.01f, (_clip.samples - from) / (float)_clip.frequency);
        Status = $"Release · 0 ms · Gain {_releaseStartLevel:0.00}";
    }

    public static void Stop()
    {
        if (_sourceA != null) _sourceA.Stop();
        if (_sourceB != null) _sourceB.Stop();
        _loopClip = null;
        _hasScheduledLoop = false;
        _loopStartDspTime = double.MaxValue;
        _state = PreviewState.Idle;
        _definition = null;
        _startedPlaying = false;
        _released = false;
        _releasePending = false;
        _playFullOnce = false;
        _loopEntered = false;
        _lastLoopSample = -1;
        _silentFrames = 0;
        CurrentGain = 0f;
        Status = string.Empty;
        if (_host != null) EditorApplication.QueuePlayerLoopUpdate();
    }

    private static bool IsAnyPlaying =>
        (_sourceA != null && _sourceA.isPlaying) || (_sourceB != null && _sourceB.isPlaying);

    private static void Tick()
    {
        if (_state == PreviewState.Idle || _sourceA == null) return;
        InvokeUpdateAudio();
        double now = EditorApplication.timeSinceStartup;

        if (IsAnyPlaying)
        {
            _startedPlaying = true;
            _silentFrames = 0;
        }
        else if (!_startedPlaying)
        {
            if (now - _pressTime > 0.2)
            {
                Status = "AudioSource 未开始播放。请检查 Editor 音频输出或场景 AudioListener。";
                Stop();
            }
            return;
        }
        else
        {
            // 起段交班时两个源可能有一瞬间都不在播放，容错几帧再判定结束。
            _silentFrames++;
            if (_silentFrames > 5)
            {
                Status = "Finished";
                Stop();
            }
            return;
        }

        float heldTime = (float)(now - _pressTime);
        if (_playFullOnce) TickFullOnce(heldTime);
        else if (!_released) TickHeld(heldTime);
        else TickReleasing((float)(now - _releaseTime));
        EditorApplication.QueuePlayerLoopUpdate();
    }

    private static void TickFullOnce(float heldTime)
    {
        float amplitude = _definition.Envelope.EvaluateOn(heldTime);
        CurrentGain = amplitude;
        ApplyGainAndTone(amplitude, _sustainPitch);
        Status = $"完整播一遍 · {heldTime * 1000f:0} ms · Gain {amplitude:0.00}";
    }

    private static void TickHeld(float heldTime)
    {
        float amplitude = _definition.Envelope.EvaluateOn(heldTime);
        CurrentGain = amplitude;

        // PlayScheduled 之后 isPlaying 会立刻变 true，所以要用排程的 dsp 时间判断是否真的进了循环。
        if (_hasScheduledLoop && AudioSettings.dspTime >= _loopStartDspTime)
        {
            _loopEntered = true;
        }

        if (_loopEntered && _sourceB.isPlaying)
        {
            int position = _sourceB.timeSamples;
            if (_releasePending && _lastLoopSample >= 0 && position < _lastLoopSample)
            {
                // 这一遍循环走完了，播放头正好在 LoopEnd —— 从这里往素材结尾播就是尾音。
                BeginRelease(_loopEndSample);
                return;
            }
            _lastLoopSample = position;
        }

        ApplyGainAndTone(amplitude, _loopEntered ? _sustainPitch : _startPitch);
        string suffix = _hasScheduledLoop ? "" : " · 无循环片段";
        Status = $"{DescribeStage(_definition, heldTime)} · {heldTime * 1000f:0} ms · " +
                 $"Gain {amplitude:0.00}{suffix}";
    }

    private static void TickReleasing(float releaseSeconds)
    {
        float amplitude = _definition.Envelope.EvaluateRelease(
            releaseSeconds, _releaseStartLevel, _releaseDuration);
        CurrentGain = amplitude;
        ApplyGainAndTone(amplitude, _releasePitch);
        Status = $"Release · {releaseSeconds * 1000f:0} / {_releaseDuration * 1000f:0} ms · " +
                 $"Gain {amplitude:0.00}";
        if (releaseSeconds >= _releaseDuration) Stop();
    }

    private static void ApplyGainAndTone(float envelopeGain, float basePitch)
    {
        float speedCurve = _definition.PitchFollow.Evaluate(_normalizedSpeed);
        float speedPitch = AudioCurveUtility.SemitoneToRatio(
            _definition.PitchFollowMaxSemitones * speedCurve);
        float lowpassCurve = _definition.LowpassFollow.Evaluate(_normalizedSpeed);
        float cutoff = AudioCurveUtility.LerpFrequency(
            _definition.LowpassIdleHz,
            _definition.LowpassFastHz,
            lowpassCurve);
        cutoff = Mathf.Lerp(cutoff, _definition.LowpassIdleHz, _contactIntensity * 0.25f);
        UpdateDrift();

        float master = AudioCurveUtility.DbToLinear(_gainOffsetDb)
                       * Mathf.Clamp01(envelopeGain)
                       * AudioCurveUtility.DbToLinear(
                           _driftGainValue * _definition.SustainDriftDb);
        if (_loopEntered && !_released)
        {
            master *= AudioCurveUtility.DbToLinear(_definition.SustainGainDb);
        }
        float pitch = Mathf.Clamp(basePitch * speedPitch, 0.35f, 2.5f);
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
        _lowpass.cutoffFrequency = Mathf.Clamp(
            cutoff + _lowpassOffset + _driftCutoffValue * _definition.SustainDriftCutoffHz,
            120f, 22000f);
    }

    private static void UpdateDrift()
    {
        double now = EditorApplication.timeSinceStartup;
        float deltaTime = _lastDriftTime > 0.0
            ? Mathf.Clamp((float)(now - _lastDriftTime), 0f, 0.1f)
            : 0.016f;
        _lastDriftTime = now;
        _driftTimer -= deltaTime;
        if (_driftTimer <= 0f)
        {
            _driftTimer = 0.3f;
            _driftGainTarget = Random.value * 2f - 1f;
            _driftCutoffTarget = Random.value * 2f - 1f;
        }
        _driftGainValue = AudioCurveUtility.SmoothTowards(
            _driftGainValue, _driftGainTarget, 3f, deltaTime);
        _driftCutoffValue = AudioCurveUtility.SmoothTowards(
            _driftCutoffValue, _driftCutoffTarget, 3f, deltaTime);
    }

    /// <summary>每次触发把循环窗口整体挪一点点（长度不变），避免每遍试听完全一样。</summary>
    private static void ApplyLoopRegionJitter()
    {
        // 同 ActionAudioVoice：颗粒模式不做区域抖动，避免缓存键爆炸导致每帧重渲染。
        if (_definition.IsGranular) return;
        int loopLength = _loopEndSample - _loopStartSample;
        int range = Mathf.RoundToInt(loopLength * _definition.LoopRegionRandom01);
        if (range <= 0) return;
        int step = Mathf.Max(1, range * 2 / 16);
        int raw = Random.Range(-range, range + 1);
        int offset = Mathf.RoundToInt(raw / (float)step) * step;
        int maxStart = Mathf.Max(0, _clip.samples - loopLength - 1);
        int minStart = Mathf.Clamp(_startSample, 0, maxStart);
        _loopStartSample = Mathf.Clamp(_loopStartSample + offset, minStart, maxStart);
        _loopEndSample = _loopStartSample + loopLength;
    }

    private static void EnsureHost()
    {
        if (_host != null) return;
        _host = new GameObject("AudioActionLivePreview");
        _host.hideFlags = HideFlags.HideAndDontSave;
        if (Object.FindObjectOfType<AudioListener>() == null)
            _host.AddComponent<AudioListener>();
        _sourceA = _host.AddComponent<AudioSource>();
        _sourceB = _host.AddComponent<AudioSource>();
        foreach (AudioSource source in new[] { _sourceA, _sourceB })
        {
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 0f;
            source.dopplerLevel = 0f;
            source.ignoreListenerPause = true;
        }
        _lowpass = _host.AddComponent<AudioLowPassFilter>();
        _lowpass.cutoffFrequency = 22000f;
    }

    private static void InvokeUpdateAudio()
    {
        if (_updateAudio == null)
        {
            System.Type type = typeof(Editor).Assembly.GetType("UnityEditor.AudioUtil");
            _updateAudio = type?.GetMethod(
                "UpdateAudio",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        }
        _updateAudio?.Invoke(null, null);
    }

    private static int ToSample(AudioClip clip, float normalized)
    {
        if (clip == null || clip.samples <= 0) return 0;
        return Mathf.Clamp(
            Mathf.RoundToInt(Mathf.Clamp01(normalized) * (clip.samples - 1)),
            0,
            clip.samples - 1);
    }
}
