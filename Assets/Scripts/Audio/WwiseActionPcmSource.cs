using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Loads interleaved PCM directly from a WAV file without asking Unity to
/// decode an AudioClip. This keeps the Wwise path independent from Unity's
/// audio device, which is disabled when Wwise owns the output.
/// </summary>
public static class WwiseActionPcmSource
{
    public sealed class Data
    {
        public float[] Samples;
        public int Channels;
        public int SampleRate;

        public int Frames => Channels > 0 ? Samples.Length / Channels : 0;
    }

    private static readonly Dictionary<string, Data> Cache =
        new Dictionary<string, Data>(StringComparer.OrdinalIgnoreCase);

    public static Data Load(string relativePath, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            error = "source path is empty";
            return null;
        }

        string normalizedPath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string fullPath = Path.IsPathRooted(normalizedPath)
            ? normalizedPath
            : Path.Combine(Application.streamingAssetsPath, normalizedPath);

        if (Cache.TryGetValue(fullPath, out Data cached) && cached != null)
        {
            return cached;
        }

        if (!File.Exists(fullPath))
        {
            error = $"source WAV not found: {fullPath}";
            return null;
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(fullPath);
            Data data = Parse(bytes, out error);
            if (data != null)
            {
                Cache[fullPath] = data;
            }
            return data;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return null;
        }
    }

    public static void ClearCache()
    {
        Cache.Clear();
    }

    private static Data Parse(byte[] bytes, out string error)
    {
        error = string.Empty;
        if (bytes == null || bytes.Length < 44 ||
            !Matches(bytes, 0, "RIFF") ||
            !Matches(bytes, 8, "WAVE"))
        {
            error = "invalid RIFF/WAVE header";
            return null;
        }

        ushort formatTag = 0;
        ushort channels = 0;
        int sampleRate = 0;
        ushort bitsPerSample = 0;
        int dataOffset = -1;
        int dataLength = 0;

        int offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            int chunkSize = ReadInt32(bytes, offset + 4);
            int chunkData = offset + 8;
            if (chunkSize < 0 || chunkData > bytes.Length)
            {
                error = "invalid WAV chunk size";
                return null;
            }

            int available = Math.Min(chunkSize, bytes.Length - chunkData);
            if (Matches(bytes, offset, "fmt "))
            {
                if (available < 16)
                {
                    error = "WAV fmt chunk is too small";
                    return null;
                }

                formatTag = ReadUInt16(bytes, chunkData);
                channels = ReadUInt16(bytes, chunkData + 2);
                sampleRate = ReadInt32(bytes, chunkData + 4);
                bitsPerSample = ReadUInt16(bytes, chunkData + 14);

                if (formatTag == 0xFFFE && available >= 40)
                {
                    formatTag = ReadUInt16(bytes, chunkData + 24);
                }
            }
            else if (Matches(bytes, offset, "data"))
            {
                dataOffset = chunkData;
                dataLength = available;
            }

            int advance = chunkSize + (chunkSize & 1);
            if (advance <= 0)
            {
                error = "invalid WAV chunk advance";
                return null;
            }
            offset = chunkData + advance;
        }

        if (formatTag == 0 || channels == 0 || sampleRate <= 0 ||
            bitsPerSample == 0 || dataOffset < 0 || dataLength <= 0)
        {
            error = "WAV is missing fmt or data";
            return null;
        }

        int bytesPerSample = bitsPerSample / 8;
        if (bytesPerSample <= 0)
        {
            error = "invalid WAV sample size";
            return null;
        }

        int bytesPerFrame = channels * bytesPerSample;
        int frames = dataLength / bytesPerFrame;
        if (frames <= 1)
        {
            error = "WAV contains no usable frames";
            return null;
        }

        float[] samples = new float[frames * channels];
        for (int frame = 0; frame < frames; frame++)
        {
            int frameOffset = dataOffset + frame * bytesPerFrame;
            for (int channel = 0; channel < channels; channel++)
            {
                int sampleOffset = frameOffset + channel * bytesPerSample;
                if (!TryReadSample(
                        bytes,
                        sampleOffset,
                        formatTag,
                        bitsPerSample,
                        out samples[frame * channels + channel]))
                {
                    error = $"unsupported WAV format tag={formatTag} bits={bitsPerSample}";
                    return null;
                }
            }
        }

        return new Data
        {
            Samples = samples,
            Channels = channels,
            SampleRate = sampleRate
        };
    }

    private static bool TryReadSample(
        byte[] bytes,
        int offset,
        ushort formatTag,
        ushort bitsPerSample,
        out float value)
    {
        value = 0f;
        if (offset < 0 || offset + bitsPerSample / 8 > bytes.Length)
        {
            return false;
        }

        if (formatTag == 3)
        {
            if (bitsPerSample == 32)
            {
                value = BitConverter.ToSingle(bytes, offset);
                return true;
            }
            if (bitsPerSample == 64)
            {
                value = (float)BitConverter.ToDouble(bytes, offset);
                return true;
            }
            return false;
        }

        if (formatTag != 1)
        {
            return false;
        }

        switch (bitsPerSample)
        {
            case 8:
                value = (bytes[offset] - 128) / 128f;
                return true;
            case 16:
                value = BitConverter.ToInt16(bytes, offset) / 32768f;
                return true;
            case 24:
                int sample24 = bytes[offset] |
                               (bytes[offset + 1] << 8) |
                               (bytes[offset + 2] << 16);
                if ((sample24 & 0x800000) != 0)
                {
                    sample24 |= unchecked((int)0xFF000000);
                }
                value = sample24 / 8388608f;
                return true;
            case 32:
                value = BitConverter.ToInt32(bytes, offset) / 2147483648f;
                return true;
            default:
                return false;
        }
    }

    private static bool Matches(byte[] bytes, int offset, string value)
    {
        if (bytes == null || offset < 0 || offset + value.Length > bytes.Length)
        {
            return false;
        }

        for (int i = 0; i < value.Length; i++)
        {
            if (bytes[offset + i] != value[i])
            {
                return false;
            }
        }

        return true;
    }

    private static ushort ReadUInt16(byte[] bytes, int offset)
    {
        return BitConverter.ToUInt16(bytes, offset);
    }

    private static int ReadInt32(byte[] bytes, int offset)
    {
        return BitConverter.ToInt32(bytes, offset);
    }
}
