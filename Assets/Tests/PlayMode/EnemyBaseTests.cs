using System;
using NUnit.Framework;
using UnityEngine;

public sealed class EnemyBaseTests
{
    private GameObject _root;

    [TearDown]
    public void Cleanup()
    {
        if (_root != null) UnityEngine.Object.DestroyImmediate(_root);
    }

    private EnemyBase CreateEnemy(Type enemyType)
    {
        _root = new GameObject("EnemyBaseFixture");
        _root.SetActive(false);
        var enemy = (EnemyBase)_root.AddComponent(enemyType);
        enemy.ConfigureStats(120f, 8f, 2f);
        enemy.SetInvulnerableTime(0f);
        _root.SetActive(true);
        return enemy;
    }

    [TestCase(typeof(Enemy))]
    [TestCase(typeof(EnemyHeavy))]
    public void DamageDeathAndReset_WorkForBothEnemyTypes(Type enemyType)
    {
        EnemyBase enemy = CreateEnemy(enemyType);
        Assert.AreEqual(20f, enemy.TakeDamage(20f, Vector3.zero, Vector3.forward));
        Assert.AreEqual(100f, enemy.Health);
        Assert.AreEqual(1, enemy.DamagedCount);

        enemy.TakeDamage(200f, Vector3.zero, Vector3.forward);
        Assert.IsFalse(enemy.IsAlive);
        Assert.IsFalse(enemy.IsAttacking);

        enemy.ResetHealth();
        Assert.IsTrue(enemy.IsAlive);
        Assert.AreEqual(120f, enemy.Health);
        Assert.AreEqual(0, enemy.DamagedCount);
        Assert.IsFalse(enemy.IsAttacking);
    }

    [TestCase(typeof(Enemy))]
    [TestCase(typeof(EnemyHeavy))]
    public void Reenable_DoesNotDuplicateDamageCallbacks(Type enemyType)
    {
        EnemyBase enemy = CreateEnemy(enemyType);
        // 对象池反复启停后，一次伤害仍只能触发一次敌人反馈。
        for (int i = 0; i < 3; i++)
        {
            _root.SetActive(false);
            _root.SetActive(true);
        }

        enemy.TakeDamage(10f, Vector3.zero, Vector3.forward);
        Assert.AreEqual(110f, enemy.Health);
        Assert.AreEqual(1, enemy.DamagedCount);
    }
}
