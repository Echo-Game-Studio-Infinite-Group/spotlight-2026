using System.Collections.Generic;
using Cinemachine;
using Spotlight.VFX;
using UnityEngine;

// 玩家身上的特效总控：挂在玩家根节点，所有"给玩家看的粒子"都从这里出声。
//   · 攻击刀光：Player/Vfx/AttackSlashes/AttackTrail1~4，每刀随机四选一，激活即播
//   · 摩擦火花（地面 / 滑铲 / 贴墙）：由 FrictionSparks 组件驱动；这里只负责缺了就地补
//   · 受击 / 撞墙：直接播挂在 Player/Vfx 下的粒子，拖进列表即生效，不需要再写代码
// 与 Animator 同一节点也能工作，但不要依赖它：攻击刀光既可能由动画事件（非序列）触发，
// 也可能由动作序列的 vfx.attack 帧事件触发，两条都汇到 PlayAttack。
[DisallowMultipleComponent]
public sealed class PlayerVFXManager : MonoBehaviour
{
    [Header("攻击刀光")]
    [Tooltip("刀光根节点（Vfx/AttackSlashes）；留空则运行时在 Vfx 下找")]
    [SerializeField] private GameObject _attackVfx;

    [Tooltip("刀光根节点下的四组刀光 AttackTrail1~4；随机关关闭后 index 才决定播哪一组；留空则运行时自动收集")]
    [SerializeField] private ParticleSystem[] _attackSlashes = new ParticleSystem[0];

    [Tooltip("每刀从四段里随机抽一段（且不连续重复）；关闭则严格按 PlayAttack 的 index 指定")]
    [SerializeField] private bool _randomAttackVariant = true;

    [Header("挂载在 Player/Vfx 下的其它特效（拖进来即生效）")]
    [Tooltip("玩家受击时播放的粒子（小兵砍中 / 中弹）")]
    [SerializeField] private ParticleSystem[] _hurtEffects = new ParticleSystem[0];

    [Tooltip("撞墙时播放的粒子（碎屑 / 火花）")]
    [SerializeField] private ParticleSystem[] _wallImpactEffects = new ParticleSystem[0];

    [Header("火花调参")]
    [Tooltip("火花配方（RuinsFireConfig 的 combatSparks 段）。留空 = 用代码内置基线")]
    [SerializeField] private RuinsFireConfig _sparkTune;

    public bool IsSequenceDriven { get; private set; }
    public void SetSequenceDriven(bool driven) => IsSequenceDriven = driven;

    private CinemachineImpulseSource _impulseSource;
    private PlayerMotor _motor;
    private int _lastSlash = -1;
    private PlayerCombat _player => GetComponentInParent<PlayerCombat>();

    private void Awake()
    {
        _impulseSource = GetComponent<CinemachineImpulseSource>();
        // 撞墙特效自己订阅：玩家身上的特效都归这里管，不需要别处再接线。
        _motor = GetComponentInParent<PlayerMotor>();
        if (_motor != null) _motor.WallImpact += OnWallImpact;
        EnsureFrictionSparks();
        ResolveAttackVfx();
    }

    private void OnDestroy()
    {
        if (_motor != null) _motor.WallImpact -= OnWallImpact;
        // 刀光挂在 Player/Vfx 下，跟玩家一起销毁；粒子归各自的发射器所有，不用另外回收。
    }

    // ===== 攻击 =====
    public void UpdateAttack(int index = 1)
    {
        if (!IsSequenceDriven) PlayAttack(index);
    }

    /// <summary>播一刀：默认随机抽一段；关闭随机后 index 是 1~4 的刀光编号（超出范围按端点夹取）。一次只放一段。</summary>
    public void PlayAttack(int index = 1)
    {
        ResolveAttackVfx();
        PlayShake();
        if (_attackSlashes == null || _attackSlashes.Length == 0) return;

        int selected = _randomAttackVariant
            ? PickRandomSlash()
            : Mathf.Clamp(index, 1, _attackSlashes.Length) - 1;

        // 四组都是 playOnAwake：先把没选中的组关掉，再点亮根节点，
        // 否则根节点激活的瞬间会把四段一起放出来。
        for (int i = 0; i < _attackSlashes.Length; i++)
        {
            ParticleSystem slash = _attackSlashes[i];
            if (slash == null) continue;
            bool active = i == selected;
            if (slash.gameObject.activeSelf != active) slash.gameObject.SetActive(active);
        }
        if (_attackVfx != null && !_attackVfx.activeSelf) _attackVfx.SetActive(true);

        ParticleSystem chosen = _attackSlashes[selected];
        if (chosen == null) return;
        // 同一组连续出刀时 SetActive 不会再次触发 playOnAwake，显式清掉重播；
        // 组内粒子已整体平移到 0 秒，Play 当帧就出刀，不再等 Hovl 原始时间轴。
        chosen.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        chosen.Play(true);
    }

    // 连砍时排除上一段：随机也可能连续抽到同一段，看起来就像没变化。
    private int PickRandomSlash()
    {
        int count = _attackSlashes.Length;
        if (count <= 1) return 0;
        int picked = Random.Range(0, count - 1);
        if (_lastSlash >= 0 && _lastSlash < count && picked >= _lastSlash) picked++;
        _lastSlash = picked;
        return picked;
    }

    // 装配工具会把引用接上；没接上（例如直接拖预制体进场景做试验）就在 Vfx 下找一次。
    private void ResolveAttackVfx()
    {
        if (_attackVfx == null)
        {
            Transform host = transform.Find("Vfx/AttackSlashes");
            if (host != null) _attackVfx = host.gameObject;
        }
        if (_attackSlashes != null && _attackSlashes.Length > 0) return;
        if (_attackVfx == null) return;
        var slashes = new List<ParticleSystem>(4);
        foreach (Transform child in _attackVfx.transform)
        {
            // 只要每组刀光根上的主粒子；冒烟 / 火花 / 扭曲这些子发射器由主粒子自己带。
            ParticleSystem slash = child.GetComponent<ParticleSystem>();
            if (slash != null) slashes.Add(slash);
        }
        _attackSlashes = slashes.ToArray();
    }

    // ===== 受击 / 撞墙 =====
    // 两个通道都有「已接线优先、没接线兜底」两层：
    // 作者把粒子摆在 Player/Vfx 下并拖进列表 → 就播它（位置/朝向按命中点现摆）；
    // 列表空着 → 退回 CombatSparks 的程序化火花（与命中 / parry 同一套配方）。
    // 两层不会同时播，避免同一击叠出两簇火花。
    /// <summary>玩家受击：由 Player.OnDamaged 调。direction 是攻击方的来向。</summary>
    public void PlayHurt(Vector3 point, Vector3 direction, float amount)
    {
        if (PlayGroupAt(_hurtEffects, point, direction)) return;
        CombatSparks.PlayHit(point, direction, amount);
    }

    /// <summary>撞墙：接触点按墙面法线朝外喷，强度按撞入速度走，并带一记震屏。</summary>
    public void PlayWallImpact(WallContact contact)
    {
        float amount = WallSparkAmount(contact);
        // -Normal 是「从墙里出来」的方向，锥体朝墙外喷
        Vector3 outward = -contact.Normal;
        if (!PlayGroupAt(_wallImpactEffects, contact.Point, outward))
            CombatSparks.PlayHit(contact.Point, outward, amount);
        VCameraEffects.Emit(_impulseSource, outward, amount * 0.6f);
    }

    // 只管表现强度，不参与任何结算：撞入速度越快火花越猛，下限防止轻碰没反馈。
    private static float WallSparkAmount(WallContact contact)
        => Mathf.Clamp(contact.IncomingVelocity.magnitude * 4f, 8f, 80f);

    /// <summary>把一组粒子摆到世界坐标的命中点再播。返回是否真的播了（空列表 = false，调用方兜底）。</summary>
    private static bool PlayGroupAt(ParticleSystem[] group, Vector3 point, Vector3 forward)
    {
        if (group == null || group.Length == 0) return false;
        bool played = false;
        for (int i = 0; i < group.Length; i++)
        {
            ParticleSystem system = group[i];
            if (system == null) continue;
            // 特效常常被摆成未激活的子物体；播放前显式激活，免得"配了却看不到"。
            if (!system.gameObject.activeSelf) system.gameObject.SetActive(true);
            Vector3 direction = forward.sqrMagnitude > 0.0001f ? forward.normalized : system.transform.forward;
            // 粒子走世界坐标系，所以发射器挪到命中点后已喷出的粒子会留在原地，不跟着玩家跑。
            system.transform.SetPositionAndRotation(point, Quaternion.LookRotation(direction, Vector3.up));
            system.Play();
            played = true;
        }
        return played;
    }

    /// <summary>停掉所有玩家特效（死亡 / 传送 / 回收时收尾）。</summary>
    public void StopAll()
    {
        StopAttacks();
        StopGroup(_hurtEffects);
        StopGroup(_wallImpactEffects);
    }

    public void StopAttacks()
    {
        // 死亡 / 传送 / 被打断时立刻收干净，别让处决动画结束后还挂着上一刀的弧。
        ResolveAttackVfx();
        if (_attackSlashes != null)
            for (int i = 0; i < _attackSlashes.Length; i++) StopOne(_attackSlashes[i]);
        if (_attackVfx != null) _attackVfx.SetActive(false);
    }

    private static void StopGroup(ParticleSystem[] group)
    {
        if (group == null) return;
        for (int i = 0; i < group.Length; i++) StopOne(group[i]);
    }

    private static void StopOne(ParticleSystem system)
    {
        if (system != null) system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
    }

    private void OnWallImpact(WallContact contact) => PlayWallImpact(contact);

    // 摩擦火花（地面 / 滑铲 / 贴墙）是独立组件（要读 PlayerMotor 的移动与墙面状态）。
    // 正常情况它已经落盘在 Player.prefab 上（编辑器装配工具接好发射器）；这里是兜底：
    // 缺就补一个——与 EnemyBase 补 HealthComponent、PlayerCombat 补 CombatComponent 同一个约定。
    private void EnsureFrictionSparks()
    {
        PlayerMotor motor = _motor;
        if (motor == null) return;
        FrictionSparks sparks = motor.GetComponent<FrictionSparks>();
        if (sparks == null) sparks = motor.gameObject.AddComponent<FrictionSparks>();
        // 只在给了配方时才注入：空手调用会把已经落盘的配方清掉。
        if (_sparkTune != null) sparks.ApplyTune(_sparkTune);
    }

    // 震屏必须挂在这里：两个入口都汇到 PlayAttack——
    // 非序列走动画事件 UpdateAttack，动作序列走帧事件 vfx.attack → ActionAnimatorBridge.PlayAttackEffect。
    // 挂在 UpdateAttack 里两条都够不着：序列接入时 ActionAnimatorBridge 会关掉 Animator.fireEvents，
    // 动画事件根本不触发；就算触发，IsSequenceDriven 也会把分支挡掉。
    private void PlayShake()
    {
        PlayerCombat combat = _player;
        if (combat == null) return;
        VCameraEffects.Emit(_impulseSource, new Vector3(0.5f, 0.0f, 0.0f), combat.AttackDamage);
    }
}
