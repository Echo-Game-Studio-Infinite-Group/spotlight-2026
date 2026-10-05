using UnityEngine;

[RequireComponent(typeof(PlayerMotor))]
public sealed class DebugHUD : MonoBehaviour
{
    [SerializeField] private bool _visible = true;
    private PlayerMotor _motor;
    private PlayerInputReader _input;

    // 能量改读新的能量账户：同物体上的 VectorEnergy。不能再用 PlayerMotor.Energy：
    // 那是移动侧的旧实现，走帧缓存 dt + 每秒系数，与 VectorEnergy 的每 tick 结算不是同一条链
    // 取接口而不是具体类型：将来换实现（或并入空气那条线）这里不用改
    private IEnergyAccount _energy;

    private GUIStyle _style;

    private void Awake()
    {
        _motor = GetComponent<PlayerMotor>();
        _input = GetComponent<PlayerInputReader>();
        _energy = GetComponent<IEnergyAccount>();
    }
    private void OnEnable() { if (_input != null) _input.ToggleHUD += Toggle; }
    private void OnDisable() { if (_input != null) _input.ToggleHUD -= Toggle; }
    private void Toggle() => _visible = !_visible;
    private void OnGUI()
    {
        if (!_visible || _motor.Params == null) return;
        if (_style == null) _style = new GUIStyle(GUI.skin.label) { fontSize = 14 };
        GUI.Box(new Rect(8f, 8f, 370f, 250f), GUIContent.none);
        GUILayout.BeginArea(new Rect(16f, 16f, 350f, 230f));
        GUILayout.Label($"速度 {_motor.HorizontalSpeed:F2} / {_motor.Params.MaxSpeed:F0} m/s", _style);
        GUILayout.Label($"状态 {_motor.State}{(_motor.IsSliding ? " · 滑铲" : "")}", _style);
        GUILayout.Label($"地面窗口 {_motor.FrictionWindowRemaining:F3}s · 墙面窗口 {_motor.WallWindowRemaining:F3}s", _style);
        GUILayout.Label($"入墙角 {_motor.WallApproachAngle:F1}° · 蹬墙 {_motor.WallJumpCount}", _style);
        // 能量行改走能量账户，读不到时明确写出来，避免把"没接上"误看成"能量是 0"
        GUILayout.Label(_energy != null
            ? $"能量 {_energy.CurrentEnergy:F1} / {_energy.MaxEnergy:F0}"
            : "能量 未接入 IEnergyAccount", _style);
        DrawCombatLines();
        GUILayout.Label("WASD 移动 · Shift 奔跑 · Ctrl 滑铲", _style);
        GUILayout.Label("Space 跳跃/蹬墙 · 鼠标左键 攻击 · Esc 鼠标锁定 · F3 面板", _style);
        GUILayout.EndArea();
    }

    // 战斗数值单独一段：血量与减速是「打击感是否生效」最直接的两个观察点
    private void DrawCombatLines()
    {
        // 玩家血量走场景级注册点；读不到时明确写出来，避免把"没接上"误看成"血量是 0"
        HealthComponent playerHealth = Player.Current != null ? Player.Current.Health : null;
        GUILayout.Label(playerHealth != null
            ? $"玩家血量 {playerHealth.Health:F0} / {playerHealth.MaxHealth:F0}"
            : "玩家血量 未接入 HealthComponent", _style);

        Enemy enemy = null;
        float nearest = float.PositiveInfinity;
        foreach (Enemy candidate in FindObjectsOfType<Enemy>())
        {
            float distance = (candidate.transform.position - transform.position).sqrMagnitude;
            if (distance >= nearest) continue;
            nearest = distance;
            enemy = candidate;
        }
        if (enemy == null)
        {
            GUILayout.Label("敌人 未找到", _style);
        }
        else
        {
            GUILayout.Label($"敌人 {enemy.name} 血量 {enemy.Health:F0} / {enemy.MaxHealth:F0}", _style);
        }

        GUILayout.Label(TimeManager.InSlowMotion
            ? $"命中减速中 {TimeManager.SlowRate:0.##}x · 速率 {TimeManager.PlayerRate:0.##}"
            : $"常速 {TimeManager.PlayerRate:0.##}", _style);
    }
}
