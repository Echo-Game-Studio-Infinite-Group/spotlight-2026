using UnityEngine;
using UnityEngine.UI;
using GameJam.Actions;

public class DeathRespawnUI : MonoBehaviour
{
    public Button respawnButton;
    private HealthComponent playerHealth;

    void Awake()
    {
        //初始关闭面板
        gameObject.SetActive(false);

        //查找玩家血量组件
        GameObject player = Object.FindObjectOfType<PlayerMotor>().gameObject;
        if (player != null)
        {
            playerHealth = player.GetComponent<HealthComponent>();
            //订阅玩家血量变化事件，血量为0弹出失败面板
            playerHealth.Damaged += OnPlayerTakeDamage;
        }

        if (respawnButton != null)
        {
            respawnButton.onClick.AddListener(OnClickRespawn);
        }
    }

    private void OnPlayerTakeDamage(HealthComponent health, float damage, Vector3 point, Vector3 dir)
    {
        //血量等于0，弹出死亡面板
        if (health.Health <= 0)
        {
            OpenPanel();
        }
    }

    public void OpenPanel()
    {
        gameObject.SetActive(true);
        //暂停游戏时间
        Time.timeScale = 0;
    }

    public void ClosePanel()
    {
        gameObject.SetActive(false);
        Time.timeScale = 1;
    }

    //复活按钮点击
    private void OnClickRespawn()
    {
        //读取存档点存档，回到存档位置
        SaveManager.LoadPlayer();

        //恢复玩家全部血量
        if (playerHealth != null)
        {
            playerHealth.Reset();
        }

        ClosePanel();
    }

    void OnDestroy()
    {
        if (playerHealth != null)
        {
            playerHealth.Damaged -= OnPlayerTakeDamage;
        }
    }
}
