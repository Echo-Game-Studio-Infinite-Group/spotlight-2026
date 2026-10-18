using System;
using UnityEngine;
using UnityEngine.SceneManagement;

[DisallowMultipleComponent]
[RequireComponent(typeof(PlayerMotor), typeof(PlayerRespawn), typeof(HealthComponent))]
public sealed class ChaosRunController : MonoBehaviour
{
    private PlayerMotor _motor;
    private PlayerRespawn _respawn;
    private HealthComponent _health;
    private MapManager _map;
    private bool _pendingReturn;
    private bool _pendingDeath;
    private bool _allowNegativeDeath;
    private int _checkpointPriority;
    private string _checkpointScene;
    public string CurrentCheckpointId { get; private set; }
    public bool IsReturning => _pendingReturn;
    public event Action<bool> ReturnedToCheckpoint;
    public event Action<string> CheckpointChanged;

    private void Awake()
    {
        _motor = GetComponent<PlayerMotor>();
        _respawn = GetComponent<PlayerRespawn>();
        _health = GetComponent<HealthComponent>();
    }
    private void OnEnable()
    {
        _respawn.Falling += OnFalling;
        _health.Died += OnDied;
        _map = MapManager.Instance;
        _map.RoomEntered += OnRoomEntered;
    }
    private void OnDisable()
    {
        _respawn.Falling -= OnFalling;
        _health.Died -= OnDied;
        if (_map != null) _map.RoomEntered -= OnRoomEntered;
        _pendingReturn = _pendingDeath = false;
    }

    private void OnRoomEntered(string sceneName)
    {
        _checkpointScene = sceneName;
        _checkpointPriority = 0;
        _allowNegativeDeath = false;
        CurrentCheckpointId = sceneName + "/入口";
        foreach (GameObject root in SceneManager.GetSceneByName(sceneName).GetRootGameObjects())
            foreach (MapRoom room in root.GetComponentsInChildren<MapRoom>(true))
                if (room.RespawnPoint != null) _respawn.SetCheckpoint(room.RespawnPoint.position, room.FallDeathY);
        CheckpointChanged?.Invoke(CurrentCheckpointId);
    }

    public bool TrySetCheckpoint(ChaosCheckpoint checkpoint)
    {
        if (checkpoint == null || _pendingReturn || _map.IsTransitioning ||
            checkpoint.gameObject.scene.name != _map.CurrentRoomSceneName ||
            string.IsNullOrWhiteSpace(checkpoint.CheckpointId)) return false;
        if (checkpoint.Priority < _checkpointPriority && !checkpoint.CanOverrideHigherPriority) return false;
        string id = checkpoint.gameObject.scene.name + "/" + checkpoint.CheckpointId;
        if (CurrentCheckpointId == id) return true;
        _checkpointScene = checkpoint.gameObject.scene.name;
        _checkpointPriority = checkpoint.Priority;
        _allowNegativeDeath = checkpoint.AllowNegativeDeath;
        CurrentCheckpointId = id;
        _respawn.SetCheckpoint(checkpoint.transform.position, _respawn.KillHeight);
        CheckpointChanged?.Invoke(id);
        return true;
    }

    private void OnFalling()
    {
        if (_pendingReturn || TimeManager.IsPaused) return;
        _pendingReturn = true;
        float damage = ChaosManager.Exists && ChaosManager.Instance.Settings != null
            ? ChaosManager.Instance.Settings.FallDamage : 20f;
        _health.TakeDamage(damage);
        _pendingDeath |= !_health.IsAlive;
    }
    private void OnDied(HealthComponent source) { _pendingReturn = true; _pendingDeath = true; }

    public bool RequestCheckpointReturn()
    {
        if (_pendingReturn || !_health.IsAlive || string.IsNullOrEmpty(_checkpointScene)) return false;
        _pendingReturn = true;
        return true;
    }

    private void LateUpdate()
    {
        // 等伤害事件全部派发后再复活，避免 Player.OnDied 覆盖刚刚恢复的动作状态。
        if (!_pendingReturn || TimeManager.IsPaused || _map.IsTransitioning || !ChaosManager.Exists) return;
        bool died = _pendingDeath;
        var chaos = ChaosManager.Instance;
        Scene room = SceneManager.GetSceneByName(_checkpointScene);
        if (!room.IsValid() || !room.isLoaded) return;
        if (died)
        {
            chaos.ApplyDeathPenalty(_allowNegativeDeath);
            if (chaos.Settings != null && chaos.Settings.RestoreLampsOnDeath) chaos.ClearRoomLampRecords(_checkpointScene);
        }
        foreach (GameObject root in room.GetRootGameObjects())
        {
            foreach (ChaosRoomVariants variants in root.GetComponentsInChildren<ChaosRoomVariants>(true)) variants.Refresh(true);
            foreach (ChaosLamp lamp in root.GetComponentsInChildren<ChaosLamp>(true)) lamp.RefreshState();
        }
        _motor.Teleport(_respawn.RespawnPosition);
        if (died) _health.Reset(_health.MaxHealth * (chaos.Settings != null ? chaos.Settings.ReviveHealthRatio : 1f));
        _pendingReturn = _pendingDeath = false;
        ReturnedToCheckpoint?.Invoke(died);
    }
}
