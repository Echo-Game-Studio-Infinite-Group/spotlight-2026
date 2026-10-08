using UnityEngine;

namespace DeferredOutline
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(OutlineTarget))]
    [ExecuteAlways]
    [DefaultExecutionOrder(10001)]
    public sealed class OutlineController : MonoBehaviour
    {
        [SerializeField, Tooltip("显隐控制目标；外观和预设覆盖仍由 OutlineTarget 管理。")]
        private OutlineTarget _target;
        [SerializeField, Tooltip("运行中切换会播放原预设的出现/消失过渡。")]
        private bool _effectEnabled = true;

        private OutlineTarget _appliedTarget;
        private bool _appliedEnabled;

        public OutlineTarget Target => _target;
        public bool EffectEnabled => _effectEnabled;

        private void Reset() => _target = GetComponent<OutlineTarget>();
        private void OnEnable()
        {
            _appliedTarget = null;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= UpdateEditorPreview;
            UnityEditor.EditorApplication.update += UpdateEditorPreview;
#endif
        }

#if UNITY_EDITOR
        private void OnDisable() => UnityEditor.EditorApplication.update -= UpdateEditorPreview;
        private void UpdateEditorPreview()
        {
            if (!Application.isPlaying && isActiveAndEnabled) Update();
        }
#endif

        private void Update()
        {
            if (_target == null) _target = GetComponent<OutlineTarget>();
            // 只在命令变化时同步，避免每帧重启目标自己的过渡。
            if (_target != null && (_appliedTarget != _target || _appliedEnabled != _effectEnabled))
                ApplyState();
        }

        public void SetEnabled(bool value)
        {
            _effectEnabled = value;
            if (_target == null) _target = GetComponent<OutlineTarget>();
            ApplyState();
        }

        public void TurnOn() => SetEnabled(true);
        public void TurnOff() => SetEnabled(false);
        public void Toggle() => SetEnabled(!_effectEnabled);

        private void ApplyState()
        {
            if (_target == null) return;
            if (_effectEnabled) _target.TurnOn();
            else _target.TurnOff();
            _appliedTarget = _target;
            _appliedEnabled = _effectEnabled;
        }
    }
}