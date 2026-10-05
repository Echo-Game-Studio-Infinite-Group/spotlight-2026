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
    private const string AttackStateName = "Attack";
    private const string AttackParameterName = "IsAttacking";

    // 攻击对应的 MotionState 编号（与 PlayerAnimation.MotionStateAttack 保持一致）
    private const int AttackMotionState = 4;

    // MotionState 编号 = 状态在 states 列表里的下标，所以顺序是硬契约。
    // Unity 的 AnimatorStateMachine 只有 AddState / RemoveState，没有重排 API，
    // 因此顺序一旦被打乱就没有「局部修补」的办法 —— 只能整体重建。
    // Build() 与 EnsureStateLayout() 共用这一份顺序定义，避免两边写岔。
    private static readonly string[] StateOrder = { "Idle", "Move", "Jump", "Trackle", "Attack" };
    private const int AttackIndex = 4;

    // 校验状态布局；不符合预期就整体重建。可重复执行：布局正确时不改动任何东西。
    public static void EnsureStateLayout()
    {
        AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        if (controller == null) throw new InvalidOperationException("找不到 player.controller");

        if (MatchesLayout(controller))
        {
            Debug.Log("[PlayerAnimationSetup] 状态布局正确，跳过重建: "
                + string.Join(" / ", StateOrder));
            return;
        }

        Debug.LogWarning("[PlayerAnimationSetup] 状态布局与约定不符（MotionState 编号依赖下标顺序），"
            + "正在整体重建 player.controller");
        Build();
    }

    private static bool MatchesLayout(AnimatorController controller)
    {
        if (controller.layers.Length != 1) return false;
        AnimatorStateMachine machine = controller.layers[0].stateMachine;
        if (machine.states.Length != StateOrder.Length) return false;
        for (int i = 0; i < StateOrder.Length; i++)
        {
            if (machine.states[i].state.name != StateOrder[i]) return false;
        }

        return HasParameter(controller, "MotionState") && HasParameter(controller, "RunBlend")
            && HasParameter(controller, "IsAttacking");
    }

    private static bool HasParameter(AnimatorController controller, string name)
    {
        foreach (AnimatorControllerParameter parameter in controller.parameters)
        {
            if (parameter.name == name) return true;
        }

        return false;
    }

    // 重建 player.controller：清空后按 StateOrder 的顺序重新创建。
    // 顺序即契约（MotionState 编号 = 下标），所以这里不做「增量补状态」——
    // 增量会依赖已有状态的顺序，一旦顺序被打乱就再也修不回来。
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
                EditorUtility.SetDirty(controller); AssetDatabase.SaveAssets();
                Debug.Log("[PlayerAnimationSetup] 保留已接入的 Actions 子状态机与手工状态，按动作表增量装配");
                return controller;
            }

        // 清空旧内容：层、状态机、混合树都是挂在控制器资产内的子资源，必须一并销毁，
        // 否则重复执行会不断累积出新的层与状态。
        foreach (AnimatorControllerLayer oldLayer in controller.layers)
        {
            if (oldLayer.stateMachine != null) UnityEngine.Object.DestroyImmediate(oldLayer.stateMachine, true);
        }

        while (controller.layers.Length > 0) controller.RemoveLayer(0);
        while (controller.parameters.Length > 0) controller.RemoveParameter(0);

        AnimationClip idleClip = Clip(IdleClipPath);
        AnimationClip walkClip = Clip(WalkClipPath);
        AnimationClip runClip = Clip(RunClipPath);
        AnimationClip jumpClip = Clip(JumpClipPath);
        AnimationClip slideClip = Clip(SlideClipPath);
        AnimationClip attackClip = Clip(AttackClipPath);

        controller.AddParameter("MotionState", AnimatorControllerParameterType.Int);
        controller.AddParameter("RunBlend", AnimatorControllerParameterType.Float);
        controller.AddParameter(AttackParameterName, AnimatorControllerParameterType.Bool);

        AnimatorControllerLayer layer = new AnimatorControllerLayer
        {
            name = "Base Layer",
            defaultWeight = 1f,
            stateMachine = new AnimatorStateMachine { name = "Base Layer" }
        };
        AssetDatabase.AddObjectToAsset(layer.stateMachine, controller);
        controller.AddLayer(layer);
        AnimatorStateMachine machine = controller.layers[0].stateMachine;

        BlendTree locomotion = new BlendTree
        {
            name = "Walk Run",
            blendType = BlendTreeType.Simple1D,
            blendParameter = "RunBlend",
            useAutomaticThresholds = false
        };
        AssetDatabase.AddObjectToAsset(locomotion, controller);
        locomotion.AddChild(walkClip, 0f);
        locomotion.AddChild(runClip, 1f);

        // 严格按 StateOrder 建序：下标就是 MotionState 的编号
        AnimatorState idle = State(machine, "Idle", idleClip, new Vector3(240f, 0f));
        State(machine, "Move", locomotion, new Vector3(500f, 0f));
        State(machine, "Jump", jumpClip, new Vector3(500f, 100f));
        State(machine, "Trackle", slideClip, new Vector3(240f, 100f));
        AnimatorState attack = State(machine, "Attack", attackClip, new Vector3(760f, 100f));
        machine.defaultState = idle;

        // 移动状态：MotionState 等于编号时进入
        for (int motionState = 0; motionState < AttackIndex; motionState++)
        {
            AnimatorState target = machine.states[motionState].state;
            AnimatorStateTransition transition = machine.AddAnyStateTransition(target);
            transition.hasExitTime = false;
            transition.hasFixedDuration = true;
            transition.duration = 0.1f;
            transition.canTransitionToSelf = false;
            transition.AddCondition(AnimatorConditionMode.Equals, motionState, "MotionState");
        }

        // 攻击：布尔参数是主通道，编号通道保留以维持「编号 = 下标」的一致性
        AddAnyStateTransition(machine, attack, AnimatorConditionMode.If, 0f, AttackParameterName, 0.08f);
        AddAnyStateTransition(machine, attack, AnimatorConditionMode.Equals, AttackIndex, "MotionState", 0.08f);
        AddExitTransition(attack, idle, AttackParameterName);

        EditorUtility.SetDirty(locomotion);
        EditorUtility.SetDirty(machine);
        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();
        return controller;
    }

    private static void AddAnyStateTransition(AnimatorStateMachine machine, AnimatorState target,
        AnimatorConditionMode mode, float threshold, string parameter, float duration)
    {
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
        AnimatorStateTransition exit = attack.AddTransition(destination);
        exit.hasExitTime = true;
        exit.exitTime = 0.9f;
        exit.hasFixedDuration = true;
        exit.duration = 0.1f;
        exit.AddCondition(AnimatorConditionMode.IfNot, 0f, parameter);
    }

    // 兼容入口：原来叫 EnsureController（1.0 版），现在统一走 EnsureStateLayout，
    // 它会先校验布局、必要时才重建，不会像旧实现那样默默往后面追加状态。
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
        AnimatorState state = machine.AddState(name, position);
        state.motion = motion;
        return state;
    }
}
