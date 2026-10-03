using System.Collections;
using UnityEngine;

// 死亡重试灰盒（dev 地板版，D3–D5 验收「一个房间可重试」）：死亡 → 延迟 → 全量复位
// 死亡检测：轮询 GameManager.PlayerData.IsAlive（PlayerData 无死亡事件，静默归零）
// 复位清单显式（RetryFlowTests 守护防漏）：血量(PlayerData.Reset 现成)/能量(ResetEnergy)/
// 全场敌人(遍历 Enemy.ResetForRestart，含禁用的——遍历天然不漏)/玩家位形(Motor.Teleport 回出生点)
// 时间侧无需清理：dev 版 TimeManager 的 hit-stop/时缓走 unscaled 到期时间戳，自然衰减无残留
// 恢复策略参数化——裁决（短期待办「死亡重试恢复规则」）落地后只调开关
public class RetryFlow : MonoBehaviour
{
    [Tooltip("死亡到重开的延迟（秒，走 unscaled——死亡瞬间可能挂着 hit-stop）")]
    public float RespawnDelaySec = 1.5f;

    [Tooltip("重开是否清空能量（恢复规则待裁决——裁决后只调这里）")]
    public bool ResetEnergy = true;

    [Tooltip("重开是否复活全场敌人（含已死禁用的；进度恢复规则待裁决）")]
    public bool ReviveEnemies = true;

    [Tooltip("重开是否把玩家送回出生点")]
    public bool ResetPlayerPosition = true;

    private PlayerMotor _motor;
    private VectorEnergy _energy;
    private Vector3 _spawnPosition;   // 玩家出生点：本组件激活时按玩家当前位形记录（装配后以场景摆位为准）
    private Quaternion _spawnRotation;
    private bool _deathPending;
    private Coroutine _pending;

    /// <summary>装配与测试注入（注入即记录出生点；重复注入幂等——先解旧状态）</summary>
    public void Wire(GameObject player)
    {
        _motor = player.GetComponent<PlayerMotor>();
        _energy = player.GetComponentInParent<VectorEnergy>();
        _spawnPosition = player.transform.position;
        _spawnRotation = player.transform.rotation;
        _deathPending = false;
    }

    private void OnEnable()
    {
        if (_motor == null) // 未注入时自动找场景唯一玩家（场景装配兜底）
        {
            PlayerMotor motor = FindFirstObjectByType<PlayerMotor>();
            if (motor != null) Wire(motor.gameObject);
        }
    }

    private void OnDisable()
    {
        if (_pending != null) StopCoroutine(_pending); // 对象失效时挂起的重试协程一并终止
        _pending = null;
    }

    private void Update()
    {
        PlayerData health = GameManager.Instance.Player;
        if (health == null) return;
        if (health.IsAlive)
        {
            _deathPending = true; // 死亡沿：见过活着，之后的 IsAlive=false 才算「死亡」
            return;
        }
        if (!_deathPending) return; // 从未见过玩家活着（初始化竞态）不触发
        _deathPending = false;
        if (_pending != null) StopCoroutine(_pending);
        _pending = StartCoroutine(RetryAfterDelay());
    }

    private IEnumerator RetryAfterDelay()
    {
        if (RespawnDelaySec > 0f) yield return new WaitForSecondsRealtime(RespawnDelaySec);
        _pending = null;
        RetryNow();
    }

    /// <summary>
    /// 立即执行全量复位（UI 重开按钮/测试直调；死亡延迟流程最终也走这里）
    /// </summary>
    public void RetryNow()
    {
        PlayerData health = GameManager.Instance.Player;
        if (health != null) health.Reset(); // 基类现成：回满血+清无敌时间戳

        if (ResetEnergy && _energy != null) _energy.ResetEnergy();

        if (ReviveEnemies)
        {
            // 含已死（禁用中）的敌人：遍历天然不漏，无需注册表
            Enemy[] enemies = FindObjectsByType<Enemy>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < enemies.Length; i++) enemies[i].ResetForRestart();
        }

        if (ResetPlayerPosition && _motor != null)
        {
            _motor.Teleport(_spawnPosition); // Teleport 同时清动量并同步物理
            _motor.transform.rotation = _spawnRotation;
        }
    }
}
