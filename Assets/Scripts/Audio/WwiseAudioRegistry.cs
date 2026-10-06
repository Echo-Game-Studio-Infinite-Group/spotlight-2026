using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Explicit registration point for IDynamicAudioActionSource providers.
/// Sources register in OnEnable and unregister in OnDisable; drivers consume
/// only sources that belong to their own GameObject hierarchy.
/// </summary>
public static class WwiseAudioRegistry
{
    private static readonly List<IDynamicAudioActionSource> Sources =
        new List<IDynamicAudioActionSource>();

    public static int Version { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Reset()
    {
        Sources.Clear();
        Version++;
    }

    public static void Register(IDynamicAudioActionSource source)
    {
        if (source == null || Sources.Contains(source)) return;
        Sources.Add(source);
        Version++;
    }

    public static void Unregister(IDynamicAudioActionSource source)
    {
        if (source == null || !Sources.Remove(source)) return;
        Version++;
    }

    public static void CopyTo(List<IDynamicAudioActionSource> destination)
    {
        if (destination == null) return;
        destination.Clear();
        for (int i = 0; i < Sources.Count; i++)
        {
            IDynamicAudioActionSource source = Sources[i];
            if (source != null)
            {
                destination.Add(source);
            }
        }
    }
}
