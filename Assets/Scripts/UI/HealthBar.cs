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
        _playerData = GameObject.FindWithTag("Player").GetComponent<HealthComponent>();
        _healthBar = GetComponent<Slider>();
        _fill = GetComponentInChildren<Image>();
        _maxHealth = _playerData.MaxHealth;
    }
    void Update()
    {
        _value = _playerData.Health / _maxHealth;
        _healthBar.value = _value;
        if (_value <= 0.35f)
        {
            _fill.color = _high;
        }
        else
        {
            _fill.color = _low;
        }
    }
}
