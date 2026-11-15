using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;

public enum GameState
{
    Start,
    Playing,
    Pause,
    End
}

// 局内流程与场景 UI 装配；只缓存预制体配置，不跨场景保存玩家和 UI 实例。
public class GameManager : MonoBehaviour
{
    private static GameManager _instance = null;
    private SceneUISettings _uiSettings;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        // 直接播放任意场景时，也必须在 sceneLoaded 前注册装配入口。
        _ = Instance;
    }

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

    private void Awake()
    {
        if (_instance && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnDestroy()
    {
        if (_instance != this) return;
        SceneManager.sceneLoaded -= OnSceneLoaded;
        _instance = null;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        EnsureSceneUI(scene);
    }

    public void EnsureSceneUI(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded) return;

        GameObject[] roots = scene.GetRootGameObjects();
        // 战斗 UI 的 Awake 会绑定玩家；标题和纯展示场景不装配战斗 UI。
        PlayerMotor player = FindInScene<PlayerMotor>(roots);
        PlayerInputReader input = FindInScene<PlayerInputReader>(roots);
        if (player == null || input == null || !player.gameObject.activeInHierarchy) return;

        if (TimeManager.Resolve() == null)
        {
            GameObject timer = new GameObject("TimeManager", typeof(TimeManager));
            SceneManager.MoveGameObjectToScene(timer, scene);
        }

        Transform layer = null;
        foreach (GameObject root in roots)
        {
            if (root.name != "UILayer") continue;
            layer = root.transform;
            break;
        }

        if (layer == null)
        {
            GameObject layerObject = new GameObject("UILayer");
            SceneManager.MoveGameObjectToScene(layerObject, scene);
            layer = layerObject.transform;
        }

        HealthBar healthBar = FindInScene<HealthBar>(roots);
        Canvas hud = healthBar != null ? healthBar.GetComponentInParent<Canvas>(true) : null;
        PauseUIManager pause = FindInScene<PauseUIManager>(roots);

        DeathRespawnUI death = FindInScene<DeathRespawnUI>(roots);

        if (hud == null || pause == null || death == null)
        {
            if (_uiSettings == null) _uiSettings = Resources.Load<SceneUISettings>("SceneUISettings");
            if (_uiSettings == null || _uiSettings.HUD == null || _uiSettings.PauseMenu == null || _uiSettings.DeathScreen == null)
            {
                Debug.LogError("[GameManager] 缺少 SceneUISettings 或 HUD / PauseMenu / DeathScreen 预制体引用", this);
                return;
            }
        }

        GameObject hudObject = hud != null
            ? hud.gameObject : Instantiate(_uiSettings.HUD, layer, false);
        GameObject pauseObject = pause != null
            ? pause.GetComponentInParent<Canvas>(true).gameObject
            : Instantiate(_uiSettings.PauseMenu, layer, false);
        hudObject.transform.SetParent(layer, false);
        pauseObject.transform.SetParent(layer, false);
        EnsureChaosDisplay(hudObject);

        // 暂停菜单必须盖住 HUD，避免 HUD 的射线检测挡住菜单按钮。
        Canvas pauseCanvas = pauseObject.GetComponent<Canvas>();
        pauseCanvas.sortingOrder = Mathf.Max(pauseCanvas.sortingOrder,
            hudObject.GetComponent<Canvas>().sortingOrder + 1);

        GameObject deathObject = death != null
            ? death.GetComponentInParent<Canvas>(true).gameObject
            : Instantiate(_uiSettings.DeathScreen, layer, false);
        deathObject.transform.SetParent(layer, false);
        deathObject.GetComponent<Canvas>().sortingOrder = pauseCanvas.sortingOrder + 1;

        if (FindObjectOfType<EventSystem>() == null)
        {
            GameObject eventSystem = new GameObject("EventSystem", typeof(EventSystem),
                typeof(InputSystemUIInputModule));
            SceneManager.MoveGameObjectToScene(eventSystem, scene);
            eventSystem.transform.SetParent(layer, false);
        }
    }

    // 场景自带 HUD 可能缺少混乱度显示，装配时兜底补齐，避免流程 UI 静默缺失。
    private void EnsureChaosDisplay(GameObject hudObject)
    {
        if (hudObject.GetComponentInChildren<ChaosHUD>(true) != null) return;
        if (_uiSettings == null) _uiSettings = Resources.Load<SceneUISettings>("SceneUISettings");
        if (_uiSettings == null || _uiSettings.ChaosDisplay == null)
        {
            Debug.LogError("[GameManager] HUD 缺少混乱度显示，且 SceneUISettings 未配置 ChaosDisplay 预制体", this);
            return;
        }

        Instantiate(_uiSettings.ChaosDisplay, hudObject.transform, false);
    }

    private static T FindInScene<T>(GameObject[] roots) where T : Component
    {
        foreach (GameObject root in roots)
        {
            T component = root.GetComponentInChildren<T>(true);
            if (component != null) return component;
        }

        return null;
    }

    public void StartGame()
    {
        Player player = Player.Current;
        if (player != null) player.ResetForNewRun();
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
        if (GameJam.Flow.RunFlowController.Current != null)
        {
            GameJam.Flow.RunFlowController.Current.RestartRun();
            return;
        }
        // 流式房间不是整局入口；重开要回到常驻场景，重建玩家并重新加载首房。
        if (MapManager.Exists && !string.IsNullOrEmpty(MapManager.Instance.BootstrapSceneName))
        {
            MapManager.Instance.TryRestartMap();
            return;
        }
        Scene active = SceneManager.GetActiveScene();

        if (active.buildIndex < 0)
        {
            Debug.LogError("[GameManager] 当前场景不在 Build Settings 中，无法重载", this);
            return;
        }

        // 玩家随场景重建，血量由新的 HealthComponent 从满开始，这里不再手动 Reset。
        SceneManager.LoadScene(active.buildIndex);
    }
}
