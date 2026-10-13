#if UNITY_EDITOR
using System.Collections;
using Cinemachine;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// 子弹命中链路的回归测试。
//
// 断言的是「子弹撞到玩家 → 真的扣血 → 并且震屏」这三步都发生了。
// 为什么这样验：CameraShaker.Emit 在生成 impulse 之前会把命中方向写进
// source.m_DefaultVelocity，所以该字段被改写就等于证明整整一条链都跑到了：
//   OnTriggerEnter → Hitbox.Report → CombatComponent.TryHit（阵营过滤/扣血）
//   → Landed → Projectile.OnLanded → CameraShaker.Emit
public sealed class ProjectileHitTests
{
    private GameObject _vcamGo;
    private GameObject _bulletGo;
    private GameObject _targetGo;

    [TearDown]
    public void TearDown()
    {
        if (_bulletGo != null) Object.Destroy(_bulletGo);
        if (_targetGo != null) Object.Destroy(_targetGo);
        if (_vcamGo != null) Object.Destroy(_vcamGo);
    }

    [UnityTest]
    public IEnumerator BulletHit_DamagesTarget_AndDrivesShake()
    {
        // 接收端：vcam 上挂 CameraShaker（Awake 会设静态实例并补 ImpulseListener）
        _vcamGo = new GameObject("HitTestVcam");
        _vcamGo.AddComponent<CinemachineVirtualCamera>();
        _vcamGo.AddComponent<CameraShaker>();
        yield return null;

        // 目标：玩家阵营，带非触发碰撞体（与 Player.prefab 的 CapsuleCollider 一致）
        _targetGo = new GameObject("HitTestTarget");
        _targetGo.AddComponent<CapsuleCollider>();
        var targetHealth = _targetGo.AddComponent<HealthComponent>();
        var targetCombat = _targetGo.AddComponent<CombatComponent>();
        targetCombat.Camp = CampType.Player;
        _targetGo.transform.position = new Vector3(0f, 0f, 3f);

        // 子弹：与 Bullet.prefab 同构（Projectile + Hitbox + 触发器 + 运动学刚体 + 发射端 + 结算层）
        _bulletGo = new GameObject("HitTestBullet");
        _bulletGo.transform.position = Vector3.zero;
        var box = _bulletGo.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.size = new Vector3(0.25f, 0.25f, 0.6f);
        var body = _bulletGo.AddComponent<Rigidbody>();
        body.isKinematic = true;
        body.useGravity = false;
        _bulletGo.AddComponent<Hitbox>();
        _bulletGo.AddComponent<CombatComponent>();
        var impulse = _bulletGo.AddComponent<CinemachineImpulseSource>();
        var projectile = _bulletGo.AddComponent<Projectile>();
        yield return null;

        float healthBefore = targetHealth.Health;
        impulse.m_DefaultVelocity = Vector3.zero;

        projectile.Launch(Vector3.forward, null);

        // 让子弹飞过去并让物理步处理触发器。
        float deadline = Time.realtimeSinceStartup + 3f;
        while (Time.realtimeSinceStartup < deadline && targetHealth.Health >= healthBefore)
            yield return null;

        Assert.Less(targetHealth.Health, healthBefore,
            "子弹撞到玩家后应当扣血（否则触发器或判定宿主没接上）");
        Assert.Greater(impulse.m_DefaultVelocity.magnitude, 0.01f,
            "命中后应当由子弹自己的发射端写入 impulse 方向，震屏才会发生");
    }
}
#endif
