using System;
using UnityEngine;

// 房间灰盒（D3–D5 验收「一个房间可开门和重试」）：清怪开门——活敌归零开、复活后重新关
// 门 = 本物体子物体「Door」上的 Collider（关卡侧按房间规格标准升级，见风险登记表「房间规格标准缺失」）
// 状态用低频轮询而非订阅：敌人复活（RetryFlow.ResetForRestart）后无需通知本类，下一轮询自动重新关门
// ——灰盒取舍：0.25s 轮询的成本可忽略，换来零订阅泄漏与零生命周期耦合
public class RoomManager : MonoBehaviour
{
    [Tooltip("门物体名（本物体子级；其上的 Collider 即门阻挡）")]
    public string DoorChildName = "Door";

    [Tooltip("状态轮询间隔（秒，unscaled）")]
    public float PollIntervalSec = 0.25f;

    /// <summary>房间清空事件（开门演出/UI 订阅）</summary>
    public event Action<bool> OpennessChanged;

    private EnemyCombat[] _enemies;
    private Collider _doorCollider;
    private float _nextPollUnscaled;
    private bool _open;

    public bool IsOpen => _open;

    private void Awake()
    {
        // 敌人收集只做一次（子树静态布怪；动态刷怪由关卡侧升级时改注册制）
        _enemies = GetComponentsInChildren<EnemyCombat>(true);
        Transform door = transform.Find(DoorChildName);
        _doorCollider = door != null ? door.GetComponentInChildren<Collider>() : null;
    }

    private void Update()
    {
        if (Time.unscaledTime < _nextPollUnscaled) return;
        _nextPollUnscaled = Time.unscaledTime + PollIntervalSec;

        bool alive = false;
        for (int i = 0; i < _enemies.Length; i++)
        {
            HealthComponent health = _enemies[i].GetComponentInParent<HealthComponent>();
            if (health != null && !health.IsDead) { alive = true; break; }
        }
        SetOpen(!alive);
    }

    private void SetOpen(bool open)
    {
        if (_open == open) return;
        _open = open;
        if (_doorCollider != null) _doorCollider.enabled = !open; // 开门 = 放行；关门 = 挡路
        OpennessChanged?.Invoke(open);
    }
}
