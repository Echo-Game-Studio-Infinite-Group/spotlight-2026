using NUnit.Framework;
using UnityEngine;

public sealed class MovementTimeTests
{
    [Test]
    public void PlayerFixedTime_UsesFixedStepAndPlayerScale()
    {
        GameObject go = new GameObject("TestTime");
        try
        {
            TimeManager manager = go.AddComponent<TimeManager>();
            manager.PlayerScale = 0.25f;
            Assert.That(TimeManager.PlayerFixedDeltaTime, Is.EqualTo(Time.fixedDeltaTime * 0.25f).Within(0.000001f));
            TimeManager.HitStop(1f, 0f);
            Assert.AreEqual(0f, TimeManager.PlayerFixedDeltaTime);
        }
        finally { Object.DestroyImmediate(go); }
    }

}
