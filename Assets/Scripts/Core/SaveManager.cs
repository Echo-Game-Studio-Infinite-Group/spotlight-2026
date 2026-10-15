using UnityEngine;

public static class SaveManager
{
    public class SaveData
    {
        public Vector3 playerPosition;
    }

    private static GameObject hiddenRunner;
    private static SaveManagerRunner runner;
    public static GameObject PlayerObj { get; set; }

    public static void Init(GameObject player)
    {
        if (hiddenRunner != null) return;
        PlayerObj = player;
        hiddenRunner = new GameObject("SaveManager_HiddenRunner");
        hiddenRunner.hideFlags = HideFlags.HideAndDontSave;
        runner = hiddenRunner.AddComponent<SaveManagerRunner>();
    }

    public static void SavePlayer()
    {
        if (PlayerObj == null)
        {
            Debug.LogWarning("Player对象为空，保存失败");
            return;
        }
        SaveData data = new SaveData();
        data.playerPosition = PlayerObj.transform.position;
        string json = JsonUtility.ToJson(data);
        PlayerPrefs.SetString("playerSave", json);
        PlayerPrefs.Save();
        Debug.Log("按键存档成功");
    }

    public static void SaveAtHitPoint()
    {
        if (PlayerObj == null)
        {
            Debug.LogWarning("Player对象为空，攻击存档点保存失败");
            return;
        }
        SaveData data = new SaveData();
        data.playerPosition = PlayerObj.transform.position;
        string json = JsonUtility.ToJson(data);
        PlayerPrefs.SetString("playerSave", json);
        PlayerPrefs.Save();
        Debug.Log("攻击命中存档点，保存成功");
    }

    public static void LoadPlayer()
    {
        if (PlayerObj == null)
        {
            Debug.LogWarning("Player对象为空，读档失败");
            return;
        }
        string json = PlayerPrefs.GetString("playerSave", null);
        if (string.IsNullOrEmpty(json))
        {
            Debug.Log("没有存档");
            return;
        }
        SaveData data = JsonUtility.FromJson<SaveData>(json);
        if (PlayerMotor.Active != null)
        {
            PlayerMotor.Active.Teleport(data.playerPosition);
        }
        Debug.Log("读档成功，已传送玩家位置");
    }

    public static void ClearPlayerSave()
    {
        PlayerPrefs.DeleteKey("playerSave");
        PlayerPrefs.Save();
        Debug.Log("存档已清除");
    }

    public static void ShowDeathPanel()
    {
        Debug.Log("R键触发调用ShowDeathPanel");
        if (DeathRespawnUI.Instance != null)
        {
            DeathRespawnUI.Instance.OpenPanel();
        }
        else
        {
            Debug.LogWarning("ShowDeathPanel：找不到DeathRespawnUI.Instance！检查DeathFailPanel是否激活，脚本是否挂载");
        }
    }

    private class SaveManagerRunner : MonoBehaviour
    {
        void Update()
        {
            if (SaveManager.PlayerObj == null) return;

            if (Input.GetKeyDown(KeyCode.E))
            {
                SaveManager.SavePlayer();
            }
            if (Input.GetKeyDown(KeyCode.L))
            {
                SaveManager.LoadPlayer();
            }
            if (Input.GetKeyDown(KeyCode.R))
            {
                Debug.Log("触发死亡界面调用");
                SaveManager.ShowDeathPanel();
            }
        }

        void OnDestroy()
        {
            if (SaveManager.hiddenRunner != null)
            {
                Object.Destroy(SaveManager.hiddenRunner);
            }
        }
    }
}
