using TMPro;
using UnityEngine;
using UnityEngine.UI;

// 存档槽位故障效果：背景走局部 UI Shader，文字用 RGB 副本 + 拖影副本。
// 空槽位完全关闭；有档槽位带低强度待机基线，混乱度越高故障越强。
// 方案 1：参数集中在本文件常量里，样式阶段再迁到预制体/配置资产。
public sealed class GlitchSlotEffect : MonoBehaviour
{
    private const float MaxChaos = 100f;
    private const float IntensityExponent = 1.35f;
    // 有档槽位的待机基线：混乱度 0 时也保留轻微故障，避免“完全看不出效果”。
    private const float BaseIntensity = 0.28f;
    private const float RgbSplit = 0.006f;
    private const float BlockShift = 0.035f;
    private const float BlurStrength = 0.006f;
    private const float ScanlineStrength = 0.28f;
    private const float NoiseStrength = 0.35f;
    private const float BorderWidth = 2f;
    private const float TextOffsetX = 1.8f;
    private const float TextOffsetY = 0.6f;
    private const float GhostOffsetX = 3.6f;
    private const float TextAlpha = 0.5f;
    private const float GhostAlpha = 0.18f;
    private const float SmoothSpeed = 8f;

    private static readonly int IntensityId = Shader.PropertyToID("_Intensity");
    private static readonly int TimeId = Shader.PropertyToID("_UnscaledTime");
    private static readonly int SeedId = Shader.PropertyToID("_Seed");
    private static readonly int RgbSplitId = Shader.PropertyToID("_RgbSplit");
    private static readonly int BlockShiftId = Shader.PropertyToID("_BlockStrength");
    private static readonly int BlurId = Shader.PropertyToID("_BlurStrength");
    private static readonly int ScanlineId = Shader.PropertyToID("_ScanlineStrength");
    private static readonly int NoiseId = Shader.PropertyToID("_NoiseStrength");
    private static readonly int BorderWidthId = Shader.PropertyToID("_BorderWidth");
    private static readonly int RectSizeId = Shader.PropertyToID("_RectSize");

    private Image _background;
    private TMP_Text _source;
    private Material _material;
    private TMP_Text _redCopy;
    private TMP_Text _blueCopy;
    private TMP_Text _ghostCopy;
    private string _cachedText;
    private float _targetIntensity;
    private float _intensity;
    private float _time;
    private float _seed;
    private bool _copiesActive;
    private bool _loggedState;

    public static GlitchSlotEffect Attach(Image background, TMP_Text source, int index)
    {
        if (background == null) return null;
        GlitchSlotEffect effect = background.gameObject.AddComponent<GlitchSlotEffect>();
        effect.Initialize(background, source, index);
        return effect;
    }

    private void Initialize(Image background, TMP_Text source, int index)
    {
        _background = background;
        _source = source;
        _seed = index * 17.31f;

        Shader shader = Resources.Load<Shader>("Shaders/GlitchSlotUI");
        if (shader == null) shader = Shader.Find("UI/GlitchSlotUI");
        if (shader == null)
        {
            Debug.LogWarning("[GlitchSlotEffect] 找不到 UI/GlitchSlotUI，槽位故障效果已关闭");
            enabled = false;
            return;
        }

        _material = new Material(shader) { name = "GlitchSlotUI_Runtime" };
        _material.SetFloat(RgbSplitId, RgbSplit);
        _material.SetFloat(BlockShiftId, BlockShift);
        _material.SetFloat(BlurId, BlurStrength);
        _material.SetFloat(ScanlineId, ScanlineStrength);
        _material.SetFloat(NoiseId, NoiseStrength);
        _material.SetFloat(BorderWidthId, BorderWidth);
        _material.SetFloat(SeedId, _seed);
        _material.SetVector(RectSizeId, background.rectTransform.rect.size);
        background.material = _material;

        CreateTextCopies();
        Apply(0f);
    }

    // hasSave=false 时强制关闭；有档槽位用“基线 + 混乱度曲线”，混乱度越高越强。
    public void SetSlotState(bool hasSave, int chaos)
    {
        if (!hasSave)
        {
            _targetIntensity = 0f;
            return;
        }
        float normalized = Mathf.Clamp01(chaos / MaxChaos);
        _targetIntensity = BaseIntensity + (1f - BaseIntensity) * Mathf.Pow(normalized, IntensityExponent);
        if (!_loggedState)
        {
            _loggedState = true;
            Debug.Log($"[GlitchSlotEffect] {name}: hasSave={hasSave}, chaos={chaos}, target={_targetIntensity:0.00}");
        }
    }

    private void Update()
    {
        if (_source == null) return;
        string current = _source.text;
        if (_cachedText != current)
        {
            _cachedText = current;
            SyncCopyText();
        }

        _time += Time.unscaledDeltaTime;
        _intensity = Mathf.MoveTowards(_intensity, _targetIntensity, Time.unscaledDeltaTime * SmoothSpeed);
        Apply(_intensity);
    }

    private void Apply(float intensity)
    {
        if (_material != null)
        {
            _material.SetFloat(IntensityId, intensity);
            _material.SetFloat(TimeId, _time);
        }

        bool active = intensity > 0.001f;
        if (active != _copiesActive)
        {
            _copiesActive = active;
            if (active) SyncCopyText();
            SetCopiesActive(active);
        }
        if (!active) return;

        float step = Mathf.Floor(_time * 9f);
        float jitterX = (Hash(step + _seed) - 0.5f) * 2f * TextOffsetX * intensity;
        float jitterY = (Hash(step * 1.7f + _seed + 3.1f) - 0.5f) * 2f * TextOffsetY * intensity;
        SetCopy(_redCopy, -jitterX, jitterY, new Color(1f, 0.15f, 0.2f, TextAlpha * intensity));
        SetCopy(_blueCopy, jitterX, -jitterY, new Color(0.2f, 0.45f, 1f, TextAlpha * intensity));
        SetCopy(_ghostCopy, GhostOffsetX * intensity, 0f, new Color(1f, 1f, 1f, GhostAlpha * intensity));
    }

    private void CreateTextCopies()
    {
        if (_source == null) return;
        _redCopy = CreateCopy("GlitchR");
        _blueCopy = CreateCopy("GlitchB");
        _ghostCopy = CreateCopy("GlitchGhost");
        _cachedText = _source.text;
    }

    private TMP_Text CreateCopy(string name)
    {
        GameObject clone = Instantiate(_source.gameObject, _source.transform.parent);
        clone.name = name;
        // 插到原文字之前，保证原文字始终在最上层、可读性优先
        clone.transform.SetSiblingIndex(_source.transform.GetSiblingIndex());
        TMP_Text copy = clone.GetComponent<TMP_Text>();
        copy.raycastTarget = false;
        copy.color = new Color(1f, 1f, 1f, 0f);
        copy.text = _source.text;
        copy.gameObject.SetActive(false);
        return copy;
    }

    private void SyncCopyText()
    {
        if (_redCopy != null) _redCopy.text = _cachedText;
        if (_blueCopy != null) _blueCopy.text = _cachedText;
        if (_ghostCopy != null) _ghostCopy.text = _cachedText;
    }

    private void SetCopiesActive(bool active)
    {
        if (_redCopy != null) _redCopy.gameObject.SetActive(active);
        if (_blueCopy != null) _blueCopy.gameObject.SetActive(active);
        if (_ghostCopy != null) _ghostCopy.gameObject.SetActive(active);
    }

    private void SetCopy(TMP_Text copy, float offsetX, float offsetY, Color color)
    {
        if (copy == null) return;
        RectTransform sourceRect = (RectTransform)_source.transform;
        RectTransform copyRect = (RectTransform)copy.transform;
        copyRect.localPosition = sourceRect.localPosition + new Vector3(offsetX, offsetY, 0f);
        copy.color = color;
    }

    private static float Hash(float value)
    {
        value = Mathf.Sin(value * 12.9898f + 78.233f) * 43758.5453f;
        return value - Mathf.Floor(value);
    }

    private void OnDestroy()
    {
        if (_background != null && _material != null) _background.material = null;
        if (_material != null) Destroy(_material);
    }
}
