using UnityEngine;

namespace GameJam.Flow
{
    [DisallowMultipleComponent]
    public sealed class FlowTextPoint : MonoBehaviour
    {
        [InspectorName("文字点编号"), Tooltip("同一地图模板内唯一。改名和移动不会改变编号，复制后在差分窗口修复编号。")] public string Id;
        [InspectorName("中文备注")] public string DisplayName = "地图文字";
        [InspectorName("文字表现")] public FlowTextVisual Visual;
        [InspectorName("默认出现效果")] public FlowTextAnimation DefaultAppearance;
        [InspectorName("默认消失效果")] public FlowTextAnimation DefaultDisappearance;

        private FlowTextBinding _binding;
        private bool _current, _triggered;
        public bool HasTriggered => _triggered;

        public void Bind(FlowTextBinding binding)
        {
            _binding = binding; _current = false; _triggered = false;
            if (Visual == null) return;
            Visual.Hide();
            if (binding != null && binding.Enabled) Visual.Configure(binding);
        }

        public void SetCurrent(bool current)
        {
            if (_current == current) return;
            _current = current;
            if (!current && Visual != null) Visual.Hide();
            // 保持显示的文字回到同一房间时恢复；一次性文字保留本次访问的触发记录。
            if (current && _triggered && _binding != null && _binding.Display == FlowTextDisplay.Persistent
                && gameObject.activeInHierarchy && Visual != null) Visual.ShowFinal(_binding.Appearance ?? DefaultAppearance);
        }

        private void Update()
        {
            if (!_current || _triggered || _binding == null || !_binding.Enabled || Visual == null || TimeManager.IsPaused) return;
            Camera camera = Camera.main;
            if (_binding.MaximumDistance > 0f && (camera == null ||
                (camera.transform.position - Visual.transform.position).sqrMagnitude > _binding.MaximumDistance * _binding.MaximumDistance)) return;
            if (_binding.Trigger == FlowTextTrigger.Visible && !CanSee(camera)) return;
            _triggered = true;
            Visual.Play(_binding.Appearance ?? DefaultAppearance, _binding.Disappearance ?? DefaultDisappearance,
                _binding.HoldSeconds, _binding.Display == FlowTextDisplay.Persistent,
                _binding.AppearanceSpeed, _binding.DisappearanceSpeed);
        }

        public bool CanSee(Camera camera)
        {
            if (camera == null) return false;
            Vector3 viewport = camera.WorldToViewportPoint(Visual.transform.position);
            if (viewport.z < camera.nearClipPlane || viewport.z > camera.farClipPlane) return false;
            return GeometryUtility.TestPlanesAABB(GeometryUtility.CalculateFrustumPlanes(camera), Visual.WorldBounds);
        }

        private void OnDisable() { if (Visual != null) Visual.Hide(); }

        private void OnDrawGizmos()
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireCube(transform.position, Vector3.one * .35f);
            Gizmos.DrawRay(transform.position, -transform.forward);
        }
    }
}
