using UnityEngine;

// 敌人攻击提示（attack telegraph）：只负责「起手亮、收招灭」，不含任何策划数值。
// 生命周期完全跟敌人的攻击窗口走：Enemy.TryAttack 调 Play()，Enemy.FinishAttack 调 Stop()。
// 于是受击、死亡、动画状态被打断、回收进池这些路径都会自动收掉，不需要额外兜底。
//
// 刻意不自己计时：时长由「攻击窗口」本身决定（近战就是动画前摇），
// 所以既不用策划填数，也不存在「提示比敌人先跑完 / 被时停甩开」的问题。
//
// 表现层是占位的：激活一个约定名字的子物体，内容（粒子 / 面片 / 贴花）后面换，接口不动。
// 子物体里如果放粒子，把 Play On Awake 勾上就行，不用在这里再调一遍。
[DisallowMultipleComponent]
public sealed class AttackTelegraph : MonoBehaviour
{
    [Tooltip("占位表现：Play 时激活这个子物体。留空则按下面的名字在子节点里找。")]
    [SerializeField] private Transform _visual;

    [Tooltip("普通 parry 的图。留空 = 不动 SpriteRenderer 上原来的图。")]
    [SerializeField] private Sprite _normalSprite;

    [Tooltip("劣势 parry 的图（红）。")]
    [SerializeField] private Sprite _disadvantageSprite;

    [Tooltip("渲染器。留空则自动从子物体里找。")]
    [SerializeField] private SpriteRenderer _renderer;

    [SerializeField] private string _visualName = "AttackTelegraph";

    public bool IsPlaying { get; private set; }

    private void Awake()
    {
        if (_visual == null) _visual = transform.Find(_visualName);
        SetVisual(false);
    }

    /// <summary>起手：敌人确定要打这一下时调（Enemy.TryAttack）</summary>
    public void Play()
    {
        if (IsPlaying) return;
        IsPlaying = true;
        ApplySprite();
        SetVisual(true);
    }

    /// <summary>收招：攻击窗口关闭时调（Enemy.FinishAttack）。受击 / 死亡 / 被打断 / 回收都会走到。</summary>
    public void Stop()
    {
        if (!IsPlaying) return;
        IsPlaying = false;
        SetVisual(false);
    }

    // 广告牌：只让提示绕 Y 轴转向摄像机（俯仰不跟，图不会上下翻滚）。
    // 转的是 _visual（子物体）—— 组件挂在敌人根节点上，转 transform 会把整只敌人转歪。
    private void LateUpdate()
    {
        if (!IsPlaying || _visual == null) return;      // 没亮就不需要朝向，直接跳过

        Camera cam = Camera.main;
        if (cam == null) return;

        Vector3 toCamera = _visual.position - cam.transform.position;
        toCamera.y = 0f;
        if (toCamera.sqrMagnitude < 0.0001f) return;    // 摄像机正好在头顶时别算出零向量
        _visual.rotation = Quaternion.LookRotation(toCamera, Vector3.up);
    }
    // 按类型换图。两张都没配就保持 SpriteRenderer 上原来的图（不会把图清空）。
    private void ApplySprite()
    {
        if (_renderer == null) _renderer = GetComponentInChildren<SpriteRenderer>(true);
        CombatComponent combat = GetComponent<CombatComponent>();
        Sprite target = combat != null && combat.ParryKind == AttackParryKind.Disadvantage ? _disadvantageSprite : _normalSprite;
        if (_renderer != null && target != null) _renderer.sprite = target;
    }

    private void SetVisual(bool on)
    {
        if (_visual != null) _visual.gameObject.SetActive(on);
    }

    private void OnDisable() => Stop();
}
