using UnityEngine;
using UnityEngine.EventSystems;

public sealed class CRTMenuButton : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, ISelectHandler, IDeselectHandler
{
    [SerializeField] private RectTransform _shutter;
    [SerializeField] private float _tiltDegrees = 1.4f;
    [SerializeField] private float _period = 6f;
    [SerializeField] private float _shutterDuration = .3f;
    [SerializeField] private float _phase;
    private bool _hovered;
    private bool _selected;
    private float _amount;

    private void Update() => Preview(Time.unscaledTime, Time.unscaledDeltaTime);

    public void Preview(float clock, float delta)
    {
        transform.localRotation = Quaternion.Euler(0, 0, Mathf.Sin(clock * Mathf.PI * 2 / _period + _phase) * _tiltDegrees);
        _amount = Mathf.MoveTowards(_amount, _hovered || _selected ? 1 : 0, delta / _shutterDuration);
        _shutter.localScale = new Vector3(1 - Mathf.Pow(1 - _amount, 3), 1, 1);
    }

    public void OnPointerEnter(PointerEventData data) => _hovered = true;
    public void OnPointerExit(PointerEventData data) => _hovered = false;
    public void OnSelect(BaseEventData data) => _selected = true;
    public void OnDeselect(BaseEventData data) => _selected = false;
}
