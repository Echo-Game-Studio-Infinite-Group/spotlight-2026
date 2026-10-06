using UnityEngine;

public enum AudioCueSelectionMode
{
    Random,
    RandomNoImmediateRepeat,
    Sequential
}

[CreateAssetMenu(fileName = "AudioCue", menuName = "超高速行者/音频/单次音效")]
public sealed class AudioCueDefinition : ScriptableObject
{
    public string Id;
    public AudioClip[] Clips;
    public AudioCueSelectionMode SelectionMode = AudioCueSelectionMode.RandomNoImmediateRepeat;

    [Header("随机差分")]
    public AudioRange GainDbRange = new AudioRange(0f, 0f);
    public AudioRange PitchSemitoneRange = new AudioRange(0f, 0f);

    [Header("空间和音色")]
    [Range(0f, 1f)] public float SpatialBlend = 1f;
    [Min(0.01f)] public float MinDistance = 2f;
    [Min(0.02f)] public float MaxDistance = 60f;
    [Range(0, 256)] public int Priority = 128;
    [Min(10f)] public float LowpassHz = 22000f;
    [Min(10f)] public float HighpassHz = 20f;

    public bool HasClips => Clips != null && Clips.Length > 0;

    public AudioClip PickClip(System.Random random)
    {
        if (!HasClips) return null;
        if (Clips.Length == 1 || random == null) return Clips[0];
        return Clips[random.Next(Clips.Length)];
    }
}
