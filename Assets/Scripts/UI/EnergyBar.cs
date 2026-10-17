using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class EnergyBar : MonoBehaviour
{
    [SerializeField] private GameObject[] _segments;
    [SerializeField] private GameObject[] _glows;
    [SerializeField] private TextMeshProUGUI[] _tmps;

    private IEnergyAccount _energy;
    private Material _valueMaterial;
    private float _maxValue;
    private float _lastEnergy;
    private int _lastValue;

    private void Awake()
    {
        _energy = GameObject.FindWithTag("Player").GetComponentInParent<IEnergyAccount>();
        _valueMaterial = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>("Assets/Art/Materials/ValueMaterial.mat");
        _maxValue = _energy.MaxEnergy;
        _lastEnergy = -1.0f;
        _lastValue = -1;
        _tmps[1].text = "/ " + _maxValue.ToString();

        for (int i = 0; i < 3; ++i)
        {
            Image item = _glows[i].GetComponent<Image>();
            Color tempcolor = item.color;
            tempcolor.a = 0;
            item.color = tempcolor;
        }

        // Debug.Log(_valueMaterial == null);

        if (_valueMaterial != null)
        {
            foreach (var item in _tmps)
            {
                item.fontMaterial = _valueMaterial;
            }
        }
    }
    private void Update()
    {
        float curEnergy = _energy.CurrentEnergy;
        if(Mathf.Abs(curEnergy - _lastEnergy) < 1.0f && Mathf.Abs(curEnergy - _maxValue) > 0.001f)
        {
            return;
        }

        _lastEnergy = curEnergy;

        int integerEnergy = (int)Mathf.Floor(curEnergy);
        _tmps[0].text = integerEnergy.ToString();

        float percent = curEnergy / _maxValue;
        int val = (int)Mathf.Floor(percent * 16);
        if (_lastValue == val)
        {
            return;
        }

        _lastValue = val;

        for (int i = 1; i <= 16; ++i)
        {
            if(i <= val)
            {
                _segments[i - 1].SetActive(true);
            }
            else
            {
                _segments[i - 1].SetActive(false);
            }
        }

        if (val < 5)
        {
            return;
        }

        float alpha = (percent - 0.3125f) * 16.0f / 11.0f;
        for (int i = 0; i < 3; ++i)
        {
            Image item = _glows[i].GetComponent<Image>();
            Color tempcolor = item.color;
            tempcolor.a = alpha;
            item.color = tempcolor;
        }
    }
}
