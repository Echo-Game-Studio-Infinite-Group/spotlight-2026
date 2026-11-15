using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GameJam.Flow
{
    [DefaultExecutionOrder(50)]
    [DisallowMultipleComponent]
    public sealed class RunFlowController : MonoBehaviour
    {
        [SerializeField, InspectorName("全局流程配置"), Tooltip("本场景使用的整局流程配表。")] private RunFlowConfig _config;
        [SerializeField, InspectorName("玩家运动控制器"), Tooltip("流程切房、记录点恢复及掉落检测所使用的玩家。")] private PlayerMotor _motor;
        private MapManager _maps;
        private HealthComponent _health;
        private PlayerInputReader _input;
        private PlayerRespawn _respawn;
        private CharacterController _controller;
        private VectorEnergy _energy;
        private PlayerCameraRig _cameraRig;
        private readonly List<FlowRoomInstance> _history = new List<FlowRoomInstance>();
        private readonly List<FlowRoomInstance> _owned = new List<FlowRoomInstance>();
        private bool _started, _preparing, _refreshRequested, _controllerWasEnabled, _playerLocked;
        private int _revision;
        private Vector3 _previous;
        private RoomVariantDefinition _plannedVariant;
        private int _plannedChaos;
        private long _plannedSourceId;
        public static RunFlowController Current { get; private set; }
        public RunFlowConfig Config => _config;
        public ChaosService Chaos { get; private set; }
        public RoomSelectionPlanner Planner { get; private set; }
        public int RunSeed { get; private set; }
        public CheckpointManager Checkpoints { get; } = new CheckpointManager();
        public FlowRoomInstance CurrentRoom { get; private set; }
        public FlowRoomInstance NextRoom { get; private set; }
        public IReadOnlyList<FlowRoomInstance> History => _history;
        public bool IsReady { get; private set; }
        public bool IsBusy { get; private set; }
        public bool IsPreparing => _preparing;
        public bool IsComplete { get; private set; }
        public string LastError { get; private set; } = string.Empty;
        public event Action<FlowRoomInstance> EnteredRoom;
        public event Action CheckpointCommitted;

        private void Awake() => Current = this;
        private void OnEnable() => SceneManager.activeSceneChanged += OnActiveSceneChanged;
        private void OnDisable() => SceneManager.activeSceneChanged -= OnActiveSceneChanged;

        private void OnActiveSceneChanged(Scene previous, Scene current)
        {
            if (current == gameObject.scene || _owned.Exists(room => room.Scene == current)) ApplyEnvironment();
        }

        private void ApplyEnvironment()
        {
            if (_config == null || _config.SkyboxMaterial == null) return;
            // RenderSettings 随活动场景切换，运行时新建的箱庭没有正式场景的天空盒设置。
            RenderSettings.skybox = _config.SkyboxMaterial;
            Camera camera = Camera.main;
            if (camera != null) camera.clearFlags = CameraClearFlags.Skybox;
        }
        public void Configure(RunFlowConfig config, PlayerMotor motor) { _config = config; _motor = motor; }
        private void Start()
        {
            if (_config == null || _started) return;
            if (SaveManager.HasPendingContinue) ContinueRun();
            else StartRun();
        }

        public bool StartRun()
        {
            if (_started) return false;
            SaveManager.CancelContinue();
            RoomVariantDefinition variant = LevelSelection.Consume(_config) ?? _config?.InitialRoom;
            return InitializeRun(variant, null, null);
        }

        // 菜单先准备跨场景请求；房间激活且玩家重置后才允许恢复存档。
        public bool ContinueRun()
        {
            if (_started || !SaveManager.HasPendingContinue) return false;
            RoomVariantDefinition variant = SaveManager.GetContinueVariant(_config);
            if (!SaveManager.HasPendingContinue) return StartRun();
            LevelSelection.Clear();
            return InitializeRun(variant ?? _config?.InitialRoom,
                SaveManager.ContinueCheckpointId, SaveManager.RestorePending);
        }

        private bool InitializeRun(RoomVariantDefinition variant, string checkpointId, Action restore)
        {
            if (_started) return false;
            List<string> errors = FlowConfigValidator.Validate(_config);
            if (_motor == null) errors.Add("缺少玩家 PlayerMotor");
            if (errors.Count > 0) { LastError = string.Join("\n", errors); Debug.LogError("[流程] " + LastError, this); return false; }
            _started = true; _maps = MapManager.Instance;
            ApplyEnvironment();
            _health = _motor.GetComponent<HealthComponent>();
            _input = _motor.GetComponent<PlayerInputReader>();
            _respawn = _motor.GetComponent<PlayerRespawn>();
            _controller = _motor.GetComponent<CharacterController>();
            _energy = _motor.GetComponent<VectorEnergy>();
            foreach (GameObject root in gameObject.scene.GetRootGameObjects())
            {
                _cameraRig = root.GetComponentInChildren<PlayerCameraRig>();
                if (_cameraRig != null) break;
            }
            if (_health == null) { LastError = "玩家缺少 HealthComponent"; return false; }
            _health.Died += OnDied; _motor.Teleported += OnTeleported;
            if (_respawn != null) _respawn.SetFallHandler(HandleFall);
            _maps.RegisterPersistentScene(gameObject.scene);
            IsBusy = true; LockPlayer(); StartCoroutine(BeginRun(variant, checkpointId, restore)); return true;
        }

        private IEnumerator BeginRun(RoomVariantDefinition variant = null, string checkpointId = null, Action restore = null)
        {
            variant = variant ?? _config.InitialRoom;
            Chaos = new ChaosService(_config);
            // 独立于 Unity 全局随机状态，特效或敌人抽样不会影响新局种子；旧存档沿用原固定种子。
            RunSeed = restore != null ? SaveManager.ContinueRunSeed ?? _config.Seed :
                _config.RandomizeSeed ? Guid.NewGuid().GetHashCode() : _config.Seed;
            Planner = new RoomSelectionPlanner(_config, RunSeed);
            int startingChaos = Mathf.Clamp(_config.InitialChaos, variant.MinimumChaos, variant.MaximumChaos);
            if (startingChaos < _config.MinimumChaos || startingChaos > _config.MaximumChaos)
                Chaos.TrySetSpecial(startingChaos, "关卡选择");
            else Chaos.Add(startingChaos - Chaos.Value, "关卡选择");
            Chaos.Changed += OnChaosChanged;
            FlowPlacement.TryFind(null, variant.Room.Template, _maps.FlowRooms, _config, out Pose pose);
            FlowRoomInstance first = Load(variant, pose);
            while (first.IsLoading) yield return null;
            if (!first.IsReady) { Fail(first.Error); yield break; }
            Activate(first, false);
            FlowCheckpoint entry = first.Root.FindCheckpoint(checkpointId) ?? FirstCheckpoint(first);
            if (entry == null) { Fail("起始房间没有可用的记录点"); yield break; }
            PlaceAtCheckpoint(entry);
            GameManager.Instance.StartGame();
            restore?.Invoke();
            CommitCheckpoint(entry);
            IsReady = true; IsBusy = false; UnlockPlayer(); RequestSuccessor();
        }

        private FlowRoomInstance Load(RoomVariantDefinition variant, Pose pose)
        {
            FlowRoomInstance room = _maps.PreloadFlowRoom(variant, pose, this);
            _owned.Add(room); return room;
        }
        private void OnChaosChanged(int value, string reason) { _revision++; RequestSuccessor(); }
        public void RetryPreparation() { LastError = string.Empty; RequestSuccessor(); }
        private void RequestSuccessor()
        {
            _refreshRequested = true;
            if (!_preparing && !IsBusy && !IsComplete && CurrentRoom != null) StartCoroutine(PrepareSuccessor());
        }

        private IEnumerator PrepareSuccessor()
        {
            _preparing = true;
            try
            {
                while (_refreshRequested && !IsBusy && !IsComplete)
                {
                    _refreshRequested = false;
                    int revision = _revision;
                    FlowRoomInstance source = CurrentRoom;
                    source.Root.SetExitOpen(false);
                    if (NextRoom != null)
                    {
                        FlowRoomInstance obsolete = NextRoom; NextRoom = null;
                        while (obsolete.IsLoading) yield return null;
                        _maps.TryUnloadFlowRoom(obsolete);
                        while (obsolete.IsUnloading) yield return null;
                        _owned.Remove(obsolete);
                    }
                    if (source.Variant == _config.FinalRoom) yield break;
                    RoomVariantDefinition variant = _plannedSourceId == source.Id && _plannedVariant != null && _plannedVariant.Supports(Chaos.Value) &&
                        Planner.RouteKey(_plannedChaos) == Planner.RouteKey(Chaos.Value) ? _plannedVariant : Planner.Plan(Chaos.Value, candidate =>
                            FlowPlacement.TryFind(source, candidate.Room.Template, _maps.FlowRooms, _config, out _));
                    if (variant == null) { LastError = Planner.LastError; yield break; }
                    if (!FlowPlacement.TryFind(source, variant.Room.Template, _maps.FlowRooms, _config, out Pose pose))
                    { LastError = "无法为后继房间找到安全的空间"; yield break; }
                    _plannedVariant = variant; _plannedChaos = Chaos.Value; _plannedSourceId = source.Id;
                    FlowRoomInstance pending = Load(variant, pose);
                    while (pending.IsLoading) yield return null;
                    if (revision != _revision || IsBusy || source != CurrentRoom)
                    {
                        _maps.TryUnloadFlowRoom(pending);
                        while (pending.IsUnloading) yield return null;
                        _owned.Remove(pending); continue;
                    }
                    if (!pending.IsReady) { LastError = pending.Error; _maps.TryUnloadFlowRoom(pending); yield break; }
                    FlowConnection.Create(source, pending, _config);
                    NextRoom = pending;
                    source.Root.SetExitOpen(true);
                    if (Checkpoints.Room == source) Checkpoints.SavePlan(Planner, _plannedVariant, _plannedChaos);
                    LastError = string.Empty;
                }
            }
            finally { _preparing = false; }
        }

        private void FixedUpdate()
        {
            if (!IsReady || IsBusy || IsComplete || TimeManager.IsPaused || TimeManager.PlayerFixedDeltaTime <= 0f) return;
            Vector3 position = _motor.transform.position;
            Vector3 fallPosition = _respawn != null ? _respawn.FallCheckPosition : position;
            if (fallPosition.y < CurrentRoom.Position.y + CurrentRoom.Root.FallHeight) { HandleFall(); return; }
            FlowCheckpoint best = null;
            foreach (FlowCheckpoint checkpoint in CurrentRoom.Root.Checkpoints)
                if (checkpoint.gameObject.activeInHierarchy && checkpoint.Poll(_previous, position) && (best == null || checkpoint.Priority > best.Priority)) best = checkpoint;
            if (best != null) CommitCheckpoint(best);
            if (NextRoom != null && NextRoom.IsReady && !_preparing &&
                FlowPlacement.CrossedEntry(NextRoom.Root, _previous, position, _config.EntryCommitDistance)) TryEnterNextRoom();
            // 终点与普通 BOX_S_007 共用模板，必须按差分区分，否则普通房间走到出口就会结束整局。
            if (CurrentRoom.Variant == _config.FinalRoom && CurrentRoom.Root.FinishArea != null &&
                CurrentRoom.Root.FinishArea.ContainsPlayer(_motor.transform)) CompleteRun();
            _previous = position;
        }

        public bool TryEnterNextRoom()
        {
            if (IsBusy || IsComplete || _preparing || NextRoom == null || !NextRoom.IsReady) return false;
            FlowRoomInstance next = NextRoom; NextRoom = null;
            Activate(next, true); RequestSuccessor(); return true;
        }
        private void Activate(FlowRoomInstance room, bool advance)
        {
            if (!_maps.TryActivateFlowRoom(room)) throw new InvalidOperationException("无法激活房间实例 " + room.Id);
            if (room.Variant == _config.FinalRoom && room.Root.FinishArea == null)
                Debug.LogError("[流程] 最终房缺少终点判定区域", room.Root);
            CurrentRoom = room;
            if (!_history.Contains(room)) _history.Add(room);
            if (advance) Planner.CommitSuccessor();
            Planner.RegisterEntry(room.Variant);
            _revision++;
            if (_respawn != null) _respawn.SetCheckpoint(_respawn.RespawnPosition, room.Position.y + room.Root.FallHeight);
            EnteredRoom?.Invoke(room);
        }

        public bool CommitCheckpoint(FlowCheckpoint checkpoint)
        {
            if (CurrentRoom == null || checkpoint == null || checkpoint.transform.root != CurrentRoom.Root.transform ||
                !Checkpoints.TryCommit(CurrentRoom, checkpoint, _health.Health)) return false;
            CurrentRoom.Root.SetEntryOpen(false);
            Checkpoints.SavePlan(Planner, _plannedSourceId == CurrentRoom.Id ? _plannedVariant : null, Chaos.Value);
            if (_respawn != null) _respawn.SetCheckpoint(checkpoint.transform.position, CurrentRoom.Position.y + CurrentRoom.Root.FallHeight);
            for (int i = _history.Count - 1; i >= 0; i--)
            {
                FlowRoomInstance prior = _history[i];
                if (prior == CurrentRoom) continue;
                _maps.TryUnloadFlowRoom(prior); _history.RemoveAt(i); _owned.Remove(prior);
            }
            CheckpointCommitted?.Invoke(); return true;
        }

        public bool TryBreakDevice(FlowRoomInstance room, string id)
        {
            if (IsBusy || IsComplete || room != CurrentRoom || room.Variant.StabilizerGain <= 0 || !room.BrokenDevices.Add(id)) return false;
            Chaos.Add(room.Variant.StabilizerGain, "击碎稳定装置"); return true;
        }
        private void OnDied(HealthComponent health)
        {
            if (DeathRespawnUI.Instance != null) DeathRespawnUI.Instance.OpenPanel();
            else Recover(true);
        }
        private void OnTeleported(Vector3 delta) => _previous = _motor.transform.position;
        public bool HandleFall()
        {
            if (!IsReady || IsBusy || IsComplete) return false;
            _health.TakeEnvironmentalDamage(_config.FallDamage);
            if (_health.IsAlive) Recover(false);
            return true;
        }
        public bool Recover(bool death)
        {
            if (IsBusy || IsComplete || Checkpoints.Room == null) return false;
            _respawn?.PrepareForRecovery();
            IsBusy = true; _revision++; LockPlayer();
            if (death) Chaos.ApplyDeath(Checkpoints.AllowNegativeChaos);
            StartCoroutine(RecoverAtCheckpoint(death)); return true;
        }

        private IEnumerator RecoverAtCheckpoint(bool death)
        {
            while (_preparing) yield return null;
            if (NextRoom != null)
            {
                FlowRoomInstance pending = NextRoom; NextRoom = null;
                _maps.TryUnloadFlowRoom(pending);
                while (pending.IsUnloading) yield return null;
                _owned.Remove(pending);
            }
            FlowRoomInstance room = Checkpoints.Room;
            if (Checkpoints.PlannerState != null) Planner.Restore(Checkpoints.PlannerState);
            _plannedVariant = Checkpoints.NextVariant; _plannedChaos = Checkpoints.NextChaos; _plannedSourceId = room.Id;
            RoomVariantDefinition variant = room.Variant.Room.ResolveVariant(Chaos.Value, room.Variant);
            if (variant == null) { Fail("记录点房间没有符合当前混乱度的差分"); yield break; }
            _maps.TryActivateFlowRoom(room); CurrentRoom = room;
            foreach (FlowRoomInstance prior in _history)
                if (prior != room) { _maps.TryUnloadFlowRoom(prior); _owned.Remove(prior); }
            _history.Clear(); _history.Add(room);
            yield return _maps.RebuildFlowRoom(room, variant, this);
            if (!string.IsNullOrEmpty(room.Error)) { Fail(room.Error); yield break; }
            FlowCheckpoint checkpoint = room.Root.FindCheckpoint(Checkpoints.Id);
            if (checkpoint == null) { Fail("恢复差分缺少记录点 " + Checkpoints.Id); yield break; }
            room.Root.SetEntryOpen(false);
            if (death)
            {
                if (_config.RestoreHealth) _health.Reset();
                else _health.RestoreHealth(Checkpoints.Health);
            }
            if (_config.ResetEnergy && _energy != null) _energy.ResetEnergy();
            PlaceAtCheckpoint(checkpoint);
            if (_respawn != null) _respawn.SetCheckpoint(checkpoint.transform.position, room.Position.y + room.Root.FallHeight);
            IsBusy = false; UnlockPlayer(); RequestSuccessor();
        }

        public bool RestartRun()
        {
            if (!_started || (IsBusy && string.IsNullOrEmpty(LastError))) return false;
            SaveManager.CancelContinue();
            LevelSelection.Clear();
            IsBusy = true; IsReady = false; IsComplete = false; _revision++;
            LockPlayer(); StartCoroutine(Restart()); return true;
        }
        private IEnumerator Restart()
        {
            while (_preparing) yield return null;
            yield return ReleaseRooms();
            Chaos.Changed -= OnChaosChanged; LastError = string.Empty;
            yield return BeginRun();
        }
        public IEnumerator ReleaseRooms()
        {
            IsReady = false; IsBusy = true; _revision++;
            while (_preparing) yield return null;
            Checkpoints.Clear();
            if (gameObject.scene.IsValid() && gameObject.scene.isLoaded) SceneManager.SetActiveScene(gameObject.scene);
            foreach (FlowRoomInstance room in _owned)
            {
                room.IsCurrent = false; room.IsCheckpointProtected = false;
                while (room.IsLoading) yield return null;
                _maps.TryUnloadFlowRoom(room);
            }
            foreach (FlowRoomInstance room in _owned) while (room.IsUnloading) yield return null;
            _owned.Clear(); _history.Clear(); CurrentRoom = NextRoom = null; _plannedVariant = null; _plannedSourceId = 0;
        }
        public void CompleteRun()
        {
            if (IsBusy || IsComplete || CurrentRoom == null || CurrentRoom.Variant != _config.FinalRoom) return;
            IsComplete = true;
            Debug.Log("[流程] 到达终点：" + CurrentRoom.Variant.Room.Id, this);
            GameManager.Instance.EndGame();
            if (_input != null) _input.SetGameplayEnabled(false);
        }
        private static FlowCheckpoint FirstCheckpoint(FlowRoomInstance room)
        {
            FlowCheckpoint result = null;
            foreach (FlowCheckpoint checkpoint in room.Root.Checkpoints)
                if (checkpoint.gameObject.activeSelf && (result == null || checkpoint.Priority < result.Priority)) result = checkpoint;
            return result;
        }
        private void PlaceAtCheckpoint(FlowCheckpoint checkpoint)
        {
            _motor.Teleport(checkpoint.transform.position);
            _motor.transform.rotation = checkpoint.transform.rotation;
            if (_config.ResetViewOnRecovery && _cameraRig != null) _cameraRig.SetViewRotation(checkpoint.transform.rotation);
        }
        private void LockPlayer()
        {
            if (_playerLocked) return;
            _playerLocked = true;
            _controllerWasEnabled = _controller.enabled; _controller.enabled = false;
            if (_input != null) _input.SetGameplayEnabled(false);
            TimeManager.Apply(TimeManager.TimeLayer.Player, 0f, owner: this);
            TimeManager.Apply(TimeManager.TimeLayer.World, 0f, owner: this);
        }
        private void UnlockPlayer()
        {
            _playerLocked = false;
            _controller.enabled = _controllerWasEnabled;
            TimeManager.Release(TimeManager.TimeLayer.Player, this); TimeManager.Release(TimeManager.TimeLayer.World, this);
            if (_input != null) _input.SetGameplayEnabled(!TimeManager.IsPaused && GameManager.Instance.State == GameState.Playing);
            _previous = _motor.transform.position;
        }
        private void Fail(string message) { LastError = message; Debug.LogError("[流程] " + message, this); }
        private void OnDestroy()
        {
            if (Current == this) Current = null;
            if (Chaos != null) Chaos.Changed -= OnChaosChanged;
            if (_health != null) _health.Died -= OnDied;
            if (_motor != null) _motor.Teleported -= OnTeleported;
            if (_respawn != null) _respawn.SetFallHandler(null);
            if (MapManager.Exists && _owned.Count > 0) MapManager.Instance.ReleaseDetachedFlowRooms(_owned.ToArray());
            TimeManager.Release(TimeManager.TimeLayer.Player, this); TimeManager.Release(TimeManager.TimeLayer.World, this);
        }
    }
}
