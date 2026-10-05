public sealed class ActionTestEnergy : IEnergyAccount
{
    public float Balance;
    public float CurrentEnergy => Balance;
    public float MaxEnergy => 100f;
    public bool TrySpend(float amount)
    {
        if (Balance < amount) return false;
        Balance -= amount;
        return true;
    }
}
