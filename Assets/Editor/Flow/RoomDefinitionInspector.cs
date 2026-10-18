using GameJam.Flow;
using UnityEditor;

namespace GameJam.FlowEditor
{
    [CustomEditor(typeof(RoomDefinition)), CanEditMultipleObjects]
    public sealed class RoomDefinitionInspector : FlowConfigInspector { }
}
