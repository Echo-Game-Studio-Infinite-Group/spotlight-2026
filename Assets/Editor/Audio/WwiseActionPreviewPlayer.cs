using System;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor-only audition path for AudioActionDefinition. It uses the same
/// Unity PCM renderer and Wwise transport plugin as the runtime driver, so
/// preview does not depend on Unity's disabled audio output.
/// </summary>
public static class WwiseActionPreviewPlayer
{
    private const string BankName = "DynamicAction";
    private const int ChunkFrames = 1024;
    private const float PreRollSeconds = 0.15f;
    private const float TargetBufferSeconds = 0.35f;

    private static int s_nextVoiceId = 8001;
    private static WwiseActionPcmRenderer _renderer;
    private static string _playEvent;
    private static float[] _pcmScratch;
    private static uint _voiceId;
    private static uint _playingId = AkUnitySoundEngine.AK_INVALID_PLAYING_ID;
    private static GameObject _host;
    private static GameObject _initializerObject;
    private static double _pressTime;
    private static double _releaseTime;
    private static bool _releaseRequested;
    private static bool _refreshAfterPlay;

    public static bool IsActive => _renderer != null;
    public static string Status { get; private set; } = string.Empty;
    public static int ConsumedFrames =>
        WwisePcmBridge.GetConsumedFrames(_voiceId);

    public static float EvaluateGain(
        AudioActionDefinition definition, float holdSeconds)
    {
        if (definition == null) return 0f;
        definition.EnsureDefaults();
        return definition.Envelope.EvaluateOn(holdSeconds);
    }

    public static string DescribeStage(
        AudioActionDefinition definition, float holdSeconds)
    {
        if (definition == null || definition.Envelope == null) return "Idle";
        AdsrEnvelope envelope = definition.Envelope;
        if (envelope.AttackSeconds > 0f &&
            holdSeconds < envelope.AttackSeconds)
        {
            return "Attack";
        }
        if (envelope.DecaySeconds > 0f &&
            holdSeconds <
            envelope.AttackSeconds + envelope.DecaySeconds)
        {
            return "Decay";
        }
        return "Sustain";
    }

    [InitializeOnLoadMethod]
    private static void Initialize()
    {
        AssemblyReloadEvents.beforeAssemblyReload -= Stop;
        AssemblyReloadEvents.beforeAssemblyReload += Stop;
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    [MenuItem("超高速行者/音频/重置编辑器试听")]
    public static void ResetPreviewEngine()
    {
        Stop();
        _refreshAfterPlay = false;
        AkUnitySoundEngineInitialization.Instance.ResetSoundEngine();
        if (!EditorApplication.isPlaying &&
            AkUnitySoundEngine.IsInitialized())
        {
            AkSoundEngineController.Instance.EnableEditorLateUpdate();
            AkUnitySoundEngine.WakeupFromSuspend();
        }
        Debug.Log("Wwise editor audition reset.");
    }

    public static bool Begin(
        AudioActionDefinition definition,
        float normalizedSpeed,
        float contactIntensity,
        out string error)
    {
        error = string.Empty;
        Stop();

        if (definition == null || definition.Clip == null)
        {
            error = "缺少 AudioActionDefinition 或 Clip。";
            return false;
        }

        if (!AudioActionSourceLocator.TryResolve(
                definition,
                out string sourceRelativePath,
                out error))
        {
            return false;
        }
        _playEvent = AudioActionSourceLocator.PreviewEventFor(definition);
        if (string.IsNullOrEmpty(_playEvent))
        {
            error = "没有可用的 Wwise 试听事件。";
            return false;
        }

        if (!EnsureEngine(out error))
        {
            return false;
        }

        if (!EnsurePreviewBank(out error))
        {
            Stop();
            return false;
        }

        _renderer = new WwiseActionPcmRenderer();
        int seed = Environment.TickCount ^ definition.GetInstanceID();
        if (!_renderer.Begin(
                definition,
                sourceRelativePath,
                seed,
                out error))
        {
            Stop();
            return false;
        }

        int voiceId = Interlocked.Increment(ref s_nextVoiceId);
        if (voiceId <= 0) voiceId = 8001;
        _voiceId = (uint)voiceId;
        if (!WwisePcmBridge.TryCreateVoice(
                _voiceId,
                _renderer.Channels,
                _renderer.SampleRate,
                out error))
        {
            Stop();
            return false;
        }

        _host = new GameObject("WwiseActionAudition");
        _host.hideFlags = HideFlags.HideAndDontSave;
        _host.AddComponent<AkGameObj>();
        _host.AddComponent<AkAudioListener>();
        _pcmScratch = new float[ChunkFrames * _renderer.Channels];

        PumpPcm(PreRollSeconds);
        SendVoiceInfo();
        _playingId = AkUnitySoundEngine.PostEvent(_playEvent, _host);
        if (_playingId == AkUnitySoundEngine.AK_INVALID_PLAYING_ID)
        {
            error = $"Wwise 事件播放失败：{_playEvent}";
            Stop();
            return false;
        }

        _pressTime = EditorApplication.timeSinceStartup;
        _releaseRequested = false;
        Status = "Attack";
        BindTick();
        EditorApplication.QueuePlayerLoopUpdate();
        return true;
    }

    public static void Release()
    {
        if (_renderer == null || _releaseRequested) return;
        _renderer.RequestRelease();
        _releaseTime = EditorApplication.timeSinceStartup;
        _releaseRequested = true;
        Status = "Release";
    }

    public static void Stop()
    {
        UnbindTick();
        if (_playingId != AkUnitySoundEngine.AK_INVALID_PLAYING_ID &&
            AkUnitySoundEngine.IsInitialized())
        {
            AkUnitySoundEngine.StopPlayingID(_playingId);
        }
        _playingId = AkUnitySoundEngine.AK_INVALID_PLAYING_ID;

        if (_voiceId != 0)
        {
            WwisePcmBridge.MarkFinished(_voiceId);
            WwisePcmBridge.DestroyVoice(_voiceId);
        }

        _voiceId = 0;
        _renderer = null;
        _playEvent = null;
        _pcmScratch = null;
        _releaseRequested = false;
        Status = string.Empty;

        if (_host != null)
        {
            UnityEngine.Object.DestroyImmediate(_host);
            _host = null;
        }
    }

    private static bool EnsureEngine(out string error)
    {
        error = string.Empty;
        if (!EditorApplication.isPlaying && _refreshAfterPlay)
        {
            AkUnitySoundEngineInitialization.Instance.ResetSoundEngine();
            _refreshAfterPlay = false;
        }

        GameObject initializerGameObject =
            AkInitializer.GetAkInitializerGameObject();
        if (initializerGameObject == null)
        {
            _initializerObject =
                new GameObject("WwiseActionAuditionInitializer");
            _initializerObject.hideFlags = HideFlags.HideAndDontSave;
            initializerGameObject = _initializerObject;
        }

        AkInitializer initializer =
            initializerGameObject.GetComponent<AkInitializer>();
        if (initializer == null)
        {
            initializer = initializerGameObject.AddComponent<AkInitializer>();
        }
        if (initializer == null)
        {
            error = "无法创建 AkInitializer。";
            return false;
        }

        initializer.InitializeInitializationSettings();
        AkSoundEngineController.Instance.Init(initializer);
        if (!EditorApplication.isPlaying)
        {
            AkSoundEngineController.Instance.EnableEditorLateUpdate();
            if (AkUnitySoundEngine.IsInitialized())
            {
                AkUnitySoundEngine.WakeupFromSuspend();
            }
        }

        for (int attempt = 0;
             attempt < 50 && !AkUnitySoundEngine.IsInitialized();
             attempt++)
        {
            Thread.Sleep(20);
        }

        if (AkUnitySoundEngine.IsInitialized())
        {
            return true;
        }

        error = "Wwise Sound Engine 初始化失败。";
        return false;
    }

    private static bool EnsurePreviewBank(out string error)
    {
        error = string.Empty;
        if (!AkUnitySoundEngine.IsInitialized())
        {
            error = "Wwise Sound Engine 未初始化。";
            return false;
        }

        AKRESULT result = AkUnitySoundEngine.LoadBank(
            BankName,
            out uint bankId);
        if (result == AKRESULT.AK_Success ||
            result == AKRESULT.AK_BankAlreadyLoaded)
        {
            return true;
        }

        error = $"加载 {BankName}.bnk 失败：{result}";
        return false;
    }

    private static void BindTick()
    {
        EditorApplication.update -= Tick;
        EditorApplication.update += Tick;
    }

    private static void UnbindTick()
    {
        EditorApplication.update -= Tick;
    }

    private static void OnPlayModeStateChanged(
        PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingPlayMode ||
            state == PlayModeStateChange.EnteredEditMode)
        {
            Stop();
            _refreshAfterPlay = true;
        }
    }

    private static void Tick()
    {
        if (_renderer == null)
        {
            Stop();
            return;
        }

        PumpPcm(TargetBufferSeconds);
        double now = EditorApplication.timeSinceStartup;
        if (_releaseRequested)
        {
            float releaseSeconds = (float)(now - _releaseTime);
            Status = $"Release · {releaseSeconds * 1000f:0} ms";
        }
        else
        {
            float heldSeconds = (float)(now - _pressTime);
            float gain = EvaluateGain(
                _renderer.Definition,
                heldSeconds);
            string stage = DescribeStage(
                _renderer.Definition,
                heldSeconds);
            Status = $"{stage} · {heldSeconds * 1000f:0} ms · Gain {gain:0.00}";
        }

        if (_renderer.IsFinished &&
            WwisePcmBridge.GetAvailableFrames(_voiceId) == 0)
        {
            Stop();
            return;
        }

        EditorApplication.QueuePlayerLoopUpdate();
    }

    private static void PumpPcm(float minimumBufferedSeconds)
    {
        if (_renderer == null || _voiceId == 0) return;

        int targetFrames = Mathf.CeilToInt(
            _renderer.SampleRate * minimumBufferedSeconds);
        int available = WwisePcmBridge.GetAvailableFrames(_voiceId);
        while (!_renderer.IsFinished && available < targetFrames)
        {
            int rendered = _renderer.Render(_pcmScratch, ChunkFrames);
            if (rendered <= 0) break;

            int pushed = WwisePcmBridge.PushPcm(
                _voiceId,
                _pcmScratch,
                rendered,
                _renderer.Channels);
            available += pushed;
            if (pushed < rendered) break;
        }

        if (_renderer.IsFinished)
        {
            WwisePcmBridge.MarkFinished(_voiceId);
        }
    }

    private static void SendVoiceInfo()
    {
        byte[] voiceInfo = WwisePcmBridge.BuildVoiceInfoBytes(
            _voiceId,
            _renderer.Channels,
            _renderer.SampleRate);
        GCHandle handle = GCHandle.Alloc(voiceInfo, GCHandleType.Pinned);
        try
        {
            AkUnitySoundEngine.SendPluginCustomGameData(
                AkUnitySoundEngine.AK_INVALID_UNIQUE_ID,
                _host,
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
}
