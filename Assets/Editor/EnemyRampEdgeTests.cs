using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;

// 回归：敌人不能卡在坡道边缘。
// 场景 AaBb9331_PatrolTest 的坡道是一块 Cube（scale 5.626/9.6865/4.6492，绕 Z 轴 63.369°），
// 它的可行走面是 26.63°（63.369° 的余角）——低于 agentSlope 45，所以会被烘进导航网格；
// 而它两侧是垂直壁，烘焙会向内侵蚀 agentRadius=0.5m，敌人（胶囊半径同为 0.5m）贴边站时
// transform.position 就落在网格空洞里，路径起点失效。
// 这里用与场景同尺寸的几何就地烘一张同参数的网格，把这类"网格外"站位直接摆出来测。
public class EnemyRampEdgeTests
{
    private const float RampEdgeZ = -4.675f;    // 坡道靠平台一侧的垂直壁（世界 z）
    private const float RampMidX = -3f;         // 坡道中段取样位置
    private const float FrameDelta = 1f / 60f;

    private readonly List<GameObject> _level = new List<GameObject>();
    private readonly List<GameObject> _enemies = new List<GameObject>();
    private NavMeshDataInstance _navMeshInstance;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        // 快进：timeScale 放大墙钟，maximumDeltaTime 把每帧游戏时间压到 1/60，
        // 与工程"逻辑帧 60Hz"的约定一致
        Time.timeScale = 20f;
        Time.maximumDeltaTime = FrameDelta;

        BuildLevel();
        yield return null;

        BuildNavMesh();
        yield return null;
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        Time.timeScale = 1f;
        Time.maximumDeltaTime = 1f / 3f;

        if (_navMeshInstance.valid) _navMeshInstance.Remove();
        foreach (GameObject enemy in _enemies) Object.Destroy(enemy);
        foreach (GameObject part in _level) Object.Destroy(part);
        _enemies.Clear();
        _level.Clear();
        yield return null;
    }

    // 前置校验：坡道面确实是 26.6° 左右，而且被烘进了导航网格
    [Test]
    public void RampSurface_IsWalkable()
    {
        Assert.IsTrue(Physics.Raycast(new Vector3(RampMidX, 6f, -7f), Vector3.down, out RaycastHit hit, 20f),
            "坡道中段应该能探到地面");

        float slope = Vector3.Angle(hit.normal, Vector3.up);
        Debug.Log($"[RampProbe] 坡道中段坡角 = {slope:F2}°（场景斜板 63.369° 的余角 26.63°）");
        Assert.That(slope, Is.InRange(25f, 28f), "坡道可行走面应在 26.6° 附近");

        Assert.IsTrue(NavMesh.SamplePosition(new Vector3(RampMidX, 1.85f, -7f), out NavMeshHit navHit, 1f, NavMesh.AllAreas),
            "坡道中段应该在导航网格上（26.6° < agentSlope 45）");
        Debug.Log($"[RampProbe] 坡道中段吸附距离 = {navHit.distance:F3}m");
    }

    // 上下文包 §7.5：NavMesh 的 Max Slope 必须 ≥ CharacterController.slopeLimit。
    // 这里把约定钉死：代理坡度 60 时 50° 的面烘得上、63° 的面烘不上
    [Test]
    public void AgentSlope60_BakesFiftyNotSixtyThree()
    {
        Assert.IsTrue(Physics.Raycast(new Vector3(40f, 20f, 40f), Vector3.down, out RaycastHit hit50, 40f),
            "应该打到 50° 斜板");
        float slope50 = Vector3.Angle(hit50.normal, Vector3.up);
        Debug.Log($"[RampProbe] 50° 板实测坡角 = {slope50:F2}°");
        Assert.IsTrue(NavMesh.SamplePosition(hit50.point, out _, 0.3f, NavMesh.AllAreas),
            $"代理坡度 60 时 {slope50:F1}° 的坡面应该可行走");

        Assert.IsTrue(Physics.Raycast(new Vector3(40f, 20f, 50f), Vector3.down, out RaycastHit hit63, 40f),
            "应该打到 63° 斜板");
        float slope63 = Vector3.Angle(hit63.normal, Vector3.up);
        Debug.Log($"[RampProbe] 63° 板实测坡角 = {slope63:F2}°");
        Assert.IsFalse(NavMesh.SamplePosition(hit63.point, out _, 0.3f, NavMesh.AllAreas),
            $"超过代理坡度的面（{slope63:F1}° > 60°）不该可行走");
    }

    // 关键回归：站在坡道边缘（网格空洞里）的敌人必须能自己走回来
    [UnityTest]
    public IEnumerator EnemyOnRampEdge_Recovers()
    {
        // 坡道面在 x=-3 处高约 1.85m；z=-4.9 距坡道侧边缘只有 0.225m，
        // 远在 0.5m 侵蚀带内 —— 这个站位在导航网格之外
        Vector3 edge = new Vector3(RampMidX, 1.95f, -4.9f);

        bool onMesh = NavMesh.SamplePosition(edge, out NavMeshHit snap, 0.4f, NavMesh.AllAreas) &&
                      snap.distance < 0.05f;
        Debug.Log($"[RampProbe] 测试站位 {edge} 是否在网格上 = {onMesh}");
        Assert.IsFalse(onMesh, "测试前提：该站位应落在导航网格的侵蚀空洞里");

        GameObject enemy = SpawnEnemy(edge);
        yield return null;

        Vector3 start = enemy.transform.position;
        float elapsed = 0f;
        int frames = 0;

        while (elapsed < 20f && frames++ < 200000)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }

        float moved = Vector3.ProjectOnPlane(enemy.transform.position - start, Vector3.up).magnitude;
        bool backOnMesh = NavMesh.SamplePosition(enemy.transform.position, out NavMeshHit finalHit, 1f, NavMesh.AllAreas) &&
                          finalHit.distance < 0.25f;
        Debug.Log($"[RampProbe] 坡道边缘站位：20s 后水平位移 {moved:F2}m，位置 {enemy.transform.position}，" +
                  $"回到网格上 = {backOnMesh}（吸附距离 {finalHit.distance:F3}m）");

        Assert.Greater(moved, 3f,
            $"坡道边缘（网格外）的敌人应该能重新取到路径并走开，实测只移动了 {moved:F2}m");
        Assert.IsTrue(backOnMesh,
            $"20s 后敌人应该已经回到导航网格上（否则就是又被边界滑动挡在侵蚀带里没出来）");
    }

    // 多站位游荡：坡道两侧边缘、坡脚、平台顶上的敌人都不能长时间钉住
    [UnityTest]
    public IEnumerator Wander_NearRampEdges_NoLongPin()
    {
        Random.InitState(20261002);   // 固定随机序列，A/B 两次跑的目标点完全一致

        Vector3[] spawns =
        {
            new Vector3(RampMidX, 1.95f, -5.0f),      // 坡道面上靠侧边缘
            new Vector3(RampMidX + 3f, 1.1f, -7f),    // 坡道中下段正中
            new Vector3(RampMidX, 0.05f, -3.9f),      // 坡道侧壁外的地板
            new Vector3(-7f, 3.45f, -7f),             // 平台顶上
        };

        Vector3[] starts = new Vector3[spawns.Length];
        Vector3[] anchors = new Vector3[spawns.Length];
        float[] anchorTime = new float[spawns.Length];
        float[] maxPin = new float[spawns.Length];
        float[] travelled = new float[spawns.Length];
        Vector3[] previous = new Vector3[spawns.Length];

        for (int i = 0; i < spawns.Length; i++)
        {
            SpawnEnemy(spawns[i]);
            starts[i] = spawns[i];
            anchors[i] = spawns[i];
            previous[i] = spawns[i];
        }

        yield return null;

        float elapsed = 0f;
        int frames = 0;

        while (elapsed < 45f && frames++ < 400000)
        {
            float delta = Time.deltaTime;
            elapsed += delta;

            for (int i = 0; i < _enemies.Count; i++)
            {
                Vector3 position = _enemies[i].transform.position;

                travelled[i] += Vector3.ProjectOnPlane(position - previous[i], Vector3.up).magnitude;
                previous[i] = position;

                // "钉住"＝在 0.35m 半径内磨：只要没离开锚点就一直累计，离开就重设锚点
                if (Vector3.ProjectOnPlane(position - anchors[i], Vector3.up).magnitude > 0.35f)
                {
                    anchors[i] = position;
                    anchorTime[i] = elapsed;
                }

                maxPin[i] = Mathf.Max(maxPin[i], elapsed - anchorTime[i]);
            }

            yield return null;
        }

        // 先全部打点再断言：失败时也要能看到每个站位的完整数据
        for (int i = 0; i < _enemies.Count; i++)
        {
            Debug.Log($"[RampProbe] 敌人{i} 起点{starts[i]} → 终点{_enemies[i].transform.position}，" +
                      $"总位移{travelled[i]:F1}m，最长钉住{maxPin[i]:F1}s");
        }

        for (int i = 0; i < _enemies.Count; i++)
        {
            Assert.Less(maxPin[i], 8f, $"敌人{i} 钉住 {maxPin[i]:F1}s（起点 {starts[i]}），超过阈值");
            Assert.Greater(travelled[i], 3f, $"敌人{i} 45s 内只走了 {travelled[i]:F1}m，基本没动");
        }
    }

    // —— 搭建 —— //

    private void BuildLevel()
    {
        // 尺寸全部取自场景 AaBb9331_PatrolTest 的世界坐标
        GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "ProbeGround";
        ground.transform.position = Vector3.zero;
        ground.transform.localScale = new Vector3(3f, 1f, 3f);   // Plane 10x10 → 30x30
        _level.Add(ground);

        GameObject platform = GameObject.CreatePrimitive(PrimitiveType.Cube);
        platform.name = "ProbePlatform";                          // Obstacle Type 2
        platform.transform.position = new Vector3(-7f, 0.855f, -7f);
        platform.transform.localScale = new Vector3(2f, 5f, 4.653f);
        _level.Add(platform);

        GameObject ramp = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ramp.name = "ProbeRamp";                                  // Obstacle Type 4
        ramp.transform.position = new Vector3(-2.947f, -1.325f, -7f);
        ramp.transform.localScale = new Vector3(5.626f, 9.6865f, 4.6492f);
        ramp.transform.rotation = Quaternion.Euler(0f, 0f, 63.369f);
        _level.Add(ramp);

        AddWall(new Vector3(15f, 2f, 0f), new Vector3(1f, 5f, 31f));
        AddWall(new Vector3(-15f, 2f, 0f), new Vector3(1f, 5f, 31f));
        AddWall(new Vector3(0f, 2f, 15f), new Vector3(31f, 5f, 1f));
        AddWall(new Vector3(0f, 2f, -15f), new Vector3(31f, 5f, 1f));

        // 两块悬空斜板：用来验证"代理坡度 60 能烘上 50° 面、烘不上 63° 面"（上下文包 §7.5）
        AddSlopeSlab(new Vector3(40f, 2f, 40f), 50f);
        AddSlopeSlab(new Vector3(40f, 2f, 50f), 63.37f);
    }

    private void AddSlopeSlab(Vector3 position, float slopeDegrees)
    {
        GameObject slab = GameObject.CreatePrimitive(PrimitiveType.Cube);
        slab.name = $"ProbeSlope{slopeDegrees:F0}";
        slab.transform.position = position;
        slab.transform.localScale = new Vector3(6f, 0.5f, 6f);
        slab.transform.rotation = Quaternion.Euler(0f, 0f, slopeDegrees);   // 绕 Z 轴转多少度，顶面就是多少度
        _level.Add(slab);
    }

    private void AddWall(Vector3 position, Vector3 scale)
    {
        GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.name = "ProbeWall";
        wall.transform.position = position;
        wall.transform.localScale = scale;
        _level.Add(wall);
    }

    // 与场景一致：Humanoid 代理（半径 0.5 / 高 2 / 台阶 0.75 / 体素 1/6），
    // 坡度取工程设置里的 60——上下文包 §7.5 要求 NavMesh Max Slope ≥ CharacterController.slopeLimit
    private void BuildNavMesh()
    {
        NavMeshBuildSettings settings = NavMesh.GetSettingsByID(0);
        settings.agentRadius = 0.5f;
        settings.agentHeight = 2f;
        settings.agentSlope = 60f;
        settings.agentClimb = 0.75f;
        settings.overrideVoxelSize = true;
        settings.voxelSize = 1f / 6f;

        Bounds bounds = new Bounds(Vector3.zero, new Vector3(80f, 40f, 80f));
        List<NavMeshBuildSource> sources = new List<NavMeshBuildSource>();
        NavMeshBuilder.CollectSources(bounds, ~0, NavMeshCollectGeometry.PhysicsColliders, 0,
            new List<NavMeshBuildMarkup>(), sources);

        NavMeshData data = NavMeshBuilder.BuildNavMeshData(settings, sources, bounds, Vector3.zero, Quaternion.identity);
        _navMeshInstance = NavMesh.AddNavMeshData(data);
    }

    private GameObject SpawnEnemy(Vector3 position)
    {
        GameObject enemy = new GameObject("ProbeEnemy");
        enemy.SetActive(false);

        CharacterController controller = enemy.AddComponent<CharacterController>();
        controller.height = 2f;
        controller.radius = 0.5f;
        controller.center = new Vector3(0f, 1f, 0f);
        controller.slopeLimit = 60f;    // 与场景一致：上下文包 §7.5 要求 Slope Limit 45→55~60、NavMesh Max Slope 跟上
        controller.stepOffset = 0.3f;
        controller.skinWidth = 0.08f;
        controller.minMoveDistance = 0.001f;

        EnemyNavWander wander = enemy.AddComponent<EnemyNavWander>();
        wander.Scope = WanderScope.WholeMap;
        wander.MinTravelDistance = 4f;
        wander.ArriveDistance = 0.6f;
        wander.WaitTime = 0.5f;        // 场景里是 4s，这里缩短以便观察移动
        wander.RepathInterval = 0.8f;
        wander.MoveSpeed = 3f;
        wander.TurnSpeed = 180f;
        wander.Gravity = 20f;
        wander.GroundProbe = 1.5f;
        wander.StuckTimeout = 1.5f;

        enemy.transform.position = position;
        enemy.SetActive(true);          // 触发 Awake（CharacterController 已就位）
        _enemies.Add(enemy);
        return enemy;
    }
}
