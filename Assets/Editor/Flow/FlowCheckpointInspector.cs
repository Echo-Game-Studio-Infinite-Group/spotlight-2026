using GameJam.Flow;
using UnityEditor;

namespace GameJam.FlowEditor
{
    [CustomEditor(typeof(FlowCheckpoint)), CanEditMultipleObjects]
    public sealed class FlowCheckpointInspector : FlowConfigInspector { }
}
