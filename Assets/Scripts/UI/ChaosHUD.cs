using GameJam.Flow;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[DisallowMultipleComponent, RequireComponent(typeof(CanvasGroup))]
public sealed class ChaosHUD : MonoBehaviour
{
    [SerializeField] private RawImage[] _digits;
    [SerializeField] private TextMeshProUGUI _fallbackText;
    [SerializeField] private TextMeshProUGUI _stateText;
    [SerializeField] private TextMeshProUGUI _maximumText;

    private static ChaosHUD _active;
    private readonly Texture2D[] _textures = new Texture2D[10];
    private CanvasGroup _group;
    private ChaosService _chaos;
    private RunFlowConfig _config;

    public int DisplayedValue => _chaos == null ? 0 : _chaos.Value;
    public bool NixieDigitsReady => System.Array.TrueForAll(_textures, texture => texture != null);
    public static bool IsDisplaying(ChaosService chaos) => _active != null &&
        _active.isActiveAndEnabled && _active._group.alpha > 0f && ReferenceEquals(_active._chaos, chaos);

    private void OnEnable()
    {
        _active = this;
        _group = GetComponent<CanvasGroup>();
        _group.alpha = 0f;
        for (int i = 0; i < _textures.Length; i++)
            _textures[i] = Resources.Load<Texture2D>("UI/ChaosNixie/" + i);
        BindChaos();
    }

    private void Update() => BindChaos();

    private void BindChaos()
    {
        var flow = RunFlowController.Current;
        var chaos = flow != null && flow.isActiveAndEnabled ? flow.Chaos : null;
        if (ReferenceEquals(_chaos, chaos)) return;
        UnbindChaos();
        if (chaos == null) return;
        // 整局初始化晚于 HUD 创建，重开整局也会替换服务；这里只订阅当前流程的数据。
        _chaos = chaos;
        _config = flow.Config;
        _chaos.Changed += OnChaosChanged;
        _group.alpha = 1f;
        Refresh();
    }

    private void OnChaosChanged(int value, string reason) => Refresh();

    private void Refresh()
    {
        int value = _chaos.Value;
        bool useDigits = value >= 0 && value <= 999 && NixieDigitsReady;
        string number = value.ToString("000");
        for (int i = 0; i < _digits.Length; i++)
        {
            _digits[i].gameObject.SetActive(useDigits);
            if (useDigits) _digits[i].texture = _textures[number[i] - '0'];
        }
        _fallbackText.gameObject.SetActive(!useDigits);
        _fallbackText.text = number;
        _maximumText.text = "/ " + _config.MaximumChaos;
        _stateText.text = _config.EnableSpecialRoutes && value == _config.ExceptionalChaos ? "SPECIAL" :
            value < 0 ? "NEGATIVE" : value >= _config.MaximumChaos ? "LIMIT" : "CURRENT VALUE";
    }

    private void UnbindChaos()
    {
        if (_chaos != null) _chaos.Changed -= OnChaosChanged;
        _chaos = null;
        _config = null;
        _group.alpha = 0f;
    }

    private void OnDisable()
    {
        UnbindChaos();
        if (_active == this) _active = null;
    }
}
