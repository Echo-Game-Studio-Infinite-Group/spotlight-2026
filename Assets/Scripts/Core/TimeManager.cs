using System.Collections.Generic;
using UnityEngine;

// 时间层（框架 4.3 时间四层约定）：世界逻辑（敌人/投射物/机关）走 World，
// 玩家逻辑（移动/攻击/冷却/无敌/积能耗能）走 Player；输入缓冲/UI/相机走 unscaled 不经本类
public enum TimeLayer { World, Player }

// 缩放来源句柄：登记方持有，效果结束/对象失效/重开时调 Release 释放——防止"来源死了缩放还挂着"
public readonly struct TimeScaleHandle
{
    public readonly int Id;
    public readonly TimeLayer Layer;

    internal TimeScaleHandle(int id, TimeLayer layer)
    {
        Id = id;
        Layer = layer;
    }

    public bool IsValid => Id != 0;
}

// 分层时间管理（AGENTS.md 第 5 条的收口）：世界/玩家两层缩放 + hit-stop + 暂停
// 核心语义（战斗系统底层接口设计 §4.8）：
//   · 缩放按来源登记、同层取最小——时停/时缓/关卡效果各自登记互不覆盖，谁的效果谁释放
//   · 暂停 = 全层置零（UI/相机走 unscaled 不受影响）；与来源缩放、hit-stop 是三个独立机制
//   · hit-stop 与限时缩放的解除计时走真实时间轴（unscaled），不随自身缩放自锁
//   · 固定步 dt 与渲染帧 dt 分开产出：逻辑（FixedUpdate）读 *DeltaTime，表现层读 *RenderDeltaTime
// 旧版在 Update 里用 Time.deltaTime 算"固定步 dt"，一帧多 tick / 无 tick 时会错账；
// 本版在 FixedUpdate 里按 Time.fixedDeltaTime 重算，固定步语义正确
[DefaultExecutionOrder(-100)]
public class TimeManager : MonoBehaviour
{
    private struct ScaleSource
    {
        public int Id;
        public float Scale;             // (0,1]：时间层只减速不加速（技能加速走 Motor 速度，不走时间层）
        public string Name;             // 排查用：登记方写明自己是谁
        public float RemainingUnscaled; // >0 = 限时来源（如 parry 时缓），到时自动释放
    }

    private static TimeManager _instance;
    private static int _nextSourceId = 1;

    private readonly List<ScaleSource> _worldSources = new List<ScaleSource>();
    private readonly List<ScaleSource> _playerSources = new List<ScaleSource>();
    private bool _paused;

    [Header("直调兼容面（3C 测试/Inspector 用）：作为各层隐式来源参与取最小，与登记来源互不覆盖")]
    [Range(0f, 1f)] public float WorldScale = 1f;
    [Range(0f, 1f)] public float PlayerScale = 1f;

    private float _hitStopTimer;
    private float _hitStopScale = 1f;

    private float _worldTime;
    private float _playerTime;

    // —— 读数（实时计算：直调 Scale 字段后同帧立即生效；无实例时退化为引擎时间）——
    //    Time.deltaTime 在 FixedUpdate 内等于 fixedDeltaTime，两个上下文都语义正确
    public static float WorldDeltaTime => Time.fixedDeltaTime * WorldLayerScaleNow;
    public static float PlayerDeltaTime => Time.fixedDeltaTime * PlayerLayerScaleNow;
    public static float WorldRenderDeltaTime => Time.deltaTime * WorldLayerScaleNow;
    public static float PlayerRenderDeltaTime => Time.deltaTime * PlayerLayerScaleNow;
    public static float WorldTime => _instance != null ? _instance._worldTime : Time.time;
    public static float PlayerTime => _instance != null ? _instance._playerTime : Time.time;
    public static float UnscaledDeltaTime => Time.deltaTime;
    public static float UnscaledTime => Time.unscaledTime;
    public static bool InHitStop => _instance != null && _instance._hitStopTimer > 0f;
    public static bool IsPaused => _instance != null && _instance._paused;
    public static TimeManager Instance => _instance;

    // —— 兼容别名（3C 移动重写的调用面）：玩家固定步 dt 的显式命名，语义与 PlayerDeltaTime 相同 ——
    public static float PlayerFixedDeltaTime => PlayerDeltaTime;

    private static float WorldLayerScaleNow => _instance != null ? _instance.WorldLayerScale : 1f;
    private static float PlayerLayerScaleNow => _instance != null ? _instance.PlayerLayerScale : 1f;

    private void Awake()
    {
        // 单场景单实例；跨场景常驻由后续 Bootstrap 负责
        if (_instance != null && _instance != this)
        {
            Debug.LogWarning("[TimeManager] 场景中已存在 TimeManager，多余实例已自毁", this);
            Destroy(this);
            return;
        }
        _instance = this;
    }

    private void OnDestroy()
    {
        if (_instance == this) _instance = null;
    }

    private void Update()
    {
        float unscaled = Time.deltaTime;
        TickHitStop(unscaled);
        TickTimedSources(unscaled);
    }

    private void FixedUpdate()
    {
        // 时间戳只在固定步累计（一帧多 tick 不丢账）；dt 读数实时计算，直调 Scale 后同帧立即生效
        _worldTime += Time.fixedDeltaTime * WorldLayerScale * FreezeFactor;
        _playerTime += Time.fixedDeltaTime * PlayerLayerScale * FreezeFactor;
    }

    private float FreezeFactor => _hitStopTimer > 0f ? _hitStopScale : 1f;

    private float WorldLayerScale
    {
        get
        {
            if (_paused) return 0f;
            float scale = WorldScale;
            for (int i = 0; i < _worldSources.Count; i++)
            {
                if (_worldSources[i].Scale < scale) scale = _worldSources[i].Scale;
            }
            return scale;
        }
    }

    private float PlayerLayerScale
    {
        get
        {
            if (_paused) return 0f;
            float scale = PlayerScale;
            for (int i = 0; i < _playerSources.Count; i++)
            {
                if (_playerSources[i].Scale < scale) scale = _playerSources[i].Scale;
            }
            return scale;
        }
    }

    // —— 来源登记 —— //

    /// <summary>
    /// 登记一层持续缩放（时停=世界层 0；parry 时缓=世界层 0.05~0.1）。
    /// 同层多来源取最小；调用方持句柄，效果结束时 Release。scale 夹到 [0,1]。
    /// </summary>
    public static TimeScaleHandle RegisterScale(TimeLayer layer, float scale, string sourceName = "")
    {
        if (_instance == null)
        {
            Debug.LogWarning("[TimeManager] 场景中无 TimeManager，缩放未登记");
            return default;
        }
        return _instance.AddSource(layer, Mathf.Clamp01(scale), sourceName, 0f);
    }

    /// <summary>
    /// 登记限时缩放：时长走真实时间轴（unscaled），到时自动释放——登记方无需记得释放
    /// （如 parry 时缓：世界层已被压到 8%，若解除计时走世界时间会自锁拖成 20 倍时长）
    /// </summary>
    public static TimeScaleHandle RegisterTimedScale(TimeLayer layer, float scale, float unscaledDuration, string sourceName = "")
    {
        if (_instance == null)
        {
            Debug.LogWarning("[TimeManager] 场景中无 TimeManager，缩放未登记");
            return default;
        }
        return _instance.AddSource(layer, Mathf.Clamp01(scale), sourceName, Mathf.Max(0f, unscaledDuration));
    }

    /// <summary>释放一个来源（幂等：无效/已释放的句柄直接忽略）</summary>
    public static void Release(TimeScaleHandle handle)
    {
        if (_instance == null || !handle.IsValid) return;
        _instance.RemoveSource(handle);
    }

    /// <summary>暂停：全层置零。UI/相机/输入缓冲走 unscaled 不受影响；恢复传 false</summary>
    public static void SetPaused(bool paused)
    {
        if (_instance != null) _instance._paused = paused;
    }

    /// <summary>整局重开复位：清全部来源/暂停/hit-stop——"重开无残留"验收项的时间侧</summary>
    public static void ResetAll()
    {
        if (_instance == null) return;
        _instance._worldSources.Clear();
        _instance._playerSources.Clear();
        _instance._paused = false;
        _instance._hitStopTimer = 0f;
        _instance._hitStopScale = 1f;
    }

    // 打击帧冻结：seconds 内 world/player 时间同步降到 scale；重复调用取更长冻结与更小 scale。
    // 计时走真实时间轴，不随自身缩放自锁
    public static void HitStop(float seconds, float scale = 0.05f)
    {
        if (_instance == null)
        {
            Debug.LogWarning("[TimeManager] 场景中无 TimeManager，HitStop 未生效");
            return;
        }
        if (seconds > _instance._hitStopTimer) _instance._hitStopTimer = seconds;
        if (_instance._hitStopTimer > 0f)
        {
            // 更小 scale 不放宽：更长但更宽松的后续调用（连击后段命中）不得减弱已在进行的冻结
            _instance._hitStopScale = Mathf.Min(_instance._hitStopScale, Mathf.Clamp01(scale));
        }
    }

    private TimeScaleHandle AddSource(TimeLayer layer, float scale, string name, float duration)
    {
        var source = new ScaleSource
        {
            Id = _nextSourceId++,
            Scale = scale,
            Name = string.IsNullOrEmpty(name) ? "unnamed" : name,
            RemainingUnscaled = duration,
        };
        SourcesOf(layer).Add(source);
        return new TimeScaleHandle(source.Id, layer);
    }

    private void RemoveSource(TimeScaleHandle handle)
    {
        List<ScaleSource> list = SourcesOf(handle.Layer);
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Id == handle.Id)
            {
                list.RemoveAt(i);
                return;
            }
        }
    }

    private void TickTimedSources(float unscaled)
    {
        TickTimedList(_worldSources, unscaled);
        TickTimedList(_playerSources, unscaled);
    }

    private static void TickTimedList(List<ScaleSource> list, float unscaled)
    {
        // 倒序遍历：释放（RemoveAt）不影响未处理下标
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].RemainingUnscaled <= 0f) continue; // 持续型来源，等登记方释放
            list[i] = new ScaleSource
            {
                Id = list[i].Id,
                Scale = list[i].Scale,
                Name = list[i].Name,
                RemainingUnscaled = list[i].RemainingUnscaled - unscaled,
            };
            if (list[i].RemainingUnscaled <= 0f) list.RemoveAt(i);
        }
    }

    private void TickHitStop(float unscaled)
    {
        if (_hitStopTimer > 0f)
        {
            _hitStopTimer -= unscaled; // hitstop 时长走真实时间轴，不随缩放自锁
            if (_hitStopTimer <= 0f)
            {
                _hitStopTimer = 0f;
                _hitStopScale = 1f;
            }
        }
    }

    private List<ScaleSource> SourcesOf(TimeLayer layer) => layer == TimeLayer.World ? _worldSources : _playerSources;
}
