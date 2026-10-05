namespace GameJam.Actions
{
    public readonly struct ActionCancelEdge
    {
        public readonly ActionDefinition Source;
        public readonly ActionDefinition Target;
        public readonly int WindowIndex;
        public readonly int StartFrame;
        public readonly int EndFrame;
        public readonly bool RangeValid;
        public ActionCancelWindow Window => Source.CancelWindows[WindowIndex];

        public ActionCancelEdge(ActionDefinition source, ActionDefinition target, int windowIndex,
            int startFrame, int endFrame, bool rangeValid)
        {
            Source = source;
            Target = target;
            WindowIndex = windowIndex;
            StartFrame = startFrame;
            EndFrame = endFrame;
            RangeValid = rangeValid;
        }
    }
}
