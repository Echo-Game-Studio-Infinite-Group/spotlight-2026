using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GameJam.Actions.Editor
{
    public sealed class ActionSequencePreview : IActionSequenceHost, IActionSequenceSink, IActionSequenceSimulationSink
    {
        private readonly ActionCatalog _catalog;
        private readonly List<ActionInputEdge> _edges = new List<ActionInputEdge>();
        private readonly List<string> _log = new List<string>();
        private readonly HashSet<string> _conditions = new HashSet<string>();
        private ActionSequencePlayer _player;
        private long _nextTick;
        private ActionInputButtons _held;
        private ActionInputButtons _button = ActionInputButtons.Attack;
        private Vector2 _move = Vector2.up;
        private Vector2 _scroll;
        private string _flags = "speed_low,grounded";
        private float _energy = 200;
        private float _rate = 1;
        private bool _hitStop;
        private bool _playing;
        private double _nextStepTime;
        public ActionSequencePlayer Player => _player;
        public bool Playing => _playing;
        public Action<ActionExecutionState, double, bool> AnimationSampled;
        private ActionDefinition _scheduledSource, _scheduledTarget;
        private int _scheduledFrame;

        public ActionSequencePreview(ActionCatalog catalog)
        {
            _catalog = catalog;
            ParseFlags();
            _player = new ActionSequencePlayer(catalog, this, this);
        }

        public bool Update()
        {
            if (!_playing) return false;
            double now = EditorApplication.timeSinceStartup;
            int steps = 0;
            while (now >= _nextStepTime && steps++ < 4)
            {
                Step();
                _nextStepTime += 1.0 / ActionSequencePlayer.FramesPerSecond;
            }
            if (now - _nextStepTime > 0.1) _nextStepTime = now;
            return steps > 0;
        }

        private void Step()
        {
            if (_scheduledTarget != null && _player.State.Action == _scheduledSource && _player.State.ActionFrame >= _scheduledFrame)
            { _player.Queue(_scheduledTarget, _nextTick, _move); _scheduledTarget = null; }
            _player.Tick(new ActionInputSample(_nextTick++, _held, _move, _edges), _hitStop ? 0 : _rate, _hitStop);
            _edges.Clear();
        }

        public void Draw(ActionDefinition selected)
        {
            EditorGUILayout.HelpBox("条件、能量和输入均为模拟值；模型预览使用独立克隆，只采样骨骼，不执行场景伤害和运动命令。修改配置会重置沙盒。", MessageType.Info);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(_playing ? "暂停" : "播放", GUILayout.Width(70))) { _playing = !_playing; _nextStepTime = EditorApplication.timeSinceStartup; }
            if (GUILayout.Button("单个采样帧", GUILayout.Width(110))) Step();
            if (GUILayout.Button("重置", GUILayout.Width(70))) Reset();
            EditorGUILayout.LabelField("输入采样帧 " + _nextTick + " · 请求 " + _player.PendingCount);
            EditorGUILayout.EndHorizontal();
            _rate = EditorGUILayout.Slider("玩家时间倍率", _rate, 0, 1);
            _hitStop = EditorGUILayout.Toggle("模拟顿帧", _hitStop);
            _energy = Mathf.Max(0, EditorGUILayout.FloatField("沙盒能量", _energy));
            EditorGUI.BeginChangeCheck();
            _flags = EditorGUILayout.TextField("成立的条件键（逗号分隔）", _flags);
            if (EditorGUI.EndChangeCheck()) ParseFlags();
            _move = EditorGUILayout.Vector2Field("输入方向快照", _move);
            EditorGUILayout.BeginHorizontal();
            _button = (ActionInputButtons)EditorGUILayout.EnumPopup("语义键", _button);
            using (new EditorGUI.DisabledScope(!ActionInputRecognizer.IsSingleButton(_button)))
            {
                if (GUILayout.Button("按下", GUILayout.Width(60)))
                {
                    _held |= _button;
                    _edges.Add(new ActionInputEdge(_button, ActionInputStep.Edge.Pressed, _held, _move));
                    if (!_playing) Step();
                }
                if (GUILayout.Button("松开", GUILayout.Width(60)))
                {
                    _edges.Add(new ActionInputEdge(_button, ActionInputStep.Edge.Released, _held, _move));
                    _held &= ~_button;
                    if (!_playing) Step();
                }
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField("已按住", _held.ToString());
            using (new EditorGUI.DisabledScope(selected == null))
                if (GUILayout.Button("直接请求选中动作（预留 Queue 接口）"))
                {
                    _player.Queue(selected, _nextTick, _move);
                    if (!_playing) Step();
                }
            ActionExecutionState state = _player.State;
            EditorGUILayout.Space();
            if (state.Action != null)
            {
                EditorGUILayout.LabelField("当前动作", state.Action.Label + " / 实例 " + state.InstanceId);
                EditorGUILayout.LabelField("当前子段", state.Segment.DisplayName + " / " + ActionAuthoringGUI.PhaseLabel(state.Segment.Phase));
                EditorGUILayout.LabelField("动作帧 / 段内帧", state.FrameProgress.ToString("0.00") + " / " + state.SegmentFrameProgress.ToString("0.00"));
                EditorGUILayout.LabelField("动画归一化位置", state.AnimationNormalizedTime.ToString("0.000"));
                ActionTimelineView.Draw(state.Action, state.FrameProgress, -1, null);
            }
            else EditorGUILayout.LabelField("当前动作", "空闲");
            if (!string.IsNullOrEmpty(_player.LastRejection)) EditorGUILayout.HelpBox(_player.LastRejection, MessageType.None);
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(130));
            foreach (string line in _log) EditorGUILayout.LabelField(line, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndScrollView();
        }

        private void ParseFlags()
        {
            _conditions.Clear();
            foreach (string key in _flags.Split(',')) if (!string.IsNullOrWhiteSpace(key)) _conditions.Add(key.Trim());
        }
        public bool CheckCondition(string conditionKey, ActionExecutionState source, ActionInputRequest request)
        {
            if (conditionKey == "non_stationary_jump") return request.Direction.sqrMagnitude > 0;
            if (conditionKey == "forward_jump") return request.Direction.y > 0;
            if (conditionKey == "flash_available") return _conditions.Contains("parry") || _conditions.Contains("limb_break");
            return _conditions.Contains(conditionKey);
        }
        public bool CanStart(ActionExecutionState source, ActionInputRequest request, out string reason)
        {
            bool enough = _energy >= request.Target.EnergyCost;
            reason = enough ? null : request.Target.Label + "：沙盒能量不足";
            return enough;
        }
        public bool TryCommit(ActionExecutionState source, ActionInputRequest request)
        {
            if (_energy < request.Target.EnergyCost) return false;
            _energy -= request.Target.EnergyCost;
            if (request.Target.ActionId == "flash") { _conditions.Remove("parry"); _conditions.Remove("limb_break"); _flags = string.Join(",", _conditions); }
            return true;
        }
        public void OnActionStarted(ActionExecutionState state) => Log("起招：" + state.Action.Label + " #" + state.InstanceId);
        public void OnSegmentEntered(ActionExecutionState state) { Log("进入子段：" + state.Segment.DisplayName + " @" + state.ActionFrame); AnimationSampled?.Invoke(state, 0, true); }
        public void OnFrameEvent(ActionExecutionState state, ActionFrameEvent frameEvent) => Log("帧事件：" + frameEvent.EventKey + " / 命中段 " + frameEvent.HitGroup + " @" + state.ActionFrame);
        public void OnActionEnded(ActionExecutionState state, ActionExitReason reason) { Log("退出：" + state.Action.Label + " / " + reason); AnimationSampled?.Invoke(default, 0, false); }
        public void OnStateSampled(ActionExecutionState state) => AnimationSampled?.Invoke(state, 0, false);
        public void OnSimulationStep(ActionExecutionState from, ActionExecutionState to, double frames) => AnimationSampled?.Invoke(to, frames, false);
        public void OnFrameBoundary(ActionExecutionState state) => AnimationSampled?.Invoke(state, 0, false);
        public void OnIdleSimulation(double frames) => AnimationSampled?.Invoke(default, frames, false);
        public void PreviewCancel(ActionDefinition source, ActionDefinition target, ActionCancelWindow window)
        {
            Reset();
            var flags = new HashSet<string>(_conditions);
            foreach (string key in source.StartConditions) flags.Add(key);
            foreach (string key in target.StartConditions) flags.Add(key);
            foreach (string key in window.RequireAll) flags.Add(key);
            if (window.RequireAny.Count > 0) flags.Add(window.RequireAny[0]);
            _flags = string.Join(",", flags); ParseFlags();
            _energy = Mathf.Max(_energy, source.EnergyCost + target.EnergyCost);
            source.TryGetWindowRange(window, out _scheduledFrame, out _);
            _scheduledSource = source; _scheduledTarget = target;
            _player.Queue(source, _nextTick, _move);
            _playing = true; _nextStepTime = EditorApplication.timeSinceStartup;
            Log("已填充模拟条件，按窗口起点请求目标动作；实际准入仍由执行器检查。");
        }
        private void Log(string text)
        {
            _log.Add("输入帧 " + _nextTick + " · " + text);
            if (_log.Count > 100) _log.RemoveAt(0);
        }
        public void Reset()
        {
            _player.Reset();
            _playing = false;
            _nextTick = 0;
            _held = ActionInputButtons.None;
            _edges.Clear();
            _log.Clear();
            _scheduledSource = _scheduledTarget = null;
            if (_catalog != null) _player = new ActionSequencePlayer(_catalog, this, this);
        }
    }
}
