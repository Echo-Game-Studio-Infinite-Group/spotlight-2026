using UnityEngine;
using UnityEngine.UI;

// 伤害跳字组件：作为**预制体里的常驻组件**使用，由 Enemy 复用，不再运行时动态生成。
//
// 为什么不做动态生成：每次命中要 new GameObject + AddComponent(Canvas/Text/Outline/CanvasGroup)
// + 从系统字体建动态字体 + 结束时 Destroy —— 高速战斗里这是持续的对象与 GC 压力。
// 现在是「预制体一次搭好、运行时只激活与重置」，零分配。
//
// 池由 DamagePopup 自己维护（静态注册表），调用方只需要在 Inspector 上挂一个不激活的实例引用。
[DisallowMultipleComponent]
public sealed class DamagePopup : MonoBehaviour
{
    private static readonly System.Collections.Generic.List<DamagePopup> Pool =
        new System.Collections.Generic.List<DamagePopup>(8);

    private static Font _font;

    [SerializeField] private Canvas _canvas;
    [SerializeField] private Text _label;
    [SerializeField] private Outline _outline;
    [SerializeField] private CanvasGroup _group;
    [SerializeField, Min(0.1f)] private float _lifetime = 1.2f;
    [SerializeField, Min(0f)] private float _rise = 2.2f;
    [SerializeField, Min(0f)] private float _lateralDrift = 0.35f;

    private Transform _anchor;
    private Vector3 _localOffset;
    private bool _hasAnchor;
    private Camera _camera;
    private float _age;
    private float _drift;

    public float Lifetime => _lifetime;
    public float RiseSpeed => _rise;
    public string Text => _label != null ? _label.text : string.Empty;

    private void Awake()
    {
        if (!Pool.Contains(this)) Pool.Add(this);
    }

    private void OnDestroy()
    {
        Pool.Remove(this);
    }

    // 供装配工具写入各引用（预制体已经搭好，这里是幂等回填）
    public void ConfigureReferences(Canvas canvas, Text label, Outline outline, CanvasGroup group)
    {
        _canvas = canvas;
        _label = label;
        _outline = outline;
        _group = group;
    }

    public void ConfigureAnimation(float lifetime, float rise)
    {
        _lifetime = Mathf.Max(0.1f, lifetime);
        _rise = rise;
    }

    // 原型实例登记进池；Enemy 会在初始化时调用一次
    public static void RegisterTemplate(DamagePopup template)
    {
        if (template != null && !Pool.Contains(template)) Pool.Add(template);
    }

    // 从池里取 3 个可用实例，Pooled 只接受 DamagePopup 数组，故返回数组
    public static DamagePopup[] AcquirePool(int size)
    {
        if (size <= 0 || Pool.Count == 0) return System.Array.Empty<DamagePopup>();
        DamagePopup[] result = new DamagePopup[size];
        for (int i = 0; i < size; i++) result[i] = Pool[i % Pool.Count];
        return result;
    }

    public static void ReleaseAll()
    {
        Pool.Clear();
    }

    // 播放一次跳字。position 为受击点世界坐标，anchor 用于让数字跟着目标移动。
    public void Show(float amount, Color color, Vector3 worldPosition, Transform anchor, Camera camera,
        float lifetime, float rise)
    {
        _lifetime = Mathf.Max(0.1f, lifetime);
        _rise = rise;
        _anchor = anchor;
        _hasAnchor = anchor != null;
        // 记下「锚点坐标系里的偏移」：锚点移动时数字跟着走，又不会把偏移叠加两次
        _localOffset = _hasAnchor ? anchor.InverseTransformPoint(worldPosition) : worldPosition;
        _camera = camera != null ? camera : Camera.main;
        _age = 0f;
        _drift = Random.Range(-_lateralDrift, _lateralDrift);

        if (_label != null)
        {
            _label.text = Mathf.Abs(amount % 1f) < 0.001f
                ? Mathf.RoundToInt(amount).ToString()
                : amount.ToString("0.#");
            _label.color = color;
        }

        if (_outline != null) _outline.effectColor = new Color(0f, 0f, 0f, 0.95f);
        if (_group != null) _group.alpha = 1f;
        if (_canvas != null) _canvas.sortingOrder = 100;

        EnsureFont();
        gameObject.SetActive(true);
        ApplyPosition();
        FaceCamera();
    }

    // 工程内零字体资产、TMP Essentials 未导入，运行时只能从系统字体造。
    // 预制体保存不了这种运行时字体，所以激活时补一次；只在真正需要时执行。
    public void EnsureFont()
    {
        if (_label == null || _label.font != null) return;
        if (_font == null)
        {
            _font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "SimHei", "Arial" }, 72);
        }

        if (_font == null)
        {
            Debug.LogError("[DamagePopup] 未能创建系统字体，伤害跳字将不可见");
            return;
        }

        _label.font = _font;
    }

    private void LateUpdate()
    {
        // 用 UnscaledDeltaTime：命中同时会触发减速，跳字必须照常播完，否则慢动作里数字会卡住
        _age += TimeManager.UnscaledDeltaTime;
        ApplyPosition();
        FaceCamera();

        if (_group != null) _group.alpha = Mathf.Clamp01(1f - _age / _lifetime);

        // 池化复用：播完只隐藏，不销毁
        if (_age >= _lifetime) gameObject.SetActive(false);
    }

    private void ApplyPosition()
    {
        Vector3 basePosition = _hasAnchor && _anchor != null ? _anchor.TransformPoint(_localOffset) : _localOffset;
        transform.position = basePosition
            + Vector3.up * (_rise * _age)
            + Vector3.right * (_drift * _age);
    }

    private void FaceCamera()
    {
        if (_camera == null) _camera = Camera.main;
        // 始终正对镜头，否则世界空间文字会侧过来看不清
        if (_camera != null) transform.rotation = _camera.transform.rotation;
    }
}
