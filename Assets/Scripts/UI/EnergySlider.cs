using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class EnergySlider : MonoBehaviour
{
    private PlayerMotor _playerMotor;
    private Slider _energySlider;
    private ArcBar _arcBar;

    public GameObject player;

    private void Awake()
    {
        _playerMotor = player.GetComponent<PlayerMotor>();
        _energySlider = GetComponent<Slider>();
        _arcBar = GetComponent<ArcBar>();
    }
    void Update()
    {
        _energySlider.value = _playerMotor.Energy / _playerMotor.Params.EnergyMax * (_arcBar == null? 1 : _arcBar.maxValue);
    }
}
