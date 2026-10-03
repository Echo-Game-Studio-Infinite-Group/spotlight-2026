using System.Collections;
using UnityEngine;

// 死亡重试灰盒（D3–D5 闭环「一个房间可重试」）：死亡 → 延迟 → 全量复位，恢复策略参数化
// 复位清单显式枚举（RetryFlowTests 逐项守护，防「复位漏一项」）：时间层 / 玩家攻击机 / 玩家血量 /
// 玩家位形 / 能量 / 全场敌人（遍历 EnemyCombat，含已死禁用的——不靠注册表，遍历天然不漏）
// 恢复规则裁决（短期待办「死亡重试的敌人、血量、能量与进度恢复规则」）落地前，各开关先按全恢复跑通灰盒
public class RetryFlow : MonoBehaviour
{
    [Tooltip("死亡到重开的延迟（秒，走 unscaled——死亡瞬间可能处于 hit-stop，缩放时间不可靠）")]
    public float RespawnDelaySec = 1.5f;

    [Tooltip("重开是否清空能量（恢复规则待裁决——裁决后只调这里）")]
    public bool ResetEnergy = true;

    [Tooltip("重开是否复活全场敌人（含已死禁用的；进度恢复规则待裁决）")]
    public bool ReviveEnemies = true;

    [Tooltip("重开是否把玩家送回出生点")]
    public bool ResetPlayerPosition = true;

    private PlayerMotor _motor;
    private HealthComponent _playerHealth;
    private PlayerCombat _combat;
    private VectorEnergy _energy;
    private Vector3 _spawnPosition;   // 玩家出生点：以装配时位形为准
    private Quaternion _spawnRotation;
    private Coroutine _pending;

    /// <summary>
    /// 装配与测试注入（记录出生点为注入时玩家位形）。先解旧订阅再重挂——重复 Wire 幂等
    /// </summary>
    public void Wire(GameObject player)
    {
        if (_playerHealth != null) _playerHealth.Died -= OnPlayerDied;

        _motor = player.GetComponent<PlayerMotor>();
        _playerHealth = player.GetComponentInParent<HealthComponent>();
        _combat = player.GetComponent<PlayerCombat>();
        _energy = player.GetComponentInParent<VectorEnergy>();
        _spawnPosition = player.transform.position;
        _spawnRotation = player.transform.rotation;

        // AddComponent 立即跑 OnEnable（此时还没注入），注入后在此补挂——与 OnEnable 双向兜底
        if (isActiveAndEnabled && _playerHealth != null) _playerHealth.Died += OnPlayerDied;
    }

    private void OnEnable()
    {
        if (_playerHealth != null) _playerHealth.Died += OnPlayerDied;
    }

    private void OnDisable()
    {
        if (_playerHealth != null) _playerHealth.Died -= OnPlayerDied;
        if (_pending != null) StopCoroutine(_pending); // 对象失效时挂起的重试协程一并终止
        _pending = null;
    }

    private void OnPlayerDied(DamageInfo info)
    {
        if (_pending != null) StopCoroutine(_pending); // 理论不可达（死后不再受伤），防重复挂起
        _pending = StartCoroutine(RetryAfterDelay());
    }

    private IEnumerator RetryAfterDelay()
    {
        if (RespawnDelaySec > 0f) yield return new WaitForSecondsRealtime(RespawnDelaySec);
        _pending = null;
        RetryNow();
    }

    /// <summary>
    /// 立即执行全量复位（RoomManager / 测试 / UI 重开按钮直调；死亡延迟流程最终也走这里）
    /// </summary>
    public void RetryNow()
    {
        // 时间层最先清：后续各系统的复位计时都在干净的时间轴上进行
        TimeManager.ResetAll();

        if (_combat != null) _combat.ResetForRestart();
        if (_playerHealth != null) _playerHealth.ResetForRestart();
        if (ResetEnergy && _energy != null) _energy.ResetEnergy();

        if (ReviveEnemies)
        {
            // 含已死（禁用中）的敌人：遍历天然不漏，无需注册表
            EnemyCombat[] enemies = FindObjectsByType<EnemyCombat>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < enemies.Length; i++) enemies[i].ResetForRestart();
        }

        if (ResetPlayerPosition && _motor != null)
        {
            _motor.Teleport(_spawnPosition); // Teleport 同时清动量并同步物理（WallMovementTests 已锁语义）
            _motor.transform.rotation = _spawnRotation; // Teleport 不含朝向，出生姿态在此补
        }
    }
}
