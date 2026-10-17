using GameJam.Actions;   // PlayerActionRunner 在这个命名空间里
using UnityEngine;

// 布娃娃阶段由骨骼物理接管；起身阶段由 PlayerMotor 恢复重力和胶囊碰撞。
// 输入、动作与常规动画要等起身播完再恢复，避免硬直期间重新执行招式。
[DisallowMultipleComponent]
public sealed class PlayerRagdollHitStun : MonoBehaviour
{
    [Header("硬直（两段：布娃娃 → 起身）")]
    [Tooltip("布娃娃段时长（秒）：从触发到开始播起身动画。")]
    [SerializeField, Min(0f)] private float _ragdollSeconds = 1f;

    [Tooltip("起身动画在 Animator 里的状态名（Base Layer）。留空或找不到该状态 → 退回占位（直接 Rebind 回 Idle）。")]
    [SerializeField] private string _getUpState = "GetUp";

    [Tooltip("起身段的目标时长（秒）。0 = 按动画原速；>0 时用 Animator.speed 缩放到这个时长。")]
    [SerializeField, Min(0f)] private float _getUpTargetSeconds = 0f;


    [Header("撞击触发（判定条件在移动层里，这里只管数值）")]
    [Tooltip("最低触发速度，单位是「地速阈值的倍数」。低于它撞墙不算撞击。")]
    [SerializeField, Min(0f)] private float _minSpeedFactor = 1.2f;

    [Tooltip("伤害 = 速度（地速阈值倍数）× 这个系数。策划案原文是「伤害等于其速度」，所以默认 1。")]
    [SerializeField, Min(0f)] private float _damagePerSpeed = 1f;

    [Tooltip("撞击瞬间把水平速度清零（策划案要求）。")]
    [SerializeField] private bool _zeroSpeedOnImpact = true;

    [Tooltip("两次撞击之间的最短间隔，防止贴着墙连续多帧重复触发。")]
    [SerializeField, Min(0f)] private float _impactCooldown = 0.5f;

    [Tooltip("触发瞬间先沿墙面法线把人挪出去的距离。贴墙激活布娃娃会挂在墙上，靠这个挪开。")]
    [SerializeField, Min(0f)] private float _impactPushOut = 0.6f;

    [Tooltip("起身前向下探地面的距离。找到就把根节点落到地面上；找不到就从布娃娃当前位置继续下落。")]
    [SerializeField, Min(0f)] private float _groundProbeDistance = 3f;

    [Header("引用")]
    [Tooltip("Humanoid 优先使用当前 Avatar 的 Hips，避免换模型后选中残留的旧骨架。非 Humanoid 可显式指定根骨骼。")]
    [SerializeField] private Transform _hips;

    [Tooltip("运行时统一使用根节点上的 CharacterController / Animator。")]
    [SerializeField] private CharacterController _controller;
    [SerializeField] private Animator _animator;

    private Rigidbody[] _ragdollBodies;
    private Collider[] _ragdollColliders;
    // 玩家身上「不属于布娃娃」的碰撞体：根节点那个受击胶囊、Hitbox 等。
    // 布娃娃一激活，骨骼碰撞体会和它们重叠，PhysX 会把整具布娃娃挤出去 —— 现象就是「被一股力往旁边推飞」。
    private Collider[] _otherColliders;
    private bool[] _otherWasEnabled;
    // 硬直期间要关掉的那批组件（策划原文：不受 motor 和动作序列的作用）
    private Behaviour[] _controls;
    // 进入前它们各自是开还是关：PlayerMotor 会在 Awake 里把自己关掉，
    // 所以恢复时不能无脑 enabled = true，必须按原样还原。
    private bool[] _controlsWasEnabled;
    // 输入层单独处理：它 OnDisable 里会动光标状态，不该整个组件关掉。
    private PlayerInputReader _input;
    private PlayerMotor _motor;
    private float _nextImpactAt;
    // 触发瞬间要先把人从墙里挪出来的位移（世界坐标），由 OnWallImpact 填、EnterHitStun 消费
    private Vector3 _pendingPushOut;

    // 两段计时：布娃娃段结束时刻 / 起身段结束时刻
    private float _ragdollEndAt;
    private float _getUpEndAt;
    private bool _inGetUp;
    private int _getUpUsedLayer = -1;   // 这次起身实际用的是哪一层（结束时把权重还原）
    private float _hipsRootHeight;
    private readonly ActionControlPolicy _getUpControl = new ActionControlPolicy
    {
        AllowMove = false,
        AllowTurn = false,
        AllowJump = false,
        AllowSlide = false,
        AllowSprint = false
    };

    private bool _active;

    public bool IsActive => _active;

    private void Awake()
    {
        Cache();
        // 平时必须是「刚体 kinematic + 碰撞体关着」，否则布娃娃的碰撞体会和 CharacterController 打架
        SetRagdollActive(false);
    }

    private void Cache()
    {
        _controller = GetComponent<CharacterController>();
        _animator = GetComponent<Animator>();
        _input = GetComponent<PlayerInputReader>();
        _motor = GetComponent<PlayerMotor>();
        _hips = FindHips();

        // 只收 Hips 底下的刚体：玩家 Hitbox 子物体上也有个 Rigidbody，不能一起当布娃娃。
        _ragdollBodies = _hips != null
            ? _hips.GetComponentsInChildren<Rigidbody>(true)
            : new Rigidbody[0];
        _ragdollColliders = _hips != null
            ? _hips.GetComponentsInChildren<Collider>(true)
            : new Collider[0];
        Collider[] all = GetComponentsInChildren<Collider>(true);
        var others = new System.Collections.Generic.List<Collider>(all.Length);
        foreach (Collider c in all)
        {
            // CharacterController 也是 Collider 的子类，别把它算进来：
            // 它由 _controller 字段单独管，进来时先被关掉，若这里再"按记录还原"就会把它永远关在 off。
            if (c == null || c is CharacterController || IsRagdollCollider(c)) continue;
            others.Add(c);
        }
        _otherColliders = others.ToArray();
        _otherWasEnabled = new bool[_otherColliders.Length];
        // TODO: 等确认 PlayerMotor / 输入 / 动作序列有没有更安全的「暂停」入口，再决定是不是继续用 enabled = false。
        _controls = new Behaviour[]
        {
            _motor,
            GetComponent<PlayerActionRunner>(),
            GetComponent<ActionAnimatorBridge>(),
            GetComponent<PlayerCombat>(),
            // PlayerAnimation 每帧写 _animator.speed = TimeManager.PlayerRate：
            // 受击的慢动作/命中停顿会把它压到 0.05，起身动画就变成「看着根本没播」。
            GetComponent<PlayerAnimation>(),
        };
        _controlsWasEnabled = new bool[_controls.Length];
    }

    private Transform FindHips()
    {
        // 可见模型由当前 Avatar 驱动，不能让旧骨架的名字抢先匹配。
        if (_animator != null && _animator.isHuman)
            return _animator.GetBoneTransform(HumanBodyBones.Hips);
        if (_hips != null) return _hips;
        foreach (Transform t in GetComponentsInChildren<Transform>(true))
        {
            if (t.name == "mixamorig:Hips" || t.name == "Hips") return t;
        }
        return null;
    }

    public void EnterHitStun()
    {
        if (_active) return;

        if (_ragdollBodies.Length == 0)
        {
            Debug.LogWarning("[PlayerRagdollHitStun] 没找到布娃娃刚体 —— 先给 Player 配 Ragdoll Wizard，再确认 Hips 引用。", this);
            return;
        }

        _active = true;
        _inGetUp = false;
        _hipsRootHeight = _hips != null ? _hips.position.y - transform.position.y : 0f;
        _ragdollEndAt = Time.time + _ragdollSeconds;

        // 顺序很要紧：先把所有会「碰 Animator」的东西关掉，最后才关 Animator。
        // 反过来的话 —— 关 PlayerActionRunner / ActionAnimatorBridge 会触发它的 Release()，
        // 那里把 _animator.enabled = _wasEnabled 恢复成 true，动画又开始每帧写骨骼，
        // 物理抢不过它，现象就是「只卡了一下、人没瘫」。
        if (_controller != null) _controller.enabled = false;   // 布娃娃期间让位，否则胶囊会卡住骨骼
        SetControlsEnabled(false);
        if (_input != null) _input.SetGameplayEnabled(false);
        if (_animator != null) _animator.enabled = false;

        SetOtherColliders(false);   // 根节点那个受击胶囊必须先关：不然布娃娃骨骼和它重叠，会被 PhysX 挤飞

        // 撞墙那一刻人是贴着墙的：先把整体沿墙面法线挪出来，否则布娃娃的碰撞体一开就卡在墙里（挂在墙上）
        if (_pendingPushOut.sqrMagnitude > 0.0001f)
        {
            transform.position += _pendingPushOut;
            _pendingPushOut = Vector3.zero;
        }

        // Animator 和 Motor 可能刚移动过骨骼；先同步当前姿势，避免物理沿用上一帧的位置。
        Physics.SyncTransforms();
        SetRagdollActive(true);
    }

    public void ExitHitStun()
    {
        if (!_active) return;
        FinishHitStun();
    }

    // 第二段：布娃娃时间到 → 冻住布娃娃、对齐根节点、播起身动画
    private void BeginGetUp()
    {
        _inGetUp = true;

        SetRagdollActive(false);   // 刚体回 kinematic：姿势就冻在最后一帧

        // 对齐起点：x/z 用 Hips 的落点（不搬的话人会瞬移回撞击点站起来），
        // y 向下探地面 —— 高速撞墙基本都发生在空中，不贴地就会在半空起身。
        if (_hips != null)
        {
            Vector3 p = _hips.position;
            float y = p.y - _hipsRootHeight;
            if (Physics.Raycast(p + Vector3.up * 0.5f, Vector3.down, out RaycastHit ground,
                    _groundProbeDistance, ~0, QueryTriggerInteraction.Ignore))
                y = ground.point.y;
            transform.position = new Vector3(p.x, y, p.z);
        }

        // 骨骼已冻结，必须在起身时把碰撞与重力交回 Motor，不能让两套运动都停着。
        // 只恢复进入硬直前启用的 Motor；输入、动作与常规动画仍保持关闭。
        if (_controller != null) _controller.enabled = true;
        if (_motor != null)
        {
            _motor.SetActionControl(_getUpControl);
            for (int i = 0; i < _controls.Length; i++)
                if (_controls[i] == _motor)
                {
                    _motor.enabled = _controlsWasEnabled[i];
                    break;
                }
        }

        if (_animator != null) _animator.enabled = true;

        int hash = string.IsNullOrEmpty(_getUpState) ? 0 : Animator.StringToHash(_getUpState);

        // 自动找「哪个层里有这个状态」，优先非 Base Layer：
        // Base Layer 上的状态会被按参数触发的过渡立刻拉回 Idle（实测一帧就被拉走），
        // 所以要单独建一层放起身 —— 单状态、满权重的层能盖住 Base Layer。
        int layer = -1;
        if (_animator != null && hash != 0)
        {
            for (int i = _animator.layerCount - 1; i >= 1; i--)
                if (_animator.HasState(i, hash)) { layer = i; break; }
            if (layer < 0 && _animator.HasState(0, hash)) layer = 0;
        }

        if (_animator != null && layer >= 0 && hash != 0 && _animator.HasState(layer, hash))
        {
            _getUpUsedLayer = layer;
            // 单状态、满权重的独立层会盖住 Base Layer：
            // 不然 Base Layer 那些按参数触发的过渡会把起身立刻拉回 Idle（实测一帧就被拉走）。
            _animator.SetLayerWeight(layer, 1f);
            _animator.Play(hash, layer, 0f);
            _animator.Update(0f);
            float length = _animator.GetCurrentAnimatorStateInfo(layer).length;
            float seconds = _getUpTargetSeconds > 0f ? _getUpTargetSeconds : length;
            // 这一段里移动/动作都是停的，所以直接改 Animator 整体倍率来缩放到目标时长
            _animator.speed = length > 0.0001f ? length / Mathf.Max(0.05f, seconds) : 1f;
            _getUpEndAt = Time.time + seconds;
            return;
        }

        // 没配起身动画、或 Animator 里没有这个状态 → 退回占位：直接恢复
        Debug.LogWarning("[PlayerRagdollHitStun] 没有可用的起身状态 → 直接恢复（占位行为）", this);
        FinishHitStun();
    }

    // 第三段：起身播完（或占位）→ 恢复全部控制
    private void FinishHitStun()
    {
        if (!_active) return;
        _active = false;
        _inGetUp = false;

        SetRagdollActive(false);
        if (_controller != null) _controller.enabled = true;
        SetOtherColliders(true);
        if (_motor != null) _motor.SetActionControl(null);
        SetControlsEnabled(true);
        if (_input != null) _input.SetGameplayEnabled(true);

        // 最后再交回动画器：把起身层权重还原，并还原播放倍率
        if (_animator != null)
        {
            if (_getUpUsedLayer >= 0 && _getUpUsedLayer < _animator.layerCount)
                _animator.SetLayerWeight(_getUpUsedLayer, 0f);
            _getUpUsedLayer = -1;
            _animator.speed = 1f;
            _animator.enabled = true;
            _animator.Rebind();
            _animator.Update(0f);
        }
    }

    private void Update()
    {

        if (!_active) return;
        if (!_inGetUp)
        {
            if (Time.time >= _ragdollEndAt) BeginGetUp();
            return;
        }

        if (Time.time >= _getUpEndAt) FinishHitStun();
    }


    private bool IsRagdollCollider(Collider col)
    {
        for (int i = 0; i < _ragdollColliders.Length; i++)
            if (ReferenceEquals(_ragdollColliders[i], col)) return true;
        return false;
    }

    private void SetOtherColliders(bool restore)
    {
        if (_otherColliders == null) return;
        for (int i = 0; i < _otherColliders.Length; i++)
        {
            Collider c = _otherColliders[i];
            if (c == null) continue;
            // 按进入前的状态还原：Hitbox 平时本来就是关的，不能一律打开
            if (restore) c.enabled = _otherWasEnabled[i];
            else { _otherWasEnabled[i] = c.enabled; c.enabled = false; }
        }
    }

    // ---------- 撞击触发 ----------
    // 移动层统一报告正撞接触，保护规则只过滤伤害，不能屏蔽碰撞本身。
    // 这里只负责数值与后果：够不够快、扣多少血、清不清速度、进不进硬直。
    private void OnEnable()
    {
        if (_motor == null) return;
        _motor.WallImpact -= OnWallImpact;   // 幂等：OnEnable 可能被调多次
        _motor.WallImpact += OnWallImpact;
    }

    private void OnDisable()
    {
        if (_motor != null) _motor.WallImpact -= OnWallImpact;
    }

    private void OnWallImpact(WallContact contact)
    {
        if (_active || _motor == null) return;
        if (Time.time < _nextImpactAt) return;

        MovementParams p = _motor.Params;
        float threshold = p != null ? p.GroundSpeedThreshold : 1f;
        // ⚠️ 必须用「撞墙前」的速度：接触记录里的 IncomingVelocity。
        // 移动层在碰撞后会把速度沿墙面裁掉（MovementContacts.Move 里的 ClipIntoSurface），
        // 所以读 _motor.HorizontalSpeed 拿到的是裁过的值 —— 正面撞墙时几乎归零，
        // 表现为「同一个门槛，有时触发有时不触发，越正面撞越不触发」。
        float speed = MovementMath.Horizontal(contact.IncomingVelocity).magnitude;
        if (speed < _minSpeedFactor * threshold) return;      // 不够快 → 不吃撞击惩罚

        // 低顶下无法安全播放站立起身；保留低姿态与撞墙事件，允许玩家主动低速退出通道。
        if (contact.WasSliding && _motor.IsSlideHeadBlocked)
        {
            if (_zeroSpeedOnImpact) _motor.SetHorizontalSpeed(0f);
            return;
        }

        _nextImpactAt = Time.time + _impactCooldown;

        // 伤害按「地速阈值的倍数」换算，和策划案里速度/能量用的是同一套单位
        float damage = !contact.WasSliding && threshold > 0.0001f ? _damagePerSpeed * (speed / threshold) : 0f;
        if (_zeroSpeedOnImpact) _motor.SetHorizontalSpeed(0f);

        HealthComponent health = GetComponent<HealthComponent>();
        if (health != null && damage > 0f) health.TakeDamage(damage, contact.Point, -contact.Normal);

        _pendingPushOut = contact.Normal * _impactPushOut;

        EnterHitStun();
    }

    private void SetRagdollActive(bool on)
    {
        foreach (Rigidbody body in _ragdollBodies)
        {
            if (body == null) continue;
            body.isKinematic = !on;
        }
        foreach (Collider col in _ragdollColliders)
        {
            if (col == null) continue;
            col.enabled = on;
        }
    }

    private void SetControlsEnabled(bool on)
    {
        if (_controls == null) return;
        for (int i = 0; i < _controls.Length; i++)
        {
            Behaviour b = _controls[i];
            if (b == null) continue;
            if (on) b.enabled = _controlsWasEnabled[i];   // 按进入前的状态还原
            else { _controlsWasEnabled[i] = b.enabled; b.enabled = false; }
        }
    }
}
