using UnityEngine;

// 投射物：自己飞、自己检测、自己回收。
//
// 分工（与 CombatComponent / HealthComponent 一致）：
//   · 这里只管「飞」和「撞到什么」
//   · 扣谁的血、扣多少、命中后播什么表现 → 全交发射者（RangeComponent）
//   · 什么时候发射 → 交 AI 或输入
//
//    位移用 TimeManager.WorldDeltaTime，不用 Time.deltaTime：
//    WorldDeltaTime 已经乘过 GameRate 与 WorldRate，时停时它正好为 0，子弹自然冻在空中。
//    如果再自己乘一次 Rate 就是二次缩放（这个坑项目以前踩过）。
//
// 命中判定用扫掠（SphereCast），不能用触发器重叠：
//    子弹一帧能走 0.5 米以上，只查「当前位置」会直接穿过薄墙。
[DisallowMultipleComponent]
public sealed class Projectile : MonoBehaviour, IPooledObject
{
    [Header("飞行")]
    [Tooltip("子弹速度")]
    [SerializeField, Min(0.01f)] private float _speed = 30f;

    [Tooltip("最长存活秒数")]
    [SerializeField, Min(0.05f)] private float _lifetime = 4f;

    [Tooltip("最大飞行距离")]
    [SerializeField, Min(0.1f)] private float _maxDistance = 60f;

    [Tooltip("扫掠半径，取子弹的视觉粗细。")]
    [SerializeField, Min(0.01f)] private float _radius = 0.08f;

    [Header("碰撞")]
    [Tooltip("扫掠检测的层。默认全部；若枪口埋在发射者体内，收窄这一项比加例外更省事。")]
    [SerializeField] private LayerMask _obstacleMask = -1;

    [Tooltip("发射者：命中后由它做阵营过滤与伤害结算。留空则只撞不扣血。")]
    [SerializeField] private RangeComponent _source;

    private Vector3 _direction;
    private float _speedScale = 1f;
    private float _age;
    private float _travelled;
    private bool _isFlying;

    /// <summary>发射。direction 不必归一化。返回时不做位移，移动从下一次 Update 开始。</summary>
    /// <param name="direction">飞行方向（三维，含俯仰）</param>
    /// <param name="speedScale">速度倍率。留给以后的蓄力弹，普通弹传 1</param>
    public void Launch(Vector3 direction, RangeComponent source, float speedScale = 1f)
    {
        if (direction.sqrMagnitude < 0.000001f) return;

        _direction = direction.normalized;
        _source = source;
        _speedScale = Mathf.Max(0.01f, speedScale);
        _age = 0f;
        _travelled = 0f;
        _isFlying = true;

        // 朝向对齐飞行方向：弹体如果是长条模型，不用再挂跟随脚本
        transform.rotation = Quaternion.LookRotation(_direction, Vector3.up);
    }

    private void Update()
    {
        if (!_isFlying) return;

        // 时停 → WorldDeltaTime 为 0 → 本帧位移为 0，子弹冻住
        float dt = TimeManager.WorldDeltaTime;
        if (dt <= 0f) return;

        // 活太久
        _age += dt;
        if (_age >= _lifetime)
        {
            Recycle();
            return;
        }

        float step = _speed * _speedScale * dt;

        // 扫掠
        if (Physics.SphereCast(transform.position, _radius, _direction, out RaycastHit hit, step, _obstacleMask, QueryTriggerInteraction.Ignore))
        {
            transform.position = hit.point + _direction * _radius;

            // 只把「撞到了谁」交给发射者；扣不扣血、扣多少、是不是同阵营，都由它决定。
            // 没有 HealthComponent 的东西（墙、地面）本来就不参与战斗，不必上报。
            HealthComponent target = hit.collider.GetComponentInParent<HealthComponent>();
            if (target != null) _source?.ReportHit(target, hit.point, _direction);

            Recycle();
            return;
        }

        transform.position += _direction * step;

        // 飞太远
        _travelled += step;
        if (_travelled >= _maxDistance) Recycle();
    }

    // 命中、超时、超距都走这里
    private void Recycle()
    {
        if (ObjectPool.TryReturnToOwningPool(gameObject)) return;
        gameObject.SetActive(false);
    }

    // 池取出时：Launch 紧接着就会被调用并补齐全部飞行状态，
    // 这里只需保证「取出到 Launch 之间」不会先按上一次的旧方向飞一帧。
    public void OnSpawnedFromPool()
    {
        _isFlying = false;
    }

    // 归还进池时收尾：断开对发射者的引用，免得复用时把伤害算到上一个敌人头上。
    public void OnReturnedToPool()
    {
        _isFlying = false;
        _source = null;
    }
}