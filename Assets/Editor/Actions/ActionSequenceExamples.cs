using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GameJam.Actions.Editor
{
    public static class ActionSequenceExamples
    {
        public const string DefaultFolder = "Assets/Settings/ActionSequences/Examples";
        public const string CatalogFile = "ExampleActions.asset";

        public static ActionCatalog Create(string folder = DefaultFolder)
        {
            string path = folder + "/" + CatalogFile;
            ActionCatalog existing = AssetDatabase.LoadAssetAtPath<ActionCatalog>(path);
            if (existing != null) return existing;
            EnsureFolder(folder);
            var catalog = ScriptableObject.CreateInstance<ActionCatalog>();
            catalog.DisplayName = "1.03 取消链示例（独立）";
            catalog.Description = "动作关系参照 1.03；帧数与窗口位置为工具演示占位。未接入角色、能量账户、碰撞或 Animator。";
            AssetDatabase.CreateAsset(catalog, path);
            ActionDefinition low = Add(catalog, folder, "basic_low", "常态普攻", 8, 0, ActionInputButtons.Attack);
            ActionDefinition fast = Add(catalog, folder, "basic_fast", "高速普攻", 8, 0, ActionInputButtons.Attack);
            ActionDefinition flash = Add(catalog, folder, "flash", "闪斩", 8, 0, ActionInputButtons.Attack);
            ActionDefinition chain = Add(catalog, folder, "chain", "连斩", 10, 100, ActionInputButtons.Attack);
            ActionDefinition push = Add(catalog, folder, "push", "推斩", 8, 0, ActionInputButtons.Attack);
            ActionDefinition jump = Add(catalog, folder, "move_jump", "方向跳跃", 8, 0, ActionInputButtons.Jump);
            ActionDefinition left = Add(catalog, folder, "dodge_left", "左闪避", 6, 0, ActionInputButtons.Left);
            ActionDefinition right = Add(catalog, folder, "dodge_right", "右闪避", 6, 0, ActionInputButtons.Right);
            ActionDefinition reverse = Add(catalog, folder, "reverse", "折返", 8, 50, ActionInputButtons.Skill);
            ActionDefinition slide = Add(catalog, folder, "slide", "滑铲", 6, 0, ActionInputButtons.Slide);
            low.Input.RequireAll.Add("speed_low");
            fast.Input.RequireAll.Add("speed_high");
            flash.Input.RequireAll.Add("flash_available"); flash.StartConditions.Add("flash_available"); flash.Input.Priority = 20;
            foreach (ActionDefinition basic in new[] { low, fast, flash }) basic.Input.Steps[0].ForbidHeld = ActionInputButtons.Skill;
            chain.Input.Priority = 30; chain.Input.Steps[0].RequireHeld = ActionInputButtons.Skill | ActionInputButtons.Forward;
            push.Input.Priority = 30; push.Input.Steps[0].RequireHeld = ActionInputButtons.Skill; push.Input.Steps[0].ForbidHeld = ActionInputButtons.Forward;
            push.Description = "动态耗能 max(0,150-20v) 留给 IActionSequenceHost.TryCommit 计算，不将占位固定费用当正式公式。";
            jump.Input.Steps[0].ForbidHeld = ActionInputButtons.Skill; jump.Input.RequireAll.Add("non_stationary_jump"); jump.StartConditions.Add("non_stationary_jump");
            left.Input.Steps[0].RequireHeld = right.Input.Steps[0].RequireHeld = ActionInputButtons.Sprint;
            reverse.Input.Steps[0].RequireHeld = ActionInputButtons.Backward;
            slide.Description = "此示例只表示滑铲取消关系；实际进入速度与姿态由未来接入方检查。";
            Window(low, "非原地跳", "recovery", 0, null, 0, jump);
            Window(low, "闪避", "recovery", 4, null, 0, left, right);
            Window(low, "连斩后摇派生", "recovery", 2, null, -2, chain);
            Window(low, "推斩衔接", "active", 0, null, 0, push);
            Window(fast, "连斩后摇派生", "recovery", 0, null, 0, chain);
            Window(fast, "推斩衔接", "active", 0, null, 0, push);
            Window(fast, "折返", "recovery", 0, null, 0, reverse);
            foreach (ActionDefinition basic in new[] { low, fast })
            {
                ActionCancelWindow window = Window(basic, "parry / 断肢 → 闪斩", "active", 0, null, 0, flash);
                window.RequireAny.Add("parry"); window.RequireAny.Add("limb_break"); window.Priority = 10;
            }
            Window(flash, "接高速普攻", "active", 0, null, 0, fast);
            Window(flash, "后摇接连斩", "recovery", 0, null, 0, chain);
            Window(push, "接普攻", "active", 0, null, 0, low, fast);
            chain.Timeline = new List<ActionSegment>
            {
                Segment("start", "起势", ActionPhase.Startup, 4),
                Segment("rapid", "连续斩", ActionPhase.Active, 18),
                Segment("finisher_start", "末斩发生", ActionPhase.Startup, 4),
                Segment("finisher", "末斩", ActionPhase.Active, 5),
                Segment("locked_recovery", "不可取消收招", ActionPhase.Recovery, 15)
            };
            for (int i = 0; i < 3; i++) chain.Timeline[1].Events.Add(new ActionFrameEvent { Frame = i * 6, EventKey = "attack.strike", HitGroup = i + 1 });
            chain.Timeline[3].Events.Add(new ActionFrameEvent { EventKey = "attack.finisher", HitGroup = 4 });
            Window(chain, "前段接普攻", "rapid", 0, "finisher_start", 0, low, fast);
            ActionCancelWindow slideJump = Window(slide, "前跳取消", "active", 0, null, 0, jump);
            slideJump.RequireAll.Add("forward_jump");
            foreach (ActionDefinition action in catalog.Actions) EditorUtility.SetDirty(action);
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            return catalog;
        }

        private static ActionDefinition Add(ActionCatalog catalog, string folder, string id, string label, int buffer, float cost, ActionInputButtons button)
        {
            var action = ScriptableObject.CreateInstance<ActionDefinition>();
            action.ActionId = id; action.DisplayName = label; action.EnergyCost = cost;
            action.Description = "示例配置，帧数需通过手感测试调整；当前不驱动游戏动作。";
            action.Timeline = new List<ActionSegment>
            {
                Segment("startup_a", "起势", ActionPhase.Startup, 4),
                Segment("startup_b", "引刀 / 准备", ActionPhase.Startup, 6),
                Segment("active", "执行", ActionPhase.Active, 6),
                Segment("recovery", "恢复", ActionPhase.Recovery, 14)
            };
            action.Timeline[2].Events.Add(new ActionFrameEvent { EventKey = "action.active", HitGroup = 1 });
            action.Timeline[3].Events.Add(new ActionFrameEvent { EventKey = "action.recovery" });
            action.Input.PreInputFrames = buffer;
            action.Input.Steps.Add(new ActionInputStep { Button = button });
            AssetDatabase.CreateAsset(action, AssetDatabase.GenerateUniqueAssetPath(folder + "/" + id + ".asset"));
            catalog.Actions.Add(action);
            return action;
        }
        private static ActionSegment Segment(string id, string label, ActionPhase phase, int frames)
            => new ActionSegment { SegmentId = id, DisplayName = label, Phase = phase, DurationFrames = frames };
        private static ActionCancelWindow Window(ActionDefinition source, string label, string start, int startOffset,
            string end, int endOffset, params ActionDefinition[] targets)
        {
            var window = new ActionCancelWindow
            {
                DisplayName = label,
                Start = new ActionFrameAnchor { SegmentId = start, OffsetFrames = startOffset },
                End = new ActionFrameAnchor { SegmentId = end, RelativeTo = end == null ? ActionFrameAnchor.Boundary.ActionEnd : ActionFrameAnchor.Boundary.SegmentStart, OffsetFrames = endOffset },
                Targets = new List<ActionDefinition>(targets)
            };
            source.CancelWindows.Add(window);
            return window;
        }
        public static void EnsureFolder(string folder)
        {
            string[] parts = folder.Replace('\\', '/').Split('/');
            if (parts.Length == 0 || parts[0] != "Assets") throw new System.ArgumentException("资产目录必须位于 Assets 下");
            string current = "Assets";
            for (int i = 1; i < parts.Length; i++)
            {
                string child = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(child)) AssetDatabase.CreateFolder(current, parts[i]);
                current = child;
            }
        }
    }
}
