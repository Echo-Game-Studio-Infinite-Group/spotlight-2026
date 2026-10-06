using System;
using System.Collections.Generic;
using UnityEngine;

namespace GameJam.Actions
{
    [DefaultExecutionOrder(-10)]
    [DisallowMultipleComponent]
    public sealed class PlayerActionRunner : MonoBehaviour, IActionSequenceHost, IActionSequenceSink, IActionSequenceSimulationSink
    {
        [SerializeField] private ActionCatalog _catalog;
        [SerializeField] private PlayerInputReader _input;
        [SerializeField] private PlayerMotor _motor;
        [SerializeField] private PlayerCombat _combat;
        [SerializeField] private ActionAnimatorBridge _animation;
        [SerializeField] private MonoBehaviour _energySource;
        [SerializeField, Min(0f)] private float _highSpeedRatio = 0.8f;
        private readonly HashSet<string> _conditions = new HashSet<string>(StringComparer.Ordinal);
        private IEnergyAccount _energy;
        private IEnergyAccount _energyOverride;
        private ActionSequencePlayer _player;
        private PlayerInputFrame _movementInput;
        private long _tick;
        private float _inputTime;
        private bool _connected;
        private bool _ownsJump;
        private bool _ownsSlide;
        private bool _alive = true;
        private bool _pendingLandingStop;
        private readonly List<Vector3> _motionPath = new List<Vector3>();
        public event Action<ActionExecutionState, ActionFrameEvent> FrameEvent;
        public ActionCatalog Catalog => _catalog;
        public ActionSequencePlayer Player => _player;
        public bool IsConnected => _connected;
        public float LastSimulatedSeconds { get; private set; }

        public void Configure(ActionCatalog catalog, PlayerInputReader input, PlayerMotor motor, PlayerCombat combat,
            ActionAnimatorBridge animation, MonoBehaviour energySource = null)
        {
            _catalog = catalog; _input = input; _motor = motor; _combat = combat; _animation = animation; _energySource = energySource;
        }
        private void OnEnable()
        {
            if (Application.isPlaying) Connect();
        }
        public bool Connect()
        {
            if (_connected) return true;
            if (_input == null) _input = GetComponent<PlayerInputReader>();
            if (_motor == null) _motor = GetComponent<PlayerMotor>();
            if (_combat == null) _combat = GetComponent<PlayerCombat>();
            if (_animation == null) _animation = GetComponent<ActionAnimatorBridge>();
            if (_energySource == null) _energySource = GetComponent<VectorEnergy>();
            _energy = _energyOverride ?? _energySource as IEnergyAccount;
            string reason = null;
            if (_catalog == null || _input == null || _motor == null || _combat == null || _animation == null)
                reason = "缺少动作集、输入、Motor、战斗或动画适配器";
            else if (ActionCatalogValidator.Validate(_catalog).Exists(issue => issue.Level == ActionValidationIssue.Severity.Error))
                reason = "动作集存在配置错误";
            else if (!_input.TryAcquireSequenceInput(this)) reason = "输入已被其他执行器占用";
            else if (!_motor.TryAcquireSimulation(this)) reason = "Motor 已被其他执行器占用";
            else if (!_animation.TryAcquire(this, out reason)) { }
            if (reason == null)
                foreach (ActionDefinition action in _catalog.Actions)
                    foreach (ActionSegment segment in action.Timeline)
                        if (!_animation.CanPlay(segment.Animation)) reason = "动画状态或独立时间参数缺失：" + segment.Animation.AnimatorState;
            if (reason != null)
            {
                Disconnect();
                Debug.LogError("[PlayerActionRunner] " + reason, this);
                return false;
            }
            _combat.SetSequenceDriven(true);
            _player = new ActionSequencePlayer(_catalog, this, this);
            _ownsJump = _catalog.Actions.Exists(action => action.Input.Steps.Exists(step => step.Button == ActionInputButtons.Jump));
            _ownsSlide = _catalog.Actions.Exists(action => action.Input.Steps.Exists(step => step.Button == ActionInputButtons.Slide));
            _input.Cleared += ClearPendingInput;
            _motor.Teleported += OnTeleported;
            _motor.MotionPathPoint += RecordMotionPoint;
            _tick = 0;
            _connected = true;
            return true;
        }
        private void FixedUpdate()
        {
            if (!_connected) return;
            ActionInputSample sample = _input.CaptureTick(_tick++, out PlayerInputFrame movement);
            SimulateTick(sample, movement, TimeManager.PlayerFixedDeltaTime, TimeManager.InHitStop);
        }
        public void SimulateTick(ActionInputSample sample, PlayerInputFrame movement, float playerDeltaTime, bool inHitStop = false)
        {
            if (!_connected) throw new InvalidOperationException("执行器尚未接线");
            _movementInput = movement;
            if (_ownsJump) _movementInput.JumpPressed = false;
            if (_ownsSlide) _movementInput.SlidePressed = false;
            _inputTime = TimeManager.UnscaledTime;
            LastSimulatedSeconds = 0;
            if (!_alive) sample = new ActionInputSample(sample.Tick);
            _player.Tick(sample, playerDeltaTime * ActionSequencePlayer.FramesPerSecond, inHitStop);
            // 完成帧先让新动作提交，再处理落地清速；成功接招会撤销旧动作的清速请求。
            if (_pendingLandingStop && _motor.isActiveAndEnabled && _motor.IsGrounded)
            {
                _motor.SetHorizontalSpeed(0f); _pendingLandingStop = false;
            }
        }
        public bool CheckCondition(string key, ActionExecutionState source, ActionInputRequest request)
        {
            switch (key)
            {
                case "grounded": return _motor.IsGrounded;
                case "airborne": return !_motor.IsGrounded;
                case "wall_sliding": return _motor.IsWallSliding;
                case "sliding": return _motor.IsSliding;
                case "can_jump": return _motor.CanExecute(MotorCommandKind.Jump);
                case "can_slide": return _motor.CanExecute(MotorCommandKind.EnterSlide);
                case "speed_low": return _motor.Params != null && _motor.HorizontalSpeed <= _motor.Params.GroundSpeedThreshold * _highSpeedRatio;
                case "speed_high": return _motor.Params != null && _motor.HorizontalSpeed > _motor.Params.GroundSpeedThreshold * _highSpeedRatio;
                case "non_stationary_jump": return request.Direction.sqrMagnitude > 0f;
                case "forward_jump": return request.Direction.y > 0f;
                case "flash_available": return _conditions.Contains("parry") || _conditions.Contains("limb_break");
                default: return _conditions.Contains(key);
            }
        }
        public void SetCondition(string key, bool value)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            if (value) _conditions.Add(key); else _conditions.Remove(key);
        }
        public void SetEnergyAccount(IEnergyAccount account) { _energyOverride = account; _energy = account; }
        public bool CanStart(ActionExecutionState source, ActionInputRequest request, out string reason)
        {
            reason = null;
            if (!_connected || !_alive || !_combat.enabled || !_motor.enabled) reason = "角色执行组件不可用或角色死亡";
            else if (request.Target.Combat?.Enabled == true && _combat.Hitbox == null) reason = "攻击缺少命中盒";
            else if (request.Target.EnergyCost > 0f && (_energy == null || _energy.CurrentEnergy < request.Target.EnergyCost)) reason = "能量不足";
            else
            {
                foreach (ActionHitVolume volume in request.Target.Combat.HitVolumes)
                    if (!string.IsNullOrEmpty(volume.AnchorPath) && transform.Find(volume.AnchorPath) == null) reason = "判定框挂点不存在：" + volume.AnchorPath;
                foreach (ActionSegment segment in request.Target.Timeline)
                {
                    if (!_animation.CanPlay(segment.Animation)) { reason = "动画状态不存在"; break; }
                    if (!string.IsNullOrWhiteSpace(segment.Control.MovementCommand)) { reason = "旧移动命令键尚未注册，请使用 motor.command 帧事件"; break; }
                    if (segment != request.Target.Timeline[0]) continue;
                    foreach (ActionFrameEvent frameEvent in segment.Events)
                        if (frameEvent.Frame == 0 && frameEvent.EventKey == "motor.command" && !_motor.CanExecute(frameEvent.MotorCommand))
                            reason = "起招运动命令的物理条件不满足";
                }
            }
            return reason == null;
        }
        public bool TryCommit(ActionExecutionState source, ActionInputRequest request)
        {
            if (!CanStart(source, request, out _)) return false;
            return request.Target.EnergyCost <= 0f || _energy != null && _energy.TrySpend(request.Target.EnergyCost);
        }
        public void OnActionStarted(ActionExecutionState state)
        {
            _pendingLandingStop = false;
            _combat.BeginSequenceAction(state);
        }
        public void OnSegmentEntered(ActionExecutionState state)
        {
            _motor.SetActionControl(state.Segment.Control);
            _animation.EnterSegment(state);
        }
        public void OnFrameEvent(ActionExecutionState state, ActionFrameEvent frameEvent)
        {
            _combat.HandleSequenceEvent(state, frameEvent);
            if (frameEvent.EventKey == "vfx.attack") { _animation.Sample(state, 0f); _animation.PlayAttackEffect(Mathf.RoundToInt(frameEvent.Value)); }
            else if (frameEvent.EventKey == "motor.command" && !_motor.TryExecute(frameEvent.MotorCommand, frameEvent.Value))
                Debug.LogWarning("[PlayerActionRunner] 运动命令被物理条件拒绝：" + frameEvent.MotorCommand, this);
            else if (frameEvent.EventKey != null &&
                     frameEvent.EventKey.StartsWith("audio.", StringComparison.Ordinal))
            {
                WwiseEventBridge.Play(
                    frameEvent.EventKey.Substring("audio.".Length),
                    gameObject);
            }
            FrameEvent?.Invoke(state, frameEvent);
        }
        public void OnActionEnded(ActionExecutionState state, ActionExitReason reason)
        {
            _combat.EndSequenceAction(state.InstanceId);
            _motor.EndMotion(state.InstanceId);
            _motionPath.Clear();
            if (reason == ActionExitReason.Completed && state.Action.Motion.ClearMomentumOnCompletion) _pendingLandingStop = true;
            if (reason != ActionExitReason.Completed) _motor.CancelActionCommands();
            _motor.SetActionControl(null);
            _animation.EndAction(state.InstanceId, reason != ActionExitReason.Completed);
        }
        public void OnStateSampled(ActionExecutionState state) => _animation.Sample(state, 0f);
        public void OnSimulationStep(ActionExecutionState from, ActionExecutionState to, double frames)
        {
            SimulateMovement(from.Segment.Control, frames);
            _animation.Sample(to, (float)(frames / ActionSequencePlayer.FramesPerSecond));
            _combat.SampleSequenceHitbox(from, to, _motionPath);
            _motionPath.Clear();
        }
        public void OnFrameBoundary(ActionExecutionState state)
        {
            _animation.Sample(state, 0f);
            // 先锁定挥出时的伤害，再执行一次性前移增速，避免当前招自己抬高伤害快照。
            _combat.SampleSequenceBoundary(state);
            if (state.Action.Motion.IsActive(state.Action, state.FrameProgress)) _motor.BeginMotion(state.Action.Motion, state.InstanceId);
            else _motor.EndMotion(state.InstanceId);
        }
        public void OnIdleSimulation(double frames)
        {
            SimulateMovement(null, frames);
            _animation.Sample(default, (float)(frames / ActionSequencePlayer.FramesPerSecond));
        }
        private void SimulateMovement(ActionControlPolicy control, double frames)
        {
            _motionPath.Clear();
            float dt = (float)(frames / ActionSequencePlayer.FramesPerSecond);
            _motor.SetActionControl(control);
            // 外部接管模拟后仍须尊重 Motor 的禁用状态，场景工具与演出会用它暂停角色位移。
            if (_motor.isActiveAndEnabled) _motor.Simulate(_movementInput, dt, _inputTime);
            _movementInput.JumpPressed = _movementInput.SlidePressed = false;
            LastSimulatedSeconds += dt;
        }
        public void ClearPendingInput() => _player?.ClearPendingInput();
        public void Interrupt() { _pendingLandingStop = false; _player?.Interrupt(); }
        public void SetAlive(bool alive)
        {
            _alive = alive;
            if (!alive) Interrupt();
        }
        public void ResetActions()
        {
            _pendingLandingStop = false;
            _player?.Reset();
            _conditions.Clear();
            _input?.Clear();
        }
        private void OnTeleported(Vector3 delta) => ResetActions();
        public void Disconnect()
        {
            _pendingLandingStop = false;
            _player?.Reset();
            if (_input != null) { _input.Cleared -= ClearPendingInput; _input.ReleaseSequenceInput(this); }
            if (_motor != null) { _motor.Teleported -= OnTeleported; _motor.MotionPathPoint -= RecordMotionPoint; _motor.ReleaseSimulation(this); }
            if (_animation != null) _animation.Release(this);
            if (_connected && _combat != null) _combat.SetSequenceDriven(false);
            _player = null;
            _connected = false;
        }
        private void OnDisable() => Disconnect();
        private void RecordMotionPoint(Vector3 point) => _motionPath.Add(point);
    }
}
