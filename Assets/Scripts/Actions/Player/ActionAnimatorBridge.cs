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
        private long _instance;
        private int _stateHash;
        private int _layer;
        private int _motionTimeId;
        public Animator Animator => _animator;
        public bool IsOwned => _owner != null;

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
            _locomotion.UpdateLocomotion(deltaTime);
            if (_instance == state.InstanceId && state.Action != null)
            {
                _animator.SetFloat(TimeId, state.AnimationNormalizedTime);
                _animator.SetFloat(_motionTimeId, state.AnimationNormalizedTime);
            }
            _animator.Update(deltaTime);
        }
        public void EndAction(long instanceId, bool stopEffects)
        {
            if (!IsOwned || _instance != instanceId) return;
            _instance = 0;
            _animator.SetBool(PlayingId, false);
            _locomotion.UpdateLocomotion(0f);
            int hash = Animator.StringToHash("Base Layer." + PlayerAnimation.LocomotionStateName(_locomotion.CurrentMotionState));
            if (_animator.HasState(0, hash)) _animator.CrossFadeInFixedTime(hash, _returnBlendFrames / (float)ActionSequencePlayer.FramesPerSecond, 0);
            if (stopEffects) _vfx?.StopAttacks();
            // 物体已经失活（场景卸载 / 退出播放 / 被禁用）时再驱一次 Animator 会报
            // "Can't call Animator.Update on inactive object"。Release 是从 OnDisable 调的，
            // 那条路径必然踩到这里；禁用但激活的 Animator 仍然允许手动驱（本类就靠这个跑序列）。
            if (_animator != null && _animator.gameObject.activeInHierarchy) _animator.Update(0f);
        }
        public void PlayAttackEffect(int index) => _vfx?.PlayAttack(index);
        public void Release(object owner)
        {
            if (!ReferenceEquals(_owner, owner)) return;
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
