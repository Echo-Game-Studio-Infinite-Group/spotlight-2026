using UnityEngine;
using UnityEngine.UI;

public class EnergySlider : MonoBehaviour
{
    [SerializeField] private bool _layeredVisual;
    [SerializeField] private ResourceBarVisual _visual;

    private IEnergyAccount _energy;
    private Slider _energySlider;
    private ArcBar _arcBar;

    private void Awake()
    {
        _energy = FindObjectOfType<PlayerMotor>().GetComponent<IEnergyAccount>();
        if (_layeredVisual) return;
        _energySlider = GetComponent<Slider>();
        _arcBar = GetComponent<ArcBar>();
    }
    void Update()
    {
        if (_layeredVisual)
        {
            _visual.SetValue(_energy.CurrentEnergy, _energy.MaxEnergy);
            return;
        }
        float ratio = _energy.MaxEnergy > 0f ? Mathf.Clamp01(_energy.CurrentEnergy / _energy.MaxEnergy) : 0f;
        _energySlider.value = ratio * (_arcBar == null ? 1f : _arcBar.maxValue);
    }
}
