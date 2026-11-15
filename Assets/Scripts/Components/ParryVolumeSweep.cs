public readonly struct ParryVolumeSweep
{
    public readonly AttackVolumePose From, To;
    public readonly float Spacing, Start, End;
    public ParryVolumeSweep(AttackVolumePose from, AttackVolumePose to, float spacing, float start, float end)
    { From = from; To = to; Spacing = spacing; Start = start; End = end; }
}
