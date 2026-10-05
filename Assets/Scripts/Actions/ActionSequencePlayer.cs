using System;
using System.Collections.Generic;
using UnityEngine;

namespace GameJam.Actions
{
    // 纯逻辑执行器不挂到角色，调用方显式传入输入采样帧和所属角色的缩放帧增量。
    public sealed class ActionSequencePlayer
    {
        public const int FramesPerSecond = 60;
        // Unity 固定步时长和倍率是 float，边界比较需消除累加误差，避免整帧事件晚一采样帧。
        private const double FrameTolerance = 0.000001;
        private readonly ActionCatalog _catalog;
        private readonly IActionSequenceHost _host;
        private readonly IActionSequenceSink _sink;
        private readonly ActionInputRecognizer _recognizer;
        private readonly ActionRequestBuffer _buffer;
        private readonly List<ActionInputRequest> _recognized = new List<ActionInputRequest>();
        private readonly Dictionary<ActionDefinition, double> _readyAt = new Dictionary<ActionDefinition, double>();
        private ActionDefinition _action;
        private ActionInputRequest _currentInput;
        private long _instanceId;
        private long _nextInstanceId;
        private long _nextManualId;
        private long _lastTick = -1;
        private int _segmentIndex;
        private int _segmentStart;
        private double _progress;
        private double _actorFrame;

        public ActionExecutionState State => new ActionExecutionState(_action, _instanceId, _segmentIndex,
            _progress, _segmentStart, _currentInput);
        public bool IsRunning => _action != null;
        public int PendingCount => _buffer.Count;
        public string LastRejection { get; private set; }

        public ActionSequencePlayer(ActionCatalog catalog, IActionSequenceHost host, IActionSequenceSink sink)
        {
            _catalog = catalog != null ? catalog : throw new ArgumentNullException(nameof(catalog));
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _sink = sink;
            _recognizer = new ActionInputRecognizer(catalog);
            _buffer = new ActionRequestBuffer(catalog.BufferCapacity);
        }

        // 此入口适用于已由外部识别的指令、回放和独立预览，不直接模拟设备按键。
        public bool Queue(ActionDefinition target, long inputTick, Vector2 direction = default)
        {
            if (target == null || !_catalog.Actions.Contains(target) || inputTick < _lastTick) return false;
            return _buffer.Push(new ActionInputRequest(target, --_nextManualId, inputTick, _instanceId, direction));
        }

        public void Tick(ActionInputSample sample, double playerFrameDelta, bool inHitStop = false)
        {
            if (sample.Tick <= _lastTick) throw new ArgumentOutOfRangeException(nameof(sample), "输入采样帧必须严格递增");
            if (double.IsNaN(playerFrameDelta) || double.IsInfinity(playerFrameDelta) || playerFrameDelta < 0)
                throw new ArgumentOutOfRangeException(nameof(playerFrameDelta));
            long elapsed = _lastTick < 0 ? 0 : sample.Tick - _lastTick;
            _buffer.Prune(sample.Tick, inHitStop ? elapsed : 0);
            _lastTick = sample.Tick;
            _recognized.Clear();
            _recognizer.Observe(sample, State, _host, _recognized);
            foreach (ActionInputRequest request in _recognized) _buffer.Push(request);
            if (playerFrameDelta > 0)
            {
                TryActivate(sample.Tick);
                _buffer.DiscardImmediate(sample.Tick);
                Advance(playerFrameDelta, sample.Tick);
            }
            else _buffer.DiscardImmediate(sample.Tick);
            if (IsRunning) _sink?.OnStateSampled(State);
        }

        private bool TryActivate(long tick)
        {
            int bestIndex = -1;
            int bestWindowPriority = int.MinValue;
            int bestInputPriority = int.MinValue;
            for (int i = 0; i < _buffer.Count; i++)
            {
                ActionInputRequest request = _buffer[i];
                ActionDefinition target = request.Target;
                if (request.CreatedTick > tick || target == null) continue;
                if (request.SourceInstanceId != 0 && IsRunning && request.SourceInstanceId != _instanceId && !target.Input.KeepOnSourceCancel) continue;
                if (!HasExecutableTimeline(target)) { LastRejection = target.Label + "：时间轴无效"; continue; }
                if (_readyAt.TryGetValue(target, out double ready) && _actorFrame + FrameTolerance < ready) { LastRejection = target.Label + "：冷却中"; continue; }
                if (!ActionConditionEvaluator.Matches(target.StartConditions, null, _host, State, request)) { LastRejection = target.Label + "：进入条件不满足"; continue; }
                int windowPriority = 0;
                if (IsRunning && !TryCancelPermission(request, out windowPriority)) { LastRejection = target.Label + "：取消窗口未开放或条件不满足"; continue; }
                if (!_host.CanStart(State, request, out string reason)) { LastRejection = reason; continue; }
                int inputPriority = target.Input.Priority;
                if (bestIndex >= 0 && (bestWindowPriority > windowPriority ||
                    bestWindowPriority == windowPriority && bestInputPriority >= inputPriority)) continue;
                bestIndex = i;
                bestWindowPriority = windowPriority;
                bestInputPriority = inputPriority;
            }
            if (bestIndex < 0) return false;
            ActionInputRequest chosen = _buffer[bestIndex];
            if (!_host.TryCommit(State, chosen)) { LastRejection = chosen.Target.Label + "：提交失败，保留原动作与请求"; return false; }
            _buffer.RemoveAt(bestIndex);
            if (IsRunning) End(ActionExitReason.Cancelled);
            _action = chosen.Target;
            _currentInput = chosen;
            _instanceId = ++_nextInstanceId;
            _segmentIndex = _segmentStart = 0;
            _progress = 0;
            _readyAt[_action] = _actorFrame + _action.CooldownFrames;
            LastRejection = null;
            _sink?.OnActionStarted(State);
            _sink?.OnSegmentEntered(State);
            EmitCurrentFrame();
            return true;
        }

        public bool TryCancelPermission(ActionInputRequest request, out int priority)
        {
            priority = int.MinValue;
            if (!IsRunning) { priority = 0; return true; }
            bool allowed = false;
            if (_action.CancelWindows == null) return false;
            foreach (ActionCancelWindow window in _action.CancelWindows)
            {
                if (window == null || window.Targets == null || !window.Targets.Contains(request.Target) ||
                    !_action.TryGetWindowRange(window, out int start, out int end) || _progress < start || _progress >= end ||
                    !ActionConditionEvaluator.Matches(window.RequireAll, window.RequireAny, _host, State, request)) continue;
                allowed = true;
                priority = Math.Max(priority, window.Priority);
            }
            return allowed;
        }

        private void Advance(double remaining, long tick)
        {
            while (remaining > 0 && IsRunning)
            {
                double boundary = Math.Floor(_progress) + 1;
                double step = Math.Min(remaining, boundary - _progress);
                _progress += step;
                _actorFrame += step;
                remaining -= step;
                if (boundary - _progress > FrameTolerance) break;
                _progress = boundary;
                if (_progress >= _action.TotalFrames)
                {
                    End(ActionExitReason.Completed);
                    TryActivate(tick);
                    continue;
                }
                bool entered = false;
                while (_progress >= _segmentStart + _action.Timeline[_segmentIndex].DurationFrames)
                {
                    _segmentStart += _action.Timeline[_segmentIndex].DurationFrames;
                    _segmentIndex++;
                    entered = true;
                }
                // 同帧先仲裁取消，再发送旧动作帧事件，避免已经取消的动作再打开判定。
                if (TryActivate(tick)) continue;
                if (entered) _sink?.OnSegmentEntered(State);
                EmitCurrentFrame();
            }
            _actorFrame += remaining;
        }

        private void EmitCurrentFrame()
        {
            ActionExecutionState state = State;
            if (state.Segment?.Events == null) return;
            foreach (ActionFrameEvent frameEvent in state.Segment.Events)
                if (frameEvent != null && frameEvent.Frame == state.SegmentFrame) _sink?.OnFrameEvent(state, frameEvent);
        }

        private void End(ActionExitReason reason)
        {
            ActionExecutionState ended = State;
            _buffer.DiscardSource(_instanceId, reason != ActionExitReason.Completed);
            _action = null;
            _instanceId = 0;
            _sink?.OnActionEnded(ended, reason);
        }

        private static bool HasExecutableTimeline(ActionDefinition target)
        {
            if (target.Timeline == null || target.Timeline.Count == 0 || target.TotalFrames == int.MaxValue) return false;
            foreach (ActionSegment segment in target.Timeline)
                if (segment == null || segment.DurationFrames <= 0) return false;
            return true;
        }

        public void Reset()
        {
            if (IsRunning) End(ActionExitReason.Reset);
            _buffer.Clear();
            _recognizer.Clear();
            _readyAt.Clear();
            _lastTick = -1;
            _actorFrame = 0;
            _progress = 0;
            LastRejection = null;
        }
    }
}
