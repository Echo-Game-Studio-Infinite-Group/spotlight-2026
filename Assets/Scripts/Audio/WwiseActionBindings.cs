using System;
using UnityEngine;

/// <summary>
/// Maps action audio definitions to Wwise events and game parameters.
/// The runtime driver treats this as data and does not know action categories.
/// </summary>
[CreateAssetMenu(
    fileName = "WwiseActionBindings",
    menuName = "超高速行者/音频/Wwise 动作绑定")]
public sealed class WwiseActionBindings : ScriptableObject
{
    [Serializable]
    public sealed class Entry
    {
        public AudioActionDefinition Definition;
        public string PlayEvent;
        public string StopEvent;
        public string SourceRelativePath;
    }

    public Entry[] Entries = Array.Empty<Entry>();

    public Entry Find(AudioActionDefinition definition)
    {
        if (definition == null || Entries == null)
        {
            return null;
        }

        for (int i = 0; i < Entries.Length; i++)
        {
            Entry entry = Entries[i];
            if (entry != null && entry.Definition == definition)
            {
                return entry;
            }
        }

        return null;
    }
}
