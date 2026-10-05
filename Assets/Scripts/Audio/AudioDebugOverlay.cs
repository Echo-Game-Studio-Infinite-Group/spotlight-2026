using UnityEngine;

public sealed class AudioDebugOverlay : MonoBehaviour
{
    public bool Visible { get; set; } = true;

    private AudioSystem _audio;
    private GUIStyle _title;
    private GUIStyle _label;

    private void Awake()
    {
        _audio = GetComponent<AudioSystem>();
    }

    private void EnsureStyles()
    {
        if (_title != null) return;
        _title = new GUIStyle(GUI.skin.label)
        {
            fontSize = 14,
            fontStyle = FontStyle.Bold,
            normal = { textColor = new Color(0.95f, 0.88f, 0.45f) }
        };
        _label = new GUIStyle(GUI.skin.label)
        {
            fontSize = 11,
            normal = { textColor = Color.white }
        };
    }

    private void OnGUI()
    {
        if (!Visible || _audio == null) return;
        EnsureStyles();
        GUILayout.BeginArea(new Rect(Screen.width - 350f, 12f, 338f, 520f), GUI.skin.box);
        GUILayout.Label("Audio System · F3", _title);
        GUILayout.Label("Continuous actions", _title);

        int visible = 0;
        var voices = _audio.ActionVoices;
        for (int i = 0; i < voices.Count; i++)
        {
            ActionAudioVoice voice = voices[i];
            if (voice == null || !voice.IsActive) continue;
            visible++;
            ActionControlFrame frame = voice.Control;
            GUILayout.Label(
                $"[{voice.name}] {voice.State}  t={frame.ActionElapsed:0.00}  v={frame.NormalizedSpeed:0.00}  LP={voice.LowpassHz:0}Hz",
                _label);
            DrawMeter("Gain", voice.EnvelopeGain, new Color(0.3f, 0.9f, 0.45f));
            DrawMeter("Pitch Follow", voice.PitchEnvelopeValue, new Color(0.35f, 0.7f, 1f));
            DrawMeter("Lowpass Follow", voice.LowpassEnvelopeValue, new Color(1f, 0.72f, 0.3f));
        }

        WwiseActionDriver[] wwiseDrivers =
            FindObjectsOfType<WwiseActionDriver>();
        for (int i = 0; i < wwiseDrivers.Length; i++)
        {
            WwiseActionDriver driver = wwiseDrivers[i];
            DrawWwiseSnapshot(driver.SlideDebug, ref visible);
            DrawWwiseSnapshot(driver.WallSlideDebug, ref visible);
        }

        if (visible == 0) GUILayout.Label("no active action voice", _label);

        GUILayout.Space(6f);
        GUILayout.Label("Keyboard: F3 via PlayerInputReader", _label);
        GUILayout.EndArea();
    }

    private void DrawWwiseSnapshot(
        WwiseActionDriver.DebugSnapshot snapshot,
        ref int visible)
    {
        if (!snapshot.Active) return;
        visible++;
        GUILayout.Label(
            $"[Wwise {snapshot.Name}] {snapshot.State}  " +
            $"t={snapshot.Elapsed:0.00}  v={snapshot.NormalizedSpeed:0.00}  " +
            $"LP={snapshot.LowpassHz:0}Hz",
            _label);
        DrawMeter(
            "Gain",
            snapshot.EnvelopeGain,
            new Color(0.3f, 0.9f, 0.45f));
        DrawMeter(
            "Pitch Follow",
            snapshot.PitchEnvelopeValue,
            new Color(0.35f, 0.7f, 1f));
        DrawMeter(
            "Lowpass Follow",
            snapshot.LowpassEnvelopeValue,
            new Color(1f, 0.72f, 0.3f));
    }

    private void DrawMeter(string label, float value, Color color)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, _label, GUILayout.Width(92f));
        Rect rect = GUILayoutUtility.GetRect(180f, 10f, GUILayout.ExpandWidth(false));
        EditorGUIDrawMeter(rect, value, color);
        GUILayout.Label(value.ToString("0.00"), _label, GUILayout.Width(36f));
        GUILayout.EndHorizontal();
    }

    private static void EditorGUIDrawMeter(Rect rect, float value, Color color)
    {
        Color previous = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.65f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = color;
        Rect fill = rect;
        fill.width = Mathf.Clamp01(value) * rect.width;
        GUI.DrawTexture(fill, Texture2D.whiteTexture);
        GUI.color = previous;
    }
}
