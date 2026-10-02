using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

public static class PlayerAnimationSetup
{
    private const string ControllerPath = "Assets/Animations/player.controller";

    public static AnimatorController EnsureController()
    {
        AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        if (controller == null) throw new InvalidOperationException("找不到 player.controller");
        if (controller.layers.Length > 0) return controller;

        AnimationClip idleClip = Clip("Assets/Animations/fbx/Idle (1).fbx");
        AnimationClip walkClip = Clip("Assets/Animations/fbx/Walking.fbx");
        AnimationClip runClip = Clip("Assets/Animations/fbx/Running.fbx");
        AnimationClip jumpClip = Clip("Assets/Animations/fbx/Jump.fbx");
        AnimationClip slideClip = Clip("Assets/Animations/fbx/Soccer Tackle.fbx");

        controller.AddParameter("MotionState", AnimatorControllerParameterType.Int);
        controller.AddParameter("RunBlend", AnimatorControllerParameterType.Float);
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

        AnimatorState idle = State(machine, "Idle", idleClip, new Vector3(240f, 0f));
        State(machine, "Move", locomotion, new Vector3(500f, 0f));
        State(machine, "Jump", jumpClip, new Vector3(500f, 100f));
        State(machine, "Trackle", slideClip, new Vector3(240f, 100f));
        machine.defaultState = idle;

        for (int motionState = 0; motionState < 4; motionState++)
        {
            AnimatorState target = machine.states[motionState].state;
            // AnimatorStateMachine 按创建顺序保留状态，整数值与运行时驱动一致。
            AnimatorStateTransition transition = machine.AddAnyStateTransition(target);
            transition.hasExitTime = false;
            transition.hasFixedDuration = true;
            transition.duration = 0.1f;
            transition.canTransitionToSelf = false;
            transition.AddCondition(AnimatorConditionMode.Equals, motionState, "MotionState");
        }
        EditorUtility.SetDirty(locomotion);
        EditorUtility.SetDirty(machine);
        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();
        return controller;
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
