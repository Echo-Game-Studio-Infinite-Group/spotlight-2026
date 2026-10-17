using UnityEngine;
using UnityEngine.UI;

public class SpeedSlider : MonoBehaviour
{
    private PlayerMotor _playerData;
    private Slider _speedSlider;
    private ArcBar _arcBar;
    private float _maxSpeed;

    private void Awake()
    {
        _speedSlider = GetComponent<Slider>();
        _arcBar = GetComponent<ArcBar>();
        _playerData = FindObjectOfType<PlayerMotor>();
        _maxSpeed = _playerData.Params.MaxSpeed;
    }
    void Update()
    {
        float percent = _maxSpeed > 0f ? Mathf.Clamp01(_playerData.HorizontalSpeed / _maxSpeed) : 0f;
        _speedSlider.value = percent * (_arcBar == null ? 1f : _arcBar.maxValue);
    }
}
