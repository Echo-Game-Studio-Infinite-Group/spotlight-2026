using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class HealthBar : MonoBehaviour
{
    // [SerializeField] private Color _low = new Color(0.7607843f, 0.1686274f, 0.1686274f, 1.0f);
    [SerializeField] private RectTransform _fillGlow;
    [SerializeField] private RectTransform _fillCore;
    [SerializeField] private RectTransform _fillHighlight;
    [SerializeField] private GameObject[] _glows;
    [SerializeField] private TextMeshProUGUI[] _tmps;

    private const float _MIN_WIDTH_GLOW = 1.04f;
    private const float _MAX_WIDTH_GLOW = 10.88f;
    private const float _MIN_WIDTH_CORE = 0.46f;
    private const float _MAX_WIDTH_CORE = 9.84f;
    private const float _MIN_WIDTH_HIGHLIGHT = 0.00f;
    private const float _MAX_WIDTH_HIGHLIGHT = 9.20f;

    private HealthComponent _playerData;
    private Material _valueMaterial;
    private float _maxHealth;
    private float _lastHealth;

    private void Awake()
    {
        _playerData = GameObject.FindWithTag("Player").GetComponentInParent<HealthComponent>();
        _valueMaterial = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>("Assets/Art/Materials/ValueMaterial.mat");
        _maxHealth = _playerData.MaxHealth;
        _lastHealth = -1;
        _tmps[1].text = "/ " + _maxHealth.ToString();

        for (int i = 0; i < 3; ++i)
        {
            Image item = _glows[i].GetComponent<Image>();
            Color tempcolor = item.color;
            tempcolor.a = 0;
            item.color = tempcolor;
        }

        if (_valueMaterial != null)
        {
            foreach (var item in _tmps)
            {
                item.fontMaterial = _valueMaterial;
            }
        }
    }
    void Update()
    {
        float curHealth = _playerData.Health;
        if(Mathf.Abs(curHealth - _lastHealth) < 0.01f)
        {
            return;
        }

        _lastHealth = curHealth;

        int integerVal = (int)Mathf.Floor(curHealth);
        _tmps[0].text = integerVal.ToString();

        float percent = curHealth / _maxHealth;

        Vector2 tempSize = _fillCore.sizeDelta;
        tempSize.x = Mathf.Lerp(_MIN_WIDTH_CORE, _MAX_WIDTH_CORE, percent);
        _fillCore.sizeDelta = tempSize;

        tempSize = _fillGlow.sizeDelta;
        tempSize.x = Mathf.Lerp(_MIN_WIDTH_GLOW, _MAX_WIDTH_GLOW, percent);
        _fillGlow.sizeDelta = tempSize;

        tempSize = _fillHighlight.sizeDelta;
        tempSize.x = Mathf.Lerp(_MIN_WIDTH_HIGHLIGHT, _MAX_WIDTH_HIGHLIGHT, percent);
        _fillHighlight.sizeDelta = tempSize;

        float alpha = percent * percent * (3.0f - 2.0f * percent);
        for (int i = 0; i < 3; ++i)
        {
            Image item = _glows[i].GetComponent<Image>();
            Color tempcolor = item.color;
            tempcolor.a = alpha;
            item.color = tempcolor;
        }
    }
}
