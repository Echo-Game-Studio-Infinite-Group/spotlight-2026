using System.Collections.Generic;
using UnityEngine;

// game 层只允许这两个值：它同时作用在 Time.timeScale 和固定步长上
public enum E_GameRate
{
    Stopped = 0,
    Running = 1,
}

// 三层时间：game（总闸）/ world / player
// 每层一个速率，层内可以有多个"时间来源"（时停 / 时缓 / 顿帧），同层取最小值
[DefaultExecutionOrder(-100)]
public sealed class TimeManager : MonoBehaviour
{
    public enum TimeLayer { Game, World, Player }

    private static TimeManager _instance;

    // 惰性解析：Awake 不保证跑过（编辑模式的装配工具与自检用 AddComponent 建实例时不会触发它），
    // 只有 _instance 为空时才兜底查找一次，让静态入口在任何模式下都拿得到有效实例。
    internal static TimeManager Resolve()
    {
        if (_instance == null) _instance = FindObjectOfType<TimeManager>();
        return _instance;
    }

    // 时间速率
    private E_GameRate _gameRate = E_GameRate.Running;

    // world / player 的速率来源（时停、时缓、顿帧往这里登记）
    private readonly List<Source> _worldSources = new List<Source>();
    private readonly List<Source> _playerSources = new List<Source>();

    // 一个时间来源：压到多快 + 什么时候自动恢复（Until = 0 表示不自动恢复）+ 是谁登记的
    private struct Source
    {
        public float Rate;
        public float Until;
        public object Owner;
    }

    // 各效果的来源标识：Apply / Release 都认它，取消一个不会误伤另一个
    private static readonly object SlowOwner = new object();
    private static readonly object HitStopOwner = new object();
    private static readonly object TimeStopOwner = new object();

    private bool _paused;

    [Header("时缓")]
    [Tooltip("持续秒数")]
    [Min(0f)] public float SlowSeconds = 1f;

    [Tooltip("时间流速降到的比例")]
    [Range(0f, 1f)] public float SlowRateValue = 0.05f;

    [Header("顿帧 / 命中减速")]
    [Tooltip("持续秒数（战斗侧调 SlowMotion 时传自己的值，这里是默认值）")]
    [Min(0f)] public float HitStopSeconds = 0.1f;

    [Tooltip("时间流速降到的比例")]
    [Range(0f, 1f)] public float HitStopRateValue = 0.05f;

    // 取某一层的来源表
    private static List<Source> SourcesOf(TimeLayer layer)
    {
        TimeManager instance = Resolve();
        if (instance == null) return null;
        switch (layer)
        {
            case TimeLayer.Game:
                return null;
            case TimeLayer.World:
                return instance._worldSources;
            case TimeLayer.Player:
                return instance._playerSources;
        }
        return null;
    }

    // 执行最慢速率：没有来源就是 1
    private static float MinRate(List<Source> sources)
    {
        float min = 1f;
        foreach (Source source in sources)
        {
            min = source.Rate < min ? source.Rate : min;
        }
        return min;
    }

    // 时间轴
    private float _gameTime;
    private float _worldTime;
    private float _playerTime;


    // 对外只读入口 
    public static float GameRate => Resolve() != null ? (float)Resolve()._gameRate : 1f;
    public static float WorldRate => Resolve() != null ? MinRate(Resolve()._worldSources) : 1f;
    public static float PlayerRate => Resolve() != null ? MinRate(Resolve()._playerSources) : 1f;

    public static float GameDeltaTime => Time.unscaledDeltaTime * GameRate;
    public static float WorldDeltaTime => Time.unscaledDeltaTime * WorldRate * GameRate;
    public static float PlayerDeltaTime => Time.unscaledDeltaTime * PlayerRate * GameRate;

    // 固定步基准
    public static float WorldFixedDeltaTime => Time.fixedUnscaledDeltaTime * WorldRate * GameRate;
    public static float PlayerFixedDeltaTime => Time.fixedUnscaledDeltaTime * PlayerRate * GameRate;

    public static float UnscaledDeltaTime => Time.unscaledDeltaTime;
    public static float UnscaledTime => Time.unscaledTime;

    public static float GameTime => Resolve() != null ? Resolve()._gameTime : Time.time;
    public static float WorldTime => Resolve() != null ? Resolve()._worldTime : Time.time;
    public static float PlayerTime => Resolve() != null ? Resolve()._playerTime : Time.time;

    public static bool IsPaused => Resolve() != null && Resolve()._paused;

    // 表现层用：任一层被压慢就是"减速中"，倍率取最慢的那层
    public static bool InSlowMotion => Mathf.Min(WorldRate, PlayerRate) < 1f;
    public static bool InHitStop
    {
        get
        {
            TimeManager instance = Resolve();
            if (instance == null) return false;
            foreach (Source source in instance._playerSources)
                if (ReferenceEquals(source.Owner, HitStopOwner) && (source.Until == 0f || Time.unscaledTime < source.Until)) return true;
            return false;
        }
    }
    public static float SlowRate => Mathf.Min(WorldRate, PlayerRate);

    // 暂停：game 层速率归 0，并写进 Time.timeScale 让物理/粒子/动画一起停。
    // world / player 自己的速率不动，恢复时接着用
    public static void SetPaused(bool paused)
    {
        TimeManager instance = Resolve();
        if (instance == null) return;

        instance._paused = paused;
        instance._gameRate = paused ? E_GameRate.Stopped : E_GameRate.Running;
        Time.timeScale = (float)instance._gameRate;
    }

    // 往某层加一个倍率；seconds > 0 就到点自动恢复，不传就一直留着（等 Release）
    public static void Apply(TimeLayer layer, float rate, float seconds = 0f, object owner = null)
    {
        SourcesOf(layer)?.Add(new Source
        {
            Rate = Mathf.Clamp01(rate),
            Until = seconds > 0f ? Time.unscaledTime + seconds : 0f,
            Owner = owner,
        });
    }

    // 把某层里这个来源登记的倍率全拿掉（按来源删，不会误伤别的效果）
    public static void Release(TimeLayer layer, object owner)
    {
        SourcesOf(layer)?.RemoveAll(source => source.Owner == owner);
    }

    // 顿帧 / 命中减速：命中那一刻短促一压，world 和 player 一起压，到点自动恢复
    public static void HitStop(float seconds = -1f, float rate = -1f)
    {
        TimeManager instance = Resolve();
        if (instance == null) return;

        if (seconds < 0f) seconds = instance.HitStopSeconds;
        if (rate < 0f) rate = instance.HitStopRateValue;

        Apply(TimeLayer.World, rate, seconds, HitStopOwner);
        Apply(TimeLayer.Player, rate, seconds, HitStopOwner);
    }

    // 把时缓和顿帧的来源都清掉（自检脚本复位用）
    public static void ClearSlowMotion()
    {
        Release(TimeLayer.World, SlowOwner);
        Release(TimeLayer.Player, SlowOwner);
        Release(TimeLayer.World, HitStopOwner);
        Release(TimeLayer.Player, HitStopOwner);
    }

    // 时停：只冻 world，玩家照常动。按住调 true，松开调 false，没有秒数
    public static void SetTimeStop(bool on)
    {
        if (on)
        {
            Apply(TimeLayer.World, 0f, 0f, TimeStopOwner);
        }
        else
        {
            Release(TimeLayer.World, TimeStopOwner);
        }
    }

    // 兼容：战斗侧（PlayerCombat 命中时）调的旧签名，转给 HitStop
    public static void SlowMotion(float seconds, float scale = 0.75f) => HitStop(seconds, scale);

    // 时缓：parry 成功时触发
    public static void SetSlowMotion(bool on)
    {
        TimeManager instance = Resolve();
        if (instance == null) return;

        if (on)
        {
            Apply(TimeLayer.World, instance.SlowRateValue, instance.SlowSeconds, SlowOwner);
            Apply(TimeLayer.Player, instance.SlowRateValue, instance.SlowSeconds, SlowOwner);
        }
        else
        {
            CancelSlowMotion();
        }
    }

    // 时缓结束：按任意键取消
    public static void CancelSlowMotion()
    {
        Release(TimeLayer.World, SlowOwner);
        Release(TimeLayer.Player, SlowOwner);
    }

   
    private void Awake()
    {
        _instance = this;
    }

    private void OnDestroy()
    {
        if (_instance == this) _instance = null;
        Time.timeScale = 1f;   // 别把 timeScale 留在 0
    }

    private void Update()
    {
        // 到期的来源自动拿掉
        float now = Time.unscaledTime;
        _worldSources.RemoveAll(source => source.Until > 0f && now >= source.Until);
        _playerSources.RemoveAll(source => source.Until > 0f && now >= source.Until);

        if (_paused) return;   // 暂停时时间轴不推进

        _gameTime += Time.unscaledDeltaTime * GameRate;
        _worldTime += Time.unscaledDeltaTime * WorldRate;
        _playerTime += Time.unscaledDeltaTime * PlayerRate;
    }
}
