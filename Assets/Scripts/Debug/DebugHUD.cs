using UnityEngine;

// 灰盒调试 HUD（cl_showspeed 惯例）：F3 开关，显示速度（阈值倍数）/ 能量 / 泵油模式 / 着地与免摩擦窗口状态
// 战斗面板（设计 §五）：当前招式/段/相位/可取消目标列表/派生窗口/连击数——取消表是否「转得动」必须肉眼可验
// 用 IMGUI 而非 UI 预制体：预研阶段不引入任何 UI 资产与包依赖（AGENTS.md 第 8 条）
// 只读 PlayerMotor / PlayerCombat 的公开只读属性，不自己算一遍——调参口径必须与逻辑层同源
[RequireComponent(typeof(PlayerMotor))]
public class DebugHUD : MonoBehaviour
{
    [SerializeField] private bool _visible = true;
    [SerializeField] private KeyCode _toggleKey = KeyCode.F3;

    [Header("连击统计（设计 §五）")]
    [Tooltip("连击窗口（秒）：窗口内连续命中累计连击数，超窗归零。占位数值待策划核")]
    [SerializeField] private float _comboWindowSec = 2f;

    private const float PanelMargin = 8f;
    private const float PanelWidth = 300f;
    private const float PanelHeight = 200f;
    private const int FontSize = 14;

    private PlayerMotor _motor;
    private PlayerCombat _combat; // 可选：未装配战斗组件时战斗面板静默隐藏
    private GUIStyle _style; // 懒建：GUIStyle 只在 OnGUI 的 GUI 上下文里创建才安全

    private Hitbox _damageHitbox;
    private int _comboCount;
    private float _lastHitUnscaledTime = -999f;

    private void Awake()
    {
        _motor = GetComponent<PlayerMotor>();
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

    private void OnDestroy()
    {
        // 订阅随绑定解除（框架 4.4）：HUD 销毁不得让 Hitbox 持有失效委托
        if (_damageHitbox != null) _damageHitbox.HitResolved -= OnHitResolved;
    }

    // 连击窗走 unscaled 时间（框架 4.3：UI/表现不缩放）——hit-stop/时缓不得拉长连击窗口
    private void OnHitResolved(DamageInfo info)
    {
        float now = TimeManager.UnscaledTime;
        if (now - _lastHitUnscaledTime > _comboWindowSec) _comboCount = 0;
        _comboCount++;
        _lastHitUnscaledTime = now;
    }

    private void Update()
    {
        // 旧版 Input Manager 直读（AGENTS.md 第 4 条）；走 unscaled 的 Update，暂停/慢动作下 HUD 仍可开关
        if (Input.GetKeyDown(_toggleKey)) _visible = !_visible;
    }

    private void OnGUI()
    {
        if (!_visible) return;

        if (_style == null)
        {
            _style = new GUIStyle(GUI.skin.label) { fontSize = FontSize };
        }

        GUI.Box(new Rect(PanelMargin, PanelMargin, PanelWidth, PanelHeight), GUIContent.none);

        Rect inner = new Rect(PanelMargin * 2f, PanelMargin * 1.5f,
                              PanelWidth - PanelMargin * 3f, PanelHeight - PanelMargin * 2f);
        GUILayout.BeginArea(inner);

        MovementParams parameters = _motor != null ? _motor.Params : null;
        if (parameters == null)
        {
            GUILayout.Label("PlayerMotor / MovementParams 未就绪", _style);
            GUILayout.EndArea();
            return;
        }

        float speed = _motor.HorizontalSpeed;
        // 地速阈值为 0 时倍数无意义（参数被误配），退化成只报绝对值
        string speedLine = parameters.GroundSpeedThreshold > 0f
            ? string.Format("速度 {0:F2} m/s  ({1:F2}x 阈值)", speed, speed / parameters.GroundSpeedThreshold)
            : string.Format("速度 {0:F2} m/s", speed);

        GUILayout.Label(speedLine, _style);
        GUILayout.Label(string.Format("能量 {0:F1} / {1:F1}", _motor.Energy, parameters.EnergyMax), _style);
        GUILayout.Label(string.Format("模式 {0}", parameters.Pump), _style);

        string state = _motor.IsGrounded ? "着地" : "空中";
        if (_motor.IsSliding) state += " · 滑铲";
        if (_motor.InFrictionWindow)
        {
            // 免摩擦窗口是兔子跳唯一的加速来源，剩余时间必须可见——否则泵油失败无法定位
            state += string.Format(" · 免摩擦窗口剩余 {0:F3}s", _motor.FrictionWindowRemaining);
        }
        GUILayout.Label(state, _style);

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
