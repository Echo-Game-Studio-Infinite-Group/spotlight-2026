using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace GameJam.Actions.Editor
{
    public static class PlayerLocomotionAnimatorSetup
    {
        public const string LeftClipPath = "Assets/Animations/Player/LoWall_LDash_Loop_InPlace.anim";
        public const string RightClipPath = "Assets/Animations/Player/LoWall_RDash_Loop_InPlace.anim";
        private const string LeftSourcePath = "Assets/Animations/Player/LoWall_LDash_Loop_Root.anim";
        private const string RightSourcePath = "Assets/Animations/Player/LoWall_RDash_Loop_Root.anim";
        private const int DefaultBlendFrames = 6;

        [MenuItem("超高速行者/动作序列/接入左右 WallDash 动画")]
        public static void InstallWallDash()
        {
            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ActionPlayerSetup.ControllerPath);
            EnsureWallDash(controller);
            AssetDatabase.SaveAssets();
            Debug.Log("[PlayerLocomotionAnimatorSetup] 左右 WallDash 已增量接入，动作动画继续由 ActionAnimatorBridge 采样。");
        }

        public static void EnsureWallDash(AnimatorController controller, bool recordUndo = false,
            int blendFrames = DefaultBlendFrames)
        {
            if (controller == null || controller.layers.Length == 0)
                throw new InvalidOperationException("Controller 至少需要一个基础动画层");
            if (recordUndo) Undo.RecordObject(controller, "装配划墙动画参数");
            EnsureParameter(controller, "MotionState", AnimatorControllerParameterType.Int);
            EnsureParameter(controller, ActionAnimatorBridge.PlayingParameter, AnimatorControllerParameterType.Bool);
            EnsureInPlaceClip(LeftSourcePath, LeftClipPath);
            EnsureInPlaceClip(RightSourcePath, RightClipPath);
            EnsureState(controller.layers[0].stateMachine, PlayerAnimation.MotionStateWallDashLeft,
                LeftClipPath, new Vector3(500f, 260f), recordUndo, blendFrames);
            EnsureState(controller.layers[0].stateMachine, PlayerAnimation.MotionStateWallDashRight,
                RightClipPath, new Vector3(760f, 260f), recordUndo, blendFrames);
            EditorUtility.SetDirty(controller);
            ActionAnimatorAuthoring.Invalidate();
        }

        public static bool HasWallDash(AnimatorController controller)
        {
            if (controller == null || controller.layers.Length == 0) return false;
            return HasState(controller.layers[0].stateMachine, PlayerAnimation.MotionStateWallDashLeft, LeftClipPath)
                && HasState(controller.layers[0].stateMachine, PlayerAnimation.MotionStateWallDashRight, RightClipPath);
        }

        private static bool HasState(AnimatorStateMachine machine, int motionState, string clipPath)
        {
            AnimatorState state = machine.states.FirstOrDefault(child => child.state.name == PlayerAnimation.LocomotionStateName(motionState)).state;
            return state != null && state.motion == AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath)
                && !state.timeParameterActive && machine.anyStateTransitions.Any(transition =>
                    transition.destinationState == state && !transition.hasExitTime && !transition.canTransitionToSelf
                    && transition.conditions.Any(condition => condition.parameter == "MotionState"
                        && condition.mode == AnimatorConditionMode.Equals && condition.threshold == motionState)
                    && transition.conditions.Any(condition => condition.parameter == ActionAnimatorBridge.PlayingParameter
                        && condition.mode == AnimatorConditionMode.IfNot));
        }

        private static void EnsureState(AnimatorStateMachine machine, int motionState, string clipPath,
            Vector3 position, bool recordUndo, int blendFrames)
        {
            AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
            if (clip == null || !clip.isLooping) throw new InvalidOperationException("划墙循环片段缺失或未开启循环：" + clipPath);
            if (recordUndo) Undo.RecordObject(machine, "装配划墙动画状态");
            string name = PlayerAnimation.LocomotionStateName(motionState);
            AnimatorState state = machine.states.FirstOrDefault(child => child.state.name == name).state;
            if (state == null)
            {
                state = machine.AddState(name, position);
                if (recordUndo) Undo.RegisterCreatedObjectUndo(state, "创建划墙动画状态");
            }
            else if (recordUndo) Undo.RecordObject(state, "配置划墙动画状态");
            state.motion = clip;
            // 划墙是基础循环；ActionTime 只供动作片段使用，不能把循环停在动作的采样时间。
            state.timeParameterActive = false;
            AnimatorStateTransition transition = machine.anyStateTransitions.FirstOrDefault(candidate =>
                candidate.destinationState == state && candidate.conditions.Any(condition =>
                    condition.parameter == "MotionState" && condition.mode == AnimatorConditionMode.Equals
                    && condition.threshold == motionState));
            if (transition == null)
            {
                transition = machine.AddAnyStateTransition(state);
                transition.duration = Mathf.Max(0, blendFrames) / (float)ActionSequencePlayer.FramesPerSecond;
                transition.AddCondition(AnimatorConditionMode.Equals, motionState, "MotionState");
                if (recordUndo) Undo.RegisterCreatedObjectUndo(transition, "创建划墙动画过渡");
            }
            else if (recordUndo) Undo.RecordObject(transition, "配置划墙动画过渡");
            transition.hasExitTime = false;
            transition.hasFixedDuration = true;
            transition.canTransitionToSelf = false;
            if (!transition.conditions.Any(condition => condition.parameter == ActionAnimatorBridge.PlayingParameter
                && condition.mode == AnimatorConditionMode.IfNot))
                transition.AddCondition(AnimatorConditionMode.IfNot, 0f, ActionAnimatorBridge.PlayingParameter);
            EditorUtility.SetDirty(state);
            EditorUtility.SetDirty(transition);
            EditorUtility.SetDirty(machine);
        }

        private static void EnsureParameter(AnimatorController controller, string name, AnimatorControllerParameterType type)
        {
            AnimatorControllerParameter parameter = controller.parameters.FirstOrDefault(value => value.name == name);
            if (parameter == null) controller.AddParameter(name, type);
            else if (parameter.type != type) throw new InvalidOperationException("参数类型错误：" + name);
        }

        private static void EnsureInPlaceClip(string sourcePath, string destinationPath)
        {
            if (AssetDatabase.LoadAssetAtPath<AnimationClip>(destinationPath) != null) return;
            AnimationClip source = AssetDatabase.LoadAssetAtPath<AnimationClip>(sourcePath);
            if (source == null) throw new InvalidOperationException("划墙源片段缺失：" + sourcePath);
            AnimationClip clip = UnityEngine.Object.Instantiate(source);
            clip.name = Path.GetFileNameWithoutExtension(destinationPath);
            try
            {
                foreach (string property in new[] { "RootT.x", "RootT.z" })
                {
                    EditorCurveBinding binding = EditorCurveBinding.FloatCurve("", typeof(Animator), property);
                    AnimationCurve curve = AnimationUtility.GetEditorCurve(source, binding);
                    if (curve == null || curve.length < 2)
                        throw new InvalidOperationException("划墙源片段缺少水平根曲线：" + sourcePath + " / " + property);
                    Keyframe[] keys = curve.keys;
                    Keyframe first = keys[0], last = keys[keys.Length - 1];
                    float drift = (last.value - first.value) / (last.time - first.time);
                    // 仅去掉一轮的累计位移；保留摆动以及完整的竖直、旋转和肌肉曲线。
                    for (int i = 0; i < keys.Length; i++)
                    {
                        keys[i].value -= first.value + drift * (keys[i].time - first.time);
                        keys[i].inTangent -= drift;
                        keys[i].outTangent -= drift;
                    }
                    curve.keys = keys;
                    AnimationUtility.SetEditorCurve(clip, binding, curve);
                }
                AssetDatabase.CreateAsset(clip, destinationPath);
                EditorUtility.SetDirty(clip);
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(clip);
                throw;
            }
        }
    }
}
