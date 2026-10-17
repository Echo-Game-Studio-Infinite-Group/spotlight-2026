using UnityEngine;

namespace DeferredOutline
{
    [CreateAssetMenu(menuName = "Rendering/Deferred Outline/Group", fileName = "OutlineGroup")]
    public sealed class OutlineGroup : ScriptableObject
    {
        public string groupId = "Default";
        public OutlinePreset preset;
    }
}
