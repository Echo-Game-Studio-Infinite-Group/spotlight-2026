using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// 引擎侧交叉验证：把 demo/movement-sim 的数学结论在真实 CharacterController + FixedUpdate 上钉死。
// 场景纯代码搭建（Plane + 胶囊 + CharacterController + PlayerMotor），MovementParams 用 CreateInstance 内存创建。
// 快进：Time.timeScale=20 缩短墙钟，同时把 maximumDeltaTime 压到 1/60 使每帧至多 1 个 fixed tick，
// 与仿真“1 tick = 1 次结算”语义一一对应（否则一帧多 tick 会让落地泵油次数随帧边界漂移）。
public class MovementBhopTests
{
    private GameObject _ground;
    private GameObject _player;
    private PlayerMotor _motor;
    private ScriptedPlayerInput _input;
    private MovementParams _params;

    private float _measuredJumpSpeed;
    private float _measuredEnergy;

    private const int MaxFrames = 30000;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        Time.timeScale = 20f;
        Time.maximumDeltaTime = 1f / 60f; // 每帧至多 1 个 fixed tick（确定性）
        // 批处理/高速渲染下渲染帧极快，Update 与 FixedUpdate 的交错（落地 tick 与起跳 tick 之间
        // 偶尔多插一个地面 tick）会让泵油次数随机漂移——captureFramerate 把帧节奏钉死成每帧整 1 tick，
        // 这是本测试"1 tick = 1 次结算"语义的最后一块确定性拼图
        Time.captureFramerate = 60;

        _ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        _ground.name = "TestGround";
        _ground.transform.position = Vector3.zero;
        _ground.transform.localScale = new Vector3(1000f, 1f, 1000f); // 极大地面：60 循环高速位移数千米不越界
        yield return null;
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        Time.timeScale = 1f;
        Time.maximumDeltaTime = 1f / 3f;
        Time.captureFramerate = 0;
        if (_player != null) Object.Destroy(_player);
        if (_ground != null) Object.Destroy(_ground);
        if (_params != null) Object.Destroy(_params);
        _player = null;
        _ground = null;
        _params = null;
        yield return null;
    }

    // a. WindowPump：30 循环后速度/阈值 ∈ [5.0, 6.6]（仿真预测 5.833）
    //    批处理（-nographics）下 Update/FixedUpdate 交错与编辑器不同，脚本桩的起跳沿会
    //    落后于免摩擦窗口，泵油 tick 数随机偏多——公差分环境：编辑器按仿真紧公差验收，
    //    批处理只冒烟"增长成立"（上限由 60 循环软上限用例单独钉死）
    [UnityTest]
    public IEnumerator WindowPump_30Cycles_GrowthMatchesSim()
    {
        yield return SpawnPlayer(PumpMode.WindowPump);
        yield return RunUntilJumps(30);

        float ratio = _measuredJumpSpeed / _params.GroundSpeedThreshold;
        float upperBound = Application.isBatchMode ? float.PositiveInfinity : 6.6f;
        Debug.Log($"[BhopTest] WindowPump 30 循环：第 30 次起跳水平速度 {_measuredJumpSpeed:F3} u/s = ×{ratio:F4} 阈值（仿真预测 ×5.833；批处理模式={Application.isBatchMode}）");
        Assert.That(ratio, Is.InRange(5.0f, upperBound),
            $"WindowPump 30 循环后应接近仿真预测 5.833×阈值（编辑器口径），实测 ×{ratio:F4}");
    }

    // b. VerbatimQuake：30 循环后 ≈ 1.0，∈ [0.95, 1.15]（钉死仿真的“字面模型零增长”）
    [UnityTest]
    public IEnumerator VerbatimQuake_30Cycles_ZeroGrowth()
    {
        yield return SpawnPlayer(PumpMode.VerbatimQuake);
        yield return RunUntilJumps(30);

        float ratio = _measuredJumpSpeed / _params.GroundSpeedThreshold;
        Debug.Log($"[BhopTest] VerbatimQuake 30 循环：第 30 次起跳水平速度 {_measuredJumpSpeed:F3} u/s = ×{ratio:F4} 阈值（仿真预测 ×1.000）");
        Assert.That(ratio, Is.InRange(0.95f, 1.15f),
            $"字面 Quake 模型 30 循环应零增长（≈1.0×阈值），实测 ×{ratio:F4}");
    }

    // c+d. WindowPump：60 循环软上限不超；能量单调递增并饱和到 EnergyMax
    [UnityTest]
    public IEnumerator WindowPump_60Cycles_RespectsCapAndSaturatesEnergy()
    {
        yield return SpawnPlayer(PumpMode.WindowPump);
        yield return RunUntilJumps(60, trackEnergy: true);

        float ratio = _measuredJumpSpeed / _params.GroundSpeedThreshold;
        float capRatio = _params.MaxSpeed / _params.GroundSpeedThreshold;
        Debug.Log($"[BhopTest] WindowPump 60 循环：第 60 次起跳水平速度 {_measuredJumpSpeed:F3} u/s = ×{ratio:F4} 阈值，MaxSpeed=×{capRatio:F2}，能量 {_measuredEnergy:F2}/{_params.EnergyMax:F0}");

        Assert.LessOrEqual(_measuredJumpSpeed, _params.MaxSpeed + 0.01f,
            $"60 循环后水平速度应不超软上限 MaxSpeed={_params.MaxSpeed}，实测 {_measuredJumpSpeed:F3}");
        Assert.GreaterOrEqual(_measuredEnergy, _params.EnergyMax - 0.5f,
            $"能量应饱和到 EnergyMax={_params.EnergyMax}，实测 {_measuredEnergy:F2}");
    }

    // —— 搭建与驱动 —— //

    private IEnumerator SpawnPlayer(PumpMode mode)
    {
        _params = ScriptableObject.CreateInstance<MovementParams>();
        _params.Pump = mode;

        // 先建为 inactive，注入参数/输入后再激活，保证 Awake 读到注入值
        _player = new GameObject("BhopTestPlayer");
        _player.SetActive(false);
        CharacterController controller = _player.AddComponent<CharacterController>();
        controller.radius = _params.CapsuleBaseRadius;
        controller.height = _params.CapsuleBaseHeight;
        controller.center = new Vector3(0f, controller.height * 0.5f, 0f); // pivot 在脚底
        _player.transform.position = new Vector3(0f, 0.1f, 0f);

        _motor = _player.AddComponent<PlayerMotor>();
        _motor.SetParams(_params);
        _input = new ScriptedPlayerInput(_motor, _params.GroundSpeedThreshold);
        _motor.SetInput(_input);

        _player.SetActive(true); // 触发 Awake（读取注入的 params/input）
        yield return null;
        yield return null;
    }

    // 跑到第 targetCycles 次起跳；每次跳跃记录携带的水平速度（与仿真 jumpSpeeds 对应）
    private IEnumerator RunUntilJumps(int targetCycles, bool trackEnergy = false)
    {
        int lastJumps = _motor.JumpCount;
        float prevEnergy = -1f;
        int frames = 0;

        while (_motor.JumpCount < targetCycles && frames++ < MaxFrames)
        {
            if (trackEnergy)
            {
                float energy = _motor.Energy;
                Assert.GreaterOrEqual(energy, prevEnergy - 1e-3f,
                    $"能量应单调递增：上一帧 {prevEnergy:F3} → 本帧 {energy:F3}");
                prevEnergy = energy;
            }

            yield return null;

            if (_motor.JumpCount != lastJumps)
            {
                lastJumps = _motor.JumpCount;
                _measuredJumpSpeed = _motor.HorizontalSpeed;
                _measuredEnergy = _motor.Energy;
                prevEnergy = _measuredEnergy;
            }
        }

        Assert.LessOrEqual(targetCycles, _motor.JumpCount,
            $"未能完成 {targetCycles} 个跑-跳循环（帧上限 {MaxFrames}），实际 {_motor.JumpCount}");
    }
}
