using UnityEngine;

public static class SaveManager
{
    //存档数据模型
    public class SaveData
    {
        public Vector3 playerPosition;
    }

    private static GameObject _hiddenRunner;
    private static SaveManagerRunner _runner;
    public static GameObject PlayerObj { get; set; }

    //初始化：自动创建隐藏物体，用来跑Update监听按键
    public static void Init(GameObject player)
    {
        if (_hiddenRunner != null) return;

        PlayerObj = player;
        _hiddenRunner = new GameObject("SaveManager_HiddenRunner");
        _hiddenRunner.hideFlags = HideFlags.HideAndDontSave;
        _runner = _hiddenRunner.AddComponent<SaveManagerRunner>();
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
        Debug.Log("存档成功");
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
        Rigidbody rb = PlayerObj.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.MovePosition(data.playerPosition);
        }
        else
        {
            PlayerObj.transform.position = data.playerPosition;
        }
        Debug.Log("读档成功，已设置玩家位置");
    }

    public static void ClearPlayerSave()
    {
        PlayerPrefs.DeleteKey("playerSave");
        PlayerPrefs.Save();
        Debug.Log("存档已清除");
    }

    //嵌套Mono：负责Update检测按键，全部在同一个cs文件内
    private class SaveManagerRunner : MonoBehaviour
    {
        void Update()
        {
            if (SaveManager.PlayerObj == null) return;

            // E保存，L读取
            if (Input.GetKeyDown(KeyCode.E))
            {
                SaveManager.SavePlayer();
            }
            if (Input.GetKeyDown(KeyCode.L))
            {
                SaveManager.LoadPlayer();
            }
        }

        void OnDestroy()
        {
            if (SaveManager._hiddenRunner != null)
            {
                Object.Destroy(SaveManager._hiddenRunner);
            }
        }
    }
}
