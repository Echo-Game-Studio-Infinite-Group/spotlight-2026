using UnityEngine;
using UnityEngine.SceneManagement;

public enum GameState
{
    Start,
    Playing,
    Pause,
    End
}

// 整局流程壳（框架 4.5 关卡与复位）：状态机 + 暂停聚合
// 边界：血量真值在 HealthComponent、速度真值在 PlayerMotor、时间真值在 TimeManager——本类只聚合不复制
// 暂停三线合一：状态 + 时间层冻结 + 输入禁用，调用方只需 PauseGame/ResumeGame 一个入口
public class GameManager : MonoBehaviour
{
    private static GameManager _instance = null;

    public static GameManager Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = new GameObject("GameManager").AddComponent<GameManager>();
            }

            return _instance;
        }
    }

    public GameState State { get; private set; } = GameState.Start;
    public bool InputAllowed = true;

    // 玩家角色引用：只持引用不复制状态，血量/速度真值留在各自系统里
    public Player TargetPlayer;

    private void Awake()
    {
        if (_instance && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);
    }

    public void RegisterPlayer(Player player)
    {
        TargetPlayer = player;
    }

    public void StartGame()
    {
        InputAllowed = true;
        State = GameState.Playing;
    }

    public void PauseGame()
    {
        if (State != GameState.Playing) return;

        InputAllowed = false;
        State = GameState.Pause;

        // 暂停必须三线联动：只改状态的话 TimeManager 仍在推进逻辑、输入仍在采样，
        // InputBuffer 会在暂停期间积累旧按下沿，恢复瞬间全部放出导致误触发
        TimeManager.SetPaused(true);
        PlayerInputReader input = FindFirstObjectByType<PlayerInputReader>();
        if (input != null) input.SetGameplayEnabled(false);
    }

    public void ResumeGame()
    {
        if (State != GameState.Pause) return;

        InputAllowed = true;
        State = GameState.Playing;

        // 与 PauseGame 对称恢复：时间解冻、输入恢复采样
        TimeManager.SetPaused(false);
        PlayerInputReader input = FindFirstObjectByType<PlayerInputReader>();
        if (input != null) input.SetGameplayEnabled(true);
    }

    public void EndGame()
    {
        InputAllowed = false;
        State = GameState.End;
    }

    public void RestartLevel()
    {
        // 血量/能量随场景重建自然复位，不手动清状态（TimeManager 是场景级组件，随场景销毁自清）
        Scene active = SceneManager.GetActiveScene();

        if (active.buildIndex < 0)
        {
            Debug.LogError("[GameManager] 当前场景不在 Build Settings 中，无法重载", this);
            return;
        }

        SceneManager.LoadScene(active.buildIndex);
    }
}
