using UnityEngine;
using GameJam.Actions;

public class SavePoint : MonoBehaviour
{
    public bool saveOnce = true;
    private bool hasTriggered;

    private CombatComponent playerCombat;

    void Awake()
    {
        playerCombat = Object.FindObjectOfType<CombatComponent>();
        if (playerCombat != null)
        {
            playerCombat.Landed += OnLandedHitCallback;
            Debug.Log("SavePoint 订阅战斗事件成功");
        }
        else
        {
            Debug.LogWarning("SavePoint 找不到玩家CombatComponent");
        }
    }

    private void OnLandedHitCallback(HealthComponent hitTarget, Vector3 hitPoint, Vector3 hitDir, float damageApplied)
    {
        if (hitTarget == null)
        {
            return;
        }

        if (hitTarget.gameObject != gameObject)
        {
            return;
        }

        Debug.Log("SavePoint 被攻击命中");

        if (saveOnce && hasTriggered)
        {
            Debug.Log("SavePoint 已经触发过存档");
            return;
        }

        SaveManager.SaveAtHitPoint();
        hasTriggered = true;
    }

    void OnDestroy()
    {
        if (playerCombat != null)
        {
            playerCombat.Landed -= OnLandedHitCallback;
        }
    }
}
