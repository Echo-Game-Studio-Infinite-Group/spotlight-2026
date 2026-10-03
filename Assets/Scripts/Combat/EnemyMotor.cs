using UnityEngine;

// 灰盒敌人运动器（D3–D5 首个战斗闭环·敌人侧装配）：重力 + 直线追击 + 攻击前移 + 击退执行
// 边界：不做寻路——直线走向移动目标，绕行/路径决策归寻路 AI（leo 侧落地后本类退化为纯命令执行器）；
//      与玩家侧同一约束：战斗不直接动 Transform——EnemyCombat 的朝向/前移都经本类方法请求
// 时间：运动积分走 TimeManager.WorldDeltaTime（敌人属世界层——时停时敌人冻结，框架 4.3）
[DefaultExecutionOrder(0)] // 与 PlayerMotor 同位：命令侧（EnemyCombat，-50）先行，本类只执行
public class EnemyMotor : MonoBehaviour, IKnockbackReceiver
{
    [Tooltip("重力加速度（与玩家侧 MovementParams 口径一致，灰盒独立常备——敌人不吃玩家参数资产）")]
    [SerializeField] private float _gravity = 20f;

    [Tooltip("击退冲量衰减速率（每秒指数衰减；越大恢复越快）")]
    [SerializeField] private float _knockbackDamping = 6f;

    [Tooltip("转向角速度（度/秒）——追击/攻击朝向的平滑转速，360 = 即时")]
    [SerializeField] private float _turnSpeedDeg = 360f;

    private CharacterController _controller;
    private Transform _moveTarget;
    private float _moveSpeed;
    private Vector3 _requestedMove;      // 攻击前移：本 tick 一次性位移（EnemyCombat 每 tick 请求）
    private Vector3 _knockbackVelocity;  // 击退冲量（IKnockbackReceiver 累积，自行衰减，不受移动命令影响）
    private float _verticalVelocity;

    private void Awake()
    {
        _controller = GetComponent<CharacterController>();
    }

    private void FixedUpdate()
    {
        // cc 被关（死亡断肢/瞬移中）时 Move 会抛异常——运动整体停摆即可
        if (_controller == null || !_controller.enabled) return;
        if (TimeManager.IsPaused) return;
        float dt = TimeManager.WorldDeltaTime;
        Move(dt);
    }

    private void Move(float dt)
    {
        Vector3 horizontal = _knockbackVelocity;

        if (_moveTarget != null && _moveSpeed > 0f)
        {
            Vector3 toTarget = _moveTarget.position - transform.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude > 0.0001f)
            {
                Vector3 dir = toTarget.normalized;
                horizontal += dir * _moveSpeed;
                FaceDirection(dir, dt); // 追击中朝移动方向转
            }
        }

        // 攻击前移请求（结算③同款语义：命令一次性消费，不给攻击方持续速度）
        horizontal += _requestedMove / Mathf.Max(dt, 1e-5f);
        _requestedMove = Vector3.zero;

        // 重力：地面时清零防持续累积（CharacterController.isGrounded 在 Move 调用后有效，故用上帧读数）
        _verticalVelocity -= _gravity * dt;
        if (_controller.isGrounded && _verticalVelocity < 0f) _verticalVelocity = 0f;

        _controller.Move((horizontal + Vector3.up * _verticalVelocity) * dt);

        _knockbackVelocity *= Mathf.Exp(-_knockbackDamping * dt);
        if (_knockbackVelocity.sqrMagnitude < 0.0001f) _knockbackVelocity = Vector3.zero;
    }

    #region 命令侧接口（EnemyCombat / 寻路 AI 调用）

    /// <summary>设置追击目标与速度；AI 决策层专用（灰盒期由 EnemyCombat 感知驱动）</summary>
    public void SetMoveTarget(Transform target, float speed)
    {
        _moveTarget = target;
        _moveSpeed = speed;
    }

    /// <summary>停止追击（进入攻击/失去目标/死亡）</summary>
    public void ClearMoveTarget()
    {
        _moveTarget = null;
        _moveSpeed = 0f;
    }

    /// <summary>攻击前移请求（与 PlayerMotor.RequestMove 同语义：撞墙经 CharacterController 扫掠天然截断）</summary>
    public void RequestMove(Vector3 direction, float distance)
    {
        _requestedMove += direction.normalized * distance;
    }

    /// <summary>朝向某点（水平面转向，保持竖直姿态）——攻击锁向/感知朝向用</summary>
    public void FaceTowards(Vector3 point)
    {
        Vector3 to = point - transform.position;
        to.y = 0f;
        if (to.sqrMagnitude > 0.0001f) FaceDirection(to.normalized, TimeManager.WorldDeltaTime);
    }

    /// <summary>死亡断肢（简化版）：关阻挡碰撞与运动——受击框已由结算器按 IsDead 跳过，尸体不再参与逻辑</summary>
    public void DisableLocomotion()
    {
        ClearMoveTarget();
        _knockbackVelocity = Vector3.zero;
        _requestedMove = Vector3.zero;
        _verticalVelocity = 0f;
        if (_controller != null) _controller.enabled = false;
    }

    /// <summary>重开复位：恢复阻挡碰撞（与 ResetForRestart 配套，重试灰盒用）</summary>
    public void EnableLocomotion()
    {
        if (_controller != null) _controller.enabled = true;
    }

    /// <summary>瞬移回出生点（重开复位；CharacterController.enabled 时直接设位会受碰撞推开，先关再开）</summary>
    public void Teleport(Vector3 position, Quaternion rotation)
    {
        bool wasEnabled = _controller != null && _controller.enabled;
        if (_controller != null) _controller.enabled = false;
        transform.SetPositionAndRotation(position, rotation);
        if (_controller != null) _controller.enabled = wasEnabled;
        _verticalVelocity = 0f;
        _knockbackVelocity = Vector3.zero;
        _requestedMove = Vector3.zero;
    }

    // IKnockbackReceiver（CombatSeams：结算③击退命令）——冲量叠加后自行衰减，敌人被击退不中断攻击（霸体语义由 Health 侧定）
    public void OnKnockback(Vector3 impulse)
    {
        impulse.y = 0f; // 灰盒只做水平击退，避免把轻敌打上天后 CharacterController 接不到地
        _knockbackVelocity += impulse;
    }

    #endregion

    private void FaceDirection(Vector3 direction, float dt)
    {
        if (_turnSpeedDeg >= 360f)
        {
            transform.rotation = Quaternion.LookRotation(direction);
            return;
        }
        float maxDeg = _turnSpeedDeg * dt;
        transform.rotation = Quaternion.RotateTowards(
            transform.rotation, Quaternion.LookRotation(direction), maxDeg);
    }
}
