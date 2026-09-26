using UnityEngine;

// 灰盒调试 HUD（cl_showspeed 惯例）：F3 开关，显示速度（阈值倍数）/ 能量 / 泵油模式 / 着地与免摩擦窗口状态
// 用 IMGUI 而非 UI 预制体：预研阶段不引入任何 UI 资产与包依赖（AGENTS.md 第 8 条）
// 只读 PlayerMotor 的公开只读属性，不自己算一遍——调参口径必须与移动层同源
[RequireComponent(typeof(PlayerMotor))]
public class DebugHUD : MonoBehaviour
{
    [SerializeField] private bool _visible = true;
    [SerializeField] private KeyCode _toggleKey = KeyCode.F3;

    private const float PanelMargin = 8f;
    private const float PanelWidth = 300f;
    private const float PanelHeight = 120f;
    private const int FontSize = 14;

    private PlayerMotor _motor;
    private GUIStyle _style; // 懒建：GUIStyle 只在 OnGUI 的 GUI 上下文里创建才安全

    private void Awake()
    {
        _motor = GetComponent<PlayerMotor>();
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

        GUILayout.EndArea();
    }
}
