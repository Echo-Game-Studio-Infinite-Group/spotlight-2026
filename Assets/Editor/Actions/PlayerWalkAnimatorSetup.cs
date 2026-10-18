using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace GameJam.Actions.Editor
{
    public static class PlayerWalkAnimatorSetup
    {
        public const string Folder = "Assets/CLazyRunnerActionAnimPack/Animations/P1_CLazyMovement/Mvm_8way_Jog/";
        public const string LeftClipPath = Folder + "CLazy@Mvm_Jog_Front_L.FBX";
        public const string RightClipPath = Folder + "CLazy@Mvm_Jog_Front_R.FBX";
        public const string FrontLeftClipPath = Folder + "CLazy@Mvm_Jog_Front_L45.FBX";
        public const string FrontRightClipPath = Folder + "CLazy@Mvm_Jog_Front_R45.FBX";
        private static readonly string[] ClipPaths = { LeftClipPath, RightClipPath, FrontLeftClipPath, FrontRightClipPath };
        private static readonly Vector2[] Directions = { Vector2.left, Vector2.right,
            new Vector2(-1f, 1f).normalized, new Vector2(1f, 1f).normalized };

        [MenuItem("超高速行者/动作序列/接入四方向行走")]
        public static void Install()
        {
            if (EditorApplication.isPlaying)
            {
                EditorApplication.isPlaying = false;
                EditorApplication.delayCall += Install;
                return;
            }
            Ensure(AssetDatabase.LoadAssetAtPath<AnimatorController>(ActionPlayerSetup.ControllerPath));
            AssetDatabase.SaveAssets();
            Debug.Log("[PlayerWalkAnimatorSetup] 四方向 Jog 行走已接入；高速继续使用 Move 跑步状态。");
        }

        public static void Ensure(AnimatorController controller, int blendFrames = 6)
        {
            if (controller == null || controller.layers.Length == 0)
                throw new InvalidOperationException("Controller 至少需要一个基础动画层");
            if (HasWalk(controller)) return;
            EnsureParameter(controller, "MotionState", AnimatorControllerParameterType.Int);
            EnsureParameter(controller, ActionAnimatorBridge.PlayingParameter, AnimatorControllerParameterType.Bool);
            EnsureParameter(controller, "IsHurt", AnimatorControllerParameterType.Bool);
            foreach (string parameter in new[] { PlayerAnimation.WalkXParameter, PlayerAnimation.WalkZParameter, PlayerAnimation.WalkRateParameter })
                EnsureParameter(controller, parameter, AnimatorControllerParameterType.Float);

            AnimatorStateMachine machine = controller.layers[0].stateMachine;
            AnimatorState state = machine.states.FirstOrDefault(value => value.state.name == "Walk").state;
            if (state == null) state = machine.AddState("Walk", new Vector3(240f, 400f));
            BlendTree tree = state.motion as BlendTree;
            if (tree == null)
            {
                tree = new BlendTree { name = "Four Direction Walk" };
                AssetDatabase.AddObjectToAsset(tree, controller);
                state.motion = tree;
            }
            tree.blendType = BlendTreeType.FreeformDirectional2D;
            tree.blendParameter = PlayerAnimation.WalkXParameter;
            tree.blendParameterY = PlayerAnimation.WalkZParameter;
            tree.useAutomaticThresholds = false;
            var children = new ChildMotion[ClipPaths.Length];
            for (int i = 0; i < ClipPaths.Length; i++)
            {
                AnimationClip clip = AssetDatabase.LoadAllAssetsAtPath(ClipPaths[i]).OfType<AnimationClip>()
                    .SingleOrDefault(value => !value.name.StartsWith("__preview__", StringComparison.Ordinal));
                if (clip == null || !clip.isHumanMotion || !clip.isLooping)
                    throw new InvalidOperationException("行走片段必须是 Humanoid 循环：" + ClipPaths[i]);
                children[i] = new ChildMotion { motion = clip, position = Directions[i], timeScale = 1f };
            }
            tree.children = children;
            state.timeParameterActive = false;
            state.speedParameter = PlayerAnimation.WalkRateParameter;
            state.speedParameterActive = true;
            AnimatorStateTransition transition = machine.anyStateTransitions.FirstOrDefault(value => value.destinationState == state);
            if (transition == null)
            {
                transition = machine.AddAnyStateTransition(state);
                transition.duration = Mathf.Max(0, blendFrames) / (float)ActionSequencePlayer.FramesPerSecond;
            }
            transition.hasExitTime = false;
            transition.hasFixedDuration = true;
            transition.canTransitionToSelf = false;
            transition.conditions = new[] {
                new AnimatorCondition { mode = AnimatorConditionMode.Equals, threshold = PlayerAnimation.MotionStateWalk, parameter = "MotionState" },
                new AnimatorCondition { mode = AnimatorConditionMode.IfNot, parameter = ActionAnimatorBridge.PlayingParameter },
                new AnimatorCondition { mode = AnimatorConditionMode.IfNot, parameter = "IsHurt" }
            };
            EditorUtility.SetDirty(tree); EditorUtility.SetDirty(state); EditorUtility.SetDirty(transition);
            EditorUtility.SetDirty(machine); EditorUtility.SetDirty(controller);
            ActionAnimatorAuthoring.Invalidate();
        }

        public static bool HasWalk(AnimatorController controller)
        {
            if (controller == null || controller.layers.Length == 0) return false;
            foreach (string name in new[] { PlayerAnimation.WalkXParameter, PlayerAnimation.WalkZParameter, PlayerAnimation.WalkRateParameter })
                if (!controller.parameters.Any(value => value.name == name && value.type == AnimatorControllerParameterType.Float)) return false;
            AnimatorStateMachine machine = controller.layers[0].stateMachine;
            AnimatorState state = machine.states.FirstOrDefault(value => value.state.name == "Walk").state;
            if (state == null || !(state.motion is BlendTree tree) || state.timeParameterActive ||
                !state.speedParameterActive || state.speedParameter != PlayerAnimation.WalkRateParameter ||
                tree.blendType != BlendTreeType.FreeformDirectional2D || tree.blendParameter != PlayerAnimation.WalkXParameter ||
                tree.blendParameterY != PlayerAnimation.WalkZParameter || tree.children.Length != ClipPaths.Length) return false;
            for (int i = 0; i < ClipPaths.Length; i++)
                if (AssetDatabase.GetAssetPath(tree.children[i].motion) != ClipPaths[i] || tree.children[i].position != Directions[i]) return false;
            return machine.anyStateTransitions.Any(value => value.destinationState == state && !value.hasExitTime && !value.canTransitionToSelf &&
                value.conditions.Any(condition => condition.parameter == "MotionState" && condition.threshold == PlayerAnimation.MotionStateWalk && condition.mode == AnimatorConditionMode.Equals) &&
                value.conditions.Any(condition => condition.parameter == ActionAnimatorBridge.PlayingParameter && condition.mode == AnimatorConditionMode.IfNot) &&
                value.conditions.Any(condition => condition.parameter == "IsHurt" && condition.mode == AnimatorConditionMode.IfNot));
        }

        private static void EnsureParameter(AnimatorController controller, string name, AnimatorControllerParameterType type)
        {
            AnimatorControllerParameter parameter = controller.parameters.FirstOrDefault(value => value.name == name);
            if (parameter == null) controller.AddParameter(name, type);
            else if (parameter.type != type) throw new InvalidOperationException("参数类型错误：" + name);
        }
    }
}
