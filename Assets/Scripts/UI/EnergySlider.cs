using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class EnergySlider : MonoBehaviour
{
    private IEnergyAccount _energy;
    private Slider _energySlider;
    private ArcBar _arcBar;

    private void Awake()
    {
        _energySlider = GetComponent<Slider>();
        _arcBar = GetComponent<ArcBar>();
        _energy = GameObject.FindWithTag("Player").GetComponentInParent<IEnergyAccount>();
    }
    void Update()
    {
        _energySlider.value = _energy.CurrentEnergy / _energy.MaxEnergy * (_arcBar == null? 1 : _arcBar.maxValue);
    }
    private void LateUpdate()
    {
        return;
    }
}
