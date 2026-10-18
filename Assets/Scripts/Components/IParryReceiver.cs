using System.Collections.Generic;

public interface IParryReceiver
{
    void ReceiveParry(IReadOnlyList<ParryContact> contacts);
}
