using UnityEngine;

public static class AudioSystemBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Initialize()
    {
        if (AudioSystem.Instance != null) return;
        GameObject root = new GameObject("[AudioSystem]");
        root.AddComponent<AudioSystem>();
    }
}
