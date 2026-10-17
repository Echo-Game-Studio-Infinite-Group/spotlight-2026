using UnityEngine;

namespace GameJam.Flow
{
    public sealed class StabilityDevice : MonoBehaviour, IAttackDamageReceiver
    {
        [InspectorName("装置编号")] public string Id = "lamp";
        [InspectorName("击碎所需伤害"), Min(0.01f)] public float BreakHealth = 1f;
        [InspectorName("破坏后隐藏的外观")] public GameObject Visual;
        private RunFlowController _flow;
        private FlowRoomInstance _room;
        private float _remaining;
        public bool IsBroken { get; private set; }
        public void Bind(RunFlowController flow, FlowRoomInstance room)
        { _flow = flow; _room = room; _remaining = BreakHealth; SetBroken(room.BrokenDevices.Contains(Id)); }
        public AttackDamageResult ReceiveAttack(AttackDamageRequest request)
        {
            if (IsBroken || _room == null || !_room.IsCurrent || _flow.IsBusy || request.Damage <= 0f) return default;
            float damage = Mathf.Min(_remaining, request.Damage); _remaining -= damage;
            if (_remaining <= 0f)
            {
                if (!_flow.TryBreakDevice(_room, Id)) { _remaining = BreakHealth; return default; }
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
