using UnityEngine;

namespace GameJam.Actions
{
    [DisallowMultipleComponent]
    public sealed class ActionAnimatorBridge : MonoBehaviour
    {
        public const string TimeParameter = "ActionTime";
        public const string PlayingParameter = "ActionPlaying";
        private static readonly int TimeId = Animator.StringToHash(TimeParameter);
        private static readonly int PlayingId = Animator.StringToHash(PlayingParameter);
        [SerializeField] private Animator _animator;
        [SerializeField] private PlayerAnimation _locomotion;
        [SerializeField] private PlayerVFXManager _vfx;
        [SerializeField, Min(0)] private int _returnBlendFrames = 6;
        private object _owner;
        private bool _wasEnabled;
        private float _previousSpeed;
        private bool _previousFireEvents;
        private AnimatorCullingMode _previousCulling;
        private bool _parryPose;
        private long _instance;
        private int _stateHash;
        private int _layer;
        private int _motionTimeId;
        private PlayerMotor _motor;
        private PlayerGroundPose _groundPose;
        private ActionAirborneAnimationSettings _airborneSettings;
        private ActionAnimationBinding _jumpUpBinding;
        private ActionAnimationBinding _airborneBinding;
        private float _airborneProgress;
        private int _flightStartJumpCount;
        private bool _hasLeftGround;
        public Animator Animator => _animator;
        public PlayerAnimation Locomotion => _locomotion;
        public bool IsOwned => _owner != null;
        public bool IsHoldingAirbornePose => _airborneSettings != null && _instance == 0;

        public void Configure(Animator animator, PlayerAnimation locomotion, PlayerVFXManager vfx = null)
        {
            _animator = animator;
            _locomotion = locomotion;
            _vfx = vfx;
        }
        public bool TryAcquire(object owner, out string reason)
        {
            reason = null;
            if (_owner != null) { reason = "Animator 已被执行器占用"; return ReferenceEquals(_owner, owner); }
            if (_animator == null) _animator = GetComponentInChildren<Animator>(true);
            if (_locomotion == null) _locomotion = GetComponentInChildren<PlayerAnimation>(true);
            if (_vfx == null) _vfx = GetComponentInChildren<PlayerVFXManager>(true);
            _motor = GetComponent<PlayerMotor>();
            _groundPose = GetComponent<PlayerGroundPose>();
            if (!Application.isPlaying) _groundPose?.Configure(_animator, _motor, _locomotion);
            if (owner == null || _animator == null || _animator.runtimeAnimatorController == null || _locomotion == null)
            { reason = "缺少 Animator、Controller 或 PlayerAnimation"; return false; }
            if (!HasParameter(TimeParameter, AnimatorControllerParameterType.Float) || !HasParameter(PlayingParameter, AnimatorControllerParameterType.Bool))
            { reason = "Controller 缺少 ActionTime(float) 或 ActionPlaying(bool)，请执行攻击接入装配"; return false; }
            _wasEnabled = _animator.enabled;
            _previousSpeed = _animator.speed;
            _previousFireEvents = _animator.fireEvents;
            _previousCulling = _animator.cullingMode;
            _animator.applyRootMotion = false;
            _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            _animator.fireEvents = false;
            _locomotion.Configure(_animator);
            _locomotion.SetSequenceDriven(true);
            _vfx?.SetSequenceDriven(true);
            _animator.Rebind();
            _animator.Update(0f);
            // 禁用自动求值后只由逻辑步推进，避免渲染帧再次推进过渡和基础动画。
            _animator.enabled = false;
            _animator.speed = 1f;
            _owner = owner;
            return true;
        }
        private bool HasParameter(string name, AnimatorControllerParameterType type)
        {
            foreach (AnimatorControllerParameter parameter in _animator.parameters)
                if (parameter.name == name && parameter.type == type) return true;
            return false;
        }
        public bool CanPlay(ActionAnimationBinding binding)
        {
            return binding != null && (string.IsNullOrWhiteSpace(binding.AnimatorState) ||
                _animator != null && binding.Layer == 0 && binding.Layer < _animator.layerCount &&
                _animator.HasState(binding.Layer, Animator.StringToHash(binding.AnimatorState)) && HasParameter(binding.TimeParameter, AnimatorControllerParameterType.Float));
        }
        public void EnterSegment(ActionExecutionState state, bool immediate = false)
        {
            if (!IsOwned) return;
            ActionAnimationBinding binding = state.Segment?.Animation;
            if (binding == null || string.IsNullOrWhiteSpace(binding.AnimatorState))
            { EndAction(_instance, false); return; }
            _parryPose = false;
            _airborneBinding = null;
            if (state.Action.AirborneAnimation?.HoldJumpUpPose == true && _airborneSettings != state.Action.AirborneAnimation)
            {
                _airborneSettings = state.Action.AirborneAnimation;
                _jumpUpBinding = binding;
                foreach (ActionSegment segment in state.Action.Timeline)
                    if (!string.IsNullOrWhiteSpace(segment.Animation.AnimatorState)) _jumpUpBinding = segment.Animation;
                _airborneBinding = null;
                _flightStartJumpCount = _motor != null ? _motor.JumpCount : 0;
                _hasLeftGround = false;
            }
            int hash = Animator.StringToHash(binding.AnimatorState);
            bool same = _instance == state.InstanceId && _stateHash == hash && _layer == binding.Layer;
            _animator.SetBool(PlayingId, true);
            _animator.SetFloat(TimeId, state.AnimationNormalizedTime);
            _motionTimeId = Animator.StringToHash(binding.TimeParameter);
            _animator.SetFloat(_motionTimeId, state.AnimationNormalizedTime);
            if (!same || immediate)
            {
                bool restart = _stateHash == hash && _layer == binding.Layer;
                if (immediate || binding.BlendFrames == 0 || restart) _animator.Play(hash, binding.Layer, binding.NormalizedStart);
                else _animator.CrossFadeInFixedTime(hash, binding.BlendFrames / (float)ActionSequencePlayer.FramesPerSecond,
                    binding.Layer, binding.Clip != null ? binding.NormalizedStart * binding.Clip.length : 0f);
            }
            _instance = state.InstanceId;
            _stateHash = hash;
            _layer = binding.Layer;
            Sample(state, 0f);
        }
        public void Sample(ActionExecutionState state, float deltaTime)
        {
            if (!IsOwned) return;
            if (_parryPose)
            {
                if (TimeManager.InHitStop) return;
                _parryPose = false; ReturnToLocomotion();
            }
            _locomotion.UpdateLocomotion(deltaTime);
            if (_airborneSettings != null && _motor != null)
            {
                _hasLeftGround |= _motor.JumpCount > _flightStartJumpCount;
                if (_hasLeftGround && (_motor.IsGrounded || _motor.IsWallSliding) && _instance != 0)
                    ClearAirborneVisual();
            }
            if (_instance == state.InstanceId && state.Action != null)
            {
                _animator.SetFloat(TimeId, state.AnimationNormalizedTime);
                _animator.SetFloat(_motionTimeId, state.AnimationNormalizedTime);
            }
            else if (_instance == 0) SampleAirborneVisual(deltaTime);
            _groundPose?.Prepare(deltaTime);
            _animator.Update(deltaTime);
        }
        public void EndAction(long instanceId, bool stopEffects)
        {
            if (!IsOwned || _instance != instanceId) return;
            _instance = 0;
            if (!_parryPose && !SampleAirborneVisual(0f)) ReturnToLocomotion();
            if (stopEffects) _vfx?.StopAttacks();
            // 失活对象不能再手动驱动 Animator，退出播放与卸载场景会走这条路径。
            if (_animator != null && _animator.gameObject.activeInHierarchy) _animator.Update(0f);
        }
        public void HoldParryPose() => _parryPose = TimeManager.InHitStop;
        private void ReturnToLocomotion()
        {
            _animator.SetBool(PlayingId, false);
            _locomotion.UpdateLocomotion(0f);
            int hash = Animator.StringToHash("Base Layer." + PlayerAnimation.LocomotionStateName(_locomotion.CurrentMotionState));
            if (_animator.HasState(0, hash)) _animator.CrossFadeInFixedTime(hash, _returnBlendFrames / (float)ActionSequencePlayer.FramesPerSecond, 0);
        }
        private bool SampleAirborneVisual(float deltaTime)
        {
            if (_airborneSettings == null || _motor == null || !_hasLeftGround) return false;
            bool landed = _motor.IsGrounded;
            ActionAnimationBinding binding = landed ? _airborneSettings.Landing :
                _motor.Velocity.y < 0f && !string.IsNullOrWhiteSpace(_airborneSettings.Descending.AnimatorState)
                    ? _airborneSettings.Descending : _airborneSettings.Airborne;
            bool hold = !landed && string.IsNullOrWhiteSpace(binding.AnimatorState);
            if (hold) binding = _jumpUpBinding;
            if (_motor.IsWallSliding || binding == null || string.IsNullOrWhiteSpace(binding.AnimatorState))
            { ClearAirborneVisual(); return false; }
            if (_airborneBinding != binding)
            {
                _airborneBinding = binding;
                _airborneProgress = 0f;
                _motionTimeId = Animator.StringToHash(binding.TimeParameter);
                int hash = Animator.StringToHash(binding.AnimatorState);
                _animator.CrossFadeInFixedTime(hash, binding.BlendFrames / (float)ActionSequencePlayer.FramesPerSecond, binding.Layer,
                    binding.Clip != null ? binding.NormalizedStart * binding.Clip.length : 0f);
            }
            if (!hold && binding.Clip != null && binding.Clip.length > 0f) _airborneProgress += deltaTime / binding.Clip.length;
            float normalized = hold ? binding.NormalizedEnd : Mathf.Lerp(binding.NormalizedStart, binding.NormalizedEnd, Mathf.Clamp01(_airborneProgress));
            _animator.SetBool(PlayingId, true);
            _animator.SetFloat(_motionTimeId, normalized);
            _animator.SetFloat(TimeId, normalized);
            if (landed && _airborneProgress >= 1f) { ClearAirborneVisual(); return false; }
            return true;
        }
        public void ClearAirborneVisual()
        {
            if (_airborneSettings == null) return;
            _airborneSettings = null;
            _jumpUpBinding = _airborneBinding = null;
            _hasLeftGround = false;
            if (IsOwned && _instance == 0 && !_parryPose) ReturnToLocomotion();
        }
        public void PlayAttackEffect(int index) => _vfx?.PlayAttack(index);
        public void Release(object owner)
        {
            if (!ReferenceEquals(_owner, owner)) return;
            _parryPose = false;
            ClearAirborneVisual();
            EndAction(_instance, true);
            _locomotion.SetSequenceDriven(false);
            _vfx?.SetSequenceDriven(false);
            _animator.speed = _previousSpeed;
            _animator.fireEvents = _previousFireEvents;
            _animator.cullingMode = _previousCulling;
            _animator.enabled = _wasEnabled;
            _owner = null;
        }
        private void OnDisable() { if (_owner != null) Release(_owner); }
    }
}
