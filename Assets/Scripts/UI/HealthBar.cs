using UnityEngine;
using UnityEngine.UI;

public class HealthBar : MonoBehaviour
{
    private PlayerData _playerData;
    private Slider _healthBar;
    private Image _fill;
    private Color _high = new Color(0.168498f, 0.759f, 0.2472316f, 1.0f);
    private Color _low = new Color(0.7607843f, 0.1686274f, 0.1686274f, 1.0f);
    private float _value;

    private void Awake()
    {
        _playerData = GameManager.Instance.Player;
        _healthBar = GetComponent<Slider>();
        _fill = GetComponentInChildren<Image>();
    }
    void Update()
    {
        _value = _playerData.Health / _playerData.MaxHealth;
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
