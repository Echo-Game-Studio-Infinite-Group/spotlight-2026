using UnityEngine;

[DisallowMultipleComponent]
public sealed class ChaosHUD : MonoBehaviour
{
    private const float ReferenceWidth = 1920f;
    private const float ReferenceHeight = 1080f;
    private const float PanelWidth = 408f;
    private const float PanelHeight = 154f;
    private const float PanelMargin = 28f;
    private const float ChangeFeedbackSeconds = 0.9f;

    [Header("正式 HUD")]
    [SerializeField] private bool _visible = true;
    [SerializeField] private Color _panelColor = new Color(0.035f, 0.047f, 0.058f, 0.94f);
    [SerializeField] private Color _lineColor = new Color(0.56f, 0.59f, 0.60f, 0.88f);
    [SerializeField] private Color _accentColor = new Color(0.08f, 0.72f, 0.94f, 1f);
    [SerializeField] private Color _warningColor = new Color(1f, 0.31f, 0.22f, 1f);
    [SerializeField, Range(5, 30)] private int _segmentCount = 20;

    [Header("辉光管数码")]
    [SerializeField] private bool _useNixieDigits = true;
    [SerializeField, Range(72f, 116f)] private float _nixieDigitHeight = 112f;

    [Header("联调工具")]
    [SerializeField] private bool _showIntegrationHint;
    [SerializeField] private bool _hideAudioDebugOverlay = true;

    private AudioDebugOverlay _audioOverlay;
    private bool _audioWasEnabled;
    private bool _toolsExpanded;
    private PlayerInputReader _toolInput;
    private bool _gameplayWasEnabled;
    private ChaosManager _chaos;
    private float _displayedValue;
    private float _displayVelocity;
    private int _lastDelta;
    private float _changeFeedbackRemaining;
    private Texture2D _panelTexture;
    private Texture2D _whiteTexture;
    private Font _font;
    private GUIStyle _titleStyle;
    private GUIStyle _englishStyle;
    private GUIStyle _numberStyle;
    private GUIStyle _maxStyle;
    private GUIStyle _deltaStyle;
    private GUIStyle _microStyle;
    private readonly Texture2D[] _nixieDigits = new Texture2D[10];

    public int DisplayedValue => Mathf.RoundToInt(_displayedValue);
    public bool NixieDigitsReady
    {
        get
        {
            for (int i = 0; i < _nixieDigits.Length; i++)
                if (_nixieDigits[i] == null) return false;
            return true;
        }
    }

    private void OnEnable()
    {
        BuildVisualResources();
        LoadNixieDigits();
        BindChaos();
        _audioOverlay = FindObjectOfType<AudioDebugOverlay>();
        if (_hideAudioDebugOverlay && _audioOverlay != null)
        {
            _audioWasEnabled = _audioOverlay.enabled;
            _audioOverlay.enabled = false;
        }
    }

    private void OnDisable()
    {
        UnbindChaos();
        if (_hideAudioDebugOverlay && _audioOverlay != null) _audioOverlay.enabled = _audioWasEnabled;
        RestoreInput();
        _toolsExpanded = false;
    }

    private void OnDestroy()
    {
        if (_panelTexture != null) Destroy(_panelTexture);
        if (_whiteTexture != null) Destroy(_whiteTexture);
    }

    private void Update()
    {
        BindChaos();
        if (_chaos == null) return;

        float deltaTime = TimeManager.UnscaledDeltaTime;
        float target = _chaos.Value;
        if (_chaos.IsSpecial || _chaos.IsNegative)
        {
            _displayedValue = target;
            _displayVelocity = 0f;
        }
        else
        {
            _displayedValue = Mathf.SmoothDamp(
                _displayedValue, target, ref _displayVelocity, 0.16f, Mathf.Infinity, deltaTime);
        }

        _changeFeedbackRemaining = Mathf.Max(0f, _changeFeedbackRemaining - deltaTime);
    }

    private void BindChaos()
    {
        if (_chaos != null || !ChaosManager.Exists) return;
        _chaos = ChaosManager.Instance;
        _displayedValue = _chaos.Value;
        _displayVelocity = 0f;
        _chaos.Changed += OnChaosChanged;
        _chaos.RunReset += OnRunReset;
    }

    private void UnbindChaos()
    {
        if (_chaos == null) return;
        _chaos.Changed -= OnChaosChanged;
        _chaos.RunReset -= OnRunReset;
        _chaos = null;
    }

    private void OnChaosChanged(int previous, int current)
    {
        _lastDelta = current - previous;
        _changeFeedbackRemaining = ChangeFeedbackSeconds;
    }

    private void OnRunReset()
    {
        if (_chaos == null) return;
        _displayedValue = _chaos.Value;
        _displayVelocity = 0f;
        _lastDelta = 0;
        _changeFeedbackRemaining = 0f;
    }

    private void OnGUI()
    {
        if (!_visible || _chaos == null) return;
        EnsureStyles();

        Matrix4x4 previousMatrix = GUI.matrix;
        Color previousColor = GUI.color;
        float scale = Mathf.Min(Screen.width / ReferenceWidth, Screen.height / ReferenceHeight);
        scale = Mathf.Max(0.5f, scale);
        GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));

        float canvasWidth = Screen.width / scale;
        Rect panel = new Rect(canvasWidth - PanelMargin - PanelWidth, PanelMargin, PanelWidth, PanelHeight);
        DrawOfficialHud(panel);
        if (_showIntegrationHint) DrawIntegrationTools(panel);

        GUI.color = previousColor;
        GUI.matrix = previousMatrix;
    }

    private void DrawOfficialHud(Rect panel)
    {
        int value = DisplayedValue;
        string number = _chaos.IsSpecial ? "999" : value.ToString("000");
        bool canUseNixieDigits = _useNixieDigits && NixieDigitsReady && value >= 0;
        if (canUseNixieDigits)
            DrawNixieNumber(new Rect(panel.x, panel.y, panel.width, _nixieDigitHeight), value);
        else
            GUI.Label(new Rect(panel.x, panel.y + 12f, panel.width, 64f), number, _numberStyle);

        GUI.Label(new Rect(panel.x + 4f, panel.y + 122f, 76f, 20f), "混乱度", _microStyle);
        GUI.Label(new Rect(panel.x + 84f, panel.y + 122f, 134f, 20f), ChaosStateLabel(), _englishStyle);
        GUI.Label(new Rect(panel.x + 310f, panel.y + 122f, 90f, 20f), "/ " + _chaos.NormalMaximum, _maxStyle);
    }

    private string ChaosStateLabel()
    {
        if (_chaos.IsSpecial) return "SPECIAL";
        if (_chaos.IsNegative) return "NEGATIVE";
        if (_chaos.NormalGoalReached) return "LIMIT";
        return "CURRENT VALUE";
    }

    private void DrawSegmentBar(Rect rect)
    {
        int count = Mathf.Max(1, _segmentCount);
        float gap = 3f;
        float segmentWidth = (rect.width - gap * (count - 1)) / count;
        float ratio = _chaos.IsSpecial ? 1f : Mathf.Clamp01(_displayedValue / Mathf.Max(1f, _chaos.NormalMaximum));
        float litSegments = ratio * count;

        for (int i = 0; i < count; i++)
        {
            Rect segment = new Rect(rect.x + i * (segmentWidth + gap), rect.y, segmentWidth, rect.height);
            DrawRect(segment, new Color(0.20f, 0.23f, 0.25f, 0.78f));
            float amount = Mathf.Clamp01(litSegments - i);
            if (amount <= 0f) continue;

            Rect fill = new Rect(segment.x, segment.y, segment.width * amount, segment.height);
            Color color = _chaos.IsNegative ? _warningColor : _accentColor;
            DrawGlowRect(fill, color, 2.5f, 0.20f);
        }
    }

    private void DrawNixieNumber(Rect rect, int value)
    {
        string digits = Mathf.Clamp(value, 0, 999).ToString("000");
        const float gap = 6f;
        const float visibleUvX = 0.30f;
        const float visibleUvY = 0.02f;
        const float visibleUvWidth = 0.40f;
        const float visibleUvHeight = 0.96f;
        float digitWidth = rect.height * visibleUvWidth / visibleUvHeight;
        float totalWidth = digitWidth * digits.Length + gap * (digits.Length - 1);
        float startX = rect.xMax - totalWidth;
        Rect visibleUv = new Rect(visibleUvX, visibleUvY, visibleUvWidth, visibleUvHeight);

        // 原图两侧有大量透明像素；只裁透明边缘，管体不染色、不拉伸，也不会互相重叠。
        for (int i = 0; i < digits.Length; i++)
        {
            int digit = digits[i] - '0';
            Rect digitRect = new Rect(startX + i * (digitWidth + gap), rect.y, digitWidth, rect.height);
            GUI.DrawTextureWithTexCoords(digitRect, _nixieDigits[digit], visibleUv, true);
        }
    }

    private void BuildVisualResources()
    {
        if (_whiteTexture == null)
        {
            _whiteTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                name = "ChaosHUD_White",
                hideFlags = HideFlags.HideAndDontSave
            };
            _whiteTexture.SetPixel(0, 0, Color.white);
            _whiteTexture.Apply();
        }

        if (_panelTexture != null) Destroy(_panelTexture);
        _panelTexture = CreatePanelTexture(880, 252, 25);
    }

    private void LoadNixieDigits()
    {
        for (int i = 0; i < _nixieDigits.Length; i++)
            _nixieDigits[i] = Resources.Load<Texture2D>("UI/ChaosNixie/" + i);
    }

    private Texture2D CreatePanelTexture(int width, int height, int cut)
    {
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
        {
            name = "ChaosHUD_Panel",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave
        };
        var pixels = new Color32[width * height];
        Color32 clear = new Color32(0, 0, 0, 0);
        Color32 border = _lineColor;

        for (int y = 0; y < height; y++)
        {
            int edgeInset = Mathf.Max(0, Mathf.Max(cut - y, cut - (height - 1 - y)));
            for (int x = 0; x < width; x++)
            {
                bool inside = x >= edgeInset && x <= width - 1 - edgeInset;
                if (!inside) { pixels[y * width + x] = clear; continue; }
                bool isBorder = y < 3 || y >= height - 3 ||
                    x < edgeInset + 3 || x > width - 1 - edgeInset - 3;
                if (isBorder) { pixels[y * width + x] = border; continue; }

                float rightShade = Mathf.InverseLerp(width * 0.68f, width, x);
                Color shaded = Color.Lerp(_panelColor, new Color(0.12f, 0.12f, 0.115f, _panelColor.a), rightShade * 0.52f);
                pixels[y * width + x] = shaded;
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply(false, false);
        return texture;
    }

    private void EnsureStyles()
    {
        if (_font == null)
        {
            _font = Font.CreateDynamicFontFromOSFont(
                new[] { "Microsoft YaHei UI", "Microsoft YaHei", "HarmonyOS Sans SC", "Arial" }, 64);
        }
        if (_titleStyle != null) return;

        _titleStyle = NewStyle(26, FontStyle.Normal, new Color(0.93f, 0.94f, 0.94f, 1f), TextAnchor.MiddleLeft);
        _englishStyle = NewStyle(10, FontStyle.Normal, new Color(0.63f, 0.67f, 0.69f, 1f), TextAnchor.MiddleLeft);
        _numberStyle = NewStyle(35, FontStyle.Normal, new Color(0.95f, 0.96f, 0.96f, 1f), TextAnchor.MiddleRight);
        _maxStyle = NewStyle(11, FontStyle.Normal, new Color(0.66f, 0.69f, 0.70f, 1f), TextAnchor.MiddleRight);
        _deltaStyle = NewStyle(17, FontStyle.Normal, _accentColor, TextAnchor.MiddleRight);
        _microStyle = NewStyle(9, FontStyle.Normal, new Color(0.55f, 0.59f, 0.60f, 1f), TextAnchor.MiddleLeft);
    }

    private GUIStyle NewStyle(int size, FontStyle style, Color color, TextAnchor alignment)
    {
        var result = new GUIStyle(GUI.skin.label)
        {
            font = _font,
            fontSize = size,
            fontStyle = style,
            alignment = alignment,
            clipping = TextClipping.Clip,
            richText = false
        };
        result.normal.textColor = color;
        return result;
    }

    private void DrawTrackedText(Rect rect, string text, GUIStyle style, float tracking)
    {
        float totalWidth = 0f;
        for (int i = 0; i < text.Length; i++)
            totalWidth += style.CalcSize(new GUIContent(text[i].ToString())).x + (i < text.Length - 1 ? tracking : 0f);

        float x = rect.x;
        if (style.alignment == TextAnchor.MiddleCenter) x += (rect.width - totalWidth) * 0.5f;
        else if (style.alignment == TextAnchor.MiddleRight) x += rect.width - totalWidth;

        for (int i = 0; i < text.Length; i++)
        {
            string character = text[i].ToString();
            float width = style.CalcSize(new GUIContent(character)).x;
            GUI.Label(new Rect(x, rect.y, width + 2f, rect.height), character, style);
            x += width + tracking;
        }
    }

    private void DrawRect(Rect rect, Color color)
    {
        Color previous = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, _whiteTexture, ScaleMode.StretchToFill, true);
        GUI.color = previous;
    }

    private void DrawGlowRect(Rect rect, Color color, float radius, float glowAlpha)
    {
        Color glow = color;
        glow.a = glowAlpha * color.a;
        DrawRect(new Rect(rect.x - radius, rect.y - radius, rect.width + radius * 2f, rect.height + radius * 2f), glow);
        DrawRect(rect, color);
    }

    private void ToggleTools()
    {
        _toolsExpanded = !_toolsExpanded;
        if (!_toolsExpanded) { RestoreInput(); return; }
        _toolInput = Player.Current != null ? Player.Current.GetComponent<PlayerInputReader>() : null;
        if (_toolInput == null) return;
        _gameplayWasEnabled = _toolInput.GameplayEnabled;
        _toolInput.SetGameplayEnabled(false);
    }

    private void RestoreInput()
    {
        if (_toolInput != null) _toolInput.SetGameplayEnabled(_gameplayWasEnabled);
        _toolInput = null;
    }

    private void DrawIntegrationTools(Rect panel)
    {
        Rect toggle = new Rect(panel.x + panel.width - 126f, panel.y + panel.height + 5f, 126f, 24f);
        if (GUI.Button(toggle, _toolsExpanded ? "收起联调" : "展开联调")) ToggleTools();
        if (!_toolsExpanded) return;

        float left = panel.x + panel.width - 310f;
        GUI.Box(new Rect(left, panel.y + panel.height + 33f, 310f, 158f), GUIContent.none);
        float buttonWidth = 94f;
        if (GUI.Button(new Rect(left + 4f, panel.y + 163f, buttonWidth, 28f), "设为 0")) _chaos.SetNormalValue(0);
        if (GUI.Button(new Rect(left + 104f, panel.y + 163f, buttonWidth, 28f), "设为 35")) _chaos.SetNormalValue(35);
        if (GUI.Button(new Rect(left + 204f, panel.y + 163f, buttonWidth, 28f), "设为 85")) _chaos.SetNormalValue(85);
        Player player = Player.Current;
        ChaosRunController run = player != null ? player.GetComponent<ChaosRunController>() : null;
        if (run == null) return;
        if (GUI.Button(new Rect(left + 4f, panel.y + 197f, 302f, 28f), "回记录点并刷新差分")) run.RequestCheckpointReturn();
        if (GUI.Button(new Rect(left + 4f, panel.y + 231f, 302f, 28f), "模拟致死：混乱度减 10"))
            player.Health.TakeDamage(player.Health.MaxHealth);
    }
}
