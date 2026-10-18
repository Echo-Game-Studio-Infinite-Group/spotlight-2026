public interface IAttackDamageReceiver
{
    AttackDamageResult ReceiveAttack(AttackDamageRequest request);
}

public interface IParryReceiver
{
    bool TryParry(AttackDamageRequest request);
}
