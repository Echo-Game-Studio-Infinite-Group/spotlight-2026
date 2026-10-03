using UnityEngine;

[DefaultExecutionOrder(-100)]
public sealed class TimeManager : MonoBehaviour
{
    private static TimeManager _instance;
    [Range(0f, 1f)] public float WorldScale = 1f;
    [Range(0f, 1f)] public float PlayerScale = 1f;
    private float _hitStopUntil;
    private float _hitStopScale = 1f;
    private float _slowUntil;
    private float _slowScale = 1f;
    private float _playerTime;

    private float FreezeScale => Time.unscaledTime < _hitStopUntil ? _hitStopScale : 1f;

    // 命中减速（0.75 倍速那类）与顿帧相乘叠加：顿帧解决「砍中了」的瞬时反馈，
    // 减速解决「砍中前后一小段时间」的张力，两者时长独立，可以同时存在。
    private float SlowScale => Time.unscaledTime < _slowUntil ? _slowScale : 1f;
    private float EffectScale => FreezeScale * SlowScale;

    // 惰性解析：Awake 不保证跑过（编辑模式的装配工具与自检用 AddComponent 建实例时不会触发它），
    // 只有 _instance 时才兜底查找一次，让静态入口在任何模式下都拿得到有效实例。
    internal static TimeManager Resolve()
    {
        if (_instance == null) _instance = FindObjectOfType<TimeManager>();
        return _instance;
    }

    public static float PlayerRate
    {
        get
        {
            TimeManager instance = Resolve();
            return instance != null ? instance.PlayerScale * instance.EffectScale : 1f;
        }
    }

    public static float PlayerDeltaTime => Time.unscaledDeltaTime * PlayerRate;
    public static float PlayerFixedDeltaTime => Time.fixedDeltaTime * PlayerRate;
    public static float WorldDeltaTime
    {
        get
        {
            TimeManager instance = Resolve();
            return Time.unscaledDeltaTime * (instance != null ? instance.WorldScale * instance.EffectScale : 1f);
        }
    }

    public static float UnscaledDeltaTime => Time.unscaledDeltaTime;
    public static float UnscaledTime => Time.unscaledTime;
    public static float PlayerTime => Resolve() != null ? Resolve()._playerTime : Time.time;
    public static bool InHitStop => Resolve() != null && Time.unscaledTime < Resolve()._hitStopUntil;
    public static bool InSlowMotion => Resolve() != null && Time.unscaledTime < Resolve()._slowUntil;

    // 当前实际生效的减速倍率（未减速时为 1），供 HUD 与测试读取
    public static float SlowRate => Resolve() != null ? Resolve().SlowScale : 1f;

    private void Awake() => _instance = this;
    private void OnDestroy() { if (_instance == this) _instance = null; }
    private void FixedUpdate() => _playerTime += PlayerFixedDeltaTime;

    public static void HitStop(float seconds, float scale = 0.05f)
    {
        TimeManager instance = Resolve();
        if (instance == null) return;
        bool alreadyFrozen = InHitStop;
        instance._hitStopUntil = Mathf.Max(instance._hitStopUntil, Time.unscaledTime + Mathf.Max(0f, seconds));
        instance._hitStopScale = alreadyFrozen
            ? Mathf.Min(instance._hitStopScale, Mathf.Clamp01(scale))
            : Mathf.Clamp01(scale);
    }

    // 命中减速：阈值时间内把游戏压到 scale 倍速，到时自动恢复。
    // 重复触发时取更强的那一档、并按更长的时间延长，避免连击把减速越叠越弱。
    public static void SlowMotion(float seconds, float scale = 0.75f)
    {
        TimeManager instance = Resolve();
        if (instance == null) return;
        float clamped = Mathf.Clamp(scale, 0.01f, 1f);
        bool alreadySlowed = InSlowMotion;
        instance._slowUntil = Mathf.Max(instance._slowUntil, Time.unscaledTime + Mathf.Max(0f, seconds));
        instance._slowScale = alreadySlowed ? Mathf.Min(instance._slowScale, clamped) : clamped;
        // 命中减速是玩家能直接感知的状态，进/出各打一行，便于在 Console 里确认它确实发生并恢复了
        Debug.Log($"[TimeManager] 命中减速 {clamped:0.##}x 持续 {seconds:0.##}s");
    }

    // 立即结束减速（场景重载或调试复位用）
    public static void ClearSlowMotion()
    {
        TimeManager instance = Resolve();
        if (instance == null) return;
        if (InSlowMotion) Debug.Log("[TimeManager] 命中减速结束，恢复常速");
        instance._slowUntil = 0f;
        instance._slowScale = 1f;
    }
}
