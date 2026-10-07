using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

public static class PlayerAnimationSetup
{
    private const string ControllerPath = "Assets/Animations/player.controller";
    private const string AttackClipPath = "Assets/Animations/fbx/Attack.fbx";
    // 片段路径写成常量：这几个 fbx 在工程整理时被改过名（Idle (1).fbx → Idle.fbx、
    // Soccer Tackle.fbx → Tackle.fbx），散落在代码里的硬编码路径会直接抛「动画片段缺失」。
    private const string IdleClipPath = "Assets/Animations/fbx/Idle.fbx";
    private const string WalkClipPath = "Assets/Animations/fbx/Walking.fbx";
    private const string RunClipPath = "Assets/Animations/fbx/Running.fbx";
    private const string JumpClipPath = "Assets/Animations/fbx/Jump.fbx";
    private const string SlideClipPath = "Assets/Animations/fbx/Tackle.fbx";
    private const string AttackParameterName = "IsAttacking";

    private static readonly string[] RequiredStates = { "Idle", "Move", "Jump", "Trackle", "Attack", "WallDashLeft", "WallDashRight" };

    // 参数条件与完整状态路径才是契约，手工状态和 Actions 子状态机必须保留。
    public static void EnsureStateLayout()
    {
        AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        if (controller == null) throw new InvalidOperationException("找不到 player.controller");

        if (MatchesLayout(controller))
        {
            Debug.Log("[PlayerAnimationSetup] 状态布局正确，跳过装配: "
                + string.Join(" / ", RequiredStates));
            return;
        }

        Debug.Log("[PlayerAnimationSetup] 增量补齐基础动画状态与参数");
        Build();
    }

    private static bool MatchesLayout(AnimatorController controller)
    {
        if (controller.layers.Length == 0) return false;
        AnimatorStateMachine machine = controller.layers[0].stateMachine;
        if (!RequiredStates.All(name => machine.states.Any(child => child.state.name == name))) return false;

        return HasParameter(controller, "MotionState") && HasParameter(controller, "RunBlend")
            && HasParameter(controller, "IsAttacking")
            && GameJam.Actions.Editor.PlayerLocomotionAnimatorSetup.HasWallDash(controller);
    }

    private static bool HasParameter(AnimatorController controller, string name)
    {
        foreach (AnimatorControllerParameter parameter in controller.parameters)
        {
            if (parameter.name == name) return true;
        }

        return false;
    }

    // 已有动画与过渡保留，只为缺失的基础状态提供默认素材。
    public static AnimatorController Build()
    {
        AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        if (controller == null) throw new InvalidOperationException("找不到 player.controller");
        if (controller.layers.Length > 0)
        foreach (ChildAnimatorStateMachine child in controller.layers[0].stateMachine.stateMachines)
            if (child.stateMachine.name == "Actions")
            {
                GameJam.Actions.ActionCatalog catalog = AssetDatabase.LoadAssetAtPath<GameJam.Actions.ActionCatalog>(GameJam.Actions.Editor.ActionPlayerSetup.CatalogPath);
                if (catalog != null) GameJam.Actions.Editor.ActionPlayerSetup.EnsureAnimator(controller, catalog);
                else GameJam.Actions.Editor.PlayerLocomotionAnimatorSetup.EnsureWallDash(controller);
                EditorUtility.SetDirty(controller); AssetDatabase.SaveAssets();
                Debug.Log("[PlayerAnimationSetup] 保留已接入的 Actions 子状态机与手工状态，按动作表增量装配");
                return controller;
            }

        AnimationClip idleClip = Clip(IdleClipPath);
        AnimationClip walkClip = Clip(WalkClipPath);
        AnimationClip runClip = Clip(RunClipPath);
        AnimationClip jumpClip = Clip(JumpClipPath);
        AnimationClip slideClip = Clip(SlideClipPath);
        AnimationClip attackClip = Clip(AttackClipPath);

        EnsureParameter(controller, "MotionState", AnimatorControllerParameterType.Int);
        EnsureParameter(controller, "RunBlend", AnimatorControllerParameterType.Float);
        EnsureParameter(controller, AttackParameterName, AnimatorControllerParameterType.Bool);

        if (controller.layers.Length == 0)
        {
            AnimatorControllerLayer layer = new AnimatorControllerLayer
            {
                name = "Base Layer",
                defaultWeight = 1f,
                stateMachine = new AnimatorStateMachine { name = "Base Layer" }
            };
            AssetDatabase.AddObjectToAsset(layer.stateMachine, controller);
            controller.AddLayer(layer);
        }
        AnimatorStateMachine machine = controller.layers[0].stateMachine;

        Motion locomotion = machine.states.FirstOrDefault(child => child.state.name == "Move").state?.motion;
        if (locomotion == null)
        {
            BlendTree blend = new BlendTree
            {
                name = "Walk Run",
                blendType = BlendTreeType.Simple1D,
                blendParameter = "RunBlend",
                useAutomaticThresholds = false
            };
            AssetDatabase.AddObjectToAsset(blend, controller);
            blend.AddChild(walkClip, 0f);
            blend.AddChild(runClip, 1f);
            locomotion = blend;
            EditorUtility.SetDirty(blend);
        }

        AnimatorState idle = State(machine, "Idle", idleClip, new Vector3(240f, 0f));
        State(machine, "Move", locomotion, new Vector3(500f, 0f));
        State(machine, "Jump", jumpClip, new Vector3(500f, 100f));
        State(machine, "Trackle", slideClip, new Vector3(240f, 100f));
        AnimatorState attack = State(machine, "Attack", attackClip, new Vector3(760f, 100f));
        if (machine.defaultState == null) machine.defaultState = idle;

        // 移动状态：MotionState 等于编号时进入
        for (int motionState = PlayerAnimation.MotionStateIdle; motionState <= PlayerAnimation.MotionStateSlide; motionState++)
        {
            AnimatorState target = machine.states.First(child => child.state.name == PlayerAnimation.LocomotionStateName(motionState)).state;
            AddAnyStateTransition(machine, target, AnimatorConditionMode.Equals, motionState, "MotionState", 0.1f);
        }

        // 兼容尚未接入动作序列的控制器。
        AddAnyStateTransition(machine, attack, AnimatorConditionMode.If, 0f, AttackParameterName, 0.08f);
        AddAnyStateTransition(machine, attack, AnimatorConditionMode.Equals, PlayerAnimation.MotionStateAttack, "MotionState", 0.08f);
        AddExitTransition(attack, idle, AttackParameterName);
        GameJam.Actions.Editor.PlayerLocomotionAnimatorSetup.EnsureWallDash(controller);

        EditorUtility.SetDirty(machine);
        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();
        return controller;
    }

    private static void AddAnyStateTransition(AnimatorStateMachine machine, AnimatorState target,
        AnimatorConditionMode mode, float threshold, string parameter, float duration)
    {
        if (machine.anyStateTransitions.Any(candidate => candidate.destinationState == target
            && candidate.conditions.Any(condition => condition.parameter == parameter
                && condition.mode == mode && condition.threshold == threshold))) return;
        AnimatorStateTransition transition = machine.AddAnyStateTransition(target);
        transition.hasExitTime = false;
        transition.hasFixedDuration = true;
        transition.duration = duration;
        transition.canTransitionToSelf = false;
        transition.AddCondition(mode, threshold, parameter);
    }

    // 攻击 → 待机：等挥砍播完再回，否则动画会被拦腰打断
    private static void AddExitTransition(AnimatorState attack, AnimatorState destination, string parameter)
    {
        if (attack.transitions.Any(candidate => candidate.destinationState == destination)) return;
        AnimatorStateTransition exit = attack.AddTransition(destination);
        exit.hasExitTime = true;
        exit.exitTime = 0.9f;
        exit.hasFixedDuration = true;
        exit.duration = 0.1f;
        exit.AddCondition(AnimatorConditionMode.IfNot, 0f, parameter);
    }

    public static AnimatorController EnsureController()
    {
        EnsureStateLayout();
        return AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
    }

    private static AnimationClip Clip(string path)
    {
        AnimationClip clip = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
            .FirstOrDefault(candidate => !candidate.name.StartsWith("__preview__"));
        if (clip == null) throw new InvalidOperationException($"动画片段缺失：{path}");
        return clip;
    }

    private static AnimatorState State(AnimatorStateMachine machine, string name,
        Motion motion, Vector3 position)
    {
        AnimatorState state = machine.states.FirstOrDefault(child => child.state.name == name).state;
        if (state == null) state = machine.AddState(name, position);
        if (state.motion == null) state.motion = motion;
        EditorUtility.SetDirty(state);
        return state;
    }

    private static void EnsureParameter(AnimatorController controller, string name, AnimatorControllerParameterType type)
    {
        if (!HasParameter(controller, name)) controller.AddParameter(name, type);
        else if (controller.parameters.First(parameter => parameter.name == name).type != type)
            throw new InvalidOperationException("参数类型错误：" + name);
    }
}
