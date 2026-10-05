using System.Collections.Generic;
using UnityEngine;

namespace GameJam.Actions
{
    [CreateAssetMenu(fileName = "ActionCatalog", menuName = "超高速行者/动作序列/动作集")]
    public sealed class ActionCatalog : ScriptableObject
    {
        public string DisplayName = "动作集";
        [TextArea] public string Description;
        [Min(1)] public int BufferCapacity = 16;
        [Min(1)] public int InputHistoryCapacity = 128;
        public List<ActionDefinition> Actions = new List<ActionDefinition>();
    }
}
