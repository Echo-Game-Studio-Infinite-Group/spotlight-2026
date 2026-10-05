using UnityEngine;

/// <summary>
/// Renders the same continuous-action model as ActionAudioVoice, but produces
/// interleaved PCM for the native Wwise transport plugin instead of AudioSources.
/// </summary>
public sealed class WwiseActionPcmRenderer
{
    private const float PitchSmoothingSeconds = 0.015f;

    private enum RenderState
    {
        Starting,
        Looping,
        Releasing,
        Finished
    }

    private AudioActionDefinition _definition;
    private AudioClip _clip;
    private float[] _source;
    private float[] _loop;
    private int _channels;
    private int _sampleRate;
    private int _totalFrames;
    private int _startFrame;
    private int _loopStartFrame;
    private int _loopEndFrame;
    private int _releaseFrame;
    private RenderState _state;
    private System.Random _random;

    private double _sourceRead;
    private double _loopRead;
    private double _releaseRead;
    private long _heldFrames;
    private long _releaseFrames;
    private long _releaseDurationFrames = 1;
    private bool _releasePending;
    private bool _playFullOnce;
    private bool _loopEntered;
    private float _releaseStartLevel;
    private float _gainOffsetDb;
    private float _startPitch = 1f;
    private float _sustainPitch = 1f;
    private float _releasePitch = 1f;
    private float _currentPitch = 1f;
    private float[] _frameScratch;

    public bool IsFinished => _state == RenderState.Finished;
    public bool IsReleasing =>
        _state == RenderState.Releasing || _playFullOnce;
    public bool LoopEntered => _loopEntered;
    public float LastEnvelopeGain { get; private set; }
    public float ReleaseDurationSeconds =>
        _releaseDurationFrames / (float)Mathf.Max(1, _sampleRate);
    public float IntroSeconds { get; private set; }
    public int TotalFrames => _totalFrames;
    public int Channels => _channels;
    public int SampleRate => _sampleRate;
    public AudioActionDefinition Definition => _definition;
    public string DebugState =>
        _state == RenderState.Finished
            ? "Finished"
            : IsReleasing
                ? "Release"
                : _loopEntered
                    ? "Sustain"
                    : "Start";

    public bool Begin(AudioActionDefinition definition, int seed, out string error)
    {
        return Begin(definition, string.Empty, seed, out error);
    }

    public bool Begin(
        AudioActionDefinition definition,
        string sourceRelativePath,
        int seed,
        out string error)
    {
        error = string.Empty;
        if (definition == null)
        {
            error = "missing AudioActionDefinition";
            return false;
        }

        _definition = definition;
        _definition.EnsureDefaults();
        _clip = definition.Clip;

        if (!string.IsNullOrWhiteSpace(sourceRelativePath))
        {
            WwiseActionPcmSource.Data source =
                WwiseActionPcmSource.Load(sourceRelativePath, out error);
            if (source == null)
            {
                return false;
            }

            _source = source.Samples;
            _channels = source.Channels;
            _sampleRate = source.SampleRate;
            _totalFrames = source.Frames;
        }
        else
        {
            if (_clip == null)
            {
                error = "missing AudioActionDefinition.Clip or source path";
                return false;
            }

            _channels = Mathf.Max(1, _clip.channels);
            _sampleRate = Mathf.Max(1, _clip.frequency);
            _totalFrames = _clip.samples;
            _source = ReadClip(_clip);
            if (_source == null)
            {
                error = "AudioClip.GetData failed; provide a StreamingAssets WAV source path";
                return false;
            }
        }

        _frameScratch = new float[_channels];
        if (_totalFrames <= 1 || _sampleRate <= 0 || _channels <= 0)
        {
            error = "source has no usable samples";
            return false;
        }

        int resolvedSeed = seed == 0
            ? System.Environment.TickCount
            : seed;
        _random = new System.Random(resolvedSeed);

        _gainOffsetDb = _definition.GainDbVariation.Value(_random);
        _startPitch = AudioCurveUtility.SemitoneToRatio(
            _definition.StartPitchSemitones.Value(_random));
        _sustainPitch = AudioCurveUtility.SemitoneToRatio(
            _definition.SustainPitchSemitones.Value(_random));
        _releasePitch = AudioCurveUtility.SemitoneToRatio(
            _definition.ReleasePitchSemitones.Value(_random));

        _startFrame = ToSample(_definition.StartPosition01);
        _loopStartFrame = ToSample(_definition.LoopStart01);
        _loopEndFrame = Mathf.Clamp(
            ToSample(_definition.LoopEnd01),
            _loopStartFrame + 2,
            _totalFrames);
        _releaseFrame = ToSample(_definition.ReleasePosition01);
        int introFrames = Mathf.Max(0, _loopStartFrame - _startFrame);
        IntroSeconds = _definition.LoopsWhileHeld && _startPitch > 0f
            ? introFrames / (_sampleRate * _startPitch)
            : 0f;

        float[] loopPcm = BuildLoopPcm(resolvedSeed, out error);
        if (loopPcm == null)
        {
            return false;
        }
        _loop = loopPcm;

        _sourceRead = _startFrame;
        _loopRead = 0.0;
        _releaseRead = _loopEndFrame;
        _heldFrames = 0;
        _releaseFrames = 0;
        _releaseStartLevel = 1f;
        _releasePending = false;
        _playFullOnce = false;
        _loopEntered = false;
        _currentPitch = _startPitch;
        _state = RenderState.Starting;
        return true;
    }

    public void RequestRelease()
    {
        if (_definition.LoopMode == AudioActionLoopMode.OneShot)
        {
            return;
        }

        if (_state == RenderState.Finished ||
            _releasePending ||
            _playFullOnce)
        {
            return;
        }

        if (_state == RenderState.Starting)
        {
            if (_definition.IntroReleaseMode ==
                AudioIntroReleaseMode.StopImmediately)
            {
                _state = RenderState.Finished;
                return;
            }

            _playFullOnce = true;
            BeginRelease(
                Mathf.Clamp(
                    Mathf.RoundToInt((float)_sourceRead),
                    0,
                    _totalFrames - 1));
            return;
        }

        if (_definition.IsGranular)
        {
            BeginRelease(_loopEndFrame);
        }
        else
        {
            _releasePending = true;
        }
    }

    public int Render(float[] interleavedOutput, int frames)
    {
        if (interleavedOutput == null ||
            frames <= 0 ||
            _state == RenderState.Finished)
        {
            return 0;
        }

        int rendered = 0;
        for (; rendered < frames; ++rendered)
        {
            if (_state == RenderState.Finished)
            {
                break;
            }

            float envelope = 0f;
            float gainDb = _gainOffsetDb;
            SampleCurrentFrame(_frameScratch, out float targetPitch);
            float smoothing = 1f - Mathf.Exp(
                -1f / Mathf.Max(
                    float.Epsilon,
                    _sampleRate * PitchSmoothingSeconds));
            _currentPitch = Mathf.LerpUnclamped(
                _currentPitch,
                targetPitch,
                smoothing);
            float pitch = _currentPitch;

            if (_state == RenderState.Releasing || _playFullOnce)
            {
                float releaseSeconds =
                    _releaseFrames / (float)_sampleRate;
                float releaseDuration =
                    _releaseDurationFrames / (float)_sampleRate;
                envelope = _definition.Envelope.EvaluateRelease(
                    releaseSeconds,
                    _releaseStartLevel,
                    releaseDuration);
            }
            else
            {
                envelope = _definition.Envelope.EvaluateOn(
                    _heldFrames / (float)_sampleRate);
                if (_loopEntered &&
                    _state == RenderState.Looping)
                {
                    gainDb += _definition.SustainGainDb;
                }
            }

            float gain = AudioCurveUtility.DbToLinear(gainDb) *
                         Mathf.Clamp01(envelope);
            LastEnvelopeGain = Mathf.Clamp01(envelope);
            int outputOffset = rendered * _channels;
            for (int channel = 0; channel < _channels; ++channel)
            {
                interleavedOutput[outputOffset + channel] =
                    _frameScratch[channel] * gain;
            }

            Advance(pitch);
            ++_heldFrames;
            if (_state == RenderState.Releasing)
            {
                ++_releaseFrames;
            }
        }

        return rendered;
    }

    private float[] BuildLoopPcm(int seed, out string error)
    {
        error = string.Empty;
        if (!_definition.LoopsWhileHeld)
        {
            return System.Array.Empty<float>();
        }

        float[] loopPcm = _definition.IsGranular
            ? ActionClipRenderer.GetGranularLoopPcm(
                _source,
                _channels,
                _sampleRate,
                _loopStartFrame,
                _loopEndFrame,
                _definition.GrainSeconds,
                _definition.GrainSpacingSeconds,
                _definition.GrainRandomStart01,
                _definition.GrainTuneCents,
                _definition.GrainFadeCurve,
                seed)
            : ActionClipRenderer.GetCrossfadeLoopPcm(
                _source,
                _channels,
                _totalFrames,
                _loopStartFrame,
                _loopEndFrame,
                Mathf.Max(
                    0,
                    Mathf.RoundToInt(
                        _definition.LoopSeamFadeSeconds * _sampleRate)));

        if (loopPcm == null)
        {
            error = "failed to render loop PCM";
            return null;
        }
        return loopPcm;
    }

    private void SampleCurrentFrame(
        float[] sampleFrame,
        out float pitch)
    {
        pitch = _state == RenderState.Releasing
            ? _releasePitch
            : _loopEntered
                ? _sustainPitch
                : _startPitch;

        if (_state == RenderState.Starting)
        {
            ReadSource(_sourceRead, sampleFrame);
        }
        else if (_state == RenderState.Looping)
        {
            ReadLoop(_loopRead, sampleFrame);
        }
        else
        {
            ReadSource(_releaseRead, sampleFrame);
        }
    }

    private void Advance(float pitch)
    {
        if (_state == RenderState.Starting)
        {
            _sourceRead += pitch;
            if (_sourceRead >= _loopStartFrame && _loop.Length > 0)
            {
                _state = RenderState.Looping;
                _loopEntered = true;
                _loopRead = 0.0;
            }
            else if (_sourceRead >= _totalFrames)
            {
                _state = RenderState.Finished;
            }
            return;
        }

        if (_state == RenderState.Looping)
        {
            _loopRead += pitch;
            int loopFrames = _loop.Length / _channels;
            if (_loopRead < loopFrames)
            {
                return;
            }

            if (_releasePending)
            {
                BeginRelease(_loopEndFrame);
            }
            else
            {
                _loopRead %= loopFrames;
            }
            return;
        }

        if (_state == RenderState.Releasing)
        {
            _releaseRead += pitch;
            if (_releaseRead >= _totalFrames ||
                _releaseFrames >= _releaseDurationFrames)
            {
                _state = RenderState.Finished;
            }
        }
    }

    private void BeginRelease(int resumeFrame)
    {
        _releasePending = false;
        _releaseStartLevel = Mathf.Clamp01(
            _definition.Envelope.EvaluateOn(
                _heldFrames / (float)_sampleRate));
        _releaseFrames = 0;
        _releaseRead = Mathf.Clamp(resumeFrame, 0, _totalFrames - 1);
        _releaseDurationFrames = Mathf.Max(
            1,
            _totalFrames - Mathf.RoundToInt((float)_releaseRead));
        _state = RenderState.Releasing;
    }

    private void ReadSource(double position, float[] output)
    {
        int frame0 = Mathf.Clamp(
            Mathf.FloorToInt((float)position),
            0,
            _totalFrames - 1);
        int frame1 = Mathf.Min(frame0 + 1, _totalFrames - 1);
        float fraction = (float)(position - frame0);
        int offset0 = frame0 * _channels;
        int offset1 = frame1 * _channels;
        for (int channel = 0; channel < _channels; ++channel)
        {
            float first = _source[offset0 + channel];
            output[channel] = first +
                (_source[offset1 + channel] - first) * fraction;
        }
    }

    private void ReadLoop(double position, float[] output)
    {
        int loopFrames = _loop.Length / _channels;
        int frame0 = Mathf.Clamp(
            Mathf.FloorToInt((float)position),
            0,
            loopFrames - 1);
        int frame1 = (frame0 + 1) % loopFrames;
        float fraction = (float)(position - frame0);
        int offset0 = frame0 * _channels;
        int offset1 = frame1 * _channels;
        for (int channel = 0; channel < _channels; ++channel)
        {
            float first = _loop[offset0 + channel];
            output[channel] = first +
                (_loop[offset1 + channel] - first) * fraction;
        }
    }

    private int ToSample(float normalized)
    {
        return Mathf.Clamp(
            Mathf.RoundToInt(Mathf.Clamp01(normalized) * _totalFrames),
            0,
            _totalFrames - 1);
    }

    private static float[] ReadClip(AudioClip clip)
    {
        float[] data = new float[clip.samples * clip.channels];
        return clip.GetData(data, 0) ? data : null;
    }
}
