using System;
using UnityEngine;
using UnityEngine.SceneManagement;

public enum GameState
{
    Start,
    Playing,
    Pause,
    End
}

[Serializable]
public class PlayerData
{
    public PlayerMotor Target;

    public float MaxHealth = 100f;
    public float Health;
    [SerializeField, Min(0f)] private float _invulnerableTime = 0.6f;
    [NonSerialized] private float _invulnerableUntil = float.NegativeInfinity;

    public bool IsAlive => Health > 0f;
    public bool IsInvulnerable => TimeManager.UnscaledTime < _invulnerableUntil;
    public float InvulnerableTime => _invulnerableTime;

    public void SetInvulnerableTime(float seconds)
    {
        _invulnerableTime = Mathf.Max(0f, seconds);
    }

    public float Speed
    {
        get { return Target != null ? Target.HorizontalSpeed : 0f; }
    }

    public void Reset()
    {
        Health = MaxHealth;
        _invulnerableUntil = float.NegativeInfinity;
        if (Target != null)
        {
            GameJam.Actions.PlayerActionRunner runner = Target.GetComponent<GameJam.Actions.PlayerActionRunner>();
            if (runner != null) { runner.SetAlive(true); runner.ResetActions(); }
        }
    }

    public float TakeDamage(float amount)
    {
        if (!IsAlive || amount <= 0f || IsInvulnerable) return 0f;

        float applied = Mathf.Min(amount, Health);
        Health -= applied;
        if (Health <= 0f && Target != null) Target.GetComponent<GameJam.Actions.PlayerActionRunner>()?.SetAlive(false);
        // 所有玩家受击入口共享无敌帧，避免同一帧被多个碰撞体重复扣血。
        _invulnerableUntil = TimeManager.UnscaledTime + _invulnerableTime;
        return applied;
    }

    public void Heal(float amount)
    {
        Health = Mathf.Min(MaxHealth, Health + amount);
    }
}

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

    public PlayerData Player = new PlayerData();

    private void Awake()
    {
        if (_instance && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);
        Player.Reset();
        FindPlayer();
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        if (_instance == this) _instance = null;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        FindPlayer();
    }

    private void FindPlayer()
    {
        GameObject player = GameObject.FindWithTag("Player");
        // 模型子节点也可能带 Player 标签，沿父级找到唯一运动组件。
        Player.Target = player != null ? player.GetComponentInParent<PlayerMotor>() : null;
    }

    public void StartGame()
    {
        FindPlayer();
        Player.Reset();
        InputAllowed = true;
        State = GameState.Playing;
    }

    public void PauseGame()
    {
        if (State != GameState.Playing) return;

        InputAllowed = false;
        State = GameState.Pause;
    }

    public void ResumeGame()
    {
        if (State != GameState.Pause) return;

        InputAllowed = true;
        State = GameState.Playing;
    }

    public void EndGame()
    {
        InputAllowed = false;
        State = GameState.End;
    }

    public void RestartLevel()
    {
        Player.Reset();

        Scene active = SceneManager.GetActiveScene();

        if (active.buildIndex < 0)
        {
            Debug.LogError("[GameManager] 当前场景不在 Build Settings 中，无法重载", this);
            return;
        }

        SceneManager.LoadScene(active.buildIndex);
    }
}
