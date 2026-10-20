using UnityEngine;

// 把 HUD 根节点贴到玩家头顶；屏幕空间画布用局部坐标，世界空间画布用相机平面。
public class FollowPlayerHUD : MonoBehaviour
{
    public GameObject player;

    [SerializeField] private GameObject _content;
    [SerializeField] private Vector3 _worldOffset = new Vector3(0f, 0.9f, 0f);
    [SerializeField] private Vector2 _screenOffset;
    [SerializeField, Min(0f)] private float _edgeMargin = 80f;

    private Camera _camera;
    private RectTransform _rect;
    private RectTransform _canvasRect;
    private Canvas _canvas;
    private bool _contentVisible = true;

    private void Awake()
    {
        _rect = (RectTransform)transform;
        _canvas = GetComponentInParent<Canvas>();
        _canvasRect = _canvas != null ? _canvas.rootCanvas.transform as RectTransform : null;
    }

    public void Bind(GameObject target, GameObject content, Vector3 worldOffset)
    {
        player = target;
        _content = content;
        _worldOffset = worldOffset;
    }

    private void LateUpdate()
    {
        if (player == null)
        {
            Player current = Player.Current;
            if (current != null) player = current.gameObject;
        }

        if (player == null || _canvasRect == null)
        {
            SetContentVisible(false);
            return;
        }

        if (_camera == null) _camera = Camera.main;
        if (_camera == null)
        {
            SetContentVisible(false);
            return;
        }

        Vector3 screen = _camera.WorldToScreenPoint(player.transform.position + _worldOffset);
        bool visible = screen.z > 0f
            && screen.x >= -_edgeMargin && screen.x <= Screen.width + _edgeMargin
            && screen.y >= -_edgeMargin && screen.y <= Screen.height + _edgeMargin;
        SetContentVisible(visible);
        if (!visible) return;

        Camera uiCamera = _canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : _canvas.worldCamera;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _canvasRect, screen, uiCamera, out Vector2 localPoint)) return;
        _rect.anchoredPosition = localPoint + _screenOffset;
    }

    private void SetContentVisible(bool visible)
    {
        if (_content == null || _contentVisible == visible) return;
        _contentVisible = visible;
        _content.SetActive(visible);
    }
}
