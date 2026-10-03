using System;
using UnityEngine;

// 玩家招架（trigger 方案版，D3–D5 验收项）：纯时间窗——敌盒撞到玩家本体的那一下就是判定时机，
// 窗口宽度即宽松度（策划案「parry 要给得宽松」）；判定几何复用敌盒的 trigger 事件，不新增判定体
// 效果链：格挡（本次攻击不结算伤害）→ 世界时缓 → 派生窗口（下一击伤害放大，闪斩占位）；
// 反弹击退、无敌帧、闪斩独立动画待表现层与内容阶段接入（Parried 事件是订阅口）
public class PlayerParry : MonoBehaviour
{
    [Header("窗口（数值占位，待策划核）")]
    [Tooltip("招架窗口（秒，自按下招架键起）——窗口宽度即判定宽松度")]
    [SerializeField, Min(0.02f)] private float _windowSec = 0.25f;

    [Tooltip("招架冷却（秒，unscaled）：窗口结束到下次可招架")]
    [SerializeField, Min(0f)] private float _cooldownSec = 0.8f;

    [Header("成功效果")]
    [Tooltip("parry 成功的世界时缓时长（秒，unscaled——不随自身缩放自锁）")]
    [SerializeField, Min(0f)] private float _slowSec = 0.35f;

    [Tooltip("parry 时缓比例（世界层）")]
    [SerializeField, Range(0.05f, 1f)] private float _slowScale = 0.08f;

    [Tooltip("派生窗口（秒）：parry 后按攻击出派生攻击（闪斩占位），时长待策划核")]
    [SerializeField, Min(0f)] private float _deriveWindowSec = 0.5f;

    /// <summary>当前招架窗口是否开着（Hitbox 结算前查询）</summary>
    public bool WindowOpen => TimeManager.UnscaledTime < _windowEnd;

    /// <summary>招架成功（已格挡一次攻击）：音效/特效/镜头订阅；PlayerCombat 派生窗在本类内部接线</summary>
    public event Action<Hitbox> Parried;

    private PlayerInputReader _input;
    private PlayerCombat _combat; // 惰性解析：装配顺序不保证（先挂谁都有可能），格挡时现找
    private float _windowEnd = float.NegativeInfinity;
    private float _readyAt = float.NegativeInfinity;

    private void Awake()
    {
        _input = GetComponentInParent<PlayerInputReader>();
    }

    private void Update()
    {
        if (_input != null && _input.ConsumeSkill(TimeManager.UnscaledTime)) TryOpenParry();
    }

    /// <summary>按下招架键：开窗（冷却中失败）。输入接线走 Skill 键</summary>
    public bool TryOpenParry()
    {
        float now = TimeManager.UnscaledTime;
        if (now < _readyAt) return false;
        _windowEnd = now + _windowSec;
        _readyAt = _windowEnd + _cooldownSec;
        return true;
    }

    /// <summary>
    /// 敌方 Hitbox 结算前的查询（StrikeEnemyCamp 最前调用）：窗口内 → 格挡本次攻击并触发效果链，
    /// 返回 true（调用方跳过伤害与去重——格挡不占「同挥砍仅一次」名额）
    /// </summary>
    public bool TryParry(Hitbox incoming)
    {
        if (!WindowOpen) return false;
        _windowEnd = float.NegativeInfinity; // 一次窗口只格挡一击：宽松但不可连挡
        if (_slowSec > 0f) TimeManager.SlowMotion(_slowSec, _slowScale);
        if (_combat == null) _combat = GetComponentInParent<PlayerCombat>();
        if (_combat != null) _combat.OpenDeriveWindow(_deriveWindowSec); // 派生窗内接线
        Parried?.Invoke(incoming);
        return true;
    }
}
