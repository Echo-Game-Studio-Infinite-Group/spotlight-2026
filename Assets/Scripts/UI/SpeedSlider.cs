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
        _playerData = GameObject.FindWithTag("Player").GetComponent<PlayerMotor>();
        _maxSpeed = _playerData.Params.MaxSpeed;
    }
    void Update()
    {
        float percent = _playerData.HorizontalSpeed / _maxSpeed;
        _speedSlider.value = (percent / Mathf.Exp(percent - 1)) * (_arcBar == null ? 1 : _arcBar.maxValue) / 2;
    }
}
