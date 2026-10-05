using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

// 仅修复 EnemyTest 资产，不重建场景、不保存其他未提交的编辑器修改。
public static class EnemyTestAnimatorRepair
{
    private const string ControllerPath = "Assets/Animations/EnemyTest.controller";

    public static void Repair()
    {
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        if (controller == null) throw new System.InvalidOperationException("缺少 EnemyTest.controller");
        var machine = controller.layers[0].stateMachine;
        var move = machine.states.Single(s => s.state.name == "Move").state;
        var hurt = machine.states.Single(s => s.state.name == "Hurt").state;
        var tree = (BlendTree)move.motion;
        var normal = (BlendTree)tree.children[0].motion;
        var injured = AssetDatabase.LoadAllAssetsAtPath(ControllerPath).OfType<BlendTree>()
            .FirstOrDefault(t => t.name == "Injured");
        if (injured == null)
        {
            injured = new BlendTree { name = "Injured" };
            AssetDatabase.AddObjectToAsset(injured, controller);
        }
        injured.blendType = BlendTreeType.Simple1D;
        injured.blendParameter = "Speed";
        injured.useAutomaticThresholds = false;
        injured.children = new[]
        {
            Child(Clip("IdleInjured"), 0f), Child(Clip("WalkInjured"), 2f), Child(Clip("RunInjured"), 5f)
        };
        tree.blendType = BlendTreeType.Simple1D;
        tree.blendParameter = "Injured";
        tree.useAutomaticThresholds = false;
        tree.children = new[] { Child(normal, 0f), Child(injured, 1f) };
        if (!controller.parameters.Any(p => p.name == "Hurt"))
            controller.AddParameter("Hurt", AnimatorControllerParameterType.Trigger);

        foreach (var state in machine.states)
            foreach (var transition in state.state.transitions.Where(t => t.destinationState == hurt).ToArray())
                state.state.RemoveTransition(transition);
        var enter = machine.anyStateTransitions.FirstOrDefault(t => t.destinationState == hurt)
            ?? machine.AddAnyStateTransition(hurt);
        enter.conditions = new[]
        {
            new AnimatorCondition { mode = AnimatorConditionMode.If, parameter = "Hurt" },
            new AnimatorCondition { mode = AnimatorConditionMode.IfNot, parameter = "Dead" }
        };
        enter.hasExitTime = false;
        enter.hasFixedDuration = true;
        enter.duration = 0f;
        enter.canTransitionToSelf = true;
        hurt.tag = "Hurt";
        var exit = hurt.transitions.FirstOrDefault(t => t.destinationState == move) ?? hurt.AddTransition(move);
        exit.conditions = new AnimatorCondition[0];
        exit.hasExitTime = true;
        exit.exitTime = 1f;
        exit.hasFixedDuration = true;
        exit.duration = 0.05f;

        foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(ControllerPath)) EditorUtility.SetDirty(asset);
        AssetDatabase.SaveAssetIfDirty(controller);
        Debug.Log("[EnemyTestAnimatorRepair] 已在当前编辑器保存：Move → Normal/Injured；AnyState → Hurt（零等待）；Hurt → Move。");
    }

    private static ChildMotion Child(Motion motion, float threshold)
    {
        return new ChildMotion { motion = motion, threshold = threshold, timeScale = 1f };
    }

    private static AnimationClip Clip(string name)
    {
        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/Animations/EnemyTest/" + name + ".anim");
        if (clip == null) throw new System.InvalidOperationException("缺少动画：" + name);
        return clip;
    }
}
