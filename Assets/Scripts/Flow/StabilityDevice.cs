using UnityEngine;

namespace GameJam.Flow
{
    public sealed class StabilityDevice : MonoBehaviour, IAttackDamageReceiver
    {
        [Tooltip("同一箱庭内唯一的稳定装置编号，用于保存本次访问的破坏状态。")]
        [InspectorName("装置编号")] public string Id = "lamp";
        [Tooltip("累计受到此数值的攻击伤害后击碎，混乱度增量由当前差分配置。")]
        [InspectorName("击碎所需伤害"), Min(0.01f)] public float BreakHealth = 1f;
        [Tooltip("破坏后隐藏的外观物体；同一次访问死亡恢复后仍保留已破坏状态。")]
        [InspectorName("破坏后隐藏的外观")] public GameObject Visual;
        [Tooltip("成功击碎时复用断肢切割；恢复已破坏的存档不会再次播放。")]
        [InspectorName("击碎切割效果")] public GibComponent BreakEffect;
        private RunFlowController _flow;
        private FlowRoomInstance _room;
        private float _remaining;
        public bool IsBroken { get; private set; }
        public void Bind(RunFlowController flow, FlowRoomInstance room)
        {
            _flow = flow; _room = room; _remaining = BreakHealth;
            if (BreakEffect != null) BreakEffect.ResetEffect();
            SetBroken(room.BrokenDevices.Contains(Id));
        }
        public AttackDamageResult ReceiveAttack(AttackDamageRequest request)
        {
            if (IsBroken || _room == null || !_room.IsCurrent || _flow.IsBusy || request.Damage <= 0f) return default;
            float damage = Mathf.Min(_remaining, request.Damage); _remaining -= damage;
            if (_remaining <= 0f)
            {
                if (!_flow.TryBreakDevice(_room, Id)) { _remaining = BreakHealth; return default; }
                if (BreakEffect != null) BreakEffect.TrySliceDestructible(request.Direction);
                SetBroken(true);
            }
            return new AttackDamageResult(damage, IsBroken);
        }
        private void SetBroken(bool broken)
        {
            IsBroken = broken;
            if (Visual != null) Visual.SetActive(!broken);
            foreach (Collider collider in GetComponentsInChildren<Collider>(true)) collider.enabled = !broken;
            foreach (Light light in GetComponentsInChildren<Light>(true)) light.enabled = !broken;
        }
    }
}
