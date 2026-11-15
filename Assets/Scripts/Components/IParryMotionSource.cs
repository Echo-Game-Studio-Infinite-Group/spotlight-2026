public interface IParryMotionSource
{
    bool TryGetParryMotion(out AttackVolumePose from, out AttackVolumePose to);
}
