using TMPro;
using UnityEngine;
using UnityEngine.UI;

// 局内 HUD 的数值绑定。布局与图形全部在 HUD.prefab 的 HudRoot 下手工搭好，
// 运行时只实例化 CRT 材质并把能量/血量/速度写进对应图形。
public sealed class PlayerHudView : MonoBehaviour
{
    [Header("预制体引用")]
    [SerializeField] private Transform _hudRoot;
    [SerializeField] private ArcGraphic _energyFill;
    [SerializeField] private SegmentBarGraphic _speedSegments;
    [SerializeField] private EcgGraphic _healthEcg;
    [SerializeField] private TMP_Text _healthText;
    [SerializeField] private TMP_Text _speedText;
    [SerializeField] private Image _heart;

    [Header("CRT 材质")]
    [SerializeField] private Material _textMaterial;
    [SerializeField] private Material _heartMaterial;

    private Player _player;
    private HealthComponent _health;
    private IEnergyAccount _energy;
    private PlayerMotor _motor;
    private float _nextPlayerSearch;

    private Material _textInstance;
    private Material _heartInstance;

    private float _lastHealth = -1f;
    private float _lastMaxHealth = -1f;
    private float _lastSpeed = -1f;

    private void Awake()
    {
        _textInstance = new Material(_textMaterial);
        _heartInstance = new Material(_heartMaterial);

        TMP_Text[] labels = _hudRoot.GetComponentsInChildren<TMP_Text>(true);
        for (int i = 0; i < labels.Length; i++)
        {
            labels[i].fontSharedMaterial = _textInstance;
        }

        _heart.material = _heartInstance;
    }

    private void Update()
    {
        ResolvePlayer();

        float now = Time.unscaledTime;
        // 菜单使用的 CRT 材质靠外部时钟驱动；HUD 用非缩放时间，暂停时也保持扫描线流动。
        _textInstance.SetFloat("_Clock", now);
        _textInstance.SetFloat("_CRTMenuUnscaledClock", now);
        _textInstance.SetFloat("_Glitch", Mathf.Repeat(now, 5.3f) < 0.07f ? 0.16f : 0f);
        _heartInstance.SetFloat("_Clock", now);
        _heartInstance.SetFloat("_CRTMenuUnscaledClock", now);
        _heartInstance.SetFloat("_Glitch", Mathf.Repeat(now + 2.1f, 4.1f) < 0.06f ? 0.18f : 0f);

        if (_health != null)
        {
            float health = _health.Health;
            float maxHealth = _health.MaxHealth;
            float ratio = maxHealth > 0f ? Mathf.Clamp01(health / maxHealth) : 0f;
            _healthEcg.SetHealthRatio(ratio);
            if (!Mathf.Approximately(health, _lastHealth) || !Mathf.Approximately(maxHealth, _lastMaxHealth))
            {
                _lastHealth = health;
                _lastMaxHealth = maxHealth;
                _healthText.text = $"{Mathf.CeilToInt(health)} / {Mathf.CeilToInt(maxHealth)}";
            }
        }

        if (_energy != null)
        {
            float maxEnergy = _energy.MaxEnergy;
            _energyFill.SetFill(maxEnergy > 0f ? Mathf.Clamp01(_energy.CurrentEnergy / maxEnergy) : 0f);
        }

        if (_motor != null)
        {
            float maxSpeed = _motor.Params != null ? _motor.Params.MaxSpeed : 0f;
            float speed = _motor.HorizontalSpeed;
            _speedSegments.SetRatio(maxSpeed > 0f ? Mathf.Clamp01(speed / maxSpeed) : 0f);
            if (Mathf.Abs(speed - _lastSpeed) >= 0.05f)
            {
                _lastSpeed = speed;
                _speedText.text = $"{speed:0.0} <size=44%>m/s</size>";
            }
        }
    }

    private void OnDestroy()
    {
        if (_textInstance != null) Destroy(_textInstance);
        if (_heartInstance != null) Destroy(_heartInstance);
    }

    private void ResolvePlayer()
    {
        if (_player != null) return;
        if (Time.unscaledTime < _nextPlayerSearch) return;
        _nextPlayerSearch = Time.unscaledTime + 0.5f;

        _player = Player.Current;
        if (_player == null) return;
        _health = _player.Health;
        _motor = _player.Motor;
        _energy = _player.GetComponent<IEnergyAccount>();
    }
}
