using System;
using UnityEngine;

[Serializable]
public struct EnvelopeKey
{
    [Range(0f, 1f)] public float Time;
    public float Value;
    public AudioCurveMapping CurveToNext;

    public EnvelopeKey(float time, float value, AudioCurveMapping curveToNext = AudioCurveMapping.Linear)
    {
        Time = time;
        Value = value;
        CurveToNext = curveToNext;
    }
}

[Serializable]
public sealed class AudioEnvelope
{
    public EnvelopeKey[] Keys;

    public float Evaluate(float time)
    {
        EnsureDefaults();
        time = Mathf.Clamp01(time);
        if (time <= Keys[0].Time) return Keys[0].Value;
        for (int i = 0; i < Keys.Length - 1; i++)
        {
            EnvelopeKey left = Keys[i];
            EnvelopeKey right = Keys[i + 1];
            if (time > right.Time) continue;
            if (Mathf.Approximately(left.Time, right.Time)) return right.Value;
            float u = Mathf.InverseLerp(left.Time, right.Time, time);
            return Mathf.LerpUnclamped(left.Value, right.Value,
                AudioCurveUtility.Map(u, left.CurveToNext));
        }
        return Keys[Keys.Length - 1].Value;
    }

    public void EnsureDefaults()
    {
        if (Keys != null && Keys.Length > 0) return;
        Keys = Steady().Keys;
    }

    public static AudioEnvelope FadeIn()
    {
        return new AudioEnvelope
        {
            Keys = new[]
            {
                new EnvelopeKey(0f, 0f, AudioCurveMapping.SCurve),
                new EnvelopeKey(1f, 1f)
            }
        };
    }

    public static AudioEnvelope Steady()
    {
        return new AudioEnvelope
        {
            Keys = new[]
            {
                new EnvelopeKey(0f, 1f),
                new EnvelopeKey(1f, 1f)
            }
        };
    }

    public static AudioEnvelope FadeOut()
    {
        return new AudioEnvelope
        {
            Keys = new[]
            {
                new EnvelopeKey(0f, 1f, AudioCurveMapping.Fast),
                new EnvelopeKey(1f, 0f)
            }
        };
    }

    public static AudioEnvelope Linear()
    {
        return new AudioEnvelope
        {
            Keys = new[]
            {
                new EnvelopeKey(0f, 0f),
                new EnvelopeKey(1f, 1f)
            }
        };
    }

    public static AudioEnvelope Adsr(float attack, float decay, float sustain, float release)
    {
        float total = Mathf.Max(0.0001f, attack + decay + sustain + release);
        float a = attack / total;
        float d = (attack + decay) / total;
        float s = (attack + decay + sustain) / total;
        return new AudioEnvelope
        {
            Keys = new[]
            {
                new EnvelopeKey(0f, 0f),
                new EnvelopeKey(a, 1f, AudioCurveMapping.Fast),
                new EnvelopeKey(d, 0.65f, AudioCurveMapping.Slow),
                new EnvelopeKey(s, 0.65f, AudioCurveMapping.Slow),
                new EnvelopeKey(1f, 0f)
            }
        };
    }
}
