using UnityEngine;

// 存档服务：E 键手动存档、攻击存档点存档（SavePoint 调 SaveAtHitPoint）、R 键弹挑战失败面板。
// dev 侧代码禁止改动，因此用 RuntimeInitializeOnLoadMethod 自启动隐藏 Runner，
// 不依赖任何场景内接线；PlayerObj 由 Runner 惰性解析（玩家可能随场景晚于本服务加载）。
public static class SaveManager
{
    public class SaveData
    {
        public Vector3 playerPosition;
    }

    private const string SaveKey = "playerSave";

    private static GameObject hiddenRunner;
    public static GameObject PlayerObj { get; set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInit()
    {
        if (hiddenRunner != null) return;
        hiddenRunner = new GameObject("SaveManager_HiddenRunner");
        hiddenRunner.hideFlags = HideFlags.HideAndDontSave;
        hiddenRunner.AddComponent<SaveManagerRunner>();
    }

    // 保留手动接线入口：外部若先拿到玩家可直接指定，不指定也能自动解析。
    public static void Init(GameObject player)
    {
        PlayerObj = player;
        AutoInit();
    }

    public static void SavePlayer()
    {
        if (PlayerObj == null)
        {
            Debug.LogWarning("[SaveManager] 玩家对象为空，存档失败");
            return;
        }
        WriteSave(PlayerObj.transform.position);
        Debug.Log("[SaveManager] 手动存档成功");
    }

    public static void SaveAtHitPoint()
    {
        if (PlayerObj == null)
        {
            Debug.LogWarning("[SaveManager] 玩家对象为空，存档点保存失败");
            return;
        }
        WriteSave(PlayerObj.transform.position);
        Debug.Log("[SaveManager] 命中存档点，存档成功");
    }

    public static void LoadPlayer()
    {
        if (PlayerObj == null)
        {
            Debug.LogWarning("[SaveManager] 玩家对象为空，读档失败");
            return;
        }
        string json = PlayerPrefs.GetString(SaveKey, null);
        if (string.IsNullOrEmpty(json))
        {
            Debug.Log("[SaveManager] 没有存档");
            return;
        }
        SaveData data = JsonUtility.FromJson<SaveData>(json);
        // 优先走 PlayerMotor.Teleport：它会同步 CharacterController 状态，
        // 直接改 transform 可能被控制器积分覆写。
        if (PlayerMotor.Active != null)
        {
            PlayerMotor.Active.Teleport(data.playerPosition);
        }
        else
        {
            PlayerObj.transform.position = data.playerPosition;
        }
        Debug.Log("[SaveManager] 读档成功，已传送回存档位置");
    }

    public static void ClearPlayerSave()
    {
        PlayerPrefs.DeleteKey(SaveKey);
        PlayerPrefs.Save();
    }

    public static void ShowDeathPanel()
    {
        if (DeathRespawnUI.Instance != null)
        {
            DeathRespawnUI.Instance.OpenPanel();
        }
        else
        {
            Debug.LogWarning("[SaveManager] 找不到 DeathRespawnUI 实例，请确认 Canvas 预制体已放入场景");
        }
    }

    private static void WriteSave(Vector3 position)
    {
        SaveData data = new SaveData { playerPosition = position };
        PlayerPrefs.SetString(SaveKey, JsonUtility.ToJson(data));
        PlayerPrefs.Save();
    }

    private class SaveManagerRunner : MonoBehaviour
    {
        private void Update()
        {
            // 玩家可能晚于 Runner 出现，每帧惰性解析一次直到拿到引用。
            if (SaveManager.PlayerObj == null && PlayerMotor.Active != null)
            {
                SaveManager.PlayerObj = PlayerMotor.Active.gameObject;
            }
            if (SaveManager.PlayerObj == null || TimeManager.IsPaused) return;

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
                SaveManager.ShowDeathPanel();
            }
        }
    }
}
