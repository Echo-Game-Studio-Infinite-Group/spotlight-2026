using System.Collections;
using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public sealed class WwiseSourcePluginTests
{
    [UnityTest]
    public IEnumerator DynamicActionBank_RegistersSourcePluginAndEvent()
    {
        float deadline = Time.realtimeSinceStartup + 10f;
        while (!AkUnitySoundEngine.IsInitialized() &&
               Time.realtimeSinceStartup < deadline)
        {
            yield return null;
        }

        Assert.That(
            AkUnitySoundEngine.IsInitialized(),
            Is.True,
            "Wwise sound engine did not initialize in PlayMode.");
        Debug.Log("WwiseSourcePluginTests: engine initialized");

        AkBankManager.LoadBank("DynamicAction", false, false);
        yield return null;
        bool pluginRegistered = AkUnitySoundEngine.IsPluginRegistered(
            AkPluginType.AkPluginTypeSource,
            0,
            4242);
        Debug.Log(
            $"WwiseSourcePluginTests: DynamicAction loaded, pluginRegistered={pluginRegistered}");
        Assert.That(pluginRegistered, Is.True);

        GameObject emitter = new GameObject("WwiseSourcePluginProbe");
        emitter.AddComponent<AkGameObj>();
        try
        {
            WwiseActionBindings bindings =
                Resources.Load<WwiseActionBindings>("Audio/WwiseActionBindings");
            Assert.That(bindings, Is.Not.Null);
            Assert.That(bindings.Entries, Is.Not.Empty);
            const uint voiceId = 777u;
            const int channels = 1;
            const int sampleRate = 48000;
            Assert.That(
                WwisePcmBridge.TryCreateVoice(
                    voiceId,
                    channels,
                    sampleRate,
                    out string bridgeError),
                Is.True,
                bridgeError);

            float[] pcm = new float[sampleRate / 10];
            for (int frame = 0; frame < pcm.Length; frame++)
            {
                pcm[frame] = Mathf.Sin(
                    2f * Mathf.PI * 220f * frame / sampleRate) * 0.2f;
            }
            Assert.That(
                WwisePcmBridge.PushPcm(
                    voiceId,
                    pcm,
                    pcm.Length,
                    channels),
                Is.EqualTo(pcm.Length));
            SendCustomData(
                emitter,
                WwisePcmBridge.BuildVoiceInfoBytes(
                    voiceId,
                    channels,
                    sampleRate));
            Debug.Log("WwiseSourcePluginTests: PCM voice prepared");

            uint playingId = AkUnitySoundEngine.PostEvent(
                "Play_SlideSinePlugin",
                emitter);
            Assert.That(
                playingId,
                Is.Not.EqualTo(AkUnitySoundEngine.AK_INVALID_PLAYING_ID));
            Debug.Log($"WwiseSourcePluginTests: play posted {playingId}");

            yield return new WaitForSecondsRealtime(0.25f);
            WwisePcmBridge.MarkFinished(voiceId);
            AkUnitySoundEngine.SetRTPCValue(
                "ActionSpeed",
                0.75f,
                emitter);

            uint stopPlayingId = AkUnitySoundEngine.PostEvent(
                "Stop_SlideSinePlugin",
                emitter);
            Assert.That(
                stopPlayingId,
                Is.Not.EqualTo(AkUnitySoundEngine.AK_INVALID_PLAYING_ID));
            Debug.Log($"WwiseSourcePluginTests: stop posted {stopPlayingId}");

            yield return new WaitForSecondsRealtime(1.1f);
            WwisePcmBridge.DestroyVoice(voiceId);
        }
        finally
        {
            Object.DestroyImmediate(emitter);
        }
    }

    private static void SendCustomData(GameObject emitter, byte[] bytes)
    {
        GCHandle handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            AkUnitySoundEngine.SendPluginCustomGameData(
                AkUnitySoundEngine.AK_INVALID_UNIQUE_ID,
                emitter,
                AkPluginType.AkPluginTypeSource,
                0,
                4242,
                handle.AddrOfPinnedObject(),
                (uint)bytes.Length);
        }
        finally
        {
            handle.Free();
        }
    }
}
