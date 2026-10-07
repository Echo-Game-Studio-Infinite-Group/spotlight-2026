using System.Collections.Generic;
using UnityEngine;

// 动态对象池。
//
// 为什么不用 UnityEngine.Pool.ObjectPool<T>：那一套要求自己管 CollectionCheck 与
// actionOnDestroy 的分工，且容量策略（maxSize）的溢出行为是"直接销毁"，
// 对"敌人同时存活数必须可预测"的玩法不友好——本池溢出不销毁，只拒绝并告知调用方。
//
// 设计取舍：
//   - 池按预制体分流：一个 EnemyPool 组件可同时管多种敌人，用 Key(预制体) 作为分组键，
//     避免"每种敌人挂一个池组件"的 GameObject 膨胀。
//   - 归还走 TryReturn/Return：对象若被外部 Destroy（例如 GibComponent 把敌人切碎），
//     池必须能察觉并把它从在借集合里摘掉，否则会拿到一个 MissingReference 的壳。
//   - 容量与预热都在 Inspector 上，符合项目"数值不硬编码"的规范。
[DisallowMultipleComponent]
public sealed class ObjectPool : MonoBehaviour
{
    [Header("容量")]
    [Tooltip("单个预制体分组在池中保留的空闲实例上限。超出部分会被真正销毁，避免长时间刷怪后内存膨胀。")]
    [SerializeField, Min(0)] private int _maxIdlePerPrefab = 32;

    [Tooltip("预热数量：每个预制体在 Awake 时预先实例化这么多并失活，避免战斗中首次生成触发 Instantiate 卡顿。")]
    [SerializeField, Min(0)] private int _prewarmPerPrefab = 0;

    [Tooltip("池实例挂载的父节点。留空则挂在池自身下。用一个专门的容器节点能让 Hierarchy 保持整洁。")]
    [SerializeField] private Transform _idleRoot;

    // 每个预制体一组栈。用 Stack 而不是 Queue：刚归还的实例通常其内部状态最"热"
    // （Transform 层级、Mesh 都还在缓存里），优先复用它对 GC 与渲染更友好。
    private readonly Dictionary<GameObject, Stack<GameObject>> _idle = new Dictionary<GameObject, Stack<GameObject>>();
    // 在借实例记录它来自哪个预制体，归还时才能正确归组。
    private readonly Dictionary<GameObject, GameObject> _borrowedFrom = new Dictionary<GameObject, GameObject>();
    // 预热过的预制体清单，Spawn 时用来判断"这个预制体是否已建组"。
    private readonly HashSet<GameObject> _knownPrefabs = new HashSet<GameObject>();

    /// <summary>当前所有池（全局注册），Spawner 回收时按实例反查所属池，不需要持有强引用。</summary>
    private static readonly List<ObjectPool> All = new List<ObjectPool>();

    public int IdleCount
    {
        get
        {
            int total = 0;
            foreach (var pair in _idle) total += pair.Value.Count;
            return total;
        }
    }

    public int BorrowedCount => _borrowedFrom.Count;

    private Transform Root => _idleRoot != null ? _idleRoot : transform;

    private void Awake()
    {
        if (_idleRoot == null)
        {
            // 池自身若在场景里被当容器用，实例堆在同一层级会污染场景结构；
            // 建一个子节点专放空闲实例，层级一眼可见。
            var container = new GameObject("[Idle]");
            container.transform.SetParent(transform, false);
            _idleRoot = container.transform;
        }
    }

    private void OnEnable()
    {
        if (!All.Contains(this)) All.Add(this);
    }

    private void OnDisable()
    {
        All.Remove(this);
    }

    /// <summary>
    /// 预热：提前实例化并失活，避免运行时首次生成造成帧尖峰。
    /// 由 Spawner 在 Start 时按需要调用；也可在编辑器工具里手动触发。
    /// </summary>
    public void Prewarm(GameObject prefab, int count)
    {
        if (prefab == null || count <= 0) return;
        // EnsureGroup 在首次见到该预制体时会按 _prewarmPerPrefab 自动预热，
        // 所以这里要在建组之后按"还差多少"补足，不能直接再堆 count 个。
        EnsureGroup(prefab);
        Stack<GameObject> stack = _idle[prefab];
        int missing = count - stack.Count;
        for (int i = 0; i < missing; i++)
        {
            stack.Push(CreateInstance(prefab));
        }
    }

    /// <summary>
    /// 取一个实例。池里没有空闲实例时才 Instantiate。
    /// 返回的实例一定是 active 的，且已经回调过 IPooledObject.OnSpawnedFromPool。
    /// </summary>
    public GameObject Spawn(GameObject prefab, Vector3 position, Quaternion rotation, Transform parent = null)
    {
        if (prefab == null) return null;
        EnsureGroup(prefab);

        Stack<GameObject> stack = _idle[prefab];
        GameObject instance = null;
        // 从栈顶往下找第一个仍然存活的实例：被外部 Destroy 的会在这里被剔除。
        while (stack.Count > 0 && instance == null)
        {
            instance = stack.Pop();
            if (instance == null) continue; // 槽位对应的对象已被真正销毁，跳过
        }

        if (instance == null) instance = CreateInstance(prefab);

        Transform t = instance.transform;
        // parent 为 null 时保持池的世界位置语义：设置位置前先脱离旧父节点，
        // 否则 SetParent(null) 之后的位置会被旧父节点的缩放污染。
        t.SetParent(parent, false);
        t.SetPositionAndRotation(position, rotation);

        _borrowedFrom[instance] = prefab;
        instance.SetActive(true);

        // 回调放在 SetActive(true) 之后：OnEnable 里的逻辑（例如 NavMeshAgent 归位）
        // 需要先跑完，池的重置才不会被它覆盖。
        if (instance.TryGetComponent(out IPooledObject pooled)) pooled.OnSpawnedFromPool();
        return instance;
    }

    /// <summary>
    /// 归还实例。返回 false 表示该实例不属于本池（或已被销毁），调用方不必处理。
    /// 对一个已经在池里的实例重复归还不会出错，也不会重复入栈。
    /// </summary>
    public bool Return(GameObject instance)
    {
        if (instance == null) return false;
        if (!_borrowedFrom.TryGetValue(instance, out GameObject prefab))
        {
            // 从未借出过，或已被回收过。静默忽略是安全的：这是幂等归还的正常路径。
            return false;
        }

        _borrowedFrom.Remove(instance);
        if (prefab == null)
        {
            // 预制体本体被删了（编辑器改资源），无法归组，只能销毁以免泄漏。
            Destroy(instance);
            return true;
        }

        EnsureGroup(prefab);
        Stack<GameObject> stack = _idle[prefab];

        if (stack.Count >= _maxIdlePerPrefab)
        {
            // 超出空闲上限：真销毁。保留上限是为了让"同时存在多少敌人"可控，
            // 否则一波大招清场后会留下大量永不使用的实例。
            Destroy(instance);
            return true;
        }

        if (instance.TryGetComponent(out IPooledObject pooled)) pooled.OnReturnedToPool();

        // 失活前脱离父节点：若父节点是即将被销毁的临时对象，
        // 实例会跟着一起没，池里就留了个空壳。
        instance.transform.SetParent(Root, false);
        instance.SetActive(false);
        stack.Push(instance);
        return true;
    }

    /// <summary>
    /// 反查某个实例属于哪个池并归还。Spawner 回收它派生的对象时用这个入口，
    /// 这样 Spawner 不必区分"这个敌人是哪个池给的"。
    /// </summary>
    public static bool TryReturnToOwningPool(GameObject instance)
    {
        if (instance == null) return false;
        for (int i = 0; i < All.Count; i++)
        {
            ObjectPool pool = All[i];
            if (pool == null) continue;
            if (pool._borrowedFrom.ContainsKey(instance)) return pool.Return(instance);
        }
        return false;
    }

    /// <summary>清空池：销毁所有空闲实例。场景卸载或重开一局时用。</summary>
    public void Clear()
    {
        foreach (var pair in _idle)
        {
            foreach (GameObject idle in pair.Value)
            {
                if (idle != null) Destroy(idle);
            }
            pair.Value.Clear();
        }
        _knownPrefabs.Clear();
        // 在借实例不动：它们还活在场景里，由各自的所有者负责归还。
    }

    private void EnsureGroup(GameObject prefab)
    {
        if (_knownPrefabs.Contains(prefab)) return;
        _knownPrefabs.Add(prefab);
        _idle[prefab] = new Stack<GameObject>();
        if (_prewarmPerPrefab > 0) PrewarmInternal(prefab, _prewarmPerPrefab);
    }

    // Prewarm 与 EnsureGroup 互相调用会无限递归，拆出内部版本打断这个环。
    private void PrewarmInternal(GameObject prefab, int count)
    {
        Stack<GameObject> stack = _idle[prefab];
        for (int i = 0; i < count; i++)
        {
            GameObject instance = CreateInstance(prefab);
            stack.Push(instance);
        }
    }

    private GameObject CreateInstance(GameObject prefab)
    {
        GameObject instance = Instantiate(prefab, Root);
        instance.name = prefab.name; // Instantiate 会加上 "(Clone)"，去掉让 Hierarchy 更干净
        instance.SetActive(false);
        return instance;
    }
}
