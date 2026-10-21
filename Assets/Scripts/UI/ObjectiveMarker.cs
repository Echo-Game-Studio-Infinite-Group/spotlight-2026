using UnityEngine;
using UnityEngine.UI;

// 任务点留在场景中，子级 UI 在运行时交给现有 HUD 显示。
[DefaultExecutionOrder(200)]
[DisallowMultipleComponent]
[AddComponentMenu("Game/UI/Objective Marker")]
public sealed class ObjectiveMarker : MonoBehaviour
{
    [SerializeField] private RectTransform _visual;
    [SerializeField] private Text _distanceText;
    [SerializeField] private Vector3 _worldOffset = Vector3.up;
    [Min(0.001f)] [SerializeField] private float _metersPerUnit = 1f;

    private Camera _viewCamera;
    private Transform _player;
    private Canvas _hud;
    private int _lastMeters = -1;

    private void Start()
    {
        _viewCamera = Camera.main;
        PlayerMotor motor = FindObjectOfType<PlayerMotor>();
        if (motor != null) _player = motor.transform;
        // 新 HUD 的数值绑定已改为 PlayerHudView，旧场景仍可沿用 HealthBar。
        PlayerHudView playerHud = FindObjectOfType<PlayerHudView>();
        if (playerHud != null) _hud = playerHud.GetComponentInParent<Canvas>();
        else
        {
            HealthBar healthBar = FindObjectOfType<HealthBar>();
            if (healthBar != null) _hud = healthBar.GetComponentInParent<Canvas>();
        }
        if (_visual == null || _distanceText == null || _viewCamera == null || _hud == null || _player == null)
        {
            Debug.LogError("[ObjectiveMarker] 请使用配好的预制体，场景需要玩家、MainCamera 和 HUD。", this);
            enabled = false;
            return;
        }
        if (_hud.renderMode != RenderMode.ScreenSpaceOverlay)
        {
            Debug.LogError("[ObjectiveMarker] HUD 需要使用 Screen Space - Overlay 模式。", this);
            enabled = false;
            return;
        }
        _visual.SetParent(_hud.transform, false);
    }

    private void LateUpdate()
    {
        if (_visual == null) return;
        if (_viewCamera == null || _player == null || _hud == null)
        {
            _visual.gameObject.SetActive(false);
            return;
        }

        Vector3 point = _viewCamera.WorldToScreenPoint(transform.position + _worldOffset);
        // 相机背后的点也会产生屏幕坐标，必须隐藏，避免标记出现在错误方向。
        bool inView = point.z > _viewCamera.nearClipPlane && _viewCamera.pixelRect.Contains(point);
        _visual.gameObject.SetActive(inView);
        if (!inView) return;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            (RectTransform)_hud.transform, point, null, out Vector2 localPoint);
        _visual.localPosition = new Vector3(localPoint.x, localPoint.y, 0f);
        int meters = Mathf.RoundToInt(Vector3.Distance(_player.position, transform.position) * _metersPerUnit);
        if (meters == _lastMeters) return;
        _lastMeters = meters;
        _distanceText.text = meters + " m";
    }

    public void SetVisible(bool visible) => enabled = visible;

    private void OnDisable()
    {
        if (_visual != null) _visual.gameObject.SetActive(false);
    }

    private void OnDestroy()
    {
        // UI 已经移到 HUD 下，删除任务点时需要一并清理。
        if (_visual != null) Destroy(_visual.gameObject);
    }
}