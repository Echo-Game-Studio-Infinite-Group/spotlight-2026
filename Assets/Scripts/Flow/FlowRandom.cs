using System;

namespace GameJam.Flow
{
    // 保存整个状态而非只保存种子，重试不会重新消耗前面已经执行过的抽取。
    [Serializable]
    public sealed class FlowRandom
    {
        public uint State;
        public FlowRandom(int seed) { State = unchecked((uint)seed); if (State == 0) State = 0x6D2B79F5; }
        public uint NextUInt()
        {
            uint value = State;
            value ^= value << 13; value ^= value >> 17; value ^= value << 5;
            return State = value;
        }
        public double NextUnit() => NextUInt() / 4294967296.0;
        public int NextIndex(int count) => count > 0 ? (int)(NextUnit() * count) : throw new ArgumentOutOfRangeException(nameof(count));
    }
}
