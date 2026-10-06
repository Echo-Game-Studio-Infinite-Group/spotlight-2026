using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

/// <summary>
/// Consumes DynamicAudioActionRequest values and routes each ActionId to one
/// Unity-rendered PCM voice. Wwise remains a transport and bus layer only.
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
        public string ActionId;
        public int InstanceId;
        public WwiseActionBindings.Entry Binding;
        public WwiseActionPcmRenderer Renderer;
        public float[] PcmScratch;
        public GameObject Emitter;
        public uint VoiceId;
        public uint PlayingId = AkUnitySoundEngine.AK_INVALID_PLAYING_ID;
        public float Elapsed;
        public float NormalizedSpeed;
        public float ContactIntensity;
        public float EnvelopeGain;
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
        public int InstanceId;
        public string State;
        public float Elapsed;
        public float NormalizedSpeed;
        public float EnvelopeGain;
    }

    private readonly Dictionary<string, Channel> _channels =
        new Dictionary<string, Channel>(StringComparer.Ordinal);
    private readonly List<IDynamicAudioActionSource> _sources =
        new List<IDynamicAudioActionSource>();
    private readonly List<DynamicAudioActionRequest> _requests =
        new List<DynamicAudioActionRequest>();
    private readonly List<DebugSnapshot> _debugSnapshots =
        new List<DebugSnapshot>();

    private WwiseActionBindings _bindings;
    private bool _bankLoaded;
    private int _sourceRegistryVersion = -1;

    public IReadOnlyList<DebugSnapshot> DebugSnapshots =>
        _debugSnapshots;

    private void Awake()
    {
        _bindings = Resources.Load<WwiseActionBindings>(
            BindingsResourcePath);
        if (GetComponent<AkGameObj>() == null)
        {
            gameObject.AddComponent<AkGameObj>();
        }

        StartCoroutine(EnsureBankLoaded());
    }

    private void Update()
    {
        if (!_bankLoaded || !AkUnitySoundEngine.IsInitialized())
        {
            return;
        }

        if (_sourceRegistryVersion != WwiseAudioRegistry.Version)
        {
            RefreshSources();
        }

        CollectRequests();
        for (int i = 0; i < _requests.Count; i++)
        {
            HandleRequest(_requests[i]);
        }

        float deltaTime = TimeManager.UnscaledDeltaTime;
        foreach (Channel channel in _channels.Values)
        {
            TickChannel(channel, deltaTime);
        }

        RebuildDebugSnapshots();
    }

    private void RefreshSources()
    {
        _sources.Clear();
        WwiseAudioRegistry.CopyTo(_sources);
        for (int i = _sources.Count - 1; i >= 0; i--)
        {
            if (!BelongsToThisDriver(_sources[i]))
            {
                _sources.RemoveAt(i);
            }
        }
        _sourceRegistryVersion = WwiseAudioRegistry.Version;
    }

    private bool BelongsToThisDriver(IDynamicAudioActionSource source)
    {
        if (source is not Component component) return false;
        return component.transform == transform ||
               component.transform.IsChildOf(transform);
    }

    private void CollectRequests()
    {
        _requests.Clear();
        for (int i = 0; i < _sources.Count; i++)
        {
            _sources[i]?.CollectDynamicAudioActions(_requests);
        }
    }

    private void HandleRequest(in DynamicAudioActionRequest request)
    {
        if (string.IsNullOrEmpty(request.ActionId))
        {
            return;
        }

        string channelKey = BuildChannelKey(
            request.ActionId,
            request.InstanceId);
        if (!_channels.TryGetValue(channelKey, out Channel channel))
        {
            channel = new Channel
            {
                ActionId = request.ActionId,
                InstanceId = request.InstanceId
            };
            _channels.Add(channelKey, channel);
        }

        if (channel.Binding == null)
        {
            channel.Binding = _bindings != null
                ? _bindings.Find(request.ActionId)
                : null;
        }

        if (request.Phase == DynamicAudioActionPhase.Stop)
        {
            StopChannel(channel, request.StopMode);
            return;
        }

        if (request.Phase == DynamicAudioActionPhase.Start &&
            channel.Renderer != null)
        {
            ReleaseChannel(channel);
        }

        if (channel.Renderer == null)
        {
            StartChannel(channel, request);
        }

        channel.NormalizedSpeed = request.NormalizedSpeed;
        channel.ContactIntensity = request.ContactIntensity;
    }

    private void StartChannel(
        Channel channel, in DynamicAudioActionRequest request)
    {
        if (channel.Binding == null ||
            string.IsNullOrEmpty(channel.Binding.PlayEvent) ||
            channel.Binding.Definition == null)
        {
            return;
        }

        ReleaseChannel(channel);
        channel.Renderer = new WwiseActionPcmRenderer();
        int seed = request.Seed != 0
            ? request.Seed
            : Environment.TickCount ^ GetInstanceID();
        if (!channel.Renderer.Begin(
                channel.Binding.Definition,
                channel.Binding.SourceRelativePath,
                seed,
                out string error))
        {
            Debug.LogError(
                $"Wwise PCM renderer failed for {channel.ActionId}: {error}");
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
            Debug.LogError(
                $"Wwise PCM bridge create failed for {channel.ActionId}: {error}");
            ReleaseChannel(channel);
            return;
        }

        channel.PcmScratch =
            new float[ChunkFrames * channel.Renderer.Channels];
        channel.Emitter = new GameObject(
            $"{channel.ActionId}_WwiseEmitter");
        channel.Emitter.transform.SetParent(transform, false);
        channel.Emitter.AddComponent<AkGameObj>();

        channel.Elapsed = 0f;
        channel.EnvelopeGain = 0f;
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

    private void TickChannel(Channel channel, float deltaTime)
    {
        if (channel.Renderer == null)
        {
            return;
        }

        channel.Elapsed += Mathf.Max(0f, deltaTime);
        UpdateEnvelopeState(channel, channel.Binding.Definition);
        PumpPcm(channel, TargetBufferSeconds);

        if (channel.Renderer.IsFinished &&
            WwisePcmBridge.GetAvailableFrames(channel.VoiceId) == 0)
        {
            ReleaseChannel(channel);
        }
    }

    private void StopChannel(
        Channel channel, DynamicAudioActionStopMode stopMode)
    {
        if (channel.Renderer == null) return;

        channel.ReleaseRequested = true;
        channel.ReleaseAudioStarted = false;
        channel.ReleaseElapsed = 0f;
        channel.ReleaseStartLevel = channel.EnvelopeGain;
        channel.Renderer.RequestStop(stopMode);
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

    private void ReleaseChannel(Channel channel)
    {
        if (channel == null) return;

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
        channel.ReleaseRequested = false;
        channel.ReleaseAudioStarted = false;
        channel.ReleaseElapsed = 0f;
        channel.ReleaseStartLevel = 0f;
        channel.ReleaseDuration = 0f;
    }

    private void RebuildDebugSnapshots()
    {
        _debugSnapshots.Clear();
        foreach (KeyValuePair<string, Channel> pair in _channels)
        {
            Channel channel = pair.Value;
            if (channel.Renderer == null) continue;
            _debugSnapshots.Add(new DebugSnapshot
            {
                Active = true,
                Name = channel.ActionId,
                InstanceId = channel.InstanceId,
                State = channel.ReleaseRequested
                    ? "Release"
                    : channel.Renderer.LoopEntered
                        ? "Sustain"
                        : "Start",
                Elapsed = channel.Elapsed,
                NormalizedSpeed = channel.NormalizedSpeed,
                EnvelopeGain = channel.EnvelopeGain
            });
        }
    }

    private void OnDisable()
    {
        foreach (Channel channel in _channels.Values)
        {
            ReleaseChannel(channel);
        }
        _debugSnapshots.Clear();
    }

    private void OnDestroy()
    {
        foreach (Channel channel in _channels.Values)
        {
            ReleaseChannel(channel);
        }
        _channels.Clear();
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

    private static string BuildChannelKey(string actionId, int instanceId)
    {
        return instanceId == 0
            ? actionId
            : $"{actionId}#{instanceId}";
    }
}
