using System.Collections.Generic;
using UnityEngine;

namespace GameJam.Actions
{
    // 接入方从统一输入快照生成，不允许执行器读取设备或自行读取引擎时间。
    public readonly struct ActionInputSample
    {
        public readonly long Tick;
        public readonly ActionInputButtons Held;
        public readonly Vector2 Move;
        public readonly IReadOnlyList<ActionInputEdge> Edges;

        public ActionInputSample(long tick, ActionInputButtons held = ActionInputButtons.None,
            Vector2 move = default, IReadOnlyList<ActionInputEdge> edges = null)
        {
            Tick = tick;
            Held = held;
            Move = move;
            Edges = edges;
        }
    }
}
