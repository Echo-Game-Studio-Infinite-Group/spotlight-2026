using System;
using System.Collections.Generic;
using UnityEngine;

namespace GameJam.Actions
{
    [Serializable]
    public sealed class ActionInputPolicy
    {
        public enum Replacement { LatestInGroup, EarliestInGroup, Queue }
        public List<ActionInputStep> Steps = new List<ActionInputStep>();
        [Min(0)] public int MaxStepGapFrames;
        [Min(0)] public int PreInputFrames;
        public string BufferGroup = "main";
        public Replacement ReplacePolicy;
        public bool KeepOnSourceCancel;
        public bool FreezeExpiryDuringHitStop = true;
        public int Priority;
        // 输入资格只负责分辨语义；资源不足不得让技能输入降级成普通攻击。
        public List<string> RequireAll = new List<string>();
        public List<string> RequireAny = new List<string>();
    }
}
