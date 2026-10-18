using GameJam.Flow;
using UnityEditor;
using UnityEngine;

namespace GameJam.FlowEditor
{
    [CustomEditor(typeof(FlowTextPoint)), CanEditMultipleObjects]
    public sealed class FlowTextPointInspector : FlowConfigInspector
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox("在地图模板中摆位置和朝向，在所属箱庭的差分中填写文字。文字区域宽高和对齐方式在 Text3D 子节点的 TMP 组件中设置。", MessageType.Info);
            base.OnInspectorGUI();
        }

        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected)]
        private static void DrawLabel(FlowTextPoint point, GizmoType type)
            => Handles.Label(point.transform.position, point.DisplayName + "\n" + point.Id);
    }
}
