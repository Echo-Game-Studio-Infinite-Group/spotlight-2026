using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// 碎块的生存计时与资源归属。
//
// 只做两件事：
//   1. 生存时间不为 0 时起一个协程，到点销毁自己；为 0 表示"不自动消失"，那就不挂定时器。
//   2. 释放自己用到的 Mesh/Material——它们的所有权已由 GibComponent 转交过来。
//
// 为什么要把计时和资源都放在碎块自己身上：碎块是独立场景对象，
// 但驱动它们的东西（重力、骨骼烘焙）在敌人身上。敌人被对象池回收/复用时，
// 敌人的 ResetEffect 会跑一遍；若碎块还依赖敌人持有的资源，那一刻就会被清空或变白。
// 把计时与资源都搬到自己身上，碎块才真正独立于敌人的生命周期。
//
// 生存时间只有一个真值来源：GibComponent._lifetime（策划在 Inspector 配）。
// 这里不设默认值，避免出现第二个可调数字两边打架。
[DisallowMultipleComponent]
public sealed class GibPieceLifetime : MonoBehaviour
{
    private float _lifetime;
    private readonly List<Object> _ownedAssets = new List<Object>();

    /// <summary>由 GibComponent 在创建碎块时调用，传入策划配置的存活秒数；&lt;= 0 表示不自动消失。</summary>
    public void Configure(float lifetimeSeconds)
    {
        _lifetime = lifetimeSeconds;
        // 按策划配的值决定要不要起计时，0 就干脆不起，省掉一个常驻协程。
        if (_lifetime > 0f) StartCoroutine(Countdown());
    }

    /// <summary>
    /// 接管一份运行时资源（Mesh/Material）的所有权，随本碎块一起释放。
    /// 调用方负责保证同一份资源只交给一个碎块（材质在 GibComponent 里是缓存共享的）。
    /// </summary>
    public void Own(Object asset)
    {
        if (asset == null) return;
        if (_ownedAssets.Contains(asset)) return;
        _ownedAssets.Add(asset);
    }

    private IEnumerator Countdown()
    {
        // 走世界时间：与 GibComponent 的模拟同一时间基准，时停/顿帧时碎块一起慢下来，
        // 不会出现"画面还在慢动作、碎块却按真实时间偷偷到期"的割裂感。
        float elapsed = 0f;
        while (elapsed < _lifetime)
        {
            elapsed += TimeManager.WorldDeltaTime;
            yield return null;
        }
        Destroy(gameObject);
    }

    private void OnDestroy()
    {
        foreach (Object asset in _ownedAssets)
        {
            if (asset == null) continue;
            if (Application.isPlaying) Destroy(asset);
            else DestroyImmediate(asset);
        }
        _ownedAssets.Clear();
    }
}
