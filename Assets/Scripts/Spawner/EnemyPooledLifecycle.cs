using UnityEngine;

// 让"被击杀的敌人"自动回到池里，而不是留在场上当尸体。
//
// 为什么单独一个组件、而不是把逻辑写进 Enemy：
// Enemy 是战斗实体，不该知道"池"和"生成器"的存在（否则敌人预制体必须永远配池才能用，
// CombatSelfCheck 之类只想要靶子的场合会被拖累）。这里做成附加的桥接层，
// 想池化就挂，不想池化就不挂，两边都保持干净。
//
// 死亡处理的分工：
//   - Enemy/GibComponent 负责表现（切碎、死亡动画）——本组件不插手。
//   - 本组件只负责"延迟一会儿，把实例还给池"。
// 延迟是刻意的：生命值归零的同一帧就 SetActive(false) 会让死亡动画与碎块来不及播放，
// 观感上是"敌人瞬间消失"。
//
// ⚠️ "归还"不等于"销毁"：
// 归还只做 SetActive(false) + 入栈，GameObject 本体保留，下次生成复用同一个实例。
// 只有这样"对象池"才有意义；若归还时 Destroy，下次 Spawn 只能重新 Instantiate，
// 池就退化成了单纯的延迟销毁器，白白丢掉复用带来的 GC 与实例化开销收益。
// 真正需要销毁只有两种情况，都在 ObjectPool.Return 里：空闲数超出 _maxIdlePerPrefab，
// 或预制体本体已被删除（无法归组）。
[DisallowMultipleComponent]
public sealed class EnemyPooledLifecycle : MonoBehaviour, IPooledObject
{
    // 默认值对齐敌人的死亡动画时长，不是随手取的 1 秒：
    // EnemyTest.controller 的 Die 状态 m_Speed = 1.6，Die.anim 末帧 4.5833s，
    // 实际播放 4.5833 / 1.6 ≈ 2.87 秒。回收早于这个值会把死亡动画掐断，
    // 敌人看起来是"没死透就消失了"。换动画/改 Speed 时这个值要跟着调。
    [Tooltip("死亡后延迟多久回收进池（秒）。默认对齐死亡动画时长，不影响尸块的独立生存期。")]
    [SerializeField, Min(0f)] private float _destroyTime = 2.87f;

    [Tooltip("生成器留空时，回收时改为在全局池里反查归属。一般不用填：Spawner 会自己认领。")]
    [SerializeField] private EnemySpawner _owner;

    private HealthComponent _health;
    private float _deathTime = float.NegativeInfinity;
    private bool _pendingRecycle;

    /// <summary>由 Spawner 在生成时指定归属，同时把 Died 订阅补上。</summary>
    public void SetOwner(EnemySpawner owner)
    {
        _owner = owner;
        // 运行时 AddComponent 出来的组件，OnEnable 可能已经跑过（订阅成功）也可能还没有；
        // 这里幂等补一次订阅，避免出现"监听没接上、尸体永不归还"。
        EnsureSubscribed();
    }

    private void Awake()
    {
        _health = GetComponent<HealthComponent>();
    }

    private void OnEnable()
    {
        EnsureSubscribed();
        _pendingRecycle = false;
        _deathTime = float.NegativeInfinity;
    }

    // 幂等订阅：用一个标志位防重复 += ，否则同一对象多次 SetOwner 会多挂监听，
    // 死亡时 OnDied 被调用多次（虽然逻辑上无害，但会让计时被反复重置）。
    private bool _subscribed;

    private void EnsureSubscribed()
    {
        if (_health == null) _health = GetComponent<HealthComponent>();
        if (_health == null || _subscribed) return;
        _health.Died += OnDied;
        _subscribed = true;
    }

    private void OnDisable()
    {
        if (_health != null && _subscribed)
        {
            _health.Died -= OnDied;
            _subscribed = false;
        }
    }

    private void OnDied(HealthComponent source)
    {
        // 用 UnscaledTime 而不是 WorldTime：WorldTime 在暂停/时停/顿帧时会被冻住
        // （TimeManager 暂停时直接 return，不累加 _worldTime）。
        // 若用 WorldTime 计时，一旦玩家开时停或有顿帧，这 1 秒可能永远走不完，
        // 尸体就卡在场上不归还——表现为"敌人死了但一直不动，刷怪也停了"。
        // 归还这件事属于清理，不该受游戏内时间缩放影响。
        _deathTime = TimeManager.UnscaledTime;
        _pendingRecycle = true;
    }

    private void Update()
    {
        if (!_pendingRecycle) return;
        if (TimeManager.UnscaledTime - _deathTime < _destroyTime) return;
        _pendingRecycle = false;
        Recycle();
    }

    private void Recycle()
    {
        // 优先让生成器认领：它知道自己是"同时存活上限"的记账方，走它才能把计数减对。
        if (_owner != null && _owner.Recycle(gameObject)) return;
        // 生成器没配或不管这个实例（例如手摆在场景里的敌人），退回全局池反查。
        ObjectPool.TryReturnToOwningPool(gameObject);
    }

    // ---- IPooledObject ----

    public void OnSpawnedFromPool()
    {
        // 复用前必须把上一轮的状态清干净：血量、攻击冷却、追击开关、碎块效果、死亡标志。
        // Enemy.ResetHealth 已经把这些收拢在一处，这里只负责叫它。
        var enemy = GetComponent<Enemy>();
        if (enemy != null)
        {
            enemy.ResetHealth();
            return;
        }
        // 池化的不是 Enemy（例如纯靶子）时，至少把血补满。
        if (_health != null) _health.Reset();
    }

    public void OnReturnedToPool()
    {
        _pendingRecycle = false;
        // 归还瞬间就停掉 AI：留着 NavMeshAgent 在禁用对象上活动会刷警告。
        var enemy = GetComponent<Enemy>();
        if (enemy != null) enemy.FinishAttack();
        var agent = GetComponent<UnityEngine.AI.NavMeshAgent>();
        if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh) agent.ResetPath();
    }
}
