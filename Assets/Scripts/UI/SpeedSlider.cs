using UnityEngine;
using UnityEngine.UI;

public class SpeedSlider : MonoBehaviour
{
    private PlayerData _playerData;
    private Slider _speedSlider;
    private ArcBar _arcBar;

    private void Awake()
    {
        _speedSlider = GetComponent<Slider>();
        _arcBar = GetComponent<ArcBar>();
        _playerData = GameManager.Instance.Player;
    }
    void Update()
    {
        _speedSlider.value = Mathf.Log10(_playerData.Speed) * (_arcBar == null ? 1 : _arcBar.maxValue) / 2;
    }
}
