using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

[InitializeOnLoad]
public static class PlayerHurtAnimationSetup
{
    private const string ControllerPath = "Assets/Animations/player.controller";
    private const string MaskPath = "Assets/Animations/Player/LegsOnly.mask";
    private static readonly TestRunnerApi TestRunner;

    static PlayerHurtAnimationSetup()
    {
        TestRunner = ScriptableObject.CreateInstance<TestRunnerApi>();
        TestRunner.RegisterCallbacks(new TestResults());
        EditorApplication.delayCall += RunRequested;
    }

    private static void RunRequested()
    {
        const string request = "Library/PlayerHurtBuild.request";
        if (!System.IO.File.Exists(request)) return;
        if (EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorApplication.delayCall += RunRequested;
            return;
        }
        string command = System.IO.File.ReadAllText(request);
        System.IO.File.Delete(request);
        try
        {
            Build();
            System.IO.File.WriteAllText("Library/PlayerHurtBuild.result", "SUCCESS");
            if (command.Trim() == "test") Validate();
        }
        catch (Exception error)
        {
            System.IO.File.WriteAllText("Library/PlayerHurtBuild.result", error.ToString());
            Debug.LogException(error);
        }
    }

    [MenuItem("超高速行者/动画/验证受伤腿部层")]
    public static void Validate()
    {
        SessionState.SetBool("HurtLegTests.Running", true);
        TestRunner.Execute(new ExecutionSettings(new Filter
        {
            testMode = TestMode.PlayMode,
            testNames = new[] { "PlayerHurtLegTests" }
        }));
    }

    private sealed class TestResults : ICallbacks
    {
        public void RunStarted(ITestAdaptor testsToRun) { }
        public void TestStarted(ITestAdaptor test) { }
        public void TestFinished(ITestResultAdaptor result) { }
        public void RunFinished(ITestResultAdaptor result)
        {
            if (!SessionState.GetBool("HurtLegTests.Running", false)) return;
            SessionState.SetBool("HurtLegTests.Running", false);
            // 结果落盘可跨域重载读取，不依赖 MCP 请求的十秒超时。
            System.IO.Directory.CreateDirectory("Logs/HurtLegValidation");
            System.IO.File.WriteAllText("Logs/HurtLegValidation/results.xml", result.ToXml().OuterXml);
            Debug.Log($"[HurtLegValidation] {result.PassCount} passed, {result.FailCount} failed");
        }
    }

    [MenuItem("超高速行者/动画/装配受伤腿部层")]
    public static void Build()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("请退出播放后装配受伤腿部层。");
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(MaskPath);
        if (mask == null)
        {
            mask = new AvatarMask { name = "LegsOnly" };
            AssetDatabase.CreateAsset(mask, MaskPath);
        }
        // 使用 Unity 枚举而非猜测序列化位序；Root/Body 保留受击片段的髋部表现。
        for (int i = 0; i < (int)AvatarMaskBodyPart.LastBodyPart; i++)
            mask.SetHumanoidBodyPartActive((AvatarMaskBodyPart)i,
                i == (int)AvatarMaskBodyPart.LeftLeg || i == (int)AvatarMaskBodyPart.RightLeg);
        mask.transformCount = 0;

        if (!controller.parameters.Any(p => p.name == "Hurt")) controller.AddParameter("Hurt", AnimatorControllerParameterType.Int);
        if (!controller.parameters.Any(p => p.name == "IsHurt")) controller.AddParameter("IsHurt", AnimatorControllerParameterType.Bool);
        var baseMachine = controller.layers[0].stateMachine;
        var idle = baseMachine.states.First(item => item.state.name == "Idle").state;
        foreach (string name in new[] { "HitStomach", "HitBody" })
        {
            var hurt = baseMachine.states.FirstOrDefault(item => item.state.name == name).state ?? baseMachine.AddState(name);
            hurt.motion = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/Animations/Player/" + name + ".anim");
            if (!hurt.transitions.Any(t => t.conditions.Any(c => c.parameter == "IsHurt")))
            {
                var exit = hurt.AddTransition(idle);
                exit.hasExitTime = false;
                exit.duration = 0.1f;
                exit.AddCondition(AnimatorConditionMode.IfNot, 0f, "IsHurt");
            }
            EditorUtility.SetDirty(hurt);
        }
        EditorUtility.SetDirty(baseMachine);
        var layers = controller.layers;
        var layer = layers.FirstOrDefault(item => item.name == PlayerAnimation.HurtLegLayerName);
        if (layer == null)
        {
            layer = new AnimatorControllerLayer
            {
                name = PlayerAnimation.HurtLegLayerName,
                stateMachine = new AnimatorStateMachine { name = PlayerAnimation.HurtLegLayerName }
            };
            AssetDatabase.AddObjectToAsset(layer.stateMachine, controller);
            controller.AddLayer(layer);
            layers = controller.layers;
            layer = layers.Last();
        }
        layer.avatarMask = mask;
        layer.blendingMode = AnimatorLayerBlendingMode.Override;
        layer.defaultWeight = 0f;
        var state = layer.stateMachine.states.FirstOrDefault(item => item.state.name == "Move").state
            ?? layer.stateMachine.AddState("Move");
        // 腿部层复用基础跑步的素材和倍率，避免重新装配后步频不一致。
        var run = layers[0].stateMachine.states.First(item => item.state.name == "Move").state;
        state.motion = run.motion;
        state.speed = run.speed;
        layer.stateMachine.defaultState = state;
        controller.layers = layers;

        foreach (var child in layers[0].stateMachine.states)
            if (child.state.name == "HitStomach" || child.state.name == "HitBody")
                foreach (var transition in child.state.transitions)
                    if (transition.conditions.Any(condition => condition.parameter == "IsHurt"))
                    {
                        transition.hasExitTime = false;
                        EditorUtility.SetDirty(transition);
                    }
        EditorUtility.SetDirty(state);
        EditorUtility.SetDirty(layer.stateMachine);
        EditorUtility.SetDirty(mask);
        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();
        Debug.Log("[PlayerHurtAnimationSetup] 已保存腿部 Override 层：仅 LeftLeg/RightLeg，默认权重 0。");
    }
}
