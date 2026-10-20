using System.Collections.Generic;
using UnityEngine;

// 刀光拖尾（动态 Mesh 版）：采样跟随骨骼的刀根 / 刀尖世界坐标，把历史轨迹点连成带状网格。
// 参考《Unity 纯代码实现简单刀光拖尾特效》：不用 TrailRenderer / 粒子系统，Mesh 每帧重建，
// 历史帧存世界坐标、每帧经 worldToLocalMatrix 转进网格本地空间，弧线不跟骨骼回摆。
[DisallowMultipleComponent]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public sealed class AttackTrailVfx : MonoBehaviour
{
    public const string NodeName = "AttackTrail";
    // Resources 相对路径（打包含引用，避免被裁掉）。
    public const string MaterialPath = "CombatVfx/AttackTrail";
    // 无光照 shader：URP Lit 在透明加法网格上会渲染成紫黑，不能换。
    public const string MaterialShaderName = "GameJam/SparkAdditive";

    [Tooltip("跟随目标（刀根骨骼）。节点挂在 Vfx 下保持层级整洁，采样只读它的世界坐标")]
    [SerializeField] private Transform _follow;

    [Tooltip("刀尖相对骨骼的局部偏移（决定带宽 = 刀长）。按各模型的持刀轴向配置")]
    [SerializeField] private Vector3 _tipOffset = new Vector3(0f, 0f, 0.4f);

    [Tooltip("轨迹点存活时长（秒）。决定弧线长度，也负责 Stop 后自然淡出")]
    [SerializeField, Min(0.02f)] private float _lifetime = 0.18f;

    [Tooltip("一次播放的默认发射窗口（秒）。攻击没有结束事件，靠它自己收尾")]
    [SerializeField, Min(0f)] private float _defaultEmitSeconds = 0.28f;

    [Tooltip("发射窗口上限（秒）。漏了关闭信号时不能一直喷")]
    [SerializeField, Min(0.05f)] private float _maxEmitSeconds = 1.2f;

    [Tooltip("刀根位移小于该值时不采样：原地停顿不堆点")]
    [SerializeField, Min(0.0001f)] private float _minSampleDistance = 0.01f;

    [Tooltip("沿轨迹的透明度渐变（新亮旧暗）")]
    [SerializeField] private Gradient _colorOverTrail = FadeGradient();

    private struct TrailPoint
    {
        public Vector3 Base;   // 刀根（骨骼）世界坐标
        public Vector3 Tip;    // 刀尖世界坐标
        public float Time;     // 采样时刻（表现层计时）
    }

    private readonly List<TrailPoint> _points = new List<TrailPoint>(64);
    private Mesh _mesh;
    private MeshRenderer _renderer;
    private Vector3[] _vertices;
    private Color[] _colors;
    private Vector2[] _uv;
    private int[] _triangles;
    private float _clock;
    private float _emitRemaining;
    private bool _emitting;

    public bool IsEmitting => _emitting;
    public Transform Follow => _follow;
    public Vector3 TipOffset => _tipOffset;

    public void SetFollow(Transform follow) => _follow = follow;

    public void SetTipOffset(Vector3 offset) => _tipOffset = offset;

    private void Awake()
    {
        EnsureMesh();
        _renderer = GetComponent<MeshRenderer>();
        if (_renderer != null)
        {
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            if (_renderer.sharedMaterial == null) _renderer.sharedMaterial = Resources.Load<Material>(MaterialPath);
        }
        ClearNow();
    }

    private void LateUpdate() => Tick(Time.unscaledDeltaTime);

    // 出图自证工具在编辑模式手动驱动（编辑模式 LateUpdate 不跑）；编辑模式 Awake 不执行，这里惰性补初始化。
    public void Tick(float dt)
    {
        if (_mesh == null || _renderer == null)
        {
            EnsureMesh();
            _renderer = GetComponent<MeshRenderer>();
        }
        _clock += Mathf.Max(0f, dt);
        if (_emitting)
        {
            // 表现层按真实时间收窗口：时停 / 慢动作里剑本来不动，弧线不该被拖成一团。
            _emitRemaining -= dt;
            if (_emitRemaining <= 0f) _emitting = false;
        }

        if (_emitting && _follow != null) Sample();
        while (_points.Count > 0 && _clock - _points[0].Time > _lifetime) _points.RemoveAt(0);

        if (_points.Count < 2)
        {
            if (_mesh != null && _mesh.vertexCount > 0) _mesh.Clear();
            if (_renderer != null) _renderer.enabled = false;
            return;
        }
        Rebuild();
    }

    /// <summary>开一个发射窗口。<paramref name="seconds"/> ≤ 0 时用组件上的默认值。</summary>
    public void Play(float seconds = -1f)
    {
        _emitRemaining = Mathf.Clamp(seconds > 0f ? seconds : _defaultEmitSeconds, 0.02f, _maxEmitSeconds);
        _emitting = true;
    }

    /// <summary>停发射，已经画出来的弧按寿命自然淡出。</summary>
    public void Stop() => _emitting = false;

    /// <summary>立刻收干净（死亡 / 回收进池时用，避免复用对象带着旧弧）。</summary>
    public void ClearNow()
    {
        _emitting = false;
        _points.Clear();
        if (_mesh != null) _mesh.Clear();
        if (_renderer != null) _renderer.enabled = false;
    }

    private void Sample()
    {
        Vector3 bladeBase = _follow.position;
        Vector3 bladeTip = _follow.TransformPoint(_tipOffset);
        if (_points.Count > 0)
        {
            TrailPoint last = _points[_points.Count - 1];
            if ((bladeBase - last.Base).sqrMagnitude < _minSampleDistance * _minSampleDistance
                && (bladeTip - last.Tip).sqrMagnitude < _minSampleDistance * _minSampleDistance) return;
        }
        _points.Add(new TrailPoint { Base = bladeBase, Tip = bladeTip, Time = _clock });
    }

    private void Rebuild()
    {
        int count = _points.Count;
        EnsureBuffers(count);
        Matrix4x4 worldToLocal = transform.worldToLocalMatrix;

        for (int i = 0; i < count; i++)
        {
            TrailPoint point = _points[i];
            // 0 新 → 1 旧：位置留在世界原地（经 worldToLocal 转进网格空间），透明度按寿命收掉。
            float age = Mathf.Clamp01((_clock - point.Time) / _lifetime);
            _vertices[i * 2] = worldToLocal.MultiplyPoint3x4(point.Base);
            _vertices[i * 2 + 1] = worldToLocal.MultiplyPoint3x4(point.Tip);
            Color c = _colorOverTrail.Evaluate(age);
            _colors[i * 2] = c;
            _colors[i * 2 + 1] = c;
            float u = 1f - age;                     // u 沿轨迹（贴图拖尾拉伸），v 横跨带宽
            _uv[i * 2] = new Vector2(u, 0f);
            _uv[i * 2 + 1] = new Vector2(u, 1f);
        }
        for (int s = 0; s < count - 1; s++)
        {
            int a = s * 2;
            _triangles[s * 6] = a;
            _triangles[s * 6 + 1] = a + 1;
            _triangles[s * 6 + 2] = a + 2;
            _triangles[s * 6 + 3] = a + 1;
            _triangles[s * 6 + 4] = a + 3;
            _triangles[s * 6 + 5] = a + 2;
        }

        _mesh.Clear(false);
        _mesh.vertices = _vertices;
        _mesh.colors = _colors;
        _mesh.uv = _uv;
        _mesh.triangles = _triangles;
        _mesh.RecalculateBounds();
        _renderer.enabled = true;
    }

    private void EnsureMesh()
    {
        MeshFilter filter = GetComponent<MeshFilter>();
        if (filter.sharedMesh == null || filter.sharedMesh.name != "AttackTrailMesh")
        {
            _mesh = new Mesh { name = "AttackTrailMesh" };
            _mesh.MarkDynamic();
            filter.sharedMesh = _mesh;
        }
        else _mesh = filter.sharedMesh;
    }

    private void EnsureBuffers(int count)
    {
        if (_vertices != null && _vertices.Length >= count * 2) return;
        _vertices = new Vector3[count * 2];
        _colors = new Color[count * 2];
        _uv = new Vector2[count * 2];
        _triangles = new int[(count - 1) * 6];
    }

    private static Gradient FadeGradient()
    {
        var gradient = new Gradient();
        gradient.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(0.9f, 0f), new GradientAlphaKey(0.9f, 0.3f), new GradientAlphaKey(0f, 1f) });
        return gradient;
    }
}
