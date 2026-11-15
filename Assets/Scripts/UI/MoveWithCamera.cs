using UnityEngine;

[RequireComponent(typeof(RectTransform))]
public class MoveWithCamera : MonoBehaviour
{
    public float xOffsetRate = 1f, yOffsetRate = 1f;
    public float xReturnRate = 1f, yReturnRate = 1f;
    [SerializeField, Min(0f)] private float _maxOffset = 18f;

    private RectTransform _rect;
    private Transform _cameraTransform;
    private Vector2 _defaultPosition;
    private Vector2 _offset;
    private float _lastYaw, _lastPitch;

    private void Awake()
    {
        _rect = GetComponent<RectTransform>();
        _defaultPosition = _rect.anchoredPosition;
        _cameraTransform = Camera.main.transform;
        GetYawAndPitch(out _lastYaw, out _lastPitch);
    }

    private void LateUpdate()
    {
        if (_cameraTransform == null) return;
        GetYawAndPitch(out float yaw, out float pitch);
        _offset.x += Mathf.DeltaAngle(yaw, _lastYaw) * xOffsetRate * .3f;
        _offset.y += Mathf.DeltaAngle(pitch, _lastPitch) * yOffsetRate * .3f;
        // 锚点坐标不依赖画布的世界位置；UI 回正也不随游戏时缓或帧率改变。
        _offset.x *= Mathf.Exp(-Mathf.Max(0f, xReturnRate) * 6f * Time.unscaledDeltaTime);
        _offset.y *= Mathf.Exp(-Mathf.Max(0f, yReturnRate) * 6f * Time.unscaledDeltaTime);
        _offset = Vector2.ClampMagnitude(_offset, _maxOffset);
        _rect.anchoredPosition = _defaultPosition + _offset;
        _lastYaw = yaw;
        _lastPitch = pitch;
    }

    private void GetYawAndPitch(out float yaw, out float pitch)
    {
        Vector3 forward = _cameraTransform.forward;
        Vector3 flat = new Vector3(forward.x, 0f, forward.z);
        yaw = flat.sqrMagnitude < 1e-6f ? 0f : Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
        pitch = Mathf.Asin(Mathf.Clamp(forward.y, -1f, 1f)) * Mathf.Rad2Deg;
    }
}
