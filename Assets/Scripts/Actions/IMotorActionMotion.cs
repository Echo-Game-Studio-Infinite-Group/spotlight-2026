namespace GameJam.Actions
{
    public interface IMotorActionMotion
    {
        void BeginMotion(ActionMotionSettings settings, long instanceId);
        void EndMotion(long instanceId);
    }
}
