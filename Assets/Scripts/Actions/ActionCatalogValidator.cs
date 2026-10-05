using System;
using System.Collections.Generic;

namespace GameJam.Actions
{
    public static class ActionCatalogValidator
    {
        public static List<ActionValidationIssue> Validate(ActionCatalog catalog)
        {
            var issues = new List<ActionValidationIssue>();
            if (catalog == null) { Error(issues, null, "未选择动作集"); return issues; }
            if (catalog.BufferCapacity <= 0 || catalog.InputHistoryCapacity <= 0) Error(issues, null, "缓冲容量和输入历史容量必须大于零");
            if (catalog.Actions == null || catalog.Actions.Count == 0) { Error(issues, null, "动作集为空"); return issues; }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var members = new HashSet<ActionDefinition>();
            foreach (ActionDefinition action in catalog.Actions)
            {
                if (action == null) { Error(issues, null, "动作集存在空引用"); continue; }
                if (!members.Add(action)) Error(issues, action, "动作集重复引用同一资产");
                if (string.IsNullOrWhiteSpace(action.ActionId)) Error(issues, action, "动作 ID 不能为空");
                else if (!ids.Add(action.ActionId)) Error(issues, action, "动作 ID 重复：" + action.ActionId);
                ValidateAction(action, issues);
            }
            foreach (ActionDefinition action in members)
            {
                if (action.CancelWindows == null) continue;
                for (int i = 0; i < action.CancelWindows.Count; i++)
                {
                    ActionCancelWindow window = action.CancelWindows[i];
                    if (window?.Targets == null) continue;
                    var targets = new HashSet<ActionDefinition>();
                    foreach (ActionDefinition target in window.Targets)
                    {
                        if (target == null) Error(issues, action, "窗口「" + window.DisplayName + "」包含空目标", i);
                        else if (!members.Contains(target)) Error(issues, action, "窗口目标不在当前动作集中：" + target.Label, i);
                        else if (!targets.Add(target)) Warn(issues, action, "窗口重复引用目标：" + target.Label, i);
                    }
                }
            }
            return issues;
        }

        public static void ValidateAction(ActionDefinition action, List<ActionValidationIssue> issues)
        {
            if (action == null) return;
            if (action.EnergyCost < 0 || !Finite(action.EnergyCost) || action.CooldownFrames < 0) Error(issues, action, "耗能和冷却必须为非负有限值");
            if (action.Timeline == null || action.Timeline.Count == 0) Error(issues, action, "至少配置一个子段；零时长阶段可直接省略");
            var segmentIds = new HashSet<string>(StringComparer.Ordinal);
            long total = 0;
            if (action.Timeline != null)
                foreach (ActionSegment segment in action.Timeline)
                {
                    if (segment == null) { Error(issues, action, "时间轴存在空子段"); continue; }
                    if (string.IsNullOrWhiteSpace(segment.SegmentId) || !segmentIds.Add(segment.SegmentId)) Error(issues, action, "子段稳定 ID 为空或重复：" + segment.DisplayName);
                    if (segment.DurationFrames <= 0) Error(issues, action, "子段时长至少为 1 帧：" + segment.DisplayName);
                    if (!Enum.IsDefined(typeof(ActionPhase), segment.Phase)) Error(issues, action, "子段阶段无效：" + segment.DisplayName);
                    total += Math.Max(0, segment.DurationFrames);
                    if (segment.Control == null || !Finite(segment.Control.GravityMultiplier) || segment.Control.GravityMultiplier < 0 || !Finite(segment.Control.CommandValue))
                        Error(issues, action, "子段控制参数无效：" + segment.DisplayName);
                    ActionAnimationBinding animation = segment.Animation;
                    if (animation == null || !Finite(animation.NormalizedStart) || !Finite(animation.NormalizedEnd) ||
                        animation.NormalizedStart < 0 || animation.NormalizedEnd > 1 || animation.NormalizedStart >= animation.NormalizedEnd || animation.BlendFrames < 0)
                        Error(issues, action, "动画区间需满足 0 ≤ 起点 < 终点 ≤ 1，混合帧非负：" + segment.DisplayName);
                    if (segment.Events == null) continue;
                    foreach (ActionFrameEvent frameEvent in segment.Events)
                        if (frameEvent == null || frameEvent.Frame < 0 || frameEvent.Frame >= segment.DurationFrames ||
                            string.IsNullOrWhiteSpace(frameEvent.EventKey) || !Finite(frameEvent.Value) || frameEvent.HitGroup < 0)
                            Error(issues, action, "帧事件需位于子段 [0, 时长)，且事件键非空：" + segment.DisplayName);
                }
            if (total >= int.MaxValue) Error(issues, action, "动作总帧数溢出");
            var windowIds = new HashSet<string>(StringComparer.Ordinal);
            if (action.CancelWindows != null)
                for (int i = 0; i < action.CancelWindows.Count; i++)
                {
                    ActionCancelWindow window = action.CancelWindows[i];
                    if (window == null) { Error(issues, action, "存在空取消窗口", i); continue; }
                    if (string.IsNullOrWhiteSpace(window.WindowId) || !windowIds.Add(window.WindowId)) Error(issues, action, "取消窗口稳定 ID 为空或重复", i);
                    if (!action.TryGetWindowRange(window, out _, out _)) Error(issues, action, "窗口「" + window.DisplayName + "」锚点失效或区间不满足 0 ≤ 起点 < 终点 ≤ 总帧数", i);
                    if (window.Targets == null || window.Targets.Count == 0) Warn(issues, action, "窗口「" + window.DisplayName + "」没有目标，不授予取消权限", i);
                    ValidateKeys(window.RequireAll, issues, action, "窗口全部条件", i);
                    ValidateKeys(window.RequireAny, issues, action, "窗口任一条件", i);
                }
            ValidateKeys(action.StartConditions, issues, action, "动作进入条件");
            ActionInputPolicy input = action.Input;
            if (input == null) { Error(issues, action, "缺少输入配置"); return; }
            if (input.PreInputFrames < 0 || input.MaxStepGapFrames < 0) Error(issues, action, "预输入与序列间隔必须非负");
            ValidateKeys(input.RequireAll, issues, action, "输入全部资格");
            ValidateKeys(input.RequireAny, issues, action, "输入任一资格");
            if (input.Steps == null || input.Steps.Count == 0) Warn(issues, action, "未配置输入序列，仅可从外部 Queue 接口请求");
            else
                for (int i = 0; i < input.Steps.Count; i++)
                {
                    ActionInputStep step = input.Steps[i];
                    if (step == null || !ActionInputRecognizer.IsSingleButton(step.Button)) { Error(issues, action, "每个输入步骤必须指定一个语义按键"); continue; }
                    if ((step.RequireHeld & step.ForbidHeld) != 0) Error(issues, action, "输入步骤同时要求和禁止同一修饰键");
                    if (step.MinHoldFrames < 0 || step.MaxHoldFrames < 0 || step.MaxHoldFrames > 0 && step.MaxHoldFrames < step.MinHoldFrames) Error(issues, action, "输入按住时长范围无效；最大值 0 表示不限");
                    if (step.Trigger == ActionInputStep.Edge.Held && (i != input.Steps.Count - 1 || step.MinHoldFrames < 1)) Error(issues, action, "长按只用于序列最后一步，阈值至少为 1 采样帧");
                    if (step.Trigger == ActionInputStep.Edge.Pressed && step.MinHoldFrames != 0) Error(issues, action, "按下沿不能要求已经按住若干帧，请使用长按或松开沿");
                }
        }

        private static void ValidateKeys(List<string> keys, List<ActionValidationIssue> issues, ActionDefinition action, string label, int window = -1)
        {
            if (keys == null) return;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string key in keys)
                if (string.IsNullOrWhiteSpace(key)) Error(issues, action, label + "包含空键", window);
                else if (!seen.Add(key)) Warn(issues, action, label + "重复：" + key, window);
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static void Error(List<ActionValidationIssue> issues, ActionDefinition action, string message, int window = -1)
            => issues.Add(new ActionValidationIssue(ActionValidationIssue.Severity.Error, message, action, window));
        private static void Warn(List<ActionValidationIssue> issues, ActionDefinition action, string message, int window = -1)
            => issues.Add(new ActionValidationIssue(ActionValidationIssue.Severity.Warning, message, action, window));
    }
}
