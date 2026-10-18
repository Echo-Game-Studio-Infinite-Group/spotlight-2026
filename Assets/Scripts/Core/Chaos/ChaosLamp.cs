using System;
using UnityEngine;

// 直接接已有攻击受击接口，不用交互键或额外的调试按钮代替真实挥刀。
[DisallowMultipleComponent]
public sealed class ChaosLamp : MonoBehaviour, IAttackDamageReceiver
{
    [SerializeField] private string _roomId;
    [SerializeField] private string _lampId;
    [SerializeField, Min(1)] private int _gain = 10;
    [SerializeField] private GameObject _intactVisual;
    [SerializeField] private GameObject _brokenVisual;
    [SerializeField] private Collider _hitCollider;
    public string RoomId => string.IsNullOrWhiteSpace(_roomId) ? gameObject.scene.name : _roomId;
    public string LampId => _lampId;
    public int Gain => _gain;
    public bool IsBroken { get; private set; }
    public event Action<ChaosLamp> Broken;

    public void Configure(string roomId, string lampId, int gain, GameObject intact, GameObject broken, Collider hitCollider)
    {
        if (string.IsNullOrWhiteSpace(lampId) || gain <= 0) throw new ArgumentException("稳定装置需要唯一编号和正的增量");
        _roomId = roomId; _lampId = lampId; _gain = gain;
        _intactVisual = intact; _brokenVisual = broken; _hitCollider = hitCollider;
    }

    private void Start() => RefreshState();
    public void RefreshState()
    {
        IsBroken = ChaosManager.Exists && ChaosManager.Instance.WasLampBroken(RoomId, _lampId);
        if (_intactVisual != null) _intactVisual.SetActive(!IsBroken);
        if (_brokenVisual != null) _brokenVisual.SetActive(IsBroken);
        if (_hitCollider != null) _hitCollider.enabled = !IsBroken;
    }

    public AttackDamageResult ReceiveAttack(AttackDamageRequest request)
    {
        if (!isActiveAndEnabled || request.Damage <= 0f || float.IsNaN(request.Damage) ||
            float.IsInfinity(request.Damage) || IsBroken || !ChaosManager.Exists) return default;
        // 后继房预加载后可见，但玩家还不能提前取得它的收益。
        if (MapManager.Exists && MapManager.Instance.CurrentRoomSceneName != gameObject.scene.name) return default;
        if (!ChaosManager.Instance.TryBreakLamp(RoomId, _lampId, _gain)) { RefreshState(); return default; }
        RefreshState();
        Broken?.Invoke(this);
        return new AttackDamageResult(request.Damage, true);
    }
}
