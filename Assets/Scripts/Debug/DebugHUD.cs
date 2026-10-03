using UnityEngine;

[RequireComponent(typeof(PlayerMotor))]
public sealed class DebugHUD : MonoBehaviour
{
    [SerializeField] private bool _visible = true;
    private PlayerMotor _motor;
    private PlayerInputReader _input;
    private GUIStyle _style;

    private void Awake() { _motor = GetComponent<PlayerMotor>(); _input = GetComponent<PlayerInputReader>(); }
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
        GUILayout.Label($"能量 {_motor.Energy:F1} / {_motor.Params.EnergyMax:F0}", _style);
        DrawCombatLines();
        GUILayout.Label("WASD 移动 · Shift 奔跑 · Ctrl 滑铲", _style);
        GUILayout.Label("Space 跳跃/蹬墙 · 鼠标左键 攻击 · Esc 鼠标锁定 · F3 面板", _style);
        GUILayout.EndArea();
    }

    // 战斗数值单独一段：血量与减速是「打击感是否生效」最直接的两个观察点
    private void DrawCombatLines()
    {
        PlayerData health = GameManager.Instance.Player;
        GUILayout.Label($"玩家血量 {health.Health:F0} / {health.MaxHealth:F0}", _style);

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
