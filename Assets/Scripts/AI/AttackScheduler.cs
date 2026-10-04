using System.Collections.Generic;
using UnityEngine;

// 场地级开火配额。策划案 8.(2) 硬性要求：同一场地上同时发射的机器人不多于 3 个
// 框架设计 4.5：leo 提供场地级攻击配额；死亡 / 打断 / 禁用时必须归还，否则名额会永久泄漏
// 本类只裁定「谁有资格开火」，不碰开火动作本身：远程敌人先 TryAcquire，打完（或被打断）Release
public class AttackScheduler : MonoBehaviour
{
    [Header("配额")]
    public int MaxConcurrentShooters = 3;     // 策划案写死的 3，不要凭手感加
    [Header("归还后的冷却（秒）")]
    public float SlotCooldown = 0.5f;         // 防止同一个敌人刚还回名额就立刻抢回去，别人永远轮不上

    private static AttackScheduler _instance;

    private readonly HashSet<MonoBehaviour> _shooters = new HashSet<MonoBehaviour>();
    private readonly Dictionary<MonoBehaviour, float> _readyTime = new Dictionary<MonoBehaviour, float>();

    public static AttackScheduler Instance => _instance;

    public int ActiveCount => _shooters.Count;

    private void Awake()
    {
        _instance = this;
    }

    // 申请开火资格。已持有者直接放行，避免同一敌人重复申请把名额吃掉
    public bool TryAcquire(MonoBehaviour shooter)
    {
        if (shooter == null) return false;
        if (_shooters.Contains(shooter)) return true;
        if (_shooters.Count >= MaxConcurrentShooters) return false;

        // 刚还回名额的敌人有一段冷静期，让其他人有机会插队，弹幕才有轮流开火的节奏感
        if (_readyTime.TryGetValue(shooter, out float readyAt) && TimeManager.UnscaledTime < readyAt)
        {
            return false;
        }

        _shooters.Add(shooter);
        _readyTime.Remove(shooter);
        return true;
    }

    // 归还名额。冷却计时走不缩放时间：时停期间若用世界时间，冷却永远走不完，名额会被卡死
    public void Release(MonoBehaviour shooter)
    {
        if (shooter == null) return;

        if (_shooters.Remove(shooter))
        {
            _readyTime[shooter] = TimeManager.UnscaledTime + SlotCooldown;
        }
    }

    public bool IsHolding(MonoBehaviour shooter)
    {
        return shooter != null && _shooters.Contains(shooter);
    }
}
