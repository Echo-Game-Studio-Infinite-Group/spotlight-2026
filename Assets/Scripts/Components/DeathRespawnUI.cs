using UnityEngine;
using UnityEngine.UI;

// 挑战失败面板：玩家死亡（HealthComponent.Died）或按 R 键（SaveManager 转发）时弹出。
// 复活：传送回存档点、血量回满、能量清零。
// 暂停走 dev 的 TimeManager.SetPaused，不直接散写 Time.timeScale（dev 工程规范）。
public class DeathRespawnUI : MonoBehaviour
{
    public static DeathRespawnUI Instance { get; private set; }

    public Button respawnButton;
    public Transform panelRoot;

    private HealthComponent playerHealth;
    private VectorEnergy playerEnergy;
    private PlayerDeathAnimation playerDeath;
    private bool waitingForDeathAnimation;

    private void Awake()
    {
        Instance = this;

        if (panelRoot != null)
        {
            panelRoot.gameObject.SetActive(false);
        }
        if (respawnButton != null)
        {
            respawnButton.onClick.AddListener(OnClickRespawn);
        }

        TryBindPlayer();
    }

    private void Update()
    {
        // 玩家可能晚于 UI 加载，未绑定时每帧重试一次。
        if (playerHealth == null)
        {
            TryBindPlayer();
        }
        if (!waitingForDeathAnimation || playerHealth == null) return;
        if (playerHealth.IsAlive) { waitingForDeathAnimation = false; return; }
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

        playerHealth = motor.GetComponent<HealthComponent>();
        playerEnergy = motor.GetComponent<VectorEnergy>();
        playerDeath = motor.GetComponent<PlayerDeathAnimation>();
        if (playerHealth != null)
        {
            playerHealth.Died += OnPlayerDied;
            playerHealth.Revived += OnPlayerRevived;
            if (!playerHealth.IsAlive) waitingForDeathAnimation = true;
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
        if (playerHealth != null && !playerHealth.IsAlive && playerDeath != null && playerDeath.IsPlayingDeath)
        {
            waitingForDeathAnimation = true;
            return;
        }
        if (panelRoot != null)
        {
            panelRoot.gameObject.SetActive(true);
        }
        TimeManager.SetPaused(true);
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    public void ClosePanel()
    {
        if (panelRoot != null)
        {
            panelRoot.gameObject.SetActive(false);
        }
        TimeManager.SetPaused(false);
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void OnClickRespawn()
    {
        SaveManager.LoadPlayer();
        // Reset 满血并在死亡时触发 Revived，dev 侧 Player 据此复位动作系统。
        if (playerHealth != null)
        {
            playerHealth.Reset();
        }
        if (playerEnergy != null)
        {
            playerEnergy.ResetEnergy();
        }
        ClosePanel();
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
        if (playerHealth != null)
        {
            playerHealth.Died -= OnPlayerDied;
            playerHealth.Revived -= OnPlayerRevived;
        }
    }
}
