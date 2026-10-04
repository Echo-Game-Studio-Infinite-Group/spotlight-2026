namespace GameJam.Actions
{
    public readonly struct ActionValidationIssue
    {
        public enum Severity { Warning, Error }
        public readonly Severity Level;
        public readonly ActionDefinition Action;
        public readonly string Message;
        public readonly int WindowIndex;

        public ActionValidationIssue(Severity level, string message, ActionDefinition action = null, int windowIndex = -1)
        {
            Level = level;
            Action = action;
            Message = message;
            WindowIndex = windowIndex;
        }
    }
}
