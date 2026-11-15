using GameJam.Flow;
using UnityEditor;
using UnityEngine;

namespace GameJam.FlowEditor
{
    [CustomEditor(typeof(FlowEnemySpawnPoint)), CanEditMultipleObjects]
    public sealed class FlowEnemySpawnPointInspector : FlowConfigInspector
    {
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();
            EditorGUILayout.HelpBox("在所属箱庭的差分中配置此点生成的敌人。保存地图模板后，差分通过稳定编号识别出生点。", MessageType.Info);
        }
        private void OnSceneGUI()
        {
            var point = (FlowEnemySpawnPoint)target;
            Handles.Label(point.transform.position + Vector3.up, point.DisplayName + "\n" + point.Id);
        }
    }
}
