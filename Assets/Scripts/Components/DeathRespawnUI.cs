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
    }

    private void TryBindPlayer()
    {
        PlayerMotor motor = PlayerMotor.Active;
        if (motor == null) return;
        _health = motor.GetComponent<HealthComponent>();
        _energy = motor.GetComponent<VectorEnergy>();
        _input = motor.GetComponent<PlayerInputReader>();
        if (_health != null) _health.Died += OnPlayerDied;
    }

    private void OnPlayerDied(HealthComponent health) => OpenPanel();

    public void OpenPanel()
    {
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
        if (_health != null) _health.Died -= OnPlayerDied;
    }
}
