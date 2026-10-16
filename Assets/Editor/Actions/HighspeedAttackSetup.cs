using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace GameJam.Actions.Editor
{
    public static class HighspeedAttackSetup
    {
        [MenuItem("超高速行者/动作序列/接入高速普攻 A B")]
        public static void Install()
        {
            if (EditorApplication.isPlaying)
            {
                EditorApplication.isPlaying = false;
                EditorApplication.delayCall += Install;
                return;
            }
            ActionCatalog catalog = AssetDatabase.LoadAssetAtPath<ActionCatalog>(ActionPlayerSetup.CatalogPath);
            ActionDefinition normal = AssetDatabase.LoadAssetAtPath<ActionDefinition>(ActionPlayerSetup.Folder + "/PlayerAttack.asset");
            if (catalog == null || normal == null) throw new InvalidOperationException("请先接入玩家攻击");
            ActionDefinition a = EnsureAction("PlayerHighspeed_Attack", "player_highspeed_a", "高速普攻 A", "HighspeedAttackA", 12);
            ActionDefinition b = EnsureAction("PlayerHighspeed_Attack_B", "player_highspeed_b", "高速普攻 B", "HighspeedAttackB", 10);
            EnsureMember(catalog, a); EnsureMember(catalog, b);
            if (!normal.StartConditions.Contains("speed_low")) normal.StartConditions.Add("speed_low");
            if (normal.RequestVariants.Count == 0) normal.RequestVariants.AddRange(new[] { a, b, normal });
            normal.CooldownGroup = "player_basic_attack";
            EnsureCancel(a, b); EnsureCancel(b, a);
            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ActionPlayerSetup.ControllerPath);
            ActionPlayerSetup.EnsureAnimator(controller, catalog);
            EditorUtility.SetDirty(normal); EditorUtility.SetDirty(a); EditorUtility.SetDirty(b); EditorUtility.SetDirty(catalog);
            foreach (ActionValidationIssue issue in ActionCatalogValidator.Validate(catalog))
                if (issue.Level == ActionValidationIssue.Severity.Error) throw new InvalidOperationException(issue.Message);
            AssetDatabase.SaveAssets();
            Debug.Log("[HighspeedAttackSetup] 高速 A/B 已加入正式动作集，保留现有配置；帧区间、伤害曲线与运动数值为可调初始值，请在动画预览中校准。");
        }

        private static ActionDefinition EnsureAction(string file, string id, string label, string state, int startup)
        {
            string path = ActionPlayerSetup.Folder + "/" + file + ".asset";
            ActionDefinition action = AssetDatabase.LoadAssetAtPath<ActionDefinition>(path);
            bool create = action == null;
            if (create) action = ScriptableObject.CreateInstance<ActionDefinition>();
            // 复用用户刚创建的空表及 GUID；再次装配不能覆盖已经调好的帧和判定框。
            bool empty = action.Timeline.Count == 0 || (!action.Combat.Enabled && action.Input.Steps.Count == 0 &&
                action.Timeline.Count == 1 && action.Timeline[0].DurationFrames == 1 && action.Timeline[0].Animation.Clip == null);
            if (empty)
            {
                AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/Animations/Player/" + (state.EndsWith("A") ? "Highspeed_Attack_A" : "Highspeed_Attack_B") + ".anim");
                if (clip == null) throw new InvalidOperationException("缺少高速 A/B 动画素材");
                AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
                settings.loopTime = false; AnimationUtility.SetAnimationClipSettings(clip, settings); EditorUtility.SetDirty(clip);
                int total = Mathf.RoundToInt(clip.length * ActionSequencePlayer.FramesPerSecond);
                const int active = 18;
                action.ActionId = id; action.DisplayName = label;
                action.Description = "高速左键差分；收招可互相取消，空中前移俯冲。帧区间与数值为初始占位，可在预览中校准；本轮没有 parry 判定。";
                action.Timeline.Clear();
                AddSegment(action, clip, state, "startup", "引刀", ActionPhase.Startup, 0, startup, total);
                AddSegment(action, clip, state, "active", "高速挥砍", ActionPhase.Active, startup, startup + active, total);
                AddSegment(action, clip, state, "recovery", "收招", ActionPhase.Recovery, startup + active, total, total);
                action.Timeline[1].Events.Add(new ActionFrameEvent { EventKey = "vfx.attack", Value = state.EndsWith("A") ? 1 : 2 });
                action.StartConditions = new List<string> { "speed_high" };
                action.CooldownFrames = 21; action.CooldownGroup = "player_basic_attack";
                action.Input.PreInputFrames = 8; action.Input.BufferGroup = "main";
                action.Combat.Enabled = true; action.Combat.Damage = 25f; action.Combat.ScaleDamageWithSpeed = true;
                action.Combat.SpeedDamage = AnimationCurve.Linear(0.8f, 1f, 12f, 6.6f);
                action.Combat.HitVolumes.Add(new ActionHitVolume { DisplayName = "高速刀光", Center = new Vector3(0f, .9f, 1.5f),
                    Size = new Vector3(2.2f, 1.8f, 2.4f), Start = new ActionFrameAnchor { SegmentId = "active" },
                    End = new ActionFrameAnchor { SegmentId = "active", RelativeTo = ActionFrameAnchor.Boundary.SegmentEnd } });
                action.Motion = new ActionMotionSettings { Enabled = true, Start = new ActionFrameAnchor { SegmentId = "active" },
                    End = new ActionFrameAnchor { SegmentId = "active", RelativeTo = ActionFrameAnchor.Boundary.SegmentEnd }, ClearMomentumOnCompletion = true };
            }
            if (create) AssetDatabase.CreateAsset(action, path);
            return action;
        }

        private static void AddSegment(ActionDefinition action, AnimationClip clip, string state, string id, string label, ActionPhase phase, int start, int end, int total)
            => action.Timeline.Add(new ActionSegment { SegmentId = id, DisplayName = label, Phase = phase, DurationFrames = end - start,
                Control = new ActionControlPolicy { AllowJump = false, AllowSlide = false },
                Animation = new ActionAnimationBinding { AnimatorState = "Base Layer.Actions." + state, Clip = clip,
                    NormalizedStart = start / (float)total, NormalizedEnd = end / (float)total, BlendFrames = 3 } });

        private static void EnsureMember(ActionCatalog catalog, ActionDefinition action)
        { if (!catalog.Actions.Contains(action)) catalog.Actions.Add(action); }

        private static void EnsureCancel(ActionDefinition source, ActionDefinition target)
        {
            if (source.CancelWindows.Exists(window => window.Targets.Contains(target))) return;
            source.CancelWindows.Add(new ActionCancelWindow { DisplayName = "高速收招接下一刀", Start = new ActionFrameAnchor { SegmentId = "recovery" },
                Targets = new List<ActionDefinition> { target }, RequireAll = new List<string> { "speed_high" }, IgnoreCooldown = true });
        }
    }
}
