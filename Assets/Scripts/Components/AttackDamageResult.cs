public readonly struct AttackDamageResult
{
    public readonly float AppliedDamage;
    public readonly bool Killed;
    public AttackDamageResult(float appliedDamage, bool killed)
    { AppliedDamage = appliedDamage; Killed = killed; }
}
