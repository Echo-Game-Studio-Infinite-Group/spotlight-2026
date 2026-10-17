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
        _energy = FindObjectOfType<PlayerMotor>().GetComponent<IEnergyAccount>();
    }
    void Update()
    {
        float ratio = _energy.MaxEnergy > 0f ? Mathf.Clamp01(_energy.CurrentEnergy / _energy.MaxEnergy) : 0f;
        _energySlider.value = ratio * (_arcBar == null ? 1f : _arcBar.maxValue);
    }
}
