using GameJam.Flow;
using UnityEditor;

namespace GameJam.FlowEditor
{
    [CustomEditor(typeof(RunFlowController)), CanEditMultipleObjects]
    public sealed class RunFlowControllerInspector : FlowConfigInspector { }
}
