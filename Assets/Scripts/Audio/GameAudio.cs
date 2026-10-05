using UnityEngine;

public static class GameAudio
{
    public static bool IsReady => AudioSystem.Instance != null;
    public static GameAudioCatalog Catalog => AudioSystem.Instance != null ? AudioSystem.Instance.Catalog : null;
    public static PlayerAudioProfile Profile => AudioSystem.Instance != null ? AudioSystem.Instance.Profile : null;

    public static void Configure(GameAudioCatalog catalog, PlayerAudioProfile profile)
    {
        if (AudioSystem.Instance != null) AudioSystem.Instance.Configure(catalog, profile);
    }

    public static AudioActionHandle PlayAction(AudioActionDefinition definition, Transform follow, string surfaceId = "")
    {
        return AudioSystem.Instance != null
            ? AudioSystem.Instance.PlayAction(definition, follow, surfaceId)
            : null;
    }

    public static void PlaySfx(AudioCueDefinition cue, Vector3 position, float volumeDbOffset = 0f)
    {
        if (AudioSystem.Instance != null) AudioSystem.Instance.PlaySfx(cue, position, volumeDbOffset);
    }

    public static void PlaySfx(AudioCueDefinition cue, Transform follow, float volumeDbOffset = 0f)
    {
        if (AudioSystem.Instance != null) AudioSystem.Instance.PlaySfx(cue, follow, volumeDbOffset);
    }

    public static void TriggerGlitch(Vector3 position, float intensity = 0.8f, float duration = 0.09f)
    {
        if (AudioSystem.Instance != null) AudioSystem.Instance.TriggerGlitch(position, intensity, duration);
    }

    public static void PlayMusic(AudioClip clip, float fadeSeconds = -1f)
    {
        if (AudioSystem.Instance != null) AudioSystem.Instance.PlayMusic(clip, fadeSeconds);
    }

    public static void StopMusic(float fadeSeconds = -1f)
    {
        if (AudioSystem.Instance != null) AudioSystem.Instance.StopMusic(fadeSeconds);
    }

    public static void ToggleDebug()
    {
        if (AudioSystem.Instance != null) AudioSystem.Instance.ToggleDebug();
    }
}
