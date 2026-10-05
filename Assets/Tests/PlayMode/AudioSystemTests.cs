using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public sealed class AudioSystemTests
{
    [Test]
    public void CurveMapping_EndpointsAndMidpoint()
    {
        Assert.That(AudioCurveUtility.Map(0f, AudioCurveMapping.Linear), Is.EqualTo(0f).Within(1e-4f));
        Assert.That(AudioCurveUtility.Map(1f, AudioCurveMapping.Slow), Is.EqualTo(1f).Within(1e-4f));
        Assert.That(AudioCurveUtility.Map(0.5f, AudioCurveMapping.SCurve), Is.EqualTo(0.5f).Within(1e-4f));
        Assert.That(AudioCurveUtility.Map(1f, AudioCurveMapping.Fast), Is.EqualTo(1f).Within(1e-4f));
    }

    [Test]
    public void AudioActionDefinition_ClampsSingleClipRegions()
    {
        AudioActionDefinition definition = ScriptableObject.CreateInstance<AudioActionDefinition>();
        try
        {
            definition.LoopStart01 = 0.8f;
            definition.LoopEnd01 = 0.2f;
            definition.EnsureDefaults();
            Assert.That(definition.LoopEnd01, Is.GreaterThan(definition.LoopStart01));
            Assert.That(definition.Envelope, Is.Not.Null);
            Assert.That(definition.Envelope.SustainLevel, Is.InRange(0f, 1f));
            Assert.That(definition.LoopMode, Is.EqualTo(AudioActionLoopMode.SustainLoop));
        }
        finally
        {
            Object.DestroyImmediate(definition);
        }
    }

    [Test]
    public void AudioRange_IsDeterministicWithSeed()
    {
        AudioRange range = new AudioRange(-2f, 2f);
        System.Random first = new System.Random(1234);
        System.Random second = new System.Random(1234);
        Assert.That(range.Value(first), Is.EqualTo(range.Value(second)).Within(1e-6f));
    }

    [UnityTest]
    public IEnumerator AudioSystem_StartsAndStopsSingleClipAction()
    {
        GameObject follow = new GameObject("AudioFollowProbe");
        AudioClip clip = AudioClip.Create("ActionTone", 4800, 1, 48000, false);
        AudioActionDefinition action = ScriptableObject.CreateInstance<AudioActionDefinition>();
        try
        {
            float[] samples = new float[clip.samples];
            for (int i = 0; i < samples.Length; i++) samples[i] = Mathf.Sin(i * 0.08f) * 0.1f;
            clip.SetData(samples, 0);
            action.Clip = clip;
            action.Envelope.AttackSeconds = 0.01f;
            action.Envelope.DecaySeconds = 0.01f;
            action.Envelope.SustainLevel = 1f;
            action.LoopStart01 = 0.1f;
            action.LoopEnd01 = 0.9f;
            action.EnsureDefaults();

            AudioSystem system = AudioSystem.Instance;
            Assert.That(system, Is.Not.Null, "RuntimeInitializeOnLoadMethod 应创建音频系统");

            AudioActionHandle handle = system.PlayAction(action, follow.transform, "test.instance");
            Assert.That(handle, Is.Not.Null);
            yield return null;
            Assert.That(handle.IsActive, Is.True);

            handle.Stop(true);
            yield return null;
            Assert.That(handle.IsActive, Is.False);
        }
        finally
        {
            Object.DestroyImmediate(follow);
            Object.DestroyImmediate(clip);
            Object.DestroyImmediate(action);
        }
    }
}
