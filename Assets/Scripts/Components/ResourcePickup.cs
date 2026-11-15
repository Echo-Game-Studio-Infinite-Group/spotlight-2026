using UnityEngine;

[DisallowMultipleComponent]
public sealed class ResourcePickup : MonoBehaviour
{
    public enum ResourceKind { Health, Energy }

    [SerializeField] private ResourceKind _kind;
    [SerializeField, Min(0f), Tooltip("恢复固定数值，超出上限的部分舍弃。")]
    private float _amount = 25f;
    [SerializeField, Min(.1f), Tooltip("贴近吸附目标后吃掉球，单位米。")]
    private float _pickupRadius = .25f;
    [SerializeField, Min(0f), Tooltip("玩家经过此范围后开始吸附，0 表示关闭吸附。")]
    private float _attractRadius = 5f;
    [SerializeField, Min(0f), Tooltip("吸附飞行速度，单位米/世界秒。")]
    private float _attractSpeed = 12f;
    [SerializeField, Min(0f), Tooltip("吸附目标相对玩家根节点的高度。")]
    private float _attractHeight = .6f;
    [SerializeField, Min(0f), Tooltip("按世界时间计时，0 表示不自动消失。")]
    private float _lifetime = 30f;
    [SerializeField] private Transform _visual;
    [SerializeField] private float _rotationSpeed = 70f;

    private Player _player;
    private PlayerMotor _motor;
    private Vector3 _previousPosition;
    private float _age;
    private float _visualAngle;
    private bool _consumed;
    private bool _attracting;

    private void OnEnable()
    {
        _age = 0f;
        _visualAngle = 0f;
        _consumed = false;
        BindPlayer(Player.Current);
    }

    private void Update()
    {
        if (TimeManager.IsPaused) return;
        Player current = Player.Current;
        if (current != _player) BindPlayer(current);
        if (_player != null) OnPathPoint(_player.transform.position);
        Simulate(TimeManager.WorldDeltaTime);
    }

    public void Simulate(float worldSeconds)
    {
        if (_consumed || TimeManager.IsPaused || worldSeconds <= 0f) return;
        _age += worldSeconds;
        if (_lifetime > 0f && _age >= _lifetime) { Consume(); return; }
        _visualAngle = Mathf.Repeat(_visualAngle + _rotationSpeed * worldSeconds, 360f);
        if (!_attracting) return;
        if (_attractRadius <= 0f || !CanCollect(_player)) { _attracting = false; return; }
        Vector3 target = _player.transform.position + Vector3.up * _attractHeight;
        transform.position = Vector3.MoveTowards(transform.position, target, _attractSpeed * worldSeconds);
        if ((transform.position - target).sqrMagnitude <= _pickupRadius * _pickupRadius)
            TryCollect(_player);
    }

    private void LateUpdate()
    {
        if (_visual == null) return;
        Camera view = Camera.main;
        if (view == null) return;
        // 面片完整跟随相机朝向，再在面内旋转；绕世界 Y 轴会让图片转到侧面消失。
        _visual.rotation = view.transform.rotation * Quaternion.AngleAxis(_visualAngle, Vector3.forward);
    }

    private void BindPlayer(Player player)
    {
        _attracting = false;
        if (_motor != null)
        {
            _motor.MotionPathPoint -= OnPathPoint;
            _motor.Teleported -= OnTeleported;
        }
        _player = player;
        _motor = player != null ? player.Motor : null;
        if (player == null) return;
        _previousPosition = player.transform.position + Vector3.up * _attractHeight;
        if (_motor != null)
        {
            _motor.MotionPathPoint += OnPathPoint;
            _motor.Teleported += OnTeleported;
        }
    }

    private void OnTeleported(Vector3 delta)
    {
        // 重生与房间传送不是行走，不能拾取起点到终点之间的道具。
        _previousPosition = _player.transform.position + Vector3.up * _attractHeight;
        _attracting = false;
    }

    private void OnPathPoint(Vector3 position)
    {
        position += Vector3.up * _attractHeight;
        Vector3 start = _previousPosition;
        _previousPosition = position;
        if (_consumed || _player == null || TimeManager.IsPaused) return;
        // 使用移动层报告的实际分段路径，避免高速越过拾取范围时漏检。
        Vector3 segment = position - start;
        float t = segment.sqrMagnitude > 0f
            ? Mathf.Clamp01(Vector3.Dot(transform.position - start, segment) / segment.sqrMagnitude) : 0f;
        float distanceSqr = (transform.position - (start + segment * t)).sqrMagnitude;
        if (distanceSqr <= _pickupRadius * _pickupRadius) TryCollect(_player);
        else if (_attractRadius > 0f && distanceSqr <= _attractRadius * _attractRadius && CanCollect(_player))
            _attracting = true;
    }

    private bool CanCollect(Player player)
    {
        if (_consumed || !isActiveAndEnabled || TimeManager.IsPaused || _amount <= 0f ||
            player == null || !player.isActiveAndEnabled || !player.Health.IsAlive) return false;
        return _kind == ResourceKind.Health || player.GetComponent<VectorEnergy>() != null;
    }

    public bool TryCollect(Player player)
    {
        if (!CanCollect(player)) return false;
        // 满额仍消耗道具；账户负责钳制上限。先锁定，防止 Changed 回调重入结算。
        _consumed = true;
        if (_kind == ResourceKind.Health)
        {
            player.Health.Heal(_amount);
        }
        else
        {
            VectorEnergy energy = player.GetComponent<VectorEnergy>();
            energy.AddEnergy(_amount);
        }
        Consume();
        return true;
    }

    private void Consume()
    {
        _consumed = true;
        gameObject.SetActive(false);
        Destroy(gameObject);
    }

    private void OnDisable() => BindPlayer(null);

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = _kind == ResourceKind.Health ? Color.green : Color.cyan;
        Gizmos.DrawWireSphere(transform.position, _pickupRadius);
        Gizmos.color = new Color(1f, .8f, .1f, .5f);
        Gizmos.DrawWireSphere(transform.position, _attractRadius);
    }
}
