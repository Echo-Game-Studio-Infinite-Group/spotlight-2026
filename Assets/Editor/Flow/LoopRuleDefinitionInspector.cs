using GameJam.Flow;
using UnityEditor;

namespace GameJam.FlowEditor
{
    [CustomEditor(typeof(LoopRuleDefinition)), CanEditMultipleObjects]
    public sealed class LoopRuleDefinitionInspector : FlowConfigInspector { }
}
