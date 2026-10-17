using UnityEngine;

// 存档点：被玩家攻击命中时触发存档。
// 命中判定走玩家 CombatComponent 的 Landed 事件（dev 侧结算入口），
// 场景里还有敌人的 CombatComponent，所以必须通过玩家本体取组件，不能 FindObjectOfType 乱抓。
// 玩家可能晚于本物体加载，Update 里惰性订阅一次。
public class SavePoint : MonoBehaviour
{
    [Tooltip("勾选后本次运行只触发一次存档")]
    public bool saveOnce = true;

    private bool hasTriggered;
    private CombatComponent playerCombat;

    private void Update()
    {
        if (playerCombat != null) return;
        PlayerMotor motor = PlayerMotor.Active;
        if (motor == null) return;

        playerCombat = motor.GetComponent<CombatComponent>();
        if (playerCombat != null)
        {
            playerCombat.Landed += OnLandedHitCallback;
        }
    }

    private void OnLandedHitCallback(HealthComponent hitTarget, Vector3 hitPoint, Vector3 hitDir, float damageApplied)
    {
        if (hitTarget == null || hitTarget.gameObject != gameObject) return;
        if (saveOnce && hasTriggered) return;

        SaveManager.SaveAtHitPoint();
        hasTriggered = true;
    }

    private void OnDestroy()
    {
        if (playerCombat != null)
        {
            playerCombat.Landed -= OnLandedHitCallback;
        }
    }
}
