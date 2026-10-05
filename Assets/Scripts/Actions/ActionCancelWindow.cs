using System;
using System.Collections.Generic;

namespace GameJam.Actions
{
    [Serializable]
    public sealed class ActionCancelWindow
    {
        public string WindowId = Guid.NewGuid().ToString("N");
        public string DisplayName = "新取消窗口";
        public ActionFrameAnchor Start = new ActionFrameAnchor();
        public ActionFrameAnchor End = new ActionFrameAnchor { RelativeTo = ActionFrameAnchor.Boundary.ActionEnd };
        public List<ActionDefinition> Targets = new List<ActionDefinition>();
        public List<string> RequireAll = new List<string>();
        public List<string> RequireAny = new List<string>();
        public int Priority;
    }
}
