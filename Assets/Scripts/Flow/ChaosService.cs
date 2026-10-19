using System;

namespace GameJam.Flow
{
    public sealed class ChaosService
    {
        private readonly RunFlowConfig _config;
        public int Value { get; private set; }
        public event Action<int, string> Changed;
        public ChaosService(RunFlowConfig config) { _config = config; Value = config.InitialChaos; }
        public void Add(int amount, string reason)
        {
            long total = (long)Value + amount;
            Set((int)Math.Max(_config.MinimumChaos, Math.Min(_config.MaximumChaos, total)), reason);
        }
        public void ApplyDeath(bool allowNegative)
        {
            long result = (long)Value - _config.DeathPenalty;
            Set((int)Math.Max(allowNegative && _config.EnableSpecialRoutes ? int.MinValue : _config.MinimumChaos, result), "死亡");
        }
        public bool TrySetSpecial(int value, string reason)
        {
            if (!_config.EnableSpecialRoutes) return false;
            Set(value, reason); return true;
        }
        public void Restore(int value)
        {
            // 已破坏装置可能让混乱度超出当前差分的入口区间，只按全局规则校验。
            if (!_config.EnableSpecialRoutes) value = Math.Max(_config.MinimumChaos, Math.Min(_config.MaximumChaos, value));
            Set(value, "读档");
        }
        private void Set(int value, string reason)
        {
            if (Value == value) return;
            Value = value; Changed?.Invoke(value, reason);
        }
    }
}
