using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace GameJam.Actions.Editor
{
    public static class ActionPlayerSetup
    {
        public const string PlayerPath = "Assets/Prefabs/Player.prefab";
        public const string ControllerPath = "Assets/Animations/player.controller";
        public const string Folder = "Assets/Settings/ActionSequences/Player";
        public const string CatalogPath = Folder + "/PlayerActions.asset";
        public const string AttackStatePath = "Base Layer.Actions.PlayerAttack";

        [MenuItem("超高速行者/动作序列/接入玩家攻击")]
        public static void Install()
        {
            if (EditorApplication.isPlaying)
            {
                EditorApplication.isPlaying = false;
                EditorApplication.delayCall += Install;
                return;
            }
            GameObject player = PrefabUtility.LoadPrefabContents(PlayerPath);
            try
            {
                PlayerCombat combat = player.GetComponent<PlayerCombat>();
                PlayerMotor motor = player.GetComponent<PlayerMotor>();
                if (combat == null || motor == null || motor.Params == null) throw new InvalidOperationException("Player 缺少战斗或移动参数");
                AnimationClip clip = AssetDatabase.LoadAllAssetsAtPath("Assets/Animations/fbx/Attack.fbx").OfType<AnimationClip>()
                    .FirstOrDefault(value => !value.name.StartsWith("__preview__", StringComparison.Ordinal));
                if (clip == null) throw new InvalidOperationException("缺少导入后的 Attack 动画片段");
                ActionCatalog catalog = CreateCatalog(clip, combat, motor.Params);
                AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
                if (controller == null) throw new InvalidOperationException("缺少 player.controller");
                EnsureAnimator(controller, catalog);
                Animator animator = player.GetComponentInChildren<Animator>(true);
                if (animator == null) throw new InvalidOperationException("Player 缺少 Animator");
                animator.runtimeAnimatorController = controller;
                animator.applyRootMotion = false;
                PlayerAnimation animation = player.GetComponentInChildren<PlayerAnimation>(true);
                if (animation == null) animation = animator.gameObject.AddComponent<PlayerAnimation>();
                animation.Configure(animator);
                ActionAnimatorBridge bridge = player.GetComponent<ActionAnimatorBridge>() ?? player.AddComponent<ActionAnimatorBridge>();
                bridge.Configure(animator, animation, player.GetComponentInChildren<PlayerVFXManager>(true));
                PlayerActionRunner runner = player.GetComponent<PlayerActionRunner>() ?? player.AddComponent<PlayerActionRunner>();
                runner.Configure(catalog, player.GetComponent<PlayerInputReader>(), motor, combat, bridge, player.GetComponent<VectorEnergy>());
                EditorUtility.SetDirty(animator);
                EditorUtility.SetDirty(animation);
                EditorUtility.SetDirty(bridge);
                EditorUtility.SetDirty(runner);
                PrefabUtility.SaveAsPrefabAsset(player, PlayerPath);
                AssetDatabase.SaveAssets();
                Debug.Log("[ActionPlayerSetup] 玩家攻击、跳跃和滑铲起手已接入动作序列；现有动作表不会被重建。动作集：" + CatalogPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(player); }
        }

        public static ActionCatalog CreateCatalog(AnimationClip clip, PlayerCombat combat, MovementParams movement, string folder = Folder)
        {
            ActionSequenceExamples.EnsureFolder(folder);
            ActionDefinition jump = AssetDatabase.LoadAssetAtPath<ActionDefinition>(folder + "/PlayerJump.asset");
            if (jump == null)
            {
                jump = CreateInstance<ActionDefinition>();
                jump.ActionId = "player_jump"; jump.DisplayName = "跳跃起手";
                jump.Description = "起手发出跳跃命令；空中运动和落地由 Motor 的碰撞状态决定。";
                jump.Input.PreInputFrames = Mathf.CeilToInt(movement.JumpBufferWindow * ActionSequencePlayer.FramesPerSecond);
                jump.Input.BufferGroup = "jump";
                jump.Input.Steps.Add(new ActionInputStep { Button = ActionInputButtons.Jump });
                jump.StartConditions.Add("can_jump");
                jump.Timeline.Add(new ActionSegment { SegmentId = "takeoff", DisplayName = "起跳", DurationFrames = 1,
                    Events = new List<ActionFrameEvent> { new ActionFrameEvent { EventKey = "motor.command", MotorCommand = MotorCommandKind.Jump } } });
                AssetDatabase.CreateAsset(jump, folder + "/PlayerJump.asset");
            }
            ActionDefinition slide = AssetDatabase.LoadAssetAtPath<ActionDefinition>(folder + "/PlayerSlide.asset");
            if (slide == null)
            {
                slide = CreateInstance<ActionDefinition>();
                slide.ActionId = "player_slide"; slide.DisplayName = "滑铲起手";
                slide.Description = "起手切换物理姿态；按住持续、松开和头顶空间检查由 Motor 处理。";
                slide.Input.BufferGroup = "slide";
                slide.Input.Steps.Add(new ActionInputStep { Button = ActionInputButtons.Slide });
                slide.StartConditions.Add("can_slide");
                slide.Timeline.Add(new ActionSegment { SegmentId = "enter", DisplayName = "进入滑铲", DurationFrames = 1,
                    Events = new List<ActionFrameEvent> { new ActionFrameEvent { EventKey = "motor.command", MotorCommand = MotorCommandKind.EnterSlide } } });
                AssetDatabase.CreateAsset(slide, folder + "/PlayerSlide.asset");
            }
            ActionDefinition attack = AssetDatabase.LoadAssetAtPath<ActionDefinition>(folder + "/PlayerAttack.asset");
            if (attack == null)
            {
                attack = CreateInstance<ActionDefinition>();
                attack.ActionId = "player_attack"; attack.DisplayName = "玩家当前攻击";
                attack.Description = "迁移现有 Attack 素材和伤害；支持常态普攻的移动跳跃收招取消。高速攻击差分及速度伤害由后续动作表扩充。";
                attack.Combat.Enabled = true; attack.Combat.Damage = combat.AttackDamage;
                attack.CooldownFrames = Mathf.CeilToInt(combat.AttackCooldown * ActionSequencePlayer.FramesPerSecond);
                attack.Input.PreInputFrames = Mathf.CeilToInt(0.12f * ActionSequencePlayer.FramesPerSecond);
                attack.Input.Steps.Add(new ActionInputStep { Button = ActionInputButtons.Attack, ForbidHeld = ActionInputButtons.Skill });
                int total = Mathf.Max(3, Mathf.CeilToInt(clip.length * ActionSequencePlayer.FramesPerSecond));
                AnimationEvent[] events = AnimationUtility.GetAnimationEvents(clip);
                AnimationEvent open = events.FirstOrDefault(value => value.functionName == "EnableHitbox");
                AnimationEvent close = events.FirstOrDefault(value => value.functionName == "DisableHitbox");
                AnimationEvent vfx = events.FirstOrDefault(value => value.functionName == "UpdateAttack");
                if (open == null || close == null) throw new InvalidOperationException("Attack 缺少命中开关事件，请先完成动画导入");
                int openFrame = Mathf.Clamp(Mathf.RoundToInt(open.time / clip.length * total), 1, total - 2);
                int closeFrame = Mathf.Clamp(Mathf.RoundToInt(close.time / clip.length * total), openFrame + 1, total - 1);
                int split = vfx != null ? Mathf.Clamp(Mathf.RoundToInt(vfx.time / clip.length * total), 1, openFrame) : openFrame;
                AddSegment(attack, clip, "startup_a", "起势", ActionPhase.Startup, 0, split, total);
                if (split < openFrame) AddSegment(attack, clip, "startup_b", "引刀", ActionPhase.Startup, split, openFrame, total);
                AddSegment(attack, clip, "active", "挥砍命中", ActionPhase.Active, openFrame, closeFrame, total);
                AddSegment(attack, clip, "recovery", "收招", ActionPhase.Recovery, closeFrame, total, total);
                attack.Timeline.Find(segment => segment.SegmentId == "active").Events.Add(new ActionFrameEvent { EventKey = "combat.hitbox.open", HitGroup = 1 });
                attack.Timeline.Find(segment => segment.SegmentId == "recovery").Events.Add(new ActionFrameEvent { EventKey = "combat.hitbox.close" });
                if (vfx != null) AddEvent(attack, Mathf.Clamp(Mathf.RoundToInt(vfx.time / clip.length * total), 0, total - 1),
                    new ActionFrameEvent { EventKey = "vfx.attack", Value = vfx.intParameter });
                attack.CancelWindows.Add(new ActionCancelWindow { DisplayName = "收招接移动跳跃", Start = new ActionFrameAnchor { SegmentId = "recovery" },
                    Targets = new List<ActionDefinition> { jump }, RequireAll = new List<string> { "non_stationary_jump", "speed_low" } });
                AssetDatabase.CreateAsset(attack, folder + "/PlayerAttack.asset");
            }
            ActionCatalog catalog = AssetDatabase.LoadAssetAtPath<ActionCatalog>(folder + "/PlayerActions.asset");
            if (catalog == null)
            {
                catalog = CreateInstance<ActionCatalog>(); catalog.DisplayName = "玩家实际动作";
                catalog.Actions.AddRange(new[] { attack, jump, slide });
                AssetDatabase.CreateAsset(catalog, folder + "/PlayerActions.asset");
            }
            return catalog;
        }
        private static void AddSegment(ActionDefinition action, AnimationClip clip, string id, string name, ActionPhase phase, int start, int end, int total)
        {
            action.Timeline.Add(new ActionSegment { SegmentId = id, DisplayName = name, Phase = phase, DurationFrames = end - start,
                Control = new ActionControlPolicy { AllowJump = false, AllowSlide = false },
                Animation = new ActionAnimationBinding { AnimatorState = AttackStatePath, Clip = clip,
                    NormalizedStart = start / (float)total, NormalizedEnd = end / (float)total, BlendFrames = 5 } });
        }
        private static void AddEvent(ActionDefinition action, int frame, ActionFrameEvent value)
        {
            foreach (ActionSegment segment in action.Timeline)
            {
                if (frame < segment.DurationFrames) { value.Frame = frame; segment.Events.Add(value); return; }
                frame -= segment.DurationFrames;
            }
        }
        public static void EnsureAnimator(AnimatorController controller, ActionCatalog catalog, bool recordUndo = false)
        {
            if (controller == null || controller.layers.Length == 0) throw new InvalidOperationException("Controller 至少需要一个基础动画层");
            if (recordUndo) Undo.RecordObject(controller, "装配动作动画参数");
            EnsureParameter(controller, ActionAnimatorBridge.TimeParameter, AnimatorControllerParameterType.Float);
            EnsureParameter(controller, ActionAnimatorBridge.PlayingParameter, AnimatorControllerParameterType.Bool);
            PlayerLocomotionAnimatorSetup.EnsureWallDash(controller, recordUndo);
            AnimatorStateMachine root = controller.layers[0].stateMachine;
            if (recordUndo) Undo.RecordObject(root, "装配动作动画状态机");
            AnimatorStateMachine actions = root.stateMachines.FirstOrDefault(value => value.stateMachine.name == "Actions").stateMachine;
            if (actions == null)
            {
                actions = root.AddStateMachine("Actions");
                if (recordUndo) Undo.RegisterCreatedObjectUndo(actions, "创建动作状态机");
            }
            if (recordUndo) Undo.RecordObject(actions, "装配动作状态");
            foreach (AnimatorStateTransition transition in root.anyStateTransitions)
            {
                if (recordUndo) Undo.RecordObject(transition, "配置动作占用条件");
                if (!transition.conditions.Any(condition => condition.parameter == ActionAnimatorBridge.PlayingParameter))
                    transition.AddCondition(AnimatorConditionMode.IfNot, 0, ActionAnimatorBridge.PlayingParameter);
                EditorUtility.SetDirty(transition);
            }
            foreach (ActionDefinition action in catalog.Actions)
                foreach (ActionSegment segment in action.Timeline)
                {
                    ActionAnimationBinding binding = segment.Animation;
                    if (string.IsNullOrWhiteSpace(binding.AnimatorState)) continue;
                    const string prefix = "Base Layer.Actions.";
                    if (binding.Layer != 0 || !binding.AnimatorState.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    string name = binding.AnimatorState.Substring(prefix.Length);
                    if (name.Contains(".")) continue;
                    AnimatorState state = actions.states.FirstOrDefault(value => value.state.name == name).state;
                    if (state == null)
                    {
                        state = actions.AddState(name); state.motion = binding.Clip;
                        if (recordUndo) Undo.RegisterCreatedObjectUndo(state, "创建动作动画状态");
                    }
                    else if (recordUndo) Undo.RecordObject(state, "配置动作时间参数");
                    EnsureParameter(controller, binding.TimeParameter, AnimatorControllerParameterType.Float);
                    state.timeParameter = binding.TimeParameter;
                    state.timeParameterActive = true;
                    EditorUtility.SetDirty(state);
                }
            EditorUtility.SetDirty(actions); EditorUtility.SetDirty(root); EditorUtility.SetDirty(controller);
            ActionAnimatorAuthoring.Invalidate();
        }
        private static void EnsureParameter(AnimatorController controller, string name, AnimatorControllerParameterType type)
        {
            AnimatorControllerParameter parameter = controller.parameters.FirstOrDefault(value => value.name == name);
            if (parameter == null) controller.AddParameter(name, type);
            else if (parameter.type != type) throw new InvalidOperationException("参数类型错误：" + name);
        }
        private static T CreateInstance<T>() where T : ScriptableObject => ScriptableObject.CreateInstance<T>();
    }
}
