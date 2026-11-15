using GameJam.Flow;
using UnityEditor;

namespace GameJam.FlowEditor
{
    [CustomEditor(typeof(RoomPoolDefinition)), CanEditMultipleObjects]
    public sealed class RoomPoolDefinitionInspector : FlowConfigInspector { }
}
