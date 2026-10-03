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
    public Player Target;

    public float MaxHealth = 100f;
    public float Health;

    [Min(0f)] public float InvulnerableTime = 0.6f;
    [NonSerialized] private float _invulnerableUntil = float.NegativeInfinity;
    public bool IsAlive => Health > 0f;
    public bool IsInvulnerable => TimeManager.UnscaledTime < _invulnerableUntil;

    public void SetInvulnerableTime(float seconds) => InvulnerableTime = Mathf.Max(0f, seconds);

    public float Speed
    {
        get { return Target != null ? Target.Speed : 0f; }
    }

    public void Reset()
    {
        Health = MaxHealth;
        _invulnerableUntil = float.NegativeInfinity;
    }

    public float TakeDamage(float amount)
    {
        if (!IsAlive || amount <= 0f || IsInvulnerable) return 0f;
        float applied = Mathf.Min(Health, amount);
        Health -= applied;
        _invulnerableUntil = TimeManager.UnscaledTime + InvulnerableTime;
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
    }

    public void RegisterPlayer(Player player)
    {
        Player.Target = player;
    }

    public void StartGame()
    {
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
