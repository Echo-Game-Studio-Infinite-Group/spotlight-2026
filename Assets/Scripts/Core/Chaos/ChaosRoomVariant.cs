using System;
using UnityEngine;

[Serializable]
public sealed class ChaosRoomVariant
{
    public string Id;
    public int MinimumChaos;
    public int MaximumChaos = 100;
    [Tooltip("仅包含差异部分；物件、敌人、文字、局部灯光都可放在预制体中。")]
    public GameObject ContentPrefab;
    public bool Matches(int value) => value >= MinimumChaos && value <= MaximumChaos;
}
