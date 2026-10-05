using UnityEngine;

[CreateAssetMenu(fileName = "GameAudioCatalog", menuName = "超高速行者/音频/音频库")]
public sealed class GameAudioCatalog : ScriptableObject
{
    [Header("按语义 Id 查找的库")]
    public AudioCueDefinition[] Cues;
    public AudioActionDefinition[] Actions;

    [Header("全局层")]
    public AudioClip Music;
    public AudioClip SpeedLayer;

    public AudioCueDefinition FindCue(string id)
    {
        if (string.IsNullOrEmpty(id) || Cues == null) return null;
        for (int i = 0; i < Cues.Length; i++)
            if (Cues[i] != null && Cues[i].Id == id) return Cues[i];
        return null;
    }

    public AudioActionDefinition FindAction(string id)
    {
        if (string.IsNullOrEmpty(id) || Actions == null) return null;
        for (int i = 0; i < Actions.Length; i++)
            if (Actions[i] != null && (Actions[i].Id == id || Actions[i].name == id)) return Actions[i];
        return null;
    }
}
