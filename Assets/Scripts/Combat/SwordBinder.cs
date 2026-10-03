using System.Collections.Generic;
using UnityEngine;

// 把剑挂到角色右手骨骼上，并在同一位置建出攻击判定盒。
//
// 为什么改成「运行时实例化」而不是预制体里挂好：
//   1. 剑摆在手骨下依赖具体骨架，而骨架来自 fbx，写进预制体的父级关系在换模型/重导入时容易失效；
//   2. 预制体里嵌套一个 Sword 预制体实例时，那个实例会被各种预制体操作误伤（本项目就遇到过：
//      PrefabInstance 块存在却不再实例化，运行时层级里根本没有 Sword 节点）；
//   3. 剑与判定盒都必须跟着手骨走，运行时创建同样满足，且引用不会因序列化而丢失。
[DisallowMultipleComponent]
public sealed class SwordBinder : MonoBehaviour
{
    private const string SwordPrefabPath = "Assets/Prefabs/Sword.prefab";
    private const string SwordName = "Sword";
    private const string HitboxName = "PlayerHitbox";

    // 常见骨架的右手命名：Mixamo 用 mixamorig:RightHand，Blender 导出多为 RightHand/Hand_R
    private static readonly string[] DefaultHandNames =
    {
        "mixamorig:RightHand", "RightHand", "hand_R", "Hand_R", "Right Hand", "Bip001 R Hand"
    };

    [Header("剑")]
    [SerializeField] private Transform _sword;
    [SerializeField] private string _handName = "mixamorig:RightHand";
    [SerializeField] private Vector3 _localPosition = Vector3.zero;
    [SerializeField] private Vector3 _localEulerAngles = Vector3.zero;
    [SerializeField] private Vector3 _localScale = Vector3.one;

    [Header("攻击判定盒")]
    [Tooltip("判定球半径，按角色本地尺度（≈米）。剑长约 1.6m，取 0.9 覆盖挥砍范围")]
    [SerializeField, Min(0.05f)] private float _hitboxRadius = 0.9f;
    [Tooltip("判定球相对手骨的偏移，沿手骨本地 Z 轴向前")]
    [SerializeField] private Vector3 _hitboxOffset = new Vector3(0f, 0f, 0.45f);
    [SerializeField] private Hitbox _hitbox;

    private Transform _hand;

    public Transform Sword => _sword;
    public Transform Hand => _hand;
    public Hitbox Hitbox => _hitbox;

    public void Configure(Transform sword, string handName, Vector3 localPosition,
        Vector3 localEulerAngles, Vector3 localScale)
    {
        _sword = sword;
        if (!string.IsNullOrEmpty(handName)) _handName = handName;
        _localPosition = localPosition;
        _localEulerAngles = localEulerAngles;
        _localScale = localScale;
    }

    public void SetHitboxParameters(float radius, Vector3 offset)
    {
        _hitboxRadius = Mathf.Max(0.05f, radius);
        _hitboxOffset = offset;
    }

    private void Awake()
    {
        Attach();
    }

    public bool Attach()
    {
        _hand = FindHand();
        if (_hand == null)
        {
            Debug.LogError($"[SwordBinder] 骨架中找不到右手骨骼「{_handName}」，剑与判定盒都不会挂上", this);
            return false;
        }

        AttachSword();
        AttachHitbox();
        return true;
    }

    private void AttachSword()
    {
        // 预制体里可能有旧的实例或失效引用，统一按名字清掉再重建，避免出现两把剑
        Transform stale = FindByName(SwordName);
        if (stale != null && stale.parent != _hand) Destroy(stale.gameObject);
        if (_sword != null && _sword != stale) _sword = stale;

        if (_sword == null)
        {
            _sword = CreateSwordInstance();
            if (_sword == null) return;
        }

        _sword.SetParent(_hand, false);
        _sword.localPosition = _localPosition;
        _sword.localRotation = Quaternion.Euler(_localEulerAngles);
        _sword.localScale = _localScale;
    }

    private Transform CreateSwordInstance()
    {
        GameObject prefab = LoadSwordPrefab();
        if (prefab == null)
        {
            Debug.LogError($"[SwordBinder] 找不到剑的预制体: {SwordPrefabPath}", this);
            return null;
        }

        GameObject instance = Instantiate(prefab, _hand);
        instance.name = SwordName;
        return instance.transform;
    }

    private static GameObject LoadSwordPrefab()
    {
#if UNITY_EDITOR
        GameObject fromAsset = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(SwordPrefabPath);
        if (fromAsset != null) return fromAsset;
#endif
        // 打包后 AssetDatabase 不可用，退回 Resources（把剑放进 Resources 目录即可）
        return Resources.Load<GameObject>(SwordName);
    }

    // 攻击判定盒：球形 Trigger，以手骨为心。
    // 用球而不是盒，是因为手骨在挥砍中会大幅旋转，固定朝向的盒子在某些帧会扫不到人；
    // 球体不受朝向影响，命中范围稳定。
    private void AttachHitbox()
    {
        if (_hitbox == null) _hitbox = GetComponentInChildren<Hitbox>(true);

        if (_hitbox == null)
        {
            GameObject host = new GameObject(HitboxName);
            host.transform.SetParent(_hand, false);
            _hitbox = host.AddComponent<Hitbox>();
        }
        else
        {
            _hitbox.transform.SetParent(_hand, false);
        }

        // 与剑的摆放解耦：判定盒位置只由这两个参数决定
        _hitbox.transform.localPosition = _hitboxOffset;
        _hitbox.transform.localRotation = Quaternion.identity;

        // 手骨自带缩放（Mixamo 常见 0.01），判定球半径要换算回本地空间才等于世界半径
        Vector3 handScale = _hand.lossyScale;
        float reference = Mathf.Max(0.0001f, Mathf.Max(handScale.x, Mathf.Max(handScale.y, handScale.z)));
        float localRadius = _hitboxRadius / reference;
        _hitbox.transform.localScale = Vector3.one * localRadius;

        // 用 SphereCollider：与上面局部缩放配合后，世界半径正好等于 _hitboxRadius
        SphereCollider sphere = _hitbox.GetComponent<SphereCollider>();
        if (sphere == null)
        {
            // 换掉可能存在的旧形状，避免出现两个碰撞体
            foreach (Collider extra in _hitbox.GetComponents<Collider>()) Destroy(extra);
            sphere = _hitbox.gameObject.AddComponent<SphereCollider>();
        }

        sphere.radius = 1f; // 局部半径 1 × localScale = 世界半径
        sphere.isTrigger = true;
        // 默认关闭：等动画事件 EnableHitbox 打开
        sphere.enabled = false;

        _hitbox.SetDamageSource(GetComponent<PlayerCombat>());
    }

    private Transform FindHand()
    {
        if (!string.IsNullOrEmpty(_handName))
        {
            Transform exact = FindByName(_handName);
            if (exact != null) return exact;
        }

        foreach (string candidate in DefaultHandNames)
        {
            Transform found = FindByName(candidate);
            if (found != null) return found;
        }

        return null;
    }

    // 递归按名字找（忽略大小写）。手骨层级不深，用栈而不是 Linq 避免额外 GC
    private Transform FindByName(string name)
    {
        Stack<Transform> pending = new Stack<Transform>();
        foreach (Transform child in transform) pending.Push(child);
        while (pending.Count > 0)
        {
            Transform current = pending.Pop();
            if (string.Equals(current.name, name, System.StringComparison.OrdinalIgnoreCase))
            {
                return current;
            }

            foreach (Transform child in current) pending.Push(child);
        }

        return null;
    }
}
