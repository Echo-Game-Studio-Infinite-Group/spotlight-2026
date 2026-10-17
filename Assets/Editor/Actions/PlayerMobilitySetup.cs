using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace GameJam.Actions.Editor
{
    public static class PlayerMobilitySetup
    {
        public const string HighJumpPath = ActionPlayerSetup.Folder + "/PlayerHighJump.asset";
        public const string DashPath = ActionPlayerSetup.Folder + "/PlayerDash.asset";
        public const string SprintPath = ActionPlayerSetup.Folder + "/PlayerSprint.asset";
        public const string HighJumpState = "Base Layer.Actions.PlayerHighJump";
        public const string DashState = "Base Layer.Actions.PlayerDash";
        public const string SprintState = "Base Layer.Actions.PlayerSprint";

        [MenuItem("超高速行者/动作序列/接入高跳与冲刺")]
        public static void Install()
        {
            if (EditorApplication.isPlaying)
            {
                EditorApplication.isPlaying = false;
                EditorApplication.delayCall += Install;
                return;
            }
            ActionCatalog catalog = AssetDatabase.LoadAssetAtPath<ActionCatalog>(ActionPlayerSetup.CatalogPath);
            EnergyParams energy = AssetDatabase.LoadAssetAtPath<EnergyParams>("Assets/Settings/EnergyParams.asset");
            AnimationClip jumpUp = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/Animations/Player/T_Jump.anim");
            AnimationClip dashClip = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/Animations/Player/T_Dash.anim");
            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ActionPlayerSetup.ControllerPath);
            if (catalog == null || energy == null || jumpUp == null || dashClip == null || controller == null)
                throw new InvalidOperationException("缺少玩家动作集、能量参数、T_Jump/T_Dash 或 player.controller");
            EnsureActions(catalog, energy, jumpUp, dashClip);
            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(dashClip);
            settings.loopTime = false;
            AnimationUtility.SetAnimationClipSettings(dashClip, settings);
            EditorUtility.SetDirty(dashClip);
            PlayerLocomotionAnimatorSetup.EnsureRun(controller);
            ActionPlayerSetup.EnsureAnimator(controller, catalog);
            AssetDatabase.SaveAssets();
            List<ActionValidationIssue> issues = ActionCatalogValidator.Validate(catalog);
            if (issues.Exists(issue => issue.Level == ActionValidationIssue.Severity.Error))
                throw new InvalidOperationException("高跳与冲刺动作集校验失败");
            Debug.Log("[PlayerMobilitySetup] W 直接奔跑、Shift 普通冲刺、Shift+右键能量冲刺已接入；高跳与 bhop 窗口保持原配置。");
        }

        public static void EnsureActions(ActionCatalog catalog, EnergyParams energy, AnimationClip jumpUp,
            AnimationClip dashClip, string folder = ActionPlayerSetup.Folder)
        {
            ActionSequenceExamples.EnsureFolder(folder);
            ActionDefinition jump = catalog.Actions.Find(action => action != null && action.ActionId == "player_jump");
            if (jump == null) throw new InvalidOperationException("动作集缺少普通跳跃");
            ActionDefinition high = AssetDatabase.LoadAssetAtPath<ActionDefinition>(folder + "/PlayerHighJump.asset");
            ActionDefinition dash = AssetDatabase.LoadAssetAtPath<ActionDefinition>(folder + "/PlayerDash.asset");
            ActionDefinition sprint = AssetDatabase.LoadAssetAtPath<ActionDefinition>(folder + "/PlayerSprint.asset");
            if (high == null)
            {
                high = ScriptableObject.CreateInstance<ActionDefinition>();
                high.ActionId = "player_high_jump"; high.DisplayName = "矢量高跳";
                high.Description = "右键+空格；四倍基础高度，无高度记忆。空中只预输入，起跳后六个玩家帧不可主动取消。";
                high.EnergyCostSource = ActionEnergyCostSource.HighJump; high.EnergyParameters = energy;
                high.TimeEffect.UsesVectorConverter = true;
                high.AirborneAnimation.HoldJumpUpPose = true;
                high.CompleteOnLanding = true;
                high.LandingCompletionStart.SegmentId = "jump_up";
                high.Input.PreInputFrames = jump.Input.PreInputFrames;
                high.Input.BufferGroup = "mobility"; high.Input.Priority = 30;
                high.Input.Steps.Add(new ActionInputStep { Button = ActionInputButtons.Jump, RequireHeld = ActionInputButtons.Skill });
                high.StartConditions.Add("can_high_jump");
                int total = Mathf.Max(7, Mathf.RoundToInt(jumpUp.length * ActionSequencePlayer.FramesPerSecond));
                high.Timeline.Add(Segment("takeoff_lock", "起跳硬直", ActionPhase.Startup, 6, jumpUp, HighJumpState, 0f, 6f / total));
                high.Timeline.Add(Segment("jump_up", "起跳表现与可取消段", ActionPhase.Recovery, total - 6, jumpUp, HighJumpState, 6f / total, 1f));
                high.Timeline[0].Events.Add(new ActionFrameEvent { EventKey = "motor.command", MotorCommand = MotorCommandKind.HighJump, Value = 4f });
                high.Timeline[0].Events.Add(new ActionFrameEvent { EventKey = "audio.player_jump" });
                AssetDatabase.CreateAsset(high, folder + "/PlayerHighJump.asset");
            }
            if (dash == null)
            {
                dash = ScriptableObject.CreateInstance<ActionDefinition>();
                dash.ActionId = "player_dash"; dash.DisplayName = "矢量冲刺";
                dash.Description = "Shift+右键，任一按键顺序；地面/空中加速至四倍地速阈值并保留更高速度，全局0.8倍持续0.5真实秒。";
                dash.EnergyCostSource = ActionEnergyCostSource.Accelerate; dash.EnergyParameters = energy;
                dash.TimeEffect.UsesVectorConverter = true; dash.TimeEffect.GlobalRate = 0.8f; dash.TimeEffect.DurationSeconds = 0.5f;
                dash.Input.PreInputFrames = jump.Input.PreInputFrames;
                dash.Input.BufferGroup = "mobility"; dash.Input.Priority = 20;
                dash.Input.Steps.Add(new ActionInputStep { Button = ActionInputButtons.Skill, RequireHeld = ActionInputButtons.Sprint });
                dash.Input.AlternativeTriggers.Add(new ActionInputStep { Button = ActionInputButtons.Sprint, RequireHeld = ActionInputButtons.Skill });
                dash.StartConditions.Add("can_accelerate");
                dash.Timeline.Add(Segment("startup", "冲刺起手", ActionPhase.Startup, 1, dashClip, DashState, 0f, 1f / 30f));
                dash.Timeline.Add(Segment("burst", "冲刺表现", ActionPhase.Active, 17, dashClip, DashState, 1f / 30f, 18f / 30f));
                dash.Timeline.Add(Segment("recovery", "冲刺收招", ActionPhase.Recovery, 12, dashClip, DashState, 18f / 30f, 1f));
                dash.Timeline[0].Events.Add(new ActionFrameEvent { EventKey = "motor.command", MotorCommand = MotorCommandKind.Accelerate, Value = 4f });
                AssetDatabase.CreateAsset(dash, folder + "/PlayerDash.asset");
            }
            if (sprint == null)
            {
                sprint = ScriptableObject.CreateInstance<ActionDefinition>();
                sprint.ActionId = "player_sprint"; sprint.DisplayName = "普通冲刺";
                sprint.Description = "单按 Shift，一次增加两倍低速阈值的水平速度，不耗能、不减速时间；组合键优先触发能量冲刺。";
                sprint.Input.PreInputFrames = jump.Input.PreInputFrames;
                sprint.Input.BufferGroup = "mobility"; sprint.Input.Priority = 10;
                sprint.Input.Steps.Add(new ActionInputStep { Button = ActionInputButtons.Sprint, ForbidHeld = ActionInputButtons.Skill });
                sprint.StartConditions.Add("can_sprint");
                sprint.CooldownFrames = 30; sprint.CooldownGroup = "ordinary_sprint";
                sprint.Timeline.Add(Segment("startup", "冲刺起手", ActionPhase.Startup, 1, dashClip, SprintState, 0f, 1f / 30f));
                sprint.Timeline.Add(Segment("burst", "冲刺表现", ActionPhase.Active, 17, dashClip, SprintState, 1f / 30f, 18f / 30f));
                sprint.Timeline.Add(Segment("recovery", "冲刺收招", ActionPhase.Recovery, 12, dashClip, SprintState, 18f / 30f, 1f));
                sprint.Timeline[0].Events.Add(new ActionFrameEvent { EventKey = "motor.command", MotorCommand = MotorCommandKind.SprintBoost, Value = 2f });
                AssetDatabase.CreateAsset(sprint, folder + "/PlayerSprint.asset");
            }
            if (!catalog.Actions.Contains(high)) catalog.Actions.Add(high);
            if (!catalog.Actions.Contains(dash)) catalog.Actions.Add(dash);
            if (!catalog.Actions.Contains(sprint)) catalog.Actions.Add(sprint);
            foreach (ActionInputStep step in jump.Input.Steps) step.ForbidHeld |= ActionInputButtons.Skill;
            foreach (ActionDefinition action in catalog.Actions)
            {
                if (action == high || action == dash || action == sprint) continue;
                foreach (ActionCancelWindow window in action.CancelWindows)
                    if (window.Targets.Contains(jump))
                    {
                        if (!window.Targets.Contains(high)) window.Targets.Add(high);
                        if (!window.Targets.Contains(sprint)) window.Targets.Add(sprint);
                    }
                EditorUtility.SetDirty(action);
            }
            EnsureCancelWindow(high, "jump_up", catalog);
            EnsureCancelWindow(dash, "burst", catalog);
            EnsureCancelWindow(sprint, "burst", catalog);
            foreach (ActionCancelWindow window in sprint.CancelWindows) window.Targets.Remove(sprint);
            EditorUtility.SetDirty(high); EditorUtility.SetDirty(dash); EditorUtility.SetDirty(sprint); EditorUtility.SetDirty(catalog);
        }

        private static ActionSegment Segment(string id, string label, ActionPhase phase, int frames,
            AnimationClip clip, string state, float start, float end)
            => new ActionSegment
            {
                SegmentId = id, DisplayName = label, Phase = phase, DurationFrames = frames,
                Control = new ActionControlPolicy { AllowJump = false, AllowSlide = false },
                Animation = new ActionAnimationBinding { AnimatorState = state, Clip = clip,
                    NormalizedStart = start, NormalizedEnd = end, BlendFrames = 3 }
            };

        private static void EnsureCancelWindow(ActionDefinition action, string segment, ActionCatalog catalog)
        {
            // 装配只补缺失引用，手工调整的时间轴与窗口保持原值。
            ActionCancelWindow window = action.CancelWindows.Find(value => value.DisplayName == "机动接招");
            if (window == null)
            {
                window = new ActionCancelWindow { DisplayName = "机动接招", Start = new ActionFrameAnchor { SegmentId = segment } };
                action.CancelWindows.Add(window);
            }
            foreach (ActionDefinition target in catalog.Actions)
                if (target != null && !window.Targets.Contains(target)) window.Targets.Add(target);
        }
    }
}
