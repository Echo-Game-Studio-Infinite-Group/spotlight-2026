using UnityEditor;
using UnityEngine;

// PreviewRenderUtility 持有独立场景，预览不会修改敌人的血量、材质或原场景。
public sealed class GibPreviewWindow : EditorWindow
{
    [SerializeField] private GibComponent _source;
    [SerializeField] private AnimationClip _pose;
    [SerializeField] private float _poseTime;
    [SerializeField] private float _hitAngle = 90f;
    [SerializeField] private float _speed = 1f;
    [SerializeField] private float _loopSeconds = 3f;
    [SerializeField] private bool _loop = true;
    private PreviewRenderUtility _preview;
    private GibComponent _copy;
    private Animator _animator;
    private Material _floorMaterial;
    private Vector3 _focus;
    private Vector2 _orbit = new Vector2(150f, 15f);
    private float _distance;
    private float _floor;
    private float _elapsed;
    private float _accumulator;
    private double _lastUpdate;
    private bool _playing;
    private string _error;

    [MenuItem("超高速行者/Gib 动态预览")]
    private static void OpenSelected()
    {
        Open(Selection.activeGameObject != null ? Selection.activeGameObject.GetComponentInParent<GibComponent>() : null);
    }

    public static void Open(GibComponent source)
    {
        var window = GetWindow<GibPreviewWindow>("Gib 动态预览");
        window.minSize = new Vector2(440f, 460f);
        window._source = source;
        window._distance = 0f;
        window.Restart();
    }

    private void OnEnable()
    {
        EditorApplication.update += Tick;
        AssemblyReloadEvents.beforeAssemblyReload += Clear;
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
        _lastUpdate = EditorApplication.timeSinceStartup;
    }

    private void OnDisable()
    {
        EditorApplication.update -= Tick;
        AssemblyReloadEvents.beforeAssemblyReload -= Clear;
        EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        Clear();
    }

    private void OnPlayModeChanged(PlayModeStateChange state)
    {
        Clear();
        Repaint();
    }

    private void OnGUI()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorGUILayout.HelpBox("退出 Play 模式后可预览。", MessageType.Info);
            return;
        }
        EditorGUI.BeginChangeCheck();
        _source = (GibComponent)EditorGUILayout.ObjectField("预览敌人", _source, typeof(GibComponent), true);
        _pose = (AnimationClip)EditorGUILayout.ObjectField("取样动作（可选）", _pose, typeof(AnimationClip), false);
        if (_pose != null) _poseTime = EditorGUILayout.Slider("动作取样时间", _poseTime, 0f, _pose.length);
        _hitAngle = EditorGUILayout.Slider("击飞方向", _hitAngle, -180f, 180f);
        if (EditorGUI.EndChangeCheck()) Restart();
        _speed = EditorGUILayout.Slider("播放速度", _speed, 0.1f, 2f);
        _loopSeconds = EditorGUILayout.Slider("每轮时长", _loopSeconds, 1f, 10f);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(_playing ? "暂停" : "播放"))
            {
                if (_copy == null) Restart();
                else _playing = !_playing;
            }
            if (GUILayout.Button("重播 / 更新参数")) Restart();
            _loop = GUILayout.Toggle(_loop, "循环", GUILayout.Width(55f));
            GUILayout.Label($"{_elapsed:F2} 秒", GUILayout.Width(70f));
        }
        EditorGUILayout.HelpBox("预览必定肢解，不修改死亡概率。拖动旋转，滚轮缩放；修改组件参数后点重播。地面为模拟平面。", MessageType.None);
        if (!string.IsNullOrEmpty(_error)) EditorGUILayout.HelpBox(_error, MessageType.Warning);
        Rect rect = GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        if (_preview == null || rect.width < 2f || rect.height < 2f) return;
        Event e = Event.current;
        if (rect.Contains(e.mousePosition))
        {
            if (e.type == EventType.MouseDrag && e.button == 0)
            {
                _orbit += new Vector2(e.delta.x, e.delta.y) * 0.5f;
                _orbit.y = Mathf.Clamp(_orbit.y, -80f, 80f);
                e.Use();
                Repaint();
            }
            else if (e.type == EventType.ScrollWheel)
            {
                _distance = Mathf.Clamp(_distance * Mathf.Exp(e.delta.y * 0.08f), 0.2f, 100f);
                e.Use();
                Repaint();
            }
        }
        if (e.type != EventType.Repaint) return;
        _preview.BeginPreview(rect, GUIStyle.none);
        Quaternion rotation = Quaternion.Euler(_orbit.y, _orbit.x, 0f);
        _preview.camera.transform.SetPositionAndRotation(_focus - rotation * Vector3.forward * _distance, rotation);
        _preview.lights[0].transform.rotation = rotation * Quaternion.Euler(25f, 25f, 0f);
        _preview.Render(true);
        GUI.DrawTexture(rect, _preview.EndPreview(), ScaleMode.StretchToFill, false);
    }

    private void Restart()
    {
        Clear();
        _error = null;
        if (_source == null || EditorApplication.isPlayingOrWillChangePlaymode) return;
        try
        {
            _preview = new PreviewRenderUtility();
            _preview.camera.fieldOfView = 35f;
            _preview.camera.nearClipPlane = 0.01f;
            _preview.camera.farClipPlane = 200f;
            _preview.camera.clearFlags = CameraClearFlags.SolidColor;
            _preview.camera.backgroundColor = new Color(0.12f, 0.14f, 0.18f);
            _preview.lights[0].intensity = 1.3f;
            _preview.lights[0].transform.rotation = Quaternion.Euler(35f, -30f, 0f);
            _preview.lights[1].intensity = 0.6f;
            var clone = Instantiate(_source.gameObject);
            clone.hideFlags = HideFlags.HideAndDontSave;
            _preview.AddSingleGO(clone);
            clone.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            clone.transform.localScale = _source.transform.lossyScale;
            // 仅手动推进动画状态机，禁用游戏逻辑和动画事件，避免预览触发伤害。
            foreach (var behaviour in clone.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
            clone.SetActive(true);
            _copy = clone.GetComponent<GibComponent>();
            _animator = clone.GetComponentInChildren<Animator>();
            if (_animator != null && _animator.runtimeAnimatorController != null)
            {
                _animator.enabled = true;
                _animator.fireEvents = false;
                _animator.applyRootMotion = false;
                _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                _animator.speed = 1f;
                _animator.Rebind();
                _animator.Update(0f);
            }
            if (_pose != null && _animator != null) _pose.SampleAnimation(_animator.gameObject, _poseTime);
            var renderers = clone.GetComponentsInChildren<SkinnedMeshRenderer>();
            if (renderers.Length == 0) throw new System.InvalidOperationException("该对象没有 SkinnedMeshRenderer。");
            Bounds bounds = renderers[0].bounds;
            foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
            _floor = bounds.min.y;
            _focus = bounds.center;
            if (_distance <= 0f) _distance = Mathf.Max(1f, bounds.size.magnitude * 2.2f);
            if (!_copy.BeginPreview(Quaternion.Euler(0f, _hitAngle, 0f) * Vector3.forward))
                throw new System.InvalidOperationException("无法生成 Gib，请检查 Cutout Shader 和模型渲染器。");
            if (_animator != null && _animator.runtimeAnimatorController != null)
                _animator.SetBool("Dead", true);
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.hideFlags = HideFlags.HideAndDontSave;
            _preview.AddSingleGO(ground);
            DestroyImmediate(ground.GetComponent<Collider>());
            ground.transform.position = new Vector3(0f, _floor, 0f);
            _floorMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            _floorMaterial.SetColor("_BaseColor", new Color(0.22f, 0.25f, 0.29f));
            ground.GetComponent<Renderer>().sharedMaterial = _floorMaterial;
            _elapsed = _accumulator = 0f;
            _lastUpdate = EditorApplication.timeSinceStartup;
            _playing = true;
        }
        catch (System.Exception exception)
        {
            _error = exception.Message;
            Clear();
        }
        Repaint();
    }

    private void Tick()
    {
        double now = EditorApplication.timeSinceStartup;
        float dt = Mathf.Min((float)(now - _lastUpdate), 0.1f);
        _lastUpdate = now;
        if (!_playing || _copy == null) return;
        _accumulator += dt * _speed;
        const float step = 1f / 60f;
        while (_accumulator >= step)
        {
            if (_animator != null && _animator.runtimeAnimatorController != null) _animator.Update(step);
            _copy.SimulatePreview(step, _floor);
            _elapsed += step;
            _accumulator -= step;
        }
        if (_elapsed >= _loopSeconds)
        {
            if (_loop) Restart();
            else _playing = false;
        }
        Repaint();
    }

    private void Clear()
    {
        _playing = false;
        if (_copy != null) _copy.ResetEffect();
        _copy = null;
        _animator = null;
        _preview?.Cleanup();
        _preview = null;
        if (_floorMaterial != null) DestroyImmediate(_floorMaterial);
        _floorMaterial = null;
    }
}
