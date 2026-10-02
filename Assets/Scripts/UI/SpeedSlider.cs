using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class SpeedSlider : MonoBehaviour
{
    private PlayerMotor _playerMotor;
    private Slider _speedSlider;
    private ArcBar _arcBar;

    public GameObject player;

    private void Awake()
    {
        _playerMotor = player.GetComponent<PlayerMotor>();
        _speedSlider = GetComponent<Slider>();
        _arcBar = GetComponent<ArcBar>();
    }
    void Update()
    {
        float speed = _playerMotor.HorizontalSpeed;
        _speedSlider.value = Mathf.Log10(speed) * (_arcBar == null ? 1 : _arcBar.maxValue) / 2;
    }
}
