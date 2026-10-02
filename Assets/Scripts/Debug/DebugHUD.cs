using UnityEngine;

// 灰盒调试 HUD（cl_showspeed 惯例）：开关走 PlayerInputReader.ToggleHUD（F3，新 Input System）
// 移动面板读 PlayerMotor 重写后的状态（State/墙面窗口/入墙角）；
// 战斗面板（设计 §五）：当前招式/段/相位/可取消目标列表/派生窗口/连击数——取消表是否「转得动」必须肉眼可验
// 只读组件公开只读属性，不自己算一遍——调参口径必须与逻辑层同源
[RequireComponent(typeof(PlayerMotor))]
public sealed class DebugHUD : MonoBehaviour
{
    [SerializeField] private bool _visible = true;

    [Header("连击统计（设计 §五）")]
    [Tooltip("连击窗口（秒）：窗口内连续命中累计连击数，超窗归零。占位数值待策划核")]
    [SerializeField] private float _comboWindowSec = 2f;

    private PlayerMotor _motor;
    private PlayerInputReader _input;
    private PlayerCombat _combat; // 可选：未装配战斗组件时战斗面板静默隐藏
    private GUIStyle _style;

    private Hitbox _damageHitbox;
    private int _comboCount;
    private float _lastHitUnscaledTime = -999f;

    private void Awake()
    {
        _motor = GetComponent<PlayerMotor>();
        _input = GetComponent<PlayerInputReader>();
        _combat = GetComponent<PlayerCombat>();

        // 连击数挂攻击侧命中事件（设计 §五）；框不在本物体上则面板无连击行
        foreach (Hitbox box in GetComponents<Hitbox>())
        {
            if (box.Kind != HitboxKind.Damage) continue;
            _damageHitbox = box;
            break;
        }
        if (_damageHitbox != null) _damageHitbox.HitResolved += OnHitResolved;
    }

    private void OnEnable()
    {
        if (_input != null) _input.ToggleHUD += Toggle;
    }

    private void OnDisable()
    {
        if (_input != null) _input.ToggleHUD -= Toggle;
        if (_damageHitbox != null) _damageHitbox.HitResolved -= OnHitResolved; // 订阅随绑定解除
    }

    private void Toggle() => _visible = !_visible;

    // 连击窗走 unscaled 时间（UI/表现不缩放）——hit-stop/时缓不得拉长连击窗口
    private void OnHitResolved(DamageInfo info)
    {
        float now = TimeManager.UnscaledTime;
        if (now - _lastHitUnscaledTime > _comboWindowSec) _comboCount = 0;
        _comboCount++;
        _lastHitUnscaledTime = now;
    }

    private void OnGUI()
    {
        if (!_visible || _motor.Params == null) return;
        if (_style == null) _style = new GUIStyle(GUI.skin.label) { fontSize = 14 };
        GUI.Box(new Rect(8f, 8f, 370f, 230f), GUIContent.none);
        GUILayout.BeginArea(new Rect(16f, 16f, 350f, 210f));
        GUILayout.Label($"速度 {_motor.HorizontalSpeed:F2} / {_motor.Params.MaxSpeed:F0} m/s", _style);
        GUILayout.Label($"状态 {_motor.State}{(_motor.IsSliding ? " · 滑铲" : "")}", _style);
        GUILayout.Label($"地面窗口 {_motor.FrictionWindowRemaining:F3}s · 墙面窗口 {_motor.WallWindowRemaining:F3}s", _style);
        GUILayout.Label($"入墙角 {_motor.WallApproachAngle:F1}° · 蹬墙 {_motor.WallJumpCount}", _style);
        GUILayout.Label($"能量 {_motor.Energy:F1} / {_motor.Params.EnergyMax:F0}", _style);
        GUILayout.Label("WASD 移动 · Shift 奔跑 · Ctrl 滑铲", _style);
        GUILayout.Label("Space 跳跃/蹬墙 · Esc 鼠标锁定 · F3 面板", _style);

        // —— 战斗面板（设计 §五：调手感标配）——
        if (_combat != null)
        {
            string combat;
            if (_combat.IsAttacking && _combat.CurrentDefinition != null)
            {
                combat = string.Format("招式 {0} · 段 {1}/{2} · {3} ({4:F3}s)",
                    _combat.CurrentDefinition.name,
                    _combat.CurrentSegmentIndex + 1,
                    _combat.CurrentDefinition.Phases.Length,
                    _combat.CurrentPhase,
                    _combat.TimeInPhase);
            }
            else
            {
                combat = "招式 无（待机）";
            }
            GUILayout.Label(combat, _style);

            if (_combat.DeriveWindowRemaining > 0f)
            {
                GUILayout.Label(string.Format("派生窗口剩余 {0:F3}s（左键→闪斩）", _combat.DeriveWindowRemaining), _style);
            }

            string cancels = _combat.DescribeCancelOptions();
            if (!string.IsNullOrEmpty(cancels))
            {
                GUILayout.Label("可取消: " + cancels, _style);
            }

            if (_damageHitbox != null && _comboCount > 0)
            {
                GUILayout.Label(string.Format("连击 x{0}（窗口 {1:F1}s）", _comboCount, _comboWindowSec), _style);
            }
        }

        GUILayout.EndArea();
    }
}
