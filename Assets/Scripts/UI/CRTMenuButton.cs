using UnityEngine;
using UnityEngine.EventSystems;

public sealed class CRTMenuButton : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, ISelectHandler, IDeselectHandler
{
    [SerializeField] private RectTransform _shutter;
    [SerializeField] private float _tiltDegrees = 4f;
    [SerializeField] private float _period = 6f;
    [SerializeField] private float _shutterDuration = .3f;
    [SerializeField] private float _phase;
    private bool _hovered;
    private bool _selected;
    private float _amount;

    private void Update() => Preview(Time.unscaledTime, Time.unscaledDeltaTime);

    public void Preview(float clock, float delta)
    {
        // 红光呼吸使用菜单的非缩放时钟，暂停与编辑器预览保持一致。
        Shader.SetGlobalFloat("_CRTMenuUnscaledClock", clock);
        transform.localRotation = Quaternion.Euler(0, 0, Mathf.Sin(clock * Mathf.PI * 2 / _period + _phase) * _tiltDegrees);
        _amount = Mathf.MoveTowards(_amount, _hovered || _selected ? 1 : 0, delta / _shutterDuration);
        _shutter.localScale = new Vector3(1 - Mathf.Pow(1 - _amount, 3), 1, 1);
    }

    public void OnPointerEnter(PointerEventData data) => _hovered = true;
    public void OnPointerExit(PointerEventData data)
    {
        _hovered = false;
        _selected = false;
    }
    // 鼠标点击也会触发 EventSystem 选中，只有键盘导航需要保持选中高亮。
    public void OnPointerDown(PointerEventData data) => _selected = false;
    public void OnSelect(BaseEventData data) => _selected = !(data is PointerEventData);
    public void OnDeselect(BaseEventData data) => _selected = false;

    private void OnDisable()
    {
        // 关闭菜单后不一定收到 PointerExit，重新打开时不能沿用旧悬停状态。
        _hovered = false;
        _selected = false;
        _amount = 0;
        if (_shutter != null)
            _shutter.localScale = new Vector3(0, 1, 1);
    }
}
