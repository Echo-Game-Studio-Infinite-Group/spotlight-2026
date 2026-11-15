using System.Collections.Generic;
using Cinemachine;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// 死亡菜单持有暂停；只有明确选择重试或返回标题才释放。
public class DeathRespawnUI : MonoBehaviour
{
    public static DeathRespawnUI Instance { get; private set; }
    public bool IsOpen { get; private set; }
    public Button respawnButton;
    public Transform panelRoot;
    [SerializeField] private Button _returnButton;
    [SerializeField] private string _titleScenePath = "Assets/Scenes/MainMenu.unity";
    private bool _previousInputAllowed;
    private HealthComponent _health;
    private VectorEnergy _energy;
    private PlayerInputReader _input;
    private readonly List<CinemachineBrain> _frozenBrains = new List<CinemachineBrain>();
    private PlayerDeathAnimation playerDeath;
    private bool waitingForDeathAnimation;

    private void Awake()
    {
        Instance = this;
        panelRoot.gameObject.SetActive(false);
        respawnButton.onClick.AddListener(OnClickRespawn);
        _returnButton.onClick.AddListener(ReturnToTitle);
        TryBindPlayer();
    }

    private void Update()
    {
        if (_health == null) TryBindPlayer();
        if (!waitingForDeathAnimation || _health == null) return;
        if (_health.IsAlive) { waitingForDeathAnimation = false; return; }
        if (playerDeath != null && playerDeath.IsPlayingDeath) return;
        waitingForDeathAnimation = false;
        OpenPanel();
    }

    private void TryBindPlayer()
    {
        // 死亡表现会停用 Motor；玩家实体在死亡与复活期间始终可用于绑定。
        Player player = Player.Current;
        PlayerMotor motor = player != null ? player.Motor : PlayerMotor.Active;
        if (motor == null) return;
        _health = motor.GetComponent<HealthComponent>();
        _energy = motor.GetComponent<VectorEnergy>();
        playerDeath = motor.GetComponent<PlayerDeathAnimation>();
        _input = motor.GetComponent<PlayerInputReader>();
        if (_health != null)
        {
            _health.Died += OnPlayerDied;
            _health.Revived += OnPlayerRevived;
            if (!_health.IsAlive) waitingForDeathAnimation = true;
        }
    }

    private void OnPlayerDied(HealthComponent health)
    {
        // 下一帧再检查，避免死亡事件订阅顺序让面板抢先暂停动画。
        waitingForDeathAnimation = true;
    }

    private void OnPlayerRevived(HealthComponent health)
    {
        waitingForDeathAnimation = false;
        if (panelRoot != null && panelRoot.gameObject.activeSelf) ClosePanel();
    }

    public void OpenPanel()
    {
        if (_health != null && !_health.IsAlive && playerDeath != null && playerDeath.IsPlayingDeath)
        {
            waitingForDeathAnimation = true;
            return;
        }
        if (IsOpen) return;
        // 先收起普通暂停菜单，再接管暂停，避免两套菜单互相解除暂停。
        PauseUIManager pause = FindObjectOfType<PauseUIManager>();
        if (pause != null && TimeManager.IsPaused) pause.ResumeGame();
        if (_health == null) TryBindPlayer();
        _previousInputAllowed = GameManager.Instance.InputAllowed;
        IsOpen = true;
        GameManager.Instance.PauseGame();
        GameManager.Instance.InputAllowed = false;
        _input.SetGameplayEnabled(false);
        TimeManager.SetPaused(true);
        AudioListener.pause = true;
        foreach (CinemachineBrain brain in FindObjectsOfType<CinemachineBrain>())
        {
            if (!brain.enabled) continue;
            _frozenBrains.Add(brain);
            brain.enabled = false;
        }
        panelRoot.gameObject.SetActive(true);
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(respawnButton.gameObject);
    }

    public void ClosePanel()
    {
        if (!IsOpen) return;
        IsOpen = false;
        if (panelRoot != null) panelRoot.gameObject.SetActive(false);
        foreach (CinemachineBrain brain in _frozenBrains)
            if (brain != null) brain.enabled = true;
        _frozenBrains.Clear();
        TimeManager.SetPaused(false);
        AudioListener.pause = false;
        GameManager.Instance.ResumeGame();
        GameManager.Instance.InputAllowed = _previousInputAllowed;
        if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
    }

    private void OnClickRespawn()
    {
        if (!IsOpen) return;
        ClosePanel();
        var flow = GameJam.Flow.RunFlowController.Current;
        // 流式关卡由流程控制器恢复检查点，重建期间继续锁住玩家。
        if (flow != null && flow.Recover(true)) return;
        SaveManager.LoadPlayer();
        _health.Reset();
        if (_energy != null) _energy.ResetEnergy();
        _input.SetGameplayEnabled(true);
    }

    private void ReturnToTitle()
    {
#if !UNITY_EDITOR
        if (!Application.CanStreamedLevelBeLoaded(_titleScenePath))
        {
            Debug.LogError($"Scene is missing from Build Settings: {_titleScenePath}", this);
            return;
        }
#endif
        ClosePanel();
#if UNITY_EDITOR
        UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(
            _titleScenePath, new LoadSceneParameters(LoadSceneMode.Single));
#else
        SceneManager.LoadScene(_titleScenePath);
#endif
    }

    private void OnDestroy()
    {
        ClosePanel();
        if (Instance == this) Instance = null;
        if (_health != null)
        {
            _health.Died -= OnPlayerDied;
            _health.Revived -= OnPlayerRevived;
        }
    }
}
