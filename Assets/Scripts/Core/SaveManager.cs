using UnityEngine;
using GameJam.Flow;

// 存档服务：E 键手动存档、攻击存档点存档（SavePoint 调 SaveAtHitPoint）、R 键弹挑战失败面板。
// dev 侧代码禁止改动，因此用 RuntimeInitializeOnLoadMethod 自启动隐藏 Runner，
// 不依赖任何场景内接线；PlayerObj 由 Runner 惰性解析（玩家可能随场景晚于本服务加载）。
public static class SaveManager
{
    [System.Serializable]
    public class SaveData
    {
        public Vector3 playerPosition;
        public string scenePath;
        public string roomVariantId;
        public Vector3 roomLocalPosition;
        // 旧存档没有此标记，不能把缺失的混乱度当作 0 恢复。
        public bool hasFlowState;
        public int chaos;
        public string[] brokenDeviceIds;
        public string checkpointId;
    }

    private const string SaveKey = "playerSave";

    private static GameObject hiddenRunner;
    private static SaveData _pending;
    public static GameObject PlayerObj { get; set; }
    public static bool HasSave => TryReadSave(out _);
    public static bool HasPendingContinue => _pending != null;
    public static string ContinueCheckpointId => _pending?.checkpointId;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetRuntime()
    {
        hiddenRunner = null;
        PlayerObj = null;
        _pending = null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInit()
    {
        if (hiddenRunner != null) return;
        hiddenRunner = new GameObject("SaveManager_HiddenRunner");
        hiddenRunner.hideFlags = HideFlags.HideAndDontSave;
        Object.DontDestroyOnLoad(hiddenRunner);
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
        if (WriteSave(PlayerObj.transform.position))
            Debug.Log("[SaveManager] 手动存档成功");
    }

    public static void SaveAtHitPoint()
    {
        if (PlayerObj == null)
        {
            Debug.LogWarning("[SaveManager] 玩家对象为空，存档点保存失败");
            return;
        }
        if (WriteSave(PlayerObj.transform.position))
            Debug.Log("[SaveManager] 命中存档点，存档成功");
    }

    public static void LoadPlayer()
    {
        var flow = RunFlowController.Current;
        if (flow != null && (!flow.IsReady || flow.IsBusy)) return;
        if (PlayerObj == null)
        {
            Debug.LogWarning("[SaveManager] 玩家对象为空，读档失败");
            return;
        }
        if (!TryReadSave(out SaveData data))
        {
            Debug.Log("[SaveManager] 没有存档");
            return;
        }
        RestorePlayer(data);
    }

    private static void RestorePlayer(SaveData data)
    {
        Vector3 position = data.playerPosition;
        var flow = RunFlowController.Current;
        var room = flow != null ? flow.CurrentRoom : null;
        if (!string.IsNullOrEmpty(data.roomVariantId))
        {
            if (room == null || room.Variant.Id != data.roomVariantId)
            {
                Debug.LogWarning("[SaveManager] 存档属于其他关卡，请从标题画面继续游戏");
                return;
            }
            position = room.Root.transform.TransformPoint(data.roomLocalPosition);
            if (data.hasFlowState)
            {
                room.BrokenDevices.Clear();
                if (data.brokenDeviceIds != null)
                    foreach (string id in data.brokenDeviceIds)
                        if (!string.IsNullOrEmpty(id)) room.BrokenDevices.Add(id);
                // Bind 只应用状态，不重放攻击，因此不会重复增加混乱度。
                foreach (var device in room.Root.GetComponentsInChildren<StabilityDevice>(true))
                    device.Bind(flow, room);
                flow.Chaos.Restore(data.chaos);
                var checkpoint = room.Root.FindCheckpoint(data.checkpointId);
                if (checkpoint != null && (flow.Checkpoints.Room != room || flow.Checkpoints.Id != checkpoint.Id))
                {
                    flow.Checkpoints.Clear();
                    flow.CommitCheckpoint(checkpoint);
                }
            }
        }
        // 优先走 PlayerMotor.Teleport：它会同步 CharacterController 状态，
        // 直接改 transform 可能被控制器积分覆写。
        if (PlayerMotor.Active != null)
        {
            PlayerMotor.Active.Teleport(position);
        }
        else
        {
            PlayerObj.transform.position = position;
        }
        Debug.Log("[SaveManager] 读档成功，已传送回存档位置");
    }

    public static void ClearPlayerSave()
    {
        CancelContinue();
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

    private static bool WriteSave(Vector3 position)
    {
        var flow = RunFlowController.Current;
        if (flow != null && (!flow.IsReady || flow.IsBusy)) return false;
        SaveData data = new SaveData
        {
            playerPosition = position,
            scenePath = flow != null ? flow.gameObject.scene.path : PlayerObj.scene.path
        };
        if (flow != null && flow.CurrentRoom != null)
        {
            data.roomVariantId = flow.CurrentRoom.Variant.Id;
            data.roomLocalPosition = flow.CurrentRoom.Root.transform.InverseTransformPoint(position);
            data.hasFlowState = true;
            data.chaos = flow.Chaos.Value;
            data.brokenDeviceIds = new string[flow.CurrentRoom.BrokenDevices.Count];
            flow.CurrentRoom.BrokenDevices.CopyTo(data.brokenDeviceIds);
            if (flow.Checkpoints.Room == flow.CurrentRoom) data.checkpointId = flow.Checkpoints.Id;
        }
        PlayerPrefs.SetString(SaveKey, JsonUtility.ToJson(data));
        PlayerPrefs.Save();
        return true;
    }

    private static bool TryReadSave(out SaveData data)
    {
        data = null;
        string json = PlayerPrefs.GetString(SaveKey, string.Empty);
        if (string.IsNullOrEmpty(json)) return false;
        try { data = JsonUtility.FromJson<SaveData>(json); }
        catch (System.ArgumentException) { return false; }
        return data != null && json.Contains("\"playerPosition\"");
    }

    public static bool PrepareContinue(string fallbackScene, out string scenePath)
    {
        CancelContinue();
        scenePath = fallbackScene;
        if (!TryReadSave(out _pending)) return false;
        if (!string.IsNullOrEmpty(_pending.scenePath)) scenePath = _pending.scenePath;
        _pending.scenePath = scenePath;
        LevelSelection.Clear();
        AutoInit();
        return true;
    }

    public static void CancelContinue() => _pending = null;

    // 动态房间重新装配后世界坐标会改变，因此仅额外记录房间身份和局部坐标。
    public static RoomVariantDefinition GetContinueVariant(RunFlowConfig config)
    {
        if (_pending == null || string.IsNullOrEmpty(_pending.roomVariantId)) return null;
        foreach (var variant in LevelSelection.GetLevels(config))
            if (variant.Id == _pending.roomVariantId) return variant;
        Debug.LogWarning("[SaveManager] 存档关卡已不在流程配置中，将从起始关卡开始");
        CancelContinue();
        return null;
    }

    public static void RestorePending()
    {
        if (_pending == null || PlayerMotor.Active == null) return;
        PlayerObj = PlayerMotor.Active.gameObject;
        // 必须在流程的初始出生点传送之后执行，避免恢复位置被覆盖。
        RestorePlayer(_pending);
        CancelContinue();
    }

    private class SaveManagerRunner : MonoBehaviour
    {
        private void LateUpdate()
        {
            if (_pending == null || RunFlowController.Current != null || PlayerMotor.Active == null) return;
            if (PlayerMotor.Active.gameObject.scene.path == _pending.scenePath) RestorePending();
        }
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
