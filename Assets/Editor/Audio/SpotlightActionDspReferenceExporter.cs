using System.IO;
using System;
using UnityEditor;
using UnityEngine;

public static class SpotlightActionDspReferenceExporter
{
    private const int SampleRate = 48000;
    private const int SourceFrames = 96000;
    private const int LoopStartFrame = 12000;
    private const int LoopEndFrame = 72000;
    private const int CrossfadeFrames = 480;
    private const float GrainSeconds = 0.18f;
    private const float GrainSpacingSeconds = 0.07f;
    private const float GrainRandomStart01 = 1f;
    private const float GrainTuneCents = 25f;
    private const int GranularSeed = 12345;

    [MenuItem("超高速行者/音频/导出 Wwise DSP 参考 PCM")]
    public static void ExportFromMenu()
    {
        ExportReference();
    }

    public static void ExportReference()
    {
        string projectRoot = Directory.GetParent(Application.dataPath)?.FullName
            ?? throw new InvalidOperationException("Cannot resolve Unity project root.");
        string outputDirectory = Path.Combine(
            projectRoot,
            "Logs",
            "AudioDspReference");
        Directory.CreateDirectory(outputDirectory);

        float[] source = BuildFixture();
        WriteFloatFile(
            Path.Combine(outputDirectory, "adsr_hold.raw"),
            BuildHoldReference());
        WriteFloatFile(
            Path.Combine(outputDirectory, "adsr_release.raw"),
            BuildReleaseReference());
        WriteFloatFile(
            Path.Combine(outputDirectory, "crossfade.raw"),
            RenderCrossfadeLoop(source));
        WriteFloatFile(
            Path.Combine(outputDirectory, "granular.raw"),
            RenderGranularLoop(source));

        Debug.Log($"Wwise DSP reference PCM exported to {outputDirectory}");
    }

    private static float[] BuildFixture()
    {
        float[] samples = new float[SourceFrames];
        for (int frame = 0; frame < SourceFrames; frame++)
        {
            float time = frame / (float)SampleRate;
            samples[frame] =
                0.60f * Mathf.Sin(2f * Mathf.PI * 220f * time)
                + 0.25f * Mathf.Sin(2f * Mathf.PI * 437f * time + 0.5f);
        }

        return samples;
    }

    private static float[] BuildHoldReference()
    {
        AdsrEnvelope envelope = BuildEnvelope();
        int frames = Mathf.RoundToInt(SampleRate * 1.5f);
        float[] values = new float[frames];
        for (int frame = 0; frame < frames; frame++)
        {
            values[frame] = envelope.EvaluateOn(frame / (float)SampleRate);
        }
        return values;
    }

    private static float[] BuildReleaseReference()
    {
        AdsrEnvelope envelope = BuildEnvelope();
        float releaseDuration =
            (SourceFrames - LoopEndFrame) / (float)SampleRate;
        int frames = Mathf.RoundToInt(releaseDuration * SampleRate);
        float[] values = new float[frames];
        for (int frame = 0; frame < frames; frame++)
        {
            values[frame] = envelope.EvaluateRelease(
                frame / (float)SampleRate,
                envelope.SustainLevel,
                releaseDuration);
        }
        return values;
    }

    private static AdsrEnvelope BuildEnvelope()
    {
        return new AdsrEnvelope
        {
            AttackSeconds = 0.05f,
            DecaySeconds = 0.08f,
            SustainLevel = 0.7f,
            AttackCurve = AudioCurveMapping.SCurve,
            DecayCurve = AudioCurveMapping.Fast,
            ReleaseCurve = AudioCurveMapping.Fast
        };
    }

    private static float[] RenderCrossfadeLoop(float[] source)
    {
        int loopLength = LoopEndFrame - LoopStartFrame;
        int crossfade = Mathf.Clamp(
            CrossfadeFrames,
            0,
            loopLength / 2);
        int outputLength = loopLength - crossfade;
        float[] output = new float[outputLength];
        float[] headWeights = new float[crossfade];
        for (int index = 0; index < crossfade; index++)
        {
            float normalized = crossfade > 1
                ? index / (float)(crossfade - 1)
                : 1f;
            headWeights[index] = 0.5f - 0.5f * Mathf.Cos(Mathf.PI * normalized);
        }

        int crossfadeStart = outputLength - crossfade;
        for (int outputFrame = 0; outputFrame < outputLength; outputFrame++)
        {
            if (crossfade > 0 && outputFrame >= crossfadeStart)
            {
                int index = outputFrame - crossfadeStart;
                float headWeight = headWeights[index];
                int tailFrame = LoopEndFrame - crossfade + index;
                int headFrame = LoopStartFrame + index;
                output[outputFrame] =
                    source[tailFrame] * (1f - headWeight)
                    + source[headFrame] * headWeight;
            }
            else
            {
                output[outputFrame] = source[LoopStartFrame + outputFrame];
            }
        }

        return output;
    }

    private static float[] RenderGranularLoop(float[] source)
    {
        int loopLength = Mathf.Max(2, LoopEndFrame - LoopStartFrame);
        int grainFrames = Mathf.Clamp(
            Mathf.RoundToInt(GrainSeconds * SampleRate),
            64,
            loopLength);
        int spacingFrames = Mathf.Clamp(
            Mathf.RoundToInt(GrainSpacingSeconds * SampleRate),
            1,
            grainFrames);
        int grainCount = Mathf.Clamp(
            Mathf.CeilToInt(2.5f / Mathf.Max(0.005f, GrainSpacingSeconds)),
            4,
            512);
        int totalFrames = grainCount * spacingFrames;
        float[] output = new float[totalFrames];
        int fadeFrames = Mathf.Clamp(
            Mathf.RoundToInt(grainFrames * 0.45f),
            1,
            grainFrames / 2);
        int maxOffset = Mathf.Max(
            0,
            Mathf.RoundToInt((loopLength - grainFrames) * GrainRandomStart01));

        var random = new System.Random(GranularSeed == 0 ? 12345 : GranularSeed);
        float[] windowTable = new float[grainFrames];
        for (int index = 0; index < grainFrames; index++)
        {
            windowTable[index] = GrainWindow(index, grainFrames, fadeFrames);
        }

        for (int grain = 0; grain < grainCount; grain++)
        {
            int offset = maxOffset > 0 ? random.Next(0, maxOffset + 1) : 0;
            int readStart = LoopStartFrame + offset;
            int writeStart = grain * spacingFrames;
            double tuneRatio = Math.Pow(
                2.0,
                (random.NextDouble() * 2.0 - 1.0) * GrainTuneCents / 1200.0);
            double readPosition = readStart;
            int destinationFrame = writeStart % totalFrames;
            for (int index = 0; index < grainFrames; index++)
            {
                float window = windowTable[index];
                int frame0 = (int)readPosition;
                float fraction = (float)(readPosition - frame0);
                int frame1 = WrapIntoLoop(
                    frame0 + 1,
                    LoopStartFrame,
                    LoopEndFrame,
                    loopLength);
                int wrappedFrame0 = WrapIntoLoop(
                    frame0,
                    LoopStartFrame,
                    LoopEndFrame,
                    loopLength);
                if (window > 0f)
                {
                    float first = source[wrappedFrame0];
                    output[destinationFrame] +=
                        (first + (source[frame1] - first) * fraction) * window;
                }

                readPosition += tuneRatio;
                if (++destinationFrame >= totalFrames)
                {
                    destinationFrame = 0;
                }
            }
        }

        double sourceRms = RmsOfRange(
            source,
            LoopStartFrame,
            LoopEndFrame);
        double bufferRms = RmsOfAll(output);
        if (bufferRms > 1e-6 && sourceRms > 1e-6)
        {
            float gain = (float)(sourceRms / bufferRms);
            for (int index = 0; index < output.Length; index++)
            {
                output[index] *= gain;
            }
        }

        float peak = 0f;
        for (int index = 0; index < output.Length; index++)
        {
            peak = Mathf.Max(peak, Mathf.Abs(output[index]));
        }
        if (peak > 0.95f)
        {
            float scale = 0.95f / peak;
            for (int index = 0; index < output.Length; index++)
            {
                output[index] *= scale;
            }
        }

        return output;
    }

    private static float GrainWindow(
        int index,
        int grainFrames,
        int fadeFrames)
    {
        if (index < fadeFrames)
        {
            return AudioCurveUtility.Map(
                index / (float)fadeFrames,
                AudioCurveMapping.SCurve);
        }

        int fromEnd = grainFrames - 1 - index;
        if (fromEnd < fadeFrames)
        {
            return AudioCurveUtility.Map(
                fromEnd / (float)fadeFrames,
                AudioCurveMapping.SCurve);
        }

        return 1f;
    }

    private static int WrapIntoLoop(
        int frame,
        int loopStart,
        int loopEnd,
        int loopLength)
    {
        while (frame >= loopEnd)
        {
            frame -= loopLength;
        }
        while (frame < loopStart)
        {
            frame += loopLength;
        }
        return frame;
    }

    private static double RmsOfRange(
        float[] data,
        int fromFrame,
        int toFrame)
    {
        double sum = 0.0;
        int count = 0;
        int last = Mathf.Min(toFrame, data.Length);
        for (int frame = Mathf.Max(0, fromFrame); frame < last; frame++)
        {
            double value = data[frame];
            sum += value * value;
            count++;
        }
        return count == 0 ? 0.0 : Math.Sqrt(sum / count);
    }

    private static double RmsOfAll(float[] data)
    {
        double sum = 0.0;
        for (int index = 0; index < data.Length; index++)
        {
            double value = data[index];
            sum += value * value;
        }
        return data.Length == 0 ? 0.0 : Math.Sqrt(sum / data.Length);
    }

    private static void WriteFloatFile(string path, float[] samples)
    {
        using (FileStream stream = File.Create(path))
        using (BinaryWriter writer = new BinaryWriter(stream))
        {
            foreach (float sample in samples)
            {
                writer.Write(sample);
            }
        }
    }
}
