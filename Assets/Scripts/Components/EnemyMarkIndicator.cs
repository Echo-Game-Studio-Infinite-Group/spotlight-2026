using UnityEngine;
using UnityEngine.UI;

[DisallowMultipleComponent]
[RequireComponent(typeof(Canvas))]
public sealed class EnemyMarkIndicator : MaskableGraphic
{
    [SerializeField] private EnemyMark _owner;
    [SerializeField] private Canvas _canvas;
    protected override void OnEnable()
    {
        base.OnEnable();
        if (!Application.IsPlaying(gameObject)) return;
        _owner.Changed += OnMarkChanged;
        OnMarkChanged(_owner.IsMarked);
    }
    protected override void OnDisable()
    {
        if (Application.IsPlaying(gameObject))
        {
            _owner.Changed -= OnMarkChanged;
            _canvas.enabled = false;
        }
        base.OnDisable();
    }
    private void OnMarkChanged(bool marked) => _canvas.enabled = marked;
    private void LateUpdate()
    {
        if (!Application.IsPlaying(gameObject) || !_canvas.enabled) return;
        Camera view = Camera.main;
        if (view != null) transform.rotation = view.transform.rotation;
    }
    protected override void OnPopulateMesh(VertexHelper mesh)
    {
        mesh.Clear();
        Rect rect = GetPixelAdjustedRect();
        mesh.AddVert(new Vector3(rect.xMin, rect.yMax), color, Vector2.zero);
        mesh.AddVert(new Vector3(rect.xMax, rect.yMax), color, Vector2.right);
        mesh.AddVert(new Vector3(rect.center.x, rect.yMin), color, Vector2.up);
        mesh.AddTriangle(0, 1, 2);
    }
}
