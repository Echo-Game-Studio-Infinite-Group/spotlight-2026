using UnityEngine;

// 脚底摩擦火花：玩家在地面高速移动 / 滑铲时，从脚底沿运动反方向喷火花。
// 挂在玩家根节点（与 PlayerMotor 同物体），RequireComponent 保证拿得到移动状态。
//
// 实现要点：
//   · 发射用 rateOverDistance（按移动距离发射，不按秒）——速度越快单位路程出的火花越多，
//     天然就是「摩擦强度」的表达，不需要自己按帧累计；
//   · 发射器每帧朝向速度反方向，粒子锥顺着喷，火花看起来是被脚「蹭出来」的；
//   · 粒子走世界坐标系（World simulation space），火花留在原地，不跟着人跑；
//   · 开世界碰撞 + 落地弹跳：火花贴着地面蹦跳变暗——金属摩擦的写实签名（与 CombatSparks 同一口径）；
//   · 门槛取 MovementParams.GroundSpeedThreshold（跑到高速档才开始蹭火花），
//     滑铲时乘强度倍率——滑铲是本作最有「摩擦感」的动作。
//
// 数值为提案基线，待 3C 与美术定稿后调；发射器与粒子系统全部代码装配，工程里没有对应资产。
[RequireComponent(typeof(PlayerMotor))]
[DisallowMultipleComponent]
public sealed class SlideSparks : MonoBehaviour
{
    [Header("触发门槛")]
    [Tooltip("开始蹭火花的速度 = GroundSpeedThreshold × 此系数（提案 1.0）")]
    [SerializeField, Min(0f)] private float _speedThresholdFactor = 1f;

    [Tooltip("滑铲强度倍率（提案 2.0）")]
    [SerializeField, Min(1f)] private float _slideBoost = 2f;

    [Header("发射量")]
    [Tooltip("低速档每米路程的火花数（提案 1.2）")]
    [SerializeField, Min(0f)] private float _sparksPerMeter = 1.2f;

    [Tooltip("高速档每米路程的火花数（提案 8，防高速时糊屏）")]
    [SerializeField, Min(1f)] private float _maxSparksPerMeter = 8f;

    private PlayerMotor _motor;
    private CharacterController _controller;
    private ParticleSystem _system;
    private Transform _emitter;

    private void Awake()
    {
        _motor = GetComponent<PlayerMotor>();
        _controller = GetComponent<CharacterController>();
    }

    private void OnEnable() => EnsureEmitter();
    private void OnDisable()
    {
        // 关闭（死亡/重开接线）时把残留粒子清掉，避免火花悬在半空
        if (_system != null) _system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
    }

    private void LateUpdate()
    {
        if (_system == null || _motor.Params == null) return;

        float speed = _motor.HorizontalSpeed;
        bool sliding = _motor.IsSliding;
        bool grounded = _motor.IsGrounded;
        float threshold = _motor.Params.GroundSpeedThreshold;

        // 未接地或速度不够：不发射（已有的粒子自然燃尽）
        if (!grounded || speed < threshold * _speedThresholdFactor)
        {
            ParticleSystem.EmissionModule emission = _system.emission;
            emission.rateOverDistance = new ParticleSystem.MinMaxCurve(0f);
            return;
        }

        // 发射器贴住脚底：胶囊中心在半高，脚底 = y - height/2（滑铲胶囊变矮时自动跟着降）
        float footHeight = transform.position.y - _controller.height * 0.5f + 0.06f;
        _emitter.position = new Vector3(transform.position.x, footHeight, transform.position.z);

        // 朝向速度反方向：粒子锥默认沿本地 +Z 喷，火花顺着被「蹭」出去
        Vector3 horizontal = _motor.Velocity;
        horizontal.y = 0f;
        if (horizontal.sqrMagnitude > 0.0001f)
            _emitter.rotation = Quaternion.LookRotation(-horizontal.normalized, Vector3.up);

        // 每米火花数随速度在基础档与高速档之间线性涨；滑铲再乘倍率
        float rate = Mathf.Lerp(_sparksPerMeter, _maxSparksPerMeter, Mathf.Clamp01(speed / _motor.Params.MaxSpeed));
        if (sliding) rate *= _slideBoost;

        ParticleSystem.EmissionModule emit = _system.emission;
        emit.rateOverDistance = new ParticleSystem.MinMaxCurve(rate);
    }

    // 粒子系统代码装配：一次性建好脚底发射器子节点。
    // public 供编辑器自检直呼：编辑模式 AddComponent 不跑 OnEnable，批量自检要自己触发装配。
    public void EnsureEmitter()
    {
        if (_system != null) return;

        GameObject host = new GameObject("SlideSparks(Emitter)");
        _emitter = host.transform;
        _emitter.SetParent(transform, false);
        _system = host.AddComponent<ParticleSystem>();
        _system.playOnAwake = false;

        ParticleSystem.MainModule main = _system.main;
        main.loop = true;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.18f, 0.36f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(2.5f, 5f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.03f, 0.06f);
        main.gravityModifier = 2.2f;             // 火花蹭出去后有明显的下坠弧线
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.stopAction = ParticleSystemStopAction.None;
        main.maxParticles = 240;                 // 高速滑铲时的存活上限，防糊屏

        ParticleSystem.EmissionModule emission = _system.emission;
        emission.rateOverTime = 0f;
        emission.rateOverDistance = new ParticleSystem.MinMaxCurve(0f); // 等移动状态驱动

        ParticleSystem.ShapeModule shape = _system.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = 22f;
        shape.radius = 0.04f;
        shape.rotation = new Vector3(24f, 0f, 0f); // 锥轴略微上抬：火花向后上方掠过地面，不平射也不戳地

        ParticleSystem.ColorOverLifetimeModule color = _system.colorOverLifetime;
        color.enabled = true;
        Gradient gradient = new Gradient();
        // RGB 常保亮度、alpha 负责熄灭（预乘混合口径，与 CombatSparks.BuildGradient 同一套）
        gradient.SetKeys(
            new[] {
                new GradientColorKey(new Color(1f, 0.98f, 0.92f), 0f),
                new GradientColorKey(new Color(1f, 0.66f, 0.26f), 0.4f),
                new GradientColorKey(new Color(0.85f, 0.28f, 0.05f), 0.8f),
            },
            new[] {
                new GradientAlphaKey(1f, 0f),
                new GradientAlphaKey(0.95f, 0.4f),
                new GradientAlphaKey(0.45f, 0.75f),
                new GradientAlphaKey(0f, 0.95f),
            });
        color.color = new ParticleSystem.MinMaxGradient(gradient);

        ParticleSystem.SizeOverLifetimeModule size = _system.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 1f, 1f, 0.1f));

        // 世界碰撞 + 落地弹跳：贴着地面蹦跳、LifetimeLoss 快耗寿命≈触地后快熄灭
        ParticleSystem.CollisionModule collision = _system.collision;
        collision.enabled = true;
        collision.type = ParticleSystemCollisionType.World;
        collision.mode = ParticleSystemCollisionMode.Collision3D;
        collision.dampen = 0.25f;
        collision.bounce = 0.4f;
        collision.lifetimeLoss = 0.4f;
        collision.minKillSpeed = 0.4f;
        collision.radiusScale = 1f;

        ParticleSystemRenderer renderer = _system.GetComponent<ParticleSystemRenderer>();
        renderer.material = CombatSparks.SparkMaterial;
        renderer.renderMode = ParticleSystemRenderMode.Stretch;
        renderer.velocityScale = 0.045f;
        renderer.lengthScale = 2.2f;
        renderer.alignment = ParticleSystemRenderSpace.View;

        _system.Play(); // 常驻发射器：一直开着，靠 rateOverDistance=0 静默
    }
}
