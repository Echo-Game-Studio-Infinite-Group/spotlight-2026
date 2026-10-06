using System;
using UnityEngine;

/// <summary>
/// Maps logical gameplay event ids to Wwise event references.
/// One-shot gameplay code uses WwiseEventBridge instead of Wwise APIs directly.
/// </summary>
[CreateAssetMenu(
    fileName = "WwiseEventBindings",
    menuName = "超高速行者/音频/Wwise 事件绑定")]
public sealed class WwiseEventBindings : ScriptableObject
{
    [Serializable]
    public sealed class Entry
    {
        public string EventId;
        public string WwiseEventName;
        public string Bank;
    }

    public Entry[] Entries = Array.Empty<Entry>();

    public Entry Find(string eventId)
    {
        if (string.IsNullOrEmpty(eventId) || Entries == null)
        {
            return null;
        }

        for (int i = 0; i < Entries.Length; i++)
        {
            Entry entry = Entries[i];
            if (entry != null &&
                string.Equals(
                    entry.EventId?.Trim(),
                    eventId.Trim(),
                    StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }
}
