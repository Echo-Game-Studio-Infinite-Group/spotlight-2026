using System;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;

public static class WwisePcmBridgeSmokeTest
{
    [MenuItem("超高速行者/音频/测试 Wwise PCM 桥接")]
    public static void Run()
    {
        GameObject initializerObject = null;
        if (!AkUnitySoundEngine.IsInitialized())
        {
            initializerObject = new GameObject("WwisePcmBridgeSmokeInitializer");
            initializerObject.hideFlags = HideFlags.HideAndDontSave;
            AkInitializer initializer =
                initializerObject.AddComponent<AkInitializer>();
            initializer.InitializeInitializationSettings();
            AkSoundEngineController.Instance.Init(initializer);
            for (int attempt = 0;
                 attempt < 50 && !AkUnitySoundEngine.IsInitialized();
                 attempt++)
            {
                System.Threading.Thread.Sleep(20);
            }
            if (!AkUnitySoundEngine.IsInitialized())
            {
                throw new InvalidOperationException(
                    "Wwise sound engine is not initialized.");
            }
        }

        const uint voiceId = 9001u;
        AudioActionDefinition definition =
            AssetDatabase.LoadAssetAtPath<AudioActionDefinition>(
                "Assets/AudioCollection/Sfx/PlayerSlideAudio.asset");
        if (definition == null)
        {
            throw new InvalidOperationException(
                "Slide AudioActionDefinition was not found.");
        }

        WwiseActionBindings bindings =
            AssetDatabase.LoadAssetAtPath<WwiseActionBindings>(
                "Assets/Resources/Audio/WwiseActionBindings.asset");
        WwiseActionBindings.Entry entry = bindings != null
            ? bindings.Find(definition)
            : null;
        if (entry == null || string.IsNullOrEmpty(entry.SourceRelativePath))
        {
            throw new InvalidOperationException(
                "Slide Wwise binding has no source WAV path.");
        }

        WwiseActionPcmRenderer renderer = new WwiseActionPcmRenderer();
        if (!renderer.Begin(
                definition,
                entry.SourceRelativePath,
                12345,
                out string rendererError))
        {
            throw new InvalidOperationException(
                $"PCM renderer failed: {rendererError}");
        }

        int channels = renderer.Channels;
        int sampleRate = renderer.SampleRate;
        int frames = Mathf.Min(2048, sampleRate);

        AkBankManager.LoadBank("DynamicAction", false, false);
        if (!WwisePcmBridge.TryCreateVoice(
                voiceId,
                channels,
                sampleRate,
                out string error))
        {
            throw new InvalidOperationException(
                $"CreateVoice failed: {error}");
        }

        GameObject emitter = new GameObject("WwisePcmBridgeSmokeTest");
        emitter.hideFlags = HideFlags.HideAndDontSave;
        emitter.AddComponent<AkGameObj>();
        try
        {
            float[] pcm = new float[frames * channels];
            int rendered = renderer.Render(pcm, frames);
            if (rendered <= 0)
            {
                throw new InvalidOperationException(
                    "PCM renderer produced no frames.");
            }

            int pushed = WwisePcmBridge.PushPcm(
                voiceId,
                pcm,
                rendered,
                channels);
            if (pushed != rendered)
            {
                throw new InvalidOperationException(
                    $"PushPcm wrote {pushed}/{rendered} frames.");
            }

            SendVoiceInfo(emitter, voiceId, channels, sampleRate);
            uint playingId = AkUnitySoundEngine.PostEvent(
                "Play_SlideSinePlugin",
                emitter);
            if (playingId == AkUnitySoundEngine.AK_INVALID_PLAYING_ID)
            {
                throw new InvalidOperationException(
                    "Play_SlideSinePlugin returned invalid playing ID.");
            }

            System.Threading.Thread.Sleep(150);
            WwisePcmBridge.MarkFinished(voiceId);
            System.Threading.Thread.Sleep(100);
            WwisePcmBridge.DestroyVoice(voiceId);
            RunPreviewTest(
                "Assets/AudioCollection/Sfx/PlayerSlideAudio.asset",
                "PlayerSlide");
            RunPreviewTest(
                "Assets/AudioCollection/Sfx/PlayerWallSlideAudio.asset",
                "PlayerWallSlide");
            RunPreviewTest(
                "Assets/AudioCollection/Sfx/AudioActionDefinition.asset",
                "UnboundAutoSource");
            AkUnitySoundEngineInitialization.Instance.ResetSoundEngine();
            RunPreviewTest(
                "Assets/AudioCollection/Sfx/PlayerSlideAudio.asset",
                "AfterEngineReset");
            Debug.Log(
                $"Wwise PCM bridge smoke test passed: playingId={playingId}");
        }
        finally
        {
            WwisePcmBridge.DestroyVoice(voiceId);
            UnityEngine.Object.DestroyImmediate(emitter);
            AkBankManager.UnloadBank("DynamicAction");
            if (initializerObject != null)
            {
                UnityEngine.Object.DestroyImmediate(initializerObject);
            }
        }
    }

    private static void RunPreviewTest(
        string definitionPath,
        string label)
    {
        AudioActionDefinition definition =
            AssetDatabase.LoadAssetAtPath<AudioActionDefinition>(
                definitionPath);
        if (definition == null)
        {
            throw new InvalidOperationException(
                $"{label} AudioActionDefinition was not found.");
        }

        if (!AudioActionSourceLocator.TryResolve(
                definition,
                out string sourceRelativePath,
                out string sourceError))
        {
            throw new InvalidOperationException(
                $"{label} source resolution failed: {sourceError}");
        }

        WwiseActionPcmSource.Data source = WwiseActionPcmSource.Load(
            sourceRelativePath,
            out sourceError);
        if (source == null)
        {
            throw new InvalidOperationException(
                $"{label} source WAV failed: {sourceError}");
        }

        AudioActionClipAnalysis analysis = AudioActionClipAnalysis.Analyze(
            source.Samples,
            source.Channels,
            source.SampleRate);
        if (!analysis.Success)
        {
            throw new InvalidOperationException(
                $"{label} analysis failed: {analysis.Error}");
        }
        Debug.Log(
            $"Wwise action analysis passed: {label} " +
            $"({analysis.FirstAudible01:0.00}..{analysis.LastAudible01:0.00})");

        if (!WwiseActionPreviewPlayer.Begin(
                definition,
                0.5f,
                1f,
                out string error))
        {
            throw new InvalidOperationException(
                $"{label} preview begin failed: {error}");
        }

        System.Threading.Thread.Sleep(120);
        WwiseActionPreviewPlayer.Release();
        System.Threading.Thread.Sleep(120);
        WwiseActionPreviewPlayer.Stop();
        Debug.Log($"Wwise action preview passed: {label}");
    }

    private static void SendVoiceInfo(
        GameObject emitter,
        uint voiceId,
        int channels,
        int sampleRate)
    {
        byte[] data = WwisePcmBridge.BuildVoiceInfoBytes(
            voiceId,
            channels,
            sampleRate);
        GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            AkUnitySoundEngine.SendPluginCustomGameData(
                AkUnitySoundEngine.AK_INVALID_UNIQUE_ID,
                emitter,
                AkPluginType.AkPluginTypeSource,
                0,
                4242,
                handle.AddrOfPinnedObject(),
                (uint)data.Length);
        }
        finally
        {
            handle.Free();
        }
    }
}
