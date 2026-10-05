using System.Collections;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

/// <summary>
/// Owns the Unity-side continuous action renderers and feeds their PCM into
/// the thin Wwise transport plugin. One channel handles slide, another handles
/// wall slide, so their lifecycles do not overwrite each other.
/// </summary>
[DisallowMultipleComponent]
public sealed class WwiseActionDriver : MonoBehaviour
{
    private const string BindingsResourcePath = "Audio/WwiseActionBindings";
    private const string DynamicActionBankName = "DynamicAction";
    private const int ChunkFrames = 1024;
    private const float PreRollSeconds = 0.15f;
    private const float TargetBufferSeconds = 0.35f;

    private static int s_nextVoiceId = 1;

    private sealed class Channel
    {
        public WwiseActionBindings.Entry Binding;
        public WwiseActionPcmRenderer Renderer;
        public float[] PcmScratch;
        public GameObject Emitter;
        public uint VoiceId;
        public uint PlayingId = AkUnitySoundEngine.AK_INVALID_PLAYING_ID;
        public bool LastActive;
        public float Elapsed;
        public float NormalizedSpeed;
        public float ContactIntensity;
        public float EnvelopeGain;
        public float PitchEnvelopeValue;
        public float LowpassEnvelopeValue;
        public float LowpassHz;
        public bool ReleaseRequested;
        public bool ReleaseAudioStarted;
        public float ReleaseElapsed;
        public float ReleaseStartLevel;
        public float ReleaseDuration;
    }

    public struct DebugSnapshot
    {
        public bool Active;
        public string Name;
        public string State;
        public float Elapsed;
        public float NormalizedSpeed;
        public float EnvelopeGain;
        public float PitchEnvelopeValue;
        public float LowpassEnvelopeValue;
        public float LowpassHz;
    }

    private PlayerAudioDriver _audioDriver;
    private PlayerMotor _motor;
    private WwiseActionBindings _bindings;
    private readonly Channel _slide = new Channel();
    private readonly Channel _wallSlide = new Channel();
    private bool _bankLoaded;

    public bool HasActiveAction =>
        _slide.Renderer != null || _wallSlide.Renderer != null;

    public DebugSnapshot SlideDebug =>
        BuildDebug(_slide, "Slide");

    public DebugSnapshot WallSlideDebug =>
        BuildDebug(_wallSlide, "Wall Slide");

    private void Awake()
    {
        _audioDriver = GetComponent<PlayerAudioDriver>();
        _motor = GetComponent<PlayerMotor>();
        _bindings = Resources.Load<WwiseActionBindings>(BindingsResourcePath);
        _slide.Binding = _bindings != null && _audioDriver != null
            ? _bindings.Find(_audioDriver.SlideAction)
            : null;
        _wallSlide.Binding = _bindings != null && _audioDriver != null
            ? _bindings.Find(_audioDriver.WallSlideAction)
            : null;

        if (GetComponent<AkGameObj>() == null)
        {
            gameObject.AddComponent<AkGameObj>();
        }

        StartCoroutine(EnsureBankLoaded());
    }

    private void Update()
    {
        if (_motor == null ||
            !_bankLoaded ||
            !AkUnitySoundEngine.IsInitialized())
        {
            return;
        }

        float speed01 = NormalizedSpeed();
        UpdateChannel(
            _slide,
            _audioDriver != null ? _audioDriver.SlideAction : null,
            _motor.IsSliding,
            speed01,
            1f);
        UpdateChannel(
            _wallSlide,
            _audioDriver != null ? _audioDriver.WallSlideAction : null,
            _motor.IsWallSliding,
            speed01,
            Mathf.Clamp01(_motor.WallApproachAngle / 90f));
    }

    private void UpdateChannel(
        Channel channel,
        AudioActionDefinition definition,
        bool active,
        float normalizedSpeed,
        float contactIntensity)
    {
        channel.NormalizedSpeed = Mathf.Clamp01(normalizedSpeed);
        channel.ContactIntensity = Mathf.Clamp01(contactIntensity);

        if (active && !channel.LastActive)
        {
            StartChannel(channel, definition);
        }
        else if (!active && channel.LastActive)
        {
            StopChannel(channel);
        }

        if (channel.Renderer != null)
        {
            channel.Elapsed += TimeManager.UnscaledDeltaTime;
            UpdateEnvelopeState(channel, definition);
            PumpPcm(channel, TargetBufferSeconds);
            UpdateDebugValues(channel, definition);

            if (channel.Renderer.IsFinished &&
                WwisePcmBridge.GetAvailableFrames(channel.VoiceId) == 0)
            {
                ReleaseChannel(channel);
            }
        }

        channel.LastActive = active;
    }

    private void StartChannel(
        Channel channel, AudioActionDefinition definition)
    {
        if (channel.Binding == null ||
            string.IsNullOrEmpty(channel.Binding.PlayEvent) ||
            definition == null)
        {
            return;
        }

        ReleaseChannel(channel);
        channel.Renderer = new WwiseActionPcmRenderer();
        int seed = System.Environment.TickCount ^ GetInstanceID();
        if (!channel.Renderer.Begin(
                definition,
                channel.Binding.SourceRelativePath,
                seed,
                out string error))
        {
            Debug.LogError($"Wwise PCM renderer failed: {error}");
            channel.Renderer = null;
            return;
        }

        int voiceId = Interlocked.Increment(ref s_nextVoiceId);
        if (voiceId <= 0) voiceId = 1;
        channel.VoiceId = (uint)voiceId;
        if (!WwisePcmBridge.TryCreateVoice(
                channel.VoiceId,
                channel.Renderer.Channels,
                channel.Renderer.SampleRate,
                out error))
        {
            Debug.LogError($"Wwise PCM bridge create failed: {error}");
            ReleaseChannel(channel);
            return;
        }

        channel.PcmScratch =
            new float[ChunkFrames * channel.Renderer.Channels];
        channel.Emitter = new GameObject(
            channel.Binding.Definition != null
                ? $"{channel.Binding.Definition.name}_WwiseEmitter"
                : "Action_WwiseEmitter");
        channel.Emitter.transform.SetParent(transform, false);
        channel.Emitter.AddComponent<AkGameObj>();
        channel.Elapsed = 0f;
        channel.EnvelopeGain = 0f;
        channel.PitchEnvelopeValue = 0f;
        channel.LowpassEnvelopeValue = 0f;
        channel.LowpassHz = definition.LowpassIdleHz;
        channel.ReleaseRequested = false;
        channel.ReleaseAudioStarted = false;
        channel.ReleaseElapsed = 0f;
        channel.ReleaseStartLevel = 0f;
        channel.ReleaseDuration = 0f;
        PumpPcm(channel, PreRollSeconds);
        SendVoiceInfo(channel);
        channel.PlayingId = AkUnitySoundEngine.PostEvent(
            channel.Binding.PlayEvent,
            channel.Emitter);
    }

    private void StopChannel(Channel channel)
    {
        if (channel.Renderer == null) return;
        channel.ReleaseRequested = true;
        channel.ReleaseAudioStarted = false;
        channel.ReleaseElapsed = 0f;
        channel.ReleaseStartLevel = channel.EnvelopeGain;
        channel.Renderer.RequestRelease();
    }

    private void PumpPcm(Channel channel, float minimumBufferedSeconds)
    {
        if (channel.Renderer == null || channel.VoiceId == 0) return;

        int targetFrames = Mathf.CeilToInt(
            channel.Renderer.SampleRate * minimumBufferedSeconds);
        int available = WwisePcmBridge.GetAvailableFrames(channel.VoiceId);
        while (!channel.Renderer.IsFinished && available < targetFrames)
        {
            int rendered = channel.Renderer.Render(
                channel.PcmScratch,
                ChunkFrames);
            if (rendered <= 0) break;

            int pushed = WwisePcmBridge.PushPcm(
                channel.VoiceId,
                channel.PcmScratch,
                rendered,
                channel.Renderer.Channels);
            available += pushed;
            if (pushed < rendered) break;
        }

        if (channel.Renderer.IsFinished)
        {
            WwisePcmBridge.MarkFinished(channel.VoiceId);
        }
    }

    private void SendVoiceInfo(Channel channel)
    {
        byte[] voiceInfo = WwisePcmBridge.BuildVoiceInfoBytes(
            channel.VoiceId,
            channel.Renderer.Channels,
            channel.Renderer.SampleRate);
        GCHandle handle = GCHandle.Alloc(voiceInfo, GCHandleType.Pinned);
        try
        {
            AkUnitySoundEngine.SendPluginCustomGameData(
                AkUnitySoundEngine.AK_INVALID_UNIQUE_ID,
                channel.Emitter,
                AkPluginType.AkPluginTypeSource,
                0,
                4242,
                handle.AddrOfPinnedObject(),
                (uint)voiceInfo.Length);
        }
        finally
        {
            handle.Free();
        }
    }

    private void ReleaseChannel(Channel channel)
    {
        if (channel.PlayingId != AkUnitySoundEngine.AK_INVALID_PLAYING_ID &&
            AkUnitySoundEngine.IsInitialized())
        {
            AkUnitySoundEngine.StopPlayingID(channel.PlayingId);
        }

        if (channel.VoiceId != 0)
        {
            WwisePcmBridge.MarkFinished(channel.VoiceId);
            WwisePcmBridge.DestroyVoice(channel.VoiceId);
        }

        channel.PlayingId = AkUnitySoundEngine.AK_INVALID_PLAYING_ID;
        channel.VoiceId = 0;
        channel.PcmScratch = null;
        if (channel.Emitter != null)
        {
            Destroy(channel.Emitter);
            channel.Emitter = null;
        }
        channel.Renderer = null;
        channel.Elapsed = 0f;
        channel.EnvelopeGain = 0f;
        channel.PitchEnvelopeValue = 0f;
        channel.LowpassEnvelopeValue = 0f;
        channel.LowpassHz = 0f;
        channel.ReleaseRequested = false;
        channel.ReleaseAudioStarted = false;
        channel.ReleaseElapsed = 0f;
        channel.ReleaseStartLevel = 0f;
        channel.ReleaseDuration = 0f;
    }

    private static void UpdateEnvelopeState(
        Channel channel, AudioActionDefinition definition)
    {
        if (channel.Renderer == null || definition == null) return;

        if (channel.ReleaseRequested &&
            !channel.ReleaseAudioStarted &&
            channel.Renderer.IsReleasing)
        {
            channel.ReleaseAudioStarted = true;
            channel.ReleaseStartLevel = Mathf.Clamp01(
                definition.Envelope.EvaluateOn(channel.Elapsed));
            channel.ReleaseDuration =
                channel.Renderer.ReleaseDurationSeconds;
        }

        if (channel.ReleaseAudioStarted)
        {
            channel.ReleaseElapsed += TimeManager.UnscaledDeltaTime;
            channel.EnvelopeGain = Mathf.Clamp01(
                definition.Envelope.EvaluateRelease(
                    channel.ReleaseElapsed,
                    channel.ReleaseStartLevel,
                    channel.ReleaseDuration));
        }
        else
        {
            channel.EnvelopeGain = Mathf.Clamp01(
                definition.Envelope.EvaluateOn(channel.Elapsed));
        }
    }

    private void UpdateDebugValues(
        Channel channel, AudioActionDefinition definition)
    {
        if (channel.Renderer == null || definition == null) return;

        channel.PitchEnvelopeValue =
            definition.PitchFollow.Evaluate(channel.NormalizedSpeed);
        channel.LowpassEnvelopeValue =
            definition.LowpassFollow.Evaluate(channel.NormalizedSpeed);
        channel.LowpassHz = AudioCurveUtility.LerpFrequency(
            definition.LowpassIdleHz,
            definition.LowpassFastHz,
            channel.LowpassEnvelopeValue);
        channel.LowpassHz = Mathf.Lerp(
            channel.LowpassHz,
            definition.LowpassIdleHz,
            channel.ContactIntensity * 0.25f);
    }

    private DebugSnapshot BuildDebug(Channel channel, string name)
    {
        return new DebugSnapshot
        {
            Active = channel.Renderer != null,
            Name = name,
            State = channel.Renderer == null
                ? "Idle"
                : channel.ReleaseRequested
                    ? "Release"
                    : channel.Renderer.Definition.LoopsWhileHeld &&
                      channel.Elapsed >= channel.Renderer.IntroSeconds
                        ? "Sustain"
                        : "Start",
            Elapsed = channel.Elapsed,
            NormalizedSpeed = channel.NormalizedSpeed,
            EnvelopeGain = channel.EnvelopeGain,
            PitchEnvelopeValue = channel.PitchEnvelopeValue,
            LowpassEnvelopeValue = channel.LowpassEnvelopeValue,
            LowpassHz = channel.LowpassHz
        };
    }

    private float NormalizedSpeed()
    {
        float threshold = _motor.Params != null &&
                          _motor.Params.GroundSpeedThreshold > 0f
            ? _motor.Params.GroundSpeedThreshold
            : 10f;
        return Mathf.Clamp01(
            _motor.HorizontalSpeed / Mathf.Max(1f, threshold * 3f));
    }

    private void OnDisable()
    {
        ReleaseChannel(_slide);
        ReleaseChannel(_wallSlide);
        _slide.LastActive = false;
        _wallSlide.LastActive = false;
    }

    private void OnDestroy()
    {
        ReleaseChannel(_slide);
        ReleaseChannel(_wallSlide);
        if (_bankLoaded)
        {
            AkBankManager.UnloadBank(DynamicActionBankName);
            _bankLoaded = false;
        }
    }

    private IEnumerator EnsureBankLoaded()
    {
        while (!AkUnitySoundEngine.IsInitialized())
        {
            yield return null;
        }

        AkBankManager.LoadBank(DynamicActionBankName, false, false);
        _bankLoaded = true;
    }
}
