using UnityEngine;
using UnityEngine.UI;
using GameJam.Actions;

public class DeathRespawnUI : MonoBehaviour
{
    public static DeathRespawnUI Instance;

    public Button respawnButton;
    public Transform panelRoot;
    private HealthComponent playerHealth;

    void Awake()
    {
        Instance = this;

        if (panelRoot != null)
        {
            panelRoot.gameObject.SetActive(false);
        }

        GameObject player = Object.FindObjectOfType<PlayerMotor>().gameObject;
        if (player != null)
        {
            playerHealth = player.GetComponent<HealthComponent>();
            playerHealth.Damaged += OnPlayerTakeDamage;
        }

        if (respawnButton != null)
        {
            respawnButton.onClick.AddListener(OnClickRespawn);
        }
    }

    private void OnPlayerTakeDamage(HealthComponent health, float damage, Vector3 point, Vector3 dir)
    {
        if (health.Health <= 0)
        {
            OpenPanel();
        }
    }

    public void OpenPanel()
    {
        if (panelRoot != null)
        {
            panelRoot.gameObject.SetActive(true);
        }
        Time.timeScale = 0;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        Debug.Log("死亡面板已经打开");
    }

    public void ClosePanel()
    {
        if (panelRoot != null)
        {
            panelRoot.gameObject.SetActive(false);
        }
        Time.timeScale = 1;
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void OnClickRespawn()
    {
        Debug.Log("复活按钮被点击！");
        SaveManager.LoadPlayer();
        if (playerHealth != null)
        {
            playerHealth.Reset();
        }
        ClosePanel();
    }

    void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
        if (playerHealth != null)
        {
            playerHealth.Damaged -= OnPlayerTakeDamage;
        }
    }
}
