using UnityEngine;

/// <summary>
/// Thin logical-event facade for one-shot Wwise events and common controls.
/// It does not render PCM and is separate from the dynamic action source path.
/// </summary>
public static class WwiseEventBridge
{
    private const string BindingsResourcePath = "Audio/WwiseEventBindings";

    private static WwiseEventBindings _bindings;
    private static bool _missingBindingsLogged;
    private static bool _missingEventLogged;

    public static uint Play(
        string eventId,
        GameObject emitter)
    {
        WwiseEventBindings.Entry entry = Find(eventId);
        if (entry == null || string.IsNullOrEmpty(entry.WwiseEventName))
        {
            LogMissingEvent(eventId);
            return AkUnitySoundEngine.AK_INVALID_PLAYING_ID;
        }

        string bank = entry.Bank?.Trim();
        if (!string.IsNullOrEmpty(bank))
        {
            AkBankManager.LoadBank(bank, false, false);
        }

        string eventName = entry.WwiseEventName?.Trim();
        return AkUnitySoundEngine.PostEvent(eventName, emitter);
    }

    public static void Stop(
        string eventId,
        GameObject emitter,
        int transitionDurationMs = 0)
    {
        WwiseEventBindings.Entry entry = Find(eventId);
        if (entry == null || string.IsNullOrEmpty(entry.WwiseEventName))
        {
            LogMissingEvent(eventId);
            return;
        }

        AkUnitySoundEngine.ExecuteActionOnEvent(
            entry.WwiseEventName?.Trim(),
            AkActionOnEventType.AkActionOnEventType_Stop,
            emitter,
            transitionDurationMs);
    }

    public static bool HasEvent(string eventId)
    {
        WwiseEventBindings.Entry entry = Find(eventId);
        return entry != null &&
               !string.IsNullOrEmpty(entry.WwiseEventName?.Trim());
    }

    public static void Reload()
    {
        _bindings = Resources.Load<WwiseEventBindings>(
            BindingsResourcePath);
        _missingBindingsLogged = false;
        _missingEventLogged = false;
    }

    public static void SetSwitch(
        string switchGroup,
        string switchState,
        GameObject emitter)
    {
        if (string.IsNullOrEmpty(switchGroup) ||
            string.IsNullOrEmpty(switchState))
        {
            return;
        }

        AkUnitySoundEngine.SetSwitch(
            switchGroup,
            switchState,
            emitter);
    }

    public static void SetRTPC(
        string rtpcName,
        float value,
        GameObject emitter)
    {
        if (string.IsNullOrEmpty(rtpcName)) return;
        AkUnitySoundEngine.SetRTPCValue(
            rtpcName,
            value,
            emitter);
    }

    private static WwiseEventBindings.Entry Find(string eventId)
    {
        if (_bindings == null)
        {
            _bindings = Resources.Load<WwiseEventBindings>(
                BindingsResourcePath);
        }

        if (_bindings == null)
        {
            if (!_missingBindingsLogged)
            {
                _missingBindingsLogged = true;
                Debug.LogWarning(
                    $"Wwise event bindings not found at Resources/{BindingsResourcePath}.asset");
            }
            return null;
        }

        return _bindings.Find(eventId);
    }

    private static void LogMissingEvent(string eventId)
    {
        if (_missingEventLogged) return;
        _missingEventLogged = true;
        Debug.LogWarning(
            $"Wwise event binding is missing or invalid: {eventId}");
    }
}
