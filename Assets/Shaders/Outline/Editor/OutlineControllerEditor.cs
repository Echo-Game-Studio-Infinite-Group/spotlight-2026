using UnityEditor;
using UnityEngine;

namespace DeferredOutline.Editor
{
    [CustomEditor(typeof(OutlineController))]
    public sealed class OutlineControllerEditor : UnityEditor.Editor
    {
        private void OnEnable() => EditorApplication.update += UpdatePreview;
        private void OnDisable() => EditorApplication.update -= UpdatePreview;

        private void UpdatePreview()
        {
            OutlineController controller = target as OutlineController;
            if (controller == null || controller.Target == null) return;
            OutlineTarget.PlaybackState state = controller.Target.State;
            if (state != OutlineTarget.PlaybackState.Appearing && state != OutlineTarget.PlaybackState.Disappearing) return;
            // 编辑态 ExecuteAlways 仅在场景更新时运行，预览期间请求后续帧。
            if (!Application.isPlaying) EditorApplication.QueuePlayerLoopUpdate();
            Repaint();
            SceneView.RepaintAll();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_target"), new GUIContent("Outline Target"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_effectEnabled"), new GUIContent("Effect Enabled (On / Off)"));
            serializedObject.ApplyModifiedProperties();

            OutlineController controller = (OutlineController)target;
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("On")) SetState(controller, true);
            if (GUILayout.Button("Off")) SetState(controller, false);
            if (GUILayout.Button("Toggle")) SetState(controller, !controller.EffectEnabled);
            EditorGUILayout.EndHorizontal();

            if (controller.Target != null)
            {
                EditorGUILayout.LabelField("当前状态", controller.Target.State.ToString());
                if (controller.Target.loopInDemo)
                    EditorGUILayout.HelpBox("OutlineTarget 的 Loop In Demo 已开启，会自动切换状态；手动控制时请关闭。", MessageType.Warning);
                if (controller.Target.ResolvedPreset == null)
                    EditorGUILayout.HelpBox("请在 OutlineTarget 上指定 Group 或 Preset Override。", MessageType.Warning);
            }
            EditorGUILayout.HelpBox("此组件只管理 On/Off。同一对象 OutlineTarget 可覆盖单一半径、火焰强度、UV扰动强度及黑边/HDR核心色；持续平流速度在其 Preset 调整；不会被本控制器覆盖。", MessageType.Info);
        }

        private static void SetState(OutlineController controller, bool enabled)
        {
            Undo.RecordObject(controller, "切换描边显隐");
            controller.SetEnabled(enabled);
            EditorUtility.SetDirty(controller);
            PrefabUtility.RecordPrefabInstancePropertyModifications(controller);
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
        }
    }
}