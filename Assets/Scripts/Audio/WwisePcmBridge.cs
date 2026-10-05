using System;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// Thin C# bridge to the native Wwise transport plugin.
/// The plugin owns PCM buffers only; all action rendering stays in Unity.
/// </summary>
public static class WwisePcmBridge
{
    private const string DllName = "SpotlightActionSource";

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SpotlightAction_CreateVoice(
        uint voiceId,
        uint channels,
        uint sampleRate);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SpotlightAction_DestroyVoice(uint voiceId);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint SpotlightAction_GetAvailableFrames(uint voiceId);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint SpotlightAction_GetConsumedFrames(uint voiceId);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint SpotlightAction_PushPcm(
        uint voiceId,
        [In] float[] interleavedData,
        uint frames,
        uint channels);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SpotlightAction_MarkFinished(uint voiceId);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SpotlightAction_ClearVoice(uint voiceId);

    public static bool TryCreateVoice(
        uint voiceId,
        int channels,
        int sampleRate,
        out string error)
    {
        error = string.Empty;
        try
        {
            if (SpotlightAction_CreateVoice(
                    voiceId,
                    (uint)channels,
                    (uint)sampleRate))
            {
                return true;
            }

            error = "native bridge rejected voice parameters";
            return false;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    public static void DestroyVoice(uint voiceId)
    {
        try
        {
            SpotlightAction_DestroyVoice(voiceId);
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"Wwise PCM bridge destroy failed: {exception.Message}");
        }
    }

    public static int GetAvailableFrames(uint voiceId)
    {
        try
        {
            return (int)SpotlightAction_GetAvailableFrames(voiceId);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    public static int GetConsumedFrames(uint voiceId)
    {
        try
        {
            return (int)SpotlightAction_GetConsumedFrames(voiceId);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    public static int PushPcm(
        uint voiceId,
        float[] interleavedData,
        int frames,
        int channels)
    {
        if (frames <= 0 || interleavedData == null)
        {
            return 0;
        }

        try
        {
            return (int)SpotlightAction_PushPcm(
                voiceId,
                interleavedData,
                (uint)frames,
                (uint)channels);
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"Wwise PCM bridge push failed: {exception.Message}");
            return 0;
        }
    }

    public static void MarkFinished(uint voiceId)
    {
        try
        {
            SpotlightAction_MarkFinished(voiceId);
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"Wwise PCM bridge finish failed: {exception.Message}");
        }
    }

    public static void ClearVoice(uint voiceId)
    {
        try
        {
            SpotlightAction_ClearVoice(voiceId);
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"Wwise PCM bridge clear failed: {exception.Message}");
        }
    }

    public static byte[] BuildVoiceInfoBytes(
        uint voiceId,
        int channels,
        int sampleRate)
    {
        const uint magic = 0x53504C31u;
        byte[] data = new byte[16];
        Buffer.BlockCopy(BitConverter.GetBytes(magic), 0, data, 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(voiceId), 0, data, 4, 4);
        Buffer.BlockCopy(BitConverter.GetBytes((uint)channels), 0, data, 8, 4);
        Buffer.BlockCopy(BitConverter.GetBytes((uint)sampleRate), 0, data, 12, 4);
        return data;
    }
}
