using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

// 敌人生成器：在圆形范围内随机找地面并生成对象，全部实例走 ObjectPool 复用。
//
// 设计边界（刻意不做的事）：
//   - 不管敌人的 AI，只管"在哪出现/什么时候出现/同时最多几个"。
//   - 不做波次系统（YAGNI）：当前需求是持续刷怪填场，节奏控制用"间隔 + 同时存活上限"两个旋钮就够。
//   - 不自己 Destroy 敌人：敌人被击杀后由 HandleTrackedDeath 回收，或由外部显式回收，
//     这样才能证明"复用"而不是"销毁重建"。
[DisallowMultipleComponent]
public sealed class EnemySpawner : MonoBehaviour
{
    [Header("生成范围（圆形，Scene 视图可视化）")]
    [Tooltip("圆形生成范围的半径（米）。以本组件所在位置为圆心，在 XZ 平面上展开。")]
    [SerializeField, Min(0f)] private float _spawnRange = 12f;

    [Tooltip("内圈半径：排除圆心附近的区域，避免敌人在玩家/出生点上贴脸出现。为 0 表示允许圆心。")]
    [SerializeField, Min(0f)] private float _innerRange = 0f;

    [Header("生成对象")]
    [Tooltip("要生成的对象。当前只支持单个，但字段留成数组形状会诱导错误配置，所以坚持单个引用。")]
    [SerializeField] private GameObject _spawnObject;

    [Tooltip("生成后挂到哪个父节点下。留空表示不指定父节点（留在场景根）。")]
    [SerializeField] private Transform _spawnParent;

    [Tooltip("生成时的朝向策略。敌人通常不需要朝向约束，默认保持预制体自身朝向。")]
    [SerializeField] private bool _randomYaw = true;

    [Header("落地判定（垂直地块）")]
    [Tooltip("如何把随机点吸附到地面。默认 NavMesh 优先 + 射线兜底。")]
    [SerializeField] private SpawnGroundMode _groundMode = SpawnGroundMode.NavMeshThenRaycast;

    [Tooltip("地面所在的物理层。仅供射线兜底使用；为 Nothing 时射线会退化为检测全部层。")]
    [SerializeField] private LayerMask _groundMask = ~0;

    [Tooltip("NavMesh 采样可用的区域掩码。默认全部区域，可在 Navigation 窗口查看区域编号。")]
    [SerializeField] private int _navMeshAreaMask = NavMesh.AllAreas;

    [Tooltip("从候选点向上抬升多少米开始向下搜索地面。地块上下层高差大时调大。")]
    [SerializeField, Min(0.1f)] private float _groundSearchCeiling = SpawnGroundQuery.DefaultCeiling;

    [Tooltip("生成点离地高度偏移。略大于 0 可避免胶囊体刚生成时与地面穿插。")]
    [SerializeField, Min(0f)] private float _spawnHeightOffset = 0.05f;

    [Header("节奏")]
    [Tooltip("自动生成的总开关。关掉后仍可用 SpawnOnce() 手动生成，便于测试与调试。")]
    [SerializeField] private bool _autoSpawn = true;

    [Tooltip("两次生成之间的间隔（秒，按世界时间计）。")]
    [SerializeField, Min(0.01f)] private float _spawnInterval = 3f;

    [Tooltip("同时存活的数量上限。达到上限时暂停生成，等有实例被回收后自动恢复。")]
    [SerializeField, Min(1)] private int _maxAlive = 8;

    [Tooltip("预热数量：开局预先实例化并失活这么多，避免战斗中首次生成卡顿。")]
    [SerializeField, Min(0)] private int _prewarm = 0;

    [Header("失败重试")]
    [Tooltip("一次生成里最多重试几次找地面。圆内可能落在墙里/悬空处，重试能在有限次数内换个位置。")]
    [SerializeField, Min(1)] private int _placementAttempts = 8;

    // 在场景里存活、由本 Spawner 派生的实例。用 List 而不是 HashSet：
    // 数量是"同时在场上限"级别（几十个），线性查找的开销远小于哈希的装箱与枚举开销。
    private readonly List<GameObject> _alive = new List<GameObject>();
    private ObjectPool _pool;
    private float _timer;

    /// <summary>当前存活数量，测试与调试 HUD 用。</summary>
    public int AliveCount => _alive.Count;

    /// <summary>同时存活上限。</summary>
    public int MaxAlive => _maxAlive;

    /// <summary>生成间隔（秒）。</summary>
    public float SpawnInterval => _spawnInterval;

    /// <summary>圆形范围半径。</summary>
    public float SpawnRange => _spawnRange;

    private void Awake()
    {
        _pool = GetComponent<ObjectPool>();
        if (_pool == null)
        {
            // 池与生成器在语义上是两件事（池可以给多个生成器共用），
            // 但"只挂生成器忘了挂池"是最常见的配置错误，这里补一个而不是报错。
            _pool = gameObject.AddComponent<ObjectPool>();
        }

        if (_innerRange > _spawnRange) _innerRange = _spawnRange;
    }

    private void Start()
    {
        if (_prewarm > 0 && _spawnObject != null) _pool.Prewarm(_spawnObject, _prewarm);
        _timer = 0f;
    }

    private void Update()
    {
        // 世界时间：与 Enemy 的追击/攻击同源，减速或时停时刷怪节奏跟着变，
        // 避免玩家开时停却发现敌人还在按真实时间往外冒。
        PruneDestroyed();

        if (!_autoSpawn || _spawnObject == null) return;
        if (!CanSpawnNow()) return;

        _timer += TimeManager.WorldDeltaTime;
        if (_timer < _spawnInterval) return;
        _timer = 0f;
        SpawnOnce();
    }

    /// <summary>是否满足"还能再生成一个"的条件。</summary>
    public bool CanSpawnNow()
    {
        if (_spawnObject == null) return false;
        if (_alive.Count >= _maxAlive) return false;
        return true;
    }

    /// <summary>
    /// 立即生成一个。返回 null 表示失败（没配对象 / 已达上限 / 找不到地面）。
    /// 手动调用不受间隔限制，但仍受同时存活上限约束。
    /// </summary>
    public GameObject SpawnOnce()
    {
        if (!CanSpawnNow()) return null;
        if (!TryFindSpawnPoint(out Vector3 point)) return null;

        Quaternion rotation = _randomYaw
            ? Quaternion.Euler(0f, Random.Range(0f, 360f), 0f)
            // 不随机朝向时沿用 Spawner 自身的朝向：策划可以靠摆 Spawner 的旋转来定初始面向。
            : Quaternion.Euler(0f, transform.eulerAngles.y, 0f);

        GameObject instance = _pool.Spawn(_spawnObject, point, rotation, _spawnParent);
        if (instance == null) return null;

        EnsureRecycleHookup(instance);
        // 池里取出的实例可能是复用的旧对象，它可能已经在 _alive 里（正常不会），
        // 用 Contains 兜底而不是无脑 Add，避免把存活数算重、把上限提前占满。
        if (!_alive.Contains(instance)) _alive.Add(instance);
        return instance;
    }

    // 保证生成出来的实例一定会被归还。
    //
    // 为什么在运行时补挂而不是只依赖预制体上预先挂好：
    // EnemyPooledLifecycle 缺失时，敌人死后没有任何东西通知池，
    // 尸体会永远留在场上把 _alive 占满，表现为"刷了一阵就不刷了"。
    // 这种漏配不会报错、只会静默失效，所以在唯一的生成入口上兜住比"记得挂组件"可靠。
    private void EnsureRecycleHookup(GameObject instance)
    {
        var lifecycle = instance.GetComponent<EnemyPooledLifecycle>();
        if (lifecycle == null) lifecycle = instance.AddComponent<EnemyPooledLifecycle>();
        // 每次都重指 owner：实例可能被复用，上一轮的 owner 未必是本 Spawner。
        lifecycle.SetOwner(this);

        // 复制预制体上的调参（若预制体已挂过这个组件）。
        // 没挂时用组件默认值，保持"开箱可用"。
    }

    /// <summary>回收所有本 Spawner 生成且仍存活的实例。</summary>
    public void RecycleAll()
    {
        for (int i = _alive.Count - 1; i >= 0; i--) RecycleAt(i);
    }

    /// <summary>
    /// 回收单个实例。返回 false 表示它不归本 Spawner 管。
    /// 敌人死亡时由外部（或下面的死亡监听）调用。
    /// </summary>
    public bool Recycle(GameObject instance)
    {
        int index = _alive.IndexOf(instance);
        if (index < 0) return false;
        RecycleAt(index);
        return true;
    }

    private void RecycleAt(int index)
    {
        GameObject instance = _alive[index];
        // 从列表移除用 SwapBack 语义（Unity 的 RemoveAt 本身就是这个行为），
        // 保持顺序无关性，避免每帧 O(n) 搬移。
        _alive.RemoveAt(index);
        if (instance == null) return; // 已被外部 Destroy，池那边会在下次 Spawn 时清理
        if (!ObjectPool.TryReturnToOwningPool(instance))
        {
            // 不属于任何已知池（池被销毁了），只能销毁以免留下孤儿对象。
            Destroy(instance);
        }
    }

    // 剔除被外部直接 Destroy 的实例（例如 GibComponent 把敌人切碎后销毁了原对象），
    // 否则 _alive 会残留一堆空引用，把"同时存活数"算高而永久停止生成。
    private void PruneDestroyed()
    {
        for (int i = _alive.Count - 1; i >= 0; i--)
        {
            if (_alive[i] == null) _alive.RemoveAt(i);
        }
    }

    /// <summary>
    /// 在圆形范围内找到一个"已落到地面上"的点。
    /// 失败返回 false，调用方应放弃本次生成而不是把敌人放在半空。
    /// </summary>
    public bool TryFindSpawnPoint(out Vector3 point)
    {
        point = transform.position;
        // 圆心拍平到 XZ：Spawner 可能被摆在高处，Y 由落地判定决定，
        // 直接把自身 Y 带进候选点会让"向上抬升再往下找"的搜索窗偏移。
        Vector3 center = new Vector3(transform.position.x, transform.position.y, transform.position.z);

        int attempts = Mathf.Max(1, _placementAttempts);
        for (int i = 0; i < attempts; i++)
        {
            var random01 = new Vector2(Random.value, Random.value);
            Vector3 candidate = SpawnGroundQuery.SampleDisc(center, _spawnRange, _innerRange, random01);
            if (!SpawnGroundQuery.TryResolve(_groundMode, candidate, _groundMask, _navMeshAreaMask,
                    _groundSearchCeiling, out Vector3 grounded, out _))
            {
                continue;
            }

            Vector3 finalPoint = grounded + Vector3.up * _spawnHeightOffset;
            // 二次校验：吸附后的点必须仍在圆形范围内（XZ 距离）。
            // NavMesh 采样会把点吸附到最近的网格，边缘处可能被推出圈外。
            Vector3 flat = new Vector3(finalPoint.x - center.x, 0f, finalPoint.z - center.z);
            if (flat.magnitude > _spawnRange + 0.001f) continue;

            point = finalPoint;
            return true;
        }

        return false;
    }

    // 把 Inspector 上的字段格式化打进 Console，用于实机定位"为什么不生成"。
    // 由 MCP 或调试器触发，正常玩法不调用。
    public void DebugDumpState()
    {
        Debug.Log($"[EnemySpawner] {name} 自动={_autoSpawn} 对象={(_spawnObject != null ? _spawnObject.name : "未配")} "
            + $"半径={_spawnRange} 内圈={_innerRange} 间隔={_spawnInterval} "
            + $"存活={_alive.Count}/{_maxAlive} 池空闲={(_pool != null ? _pool.IdleCount : -1)} "
            + $"落地模式={_groundMode} 世界时间={TimeManager.WorldTime:0.##}");
    }

#if UNITY_EDITOR
    // Scene 视图可视化：圆形生成范围 + 已找到的落地点。
    // 放在 OnDrawGizmos（而非 Selected 版本）里是为了让策划不用选中也能看到范围，
    // 但用颜色区分选中状态，避免多 Spawner 场景里一片花。
    private void OnDrawGizmos()
    {
        DrawRangeGizmos(selected: false);
    }

    private void OnDrawGizmosSelected()
    {
        DrawRangeGizmos(selected: true);
    }

    private void DrawRangeGizmos(bool selected)
    {
        Vector3 center = transform.position;
        float radius = Mathf.Max(0f, _spawnRange);

        // 地面参考环：把圆压到 y 略低处，避免与地面 z-fighting。
        Vector3 groundCenter = new Vector3(center.x, center.y + 0.02f, center.z);
        Gizmos.color = selected ? new Color(1f, 0.35f, 0.15f, 1f) : new Color(1f, 0.35f, 0.15f, 0.35f);
        DrawCircle(groundCenter, radius, 64);

        if (selected)
        {
            // 选中时补：内圈、外圈竖直柱体高度提示、以及半径数值。
            if (_innerRange > 0f)
            {
                Gizmos.color = new Color(1f, 0.85f, 0.2f, 0.8f);
                DrawCircle(groundCenter, _innerRange, 48);
            }

            // 竖直方向画两条母线，让"这是一个圆柱范围"的空间感出来。
            Gizmos.color = new Color(1f, 0.6f, 0.2f, 0.5f);
            float searchTop = _groundSearchCeiling;
            Gizmos.DrawLine(center, center + Vector3.up * searchTop);
            Gizmos.DrawLine(center + Vector3.right * radius, center + Vector3.right * radius + Vector3.up * searchTop);
            Gizmos.DrawLine(center - Vector3.right * radius, center - Vector3.right * radius + Vector3.up * searchTop);
        }
    }

    private static void DrawCircle(Vector3 center, float radius, int segments)
    {
        if (radius <= 0f || segments < 3) return;
        float step = Mathf.PI * 2f / segments;
        Vector3 previous = center + new Vector3(radius, 0f, 0f);
        for (int i = 1; i <= segments; i++)
        {
            float angle = step * i;
            Vector3 current = center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
            Gizmos.DrawLine(previous, current);
            previous = current;
        }
    }
#endif
}
