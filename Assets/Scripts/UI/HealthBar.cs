using UnityEngine;
using UnityEngine.UI;

public class HealthBar : MonoBehaviour
{
    [SerializeField] private Color _high = new Color(0.168498f, 0.759f, 0.2472316f, 1.0f);
    [SerializeField] private Color _low = new Color(0.7607843f, 0.1686274f, 0.1686274f, 1.0f);

    private HealthComponent _playerData;
    private Slider _healthBar;
    private Image _fill;
    private float _maxHealth;
    private float _value;

    private void Awake()
    {
        _playerData = FindObjectOfType<PlayerMotor>().GetComponent<HealthComponent>();
        _healthBar = GetComponent<Slider>();
        _fill = _healthBar.fillRect.GetComponent<Image>();
        _maxHealth = _playerData.MaxHealth;
    }
    void Update()
    {
        _value = _maxHealth > 0f ? Mathf.Clamp01(_playerData.Health / _maxHealth) : 0f;
        _healthBar.value = _value;
        if (_value <= 0.35f)
        {
            _fill.color = _low;
        }
        else
        {
            _fill.color = _high;
        }
    }
}
