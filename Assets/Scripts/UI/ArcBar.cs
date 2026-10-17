using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class ArcBar : MonoBehaviour
{
    [SerializeField] private float _maxAngle;
    [SerializeField] private float _rotateAngle;
    [SerializeField] private Image _backgroundImage;
    private Slider _slider;

    public float maxValue;

    private void Awake()
    {
        maxValue = _maxAngle / 360.0f;

        _slider = GetComponent<Slider>();
        _slider.transform.Rotate(0.0f, 0.0f, _rotateAngle);
        _slider.value = 0;

        _backgroundImage.fillAmount = maxValue;
    }
}
