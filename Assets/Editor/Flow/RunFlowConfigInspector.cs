using GameJam.Flow;
using UnityEditor;

namespace GameJam.FlowEditor
{
    [CustomEditor(typeof(RunFlowConfig)), CanEditMultipleObjects]
    public sealed class RunFlowConfigInspector : FlowConfigInspector { }
}
