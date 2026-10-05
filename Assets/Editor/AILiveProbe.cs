using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

// 编辑器实时探针（只读 + 显式指令）：Play 模式下当"黑匣子"用
//   snapshot.json —— 当前一帧的全量状态（10Hz 覆盖写，供实时观察）
//   history.json  —— 滚动历史（10Hz 采样、保留最近 30s；**精简字段 + 2 位小数**，5s 落盘 + 事件时立即落盘）
//   events        —— 关键转折（开始回网格 / 恢复寻路 / 掉出网格 / 真正停滞 / 状态机切换），带自动截图
//   cmd.json      —— 外部指令：report / clear / screenshot / pause / worldScale / teleport / wanderTo
// 为什么需要它：MCP Unity 只提供编辑器级能力，而且进 Play / 域重载会断桥；
//   "敌人在某片区域左右徘徊"这类问题只有逐帧历史看得出来（单看一帧永远是"正常走路"）
// 约束（AGENTS.md 目录约定）：放在 Assets/Editor 下，只进编辑器、不进玩家包
// 约束（上下文包 §5.3）：本类只读状态 + 执行显式指令，不参与游戏逻辑、不自己移动敌人
// 约束（血泪教训）：绝不修改场景、绝不保存任何资源
public static class AILiveProbe
{
    // 数据目录：编辑器与 AI 都能读写，且不落在工程仓库里
    private const string LiveDir = @"C:\Users\10093\Documents\deepseek-harness\default-workspace\unity-live";
    private const string SnapshotFile = "snapshot.json";
    private const string HistoryFile = "history.json";
    private const string CommandFile = "cmd.json";
    private const string EnabledKey = "AILiveProbe.Enabled";   // EditorPrefs 键：记住开关状态
    private static readonly object ScaleOwner = new object();   // 本探针在世界层登记缩放的来源标识（供 Release 用）

    private const float SampleInterval = 0.1f;        // 采样 10Hz：够看清徘徊，又不至于把盘写满
    private const float HistoryWriteInterval = 5f;    // 历史落盘 5s（事件时立即再落一次）
    private const int HistoryCapacity = 300;          // 10Hz × 30s
    private const int EventCapacity = 400;
    private const int MaxAutoShots = 8;               // 自动截图总量上限
    private const int MaxShotsPerKind = 2;            // 同一类事件最多截几张，免得被刷屏事件吃光预算
    private const float ShotCooldown = 3f;            // 两次自动截图的最小间隔
    private const float StalledSpeed = 0.25f;         // 低于这个速度算"没在动"
    private const float StalledSeconds = 1.5f;        // 连续这么久没动 = 一次停滞事件
    private const float OnMeshEpsilon = 0.35f;        // 与运行时 OffMeshEnterDistance 对齐：低于它的贴边抖动不算"掉出去"

    private static bool _enabled;
    private static float _nextSampleTime;
    private static float _nextHistoryWriteTime;
    private static DateTime _commandStamp = DateTime.MinValue;
    private static int _autoShots;
    private static uint _shotSeq;
    private static float _lastShotTime = -999f;

    private static readonly List<Sample> _samples = new List<Sample>(HistoryCapacity);
    private static readonly List<ProbeEvent> _events = new List<ProbeEvent>();
    private static readonly Dictionary<int, EnemyTrack> _tracks = new Dictionary<int, EnemyTrack>();
    private static readonly Dictionary<string, int> _shotsPerKind = new Dictionary<string, int>();

    // 快照用的全量字段（给人/AI 看当前状态）
    [Serializable]
    private class EnemySnapshot
    {
        public int id;                        // GetInstanceID：场景里有重名敌人，必须靠它区分
        public string name;
        public float x, y, z;
        public float speed;
        public bool grounded;
        public float navMeshDistance = -1f;   // -1 = 2m 内找不到网格
        public float goalX, goalZ;            // 当前目标点：看"目标是不是在左右翻"最直接
        public bool hasPath;
        public int cornerIndex;
        public int cornerCount;
        public float stuckTimer;
        public float bestRemaining;
        public bool returningToMesh;
        public bool holding;
        public string brainState = "";
        public float distanceToPlayer = -1f;
    }

    // 历史里只留排障必需的字段并压到 2 位小数：22 只敌人 × 30 秒的原始 JSON 有十几 MB，读写都受不了
    [Serializable]
    private class CompactEnemy
    {
        public int id;
        public string name;
        public float x, y, z;
        public float speed;
        public float navMeshDistance;
        public float goalX, goalZ;
        public bool hasPath;
        public int cornerIndex;
        public int cornerCount;
        public float stuckTimer;
        public bool returningToMesh;
        public bool holding;
    }

    [Serializable]
    private class Sample
    {
        public float t;
        public int frame;
        public List<CompactEnemy> enemies = new List<CompactEnemy>();
    }

    [Serializable]
    private class ProbeEvent
    {
        public float t;
        public string kind;      // returnToMesh / backOnMesh / leftMesh / stuckEscape / stalled / brainState
        public int id;
        public string enemy;
        public string detail;
        public string shot = ""; // 自动截图（相对 LiveDir）
    }

    [Serializable]
    private class Snapshot
    {
        public bool playing;
        public int frame;
        public float time;
        public string scene;
        public int autoShotsLeft;
        public int historySamples;
        public List<EnemySnapshot> enemies = new List<EnemySnapshot>();
    }

    [Serializable]
    private class History
    {
        public bool playing;
        public float time;
        public string scene;
        public int keptSamples;
        public int eventCount;
        public List<Sample> samples = new List<Sample>();
        public List<ProbeEvent> events = new List<ProbeEvent>();
    }

    // 命令是扁平结构：JsonUtility 不支持嵌套多态，扁平最省事，加字段也不破坏旧命令文件
    [Serializable]
    private class Command
    {
        public string op = "none";   // report / clear / screenshot / pause / worldScale / teleport / wanderTo / none
        public string enemy = "";    // 敌人名字或 InstanceID（重名时用 ID）
        public float x, y, z;
        public float value;          // pause: 0/1；worldScale: 缩放
        public string path = "";     // screenshot 文件名（相对 LiveDir）
    }

    private class EnemyTrack
    {
        public bool returning;
        public bool hasPath;
        public string brainState = "";
        public bool onMesh = true;
        public float stallTimer;
        public bool stallReported;
    }

    [InitializeOnLoadMethod]
    private static void Register()
    {
        // 开关记在 EditorPrefs 里：编译 / 进出 Play 都会重载域，静态字段会被清空，
        // 不记住的话每编译一次探针就自己关了（排查时最烦"以为在录其实没录"）
        _enabled = EditorPrefs.GetBool(EnabledKey, false);

        EditorApplication.update -= Tick;
        EditorApplication.update += Tick;
    }

    [MenuItem("超高速行者/AI 实时探针 开/关")]
    private static void Toggle()
    {
        _enabled = !_enabled;
        EditorPrefs.SetBool(EnabledKey, _enabled);
        if (_enabled) ClearHistory();

        Debug.Log($"[AILiveProbe] {(_enabled ? "已开启" : "已关闭")}；数据目录：{LiveDir}");
        if (_enabled) WriteSnapshot();
    }

    [MenuItem("超高速行者/AI 实时探针/清空历史")]
    private static void ClearHistory()
    {
        _samples.Clear();
        _events.Clear();
        _tracks.Clear();
        _shotsPerKind.Clear();
        _autoShots = 0;
    }

    [MenuItem("超高速行者/AI 实时探针/打开数据目录")]
    private static void OpenDir()
    {
        Directory.CreateDirectory(LiveDir);
        EditorUtility.RevealInFinder(LiveDir);
    }

    // 批处理自检：不开 Play 也能验证写盘路径与 JSON 序列化
    public static void SelfTest()
    {
        WriteSnapshot();
        WriteHistory();
        Debug.Log($"[AILiveProbe] SelfTest 写出：{Path.Combine(LiveDir, SnapshotFile)} / {HistoryFile}");
    }

    private static void Tick()
    {
        if (!_enabled) return;

        Application.runInBackground = true;   // 编辑器失焦时游戏循环也得继续，否则快照会停在原地
        HandleCommand();

        if (!Application.isPlaying) return;
        if (EditorApplication.timeSinceStartup < _nextSampleTime) return;
        _nextSampleTime = (float)EditorApplication.timeSinceStartup + SampleInterval;

        List<EnemySnapshot> enemies = CollectEnemies();
        DetectEvents(enemies);

        _samples.Add(new Sample { t = Round(Time.time), frame = Time.frameCount, enemies = Compact(enemies) });
        while (_samples.Count > HistoryCapacity) _samples.RemoveAt(0);

        WriteSnapshot(enemies);

        if (EditorApplication.timeSinceStartup >= _nextHistoryWriteTime)
        {
            _nextHistoryWriteTime = (float)EditorApplication.timeSinceStartup + HistoryWriteInterval;
            WriteHistory();
        }
    }

    // —— 采样 —— //

    private static List<EnemySnapshot> CollectEnemies()
    {
        List<EnemySnapshot> list = new List<EnemySnapshot>();
        Transform player = FindPlayer();

        foreach (EnemyNavWander wanderer in UnityEngine.Object.FindObjectsOfType<EnemyNavWander>())
        {
            if (wanderer == null) continue;   // Unity 伪 null：判空有效，访问成员会抛异常

            Transform t = wanderer.transform;
            CharacterController controller = wanderer.GetComponent<CharacterController>();
            Vector3 goal = wanderer.CurrentGoal;

            EnemySnapshot item = new EnemySnapshot
            {
                id = wanderer.GetInstanceID(),
                name = t.name,
                x = t.position.x,
                y = t.position.y,
                z = t.position.z,
                speed = controller != null ? controller.velocity.magnitude : 0f,
                grounded = controller != null && controller.isGrounded,
                goalX = goal.x,
                goalZ = goal.z,
                hasPath = wanderer.HasPath,
                cornerIndex = wanderer.CornerIndex,
                cornerCount = wanderer.CornerCount,
                stuckTimer = wanderer.StuckTimer,
                bestRemaining = wanderer.BestRemaining,
                returningToMesh = wanderer.ReturningToMesh,
                holding = wanderer.HoldingPosition,
            };

            if (NavMesh.SamplePosition(t.position, out NavMeshHit hit, 2f, NavMesh.AllAreas))
            {
                Vector3 offset = hit.position - t.position;
                offset.y = 0f;
                item.navMeshDistance = offset.magnitude;
            }

            MonoBehaviour brain = FindBrain(t);
            if (brain != null)
            {
                var property = brain.GetType().GetProperty("State");
                if (property != null) item.brainState = property.GetValue(brain, null)?.ToString() ?? "";
            }

            if (player != null) item.distanceToPlayer = Vector3.Distance(t.position, player.position);

            list.Add(item);
        }

        return list;
    }

    private static List<CompactEnemy> Compact(List<EnemySnapshot> enemies)
    {
        List<CompactEnemy> compact = new List<CompactEnemy>(enemies.Count);
        foreach (EnemySnapshot e in enemies)
        {
            compact.Add(new CompactEnemy
            {
                id = e.id,
                name = e.name,
                x = Round(e.x), y = Round(e.y), z = Round(e.z),
                speed = Round(e.speed),
                navMeshDistance = Round(e.navMeshDistance),
                goalX = Round(e.goalX), goalZ = Round(e.goalZ),
                hasPath = e.hasPath,
                cornerIndex = e.cornerIndex,
                cornerCount = e.cornerCount,
                stuckTimer = Round(e.stuckTimer),
                returningToMesh = e.returningToMesh,
                holding = e.holding,
            });
        }

        return compact;
    }

    private static float Round(float value)
    {
        return Mathf.Round(value * 100f) / 100f;
    }

    // 只在"状态转折"时记事件：这些才是排查徘徊/卡住的关键点
    // 关键：按 InstanceID 跟踪，不能按名字——场景里"Enemy (2)"有 4 个，按名字会把它们串成一条乱跳的时间线
    private static void DetectEvents(List<EnemySnapshot> enemies)
    {
        foreach (EnemySnapshot e in enemies)
        {
            if (!_tracks.TryGetValue(e.id, out EnemyTrack track))
            {
                track = new EnemyTrack();
                _tracks[e.id] = track;
            }

            bool onMesh = e.navMeshDistance >= 0f && e.navMeshDistance <= OnMeshEpsilon;

            if (e.returningToMesh && !track.returning)
                AddEvent("returnToMesh", e, $"离网格 {e.navMeshDistance:F2}m，开始往回走", true);
            if (!e.returningToMesh && track.returning)
                AddEvent("backOnMesh", e, $"离网格 {e.navMeshDistance:F2}m，恢复寻路");
            if (!onMesh && track.onMesh)
                AddEvent("leftMesh", e, $"离网格 {e.navMeshDistance:F2}m，掉出可走区");
            if (e.brainState != track.brainState && e.brainState.Length > 0)
                AddEvent("brainState", e, $"{track.brainState} → {e.brainState}");

            // 停滞判定必须要求"确实在赶路"（hasPath）：到达终点后的 WaitTime 是设计内的等待，
            // 旧版把它也报成停滞，结果开局 4 秒就把 8 张自动截图预算烧光了
            if (e.speed < StalledSpeed && onMesh && e.hasPath)
            {
                track.stallTimer += SampleInterval;
                if (track.stallTimer >= StalledSeconds && !track.stallReported)
                {
                    track.stallReported = true;
                    AddEvent("stalled", e,
                        $"位置 ({e.x:F2},{e.z:F2}) 停滞 {track.stallTimer:F1}s；目标 ({e.goalX:F2},{e.goalZ:F2})；" +
                        $"拐点 {e.cornerIndex}/{e.cornerCount}；卡住计时 {e.stuckTimer:F2}s", true);
                }
            }
            else
            {
                track.stallTimer = 0f;
                track.stallReported = false;
            }

            track.returning = e.returningToMesh;
            track.hasPath = e.hasPath;
            track.onMesh = onMesh;
            track.brainState = e.brainState;
        }
    }

    private static void AddEvent(string kind, EnemySnapshot enemy, string detail, bool wantShot = false)
    {
        ProbeEvent evt = new ProbeEvent
        {
            t = Round(Time.time),
            kind = kind,
            id = enemy.id,
            enemy = enemy.name,
            detail = detail,
        };

        if (wantShot && CanTakeShot(kind))
        {
            evt.shot = NextShotName();
            ScreenCapture.CaptureScreenshot(FullPath(evt.shot));
        }

        _events.Add(evt);
        while (_events.Count > EventCapacity) _events.RemoveAt(0);

        Debug.Log($"[AILiveProbe] 事件 {kind} {enemy.name}#{enemy.id}：{detail}" +
                  (evt.shot.Length > 0 ? $"（截图 {evt.shot}）" : ""));

        WriteHistory();   // 事件时立即落盘，别让黑匣子丢关键帧
    }

    // 截图预算：同类事件最多 MaxShotsPerKind 张，且两次之间至少隔 ShotCooldown 秒
    private static bool CanTakeShot(string kind)
    {
        if (_autoShots >= MaxAutoShots) return false;
        if (Time.time - _lastShotTime < ShotCooldown) return false;

        _shotsPerKind.TryGetValue(kind, out int used);
        if (used >= MaxShotsPerKind) return false;

        _shotsPerKind[kind] = used + 1;
        _autoShots++;
        _lastShotTime = Time.time;
        return true;
    }

    private static string NextShotName()
    {
        Directory.CreateDirectory(Path.Combine(LiveDir, "shots"));
        return Path.Combine("shots", $"shot-{++_shotSeq:D2}-{DateTime.Now:HHmmss}.png");
    }

    // 统一走 GetFullPath：它会把 '/' 归一成 '\' 并补全绝对路径。
    // 之前手动截图用 "shots/xxx.png" 直接拼给 ScreenCapture，文件根本没落盘（自动截图用的是 '\' 所以正常）
    private static string FullPath(string relative)
    {
        return Path.GetFullPath(Path.Combine(LiveDir, relative));
    }

    // —— 落盘 —— //

    private static void WriteSnapshot(List<EnemySnapshot> enemies = null)
    {
        Snapshot snapshot = new Snapshot
        {
            playing = Application.isPlaying,
            frame = Time.frameCount,
            time = Round(Time.time),
            scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
            autoShotsLeft = Mathf.Max(0, MaxAutoShots - _autoShots),
            historySamples = _samples.Count,
            enemies = enemies ?? CollectEnemies(),
        };

        WriteAtomic(Path.Combine(LiveDir, SnapshotFile), JsonUtility.ToJson(snapshot, true));
    }

    private static void WriteHistory()
    {
        History history = new History
        {
            playing = Application.isPlaying,
            time = Round(Time.time),
            scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
            keptSamples = _samples.Count,
            eventCount = _events.Count,
            samples = _samples,
            events = _events,
        };

        WriteAtomic(Path.Combine(LiveDir, HistoryFile), JsonUtility.ToJson(history));
    }

    // 先写临时文件再替换：AI 侧是轮询读取，不能让它读到写了一半的 JSON
    private static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temp = path + ".tmp";
        File.WriteAllText(temp, content);

        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);
    }

    // —— 命令 —— //

    private static void HandleCommand()
    {
        string path = Path.Combine(LiveDir, CommandFile);
        if (!File.Exists(path)) return;

        DateTime stamp = File.GetLastWriteTimeUtc(path);
        if (stamp == _commandStamp) return;   // 同一条命令只执行一次
        _commandStamp = stamp;

        Command command;
        try
        {
            command = JsonUtility.FromJson<Command>(File.ReadAllText(path));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[AILiveProbe] 命令文件解析失败：{e.Message}");
            return;
        }

        if (command == null || command.op == "none") return;

        switch (command.op)
        {
            case "report":
                WriteSnapshot();
                WriteHistory();
                Debug.Log("[AILiveProbe] 已按命令写快照与历史");
                break;

            case "clear":
                ClearHistory();
                Debug.Log("[AILiveProbe] 历史已清空");
                break;

            case "screenshot":
                string file = string.IsNullOrEmpty(command.path) ? NextShotName() : command.path;
                string full = FullPath(file);
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                ScreenCapture.CaptureScreenshot(full);
                Debug.Log($"[AILiveProbe] 已请求截图：{full}（帧末写入）");
                break;

            case "pause":
                EditorApplication.isPaused = command.value > 0.5f;
                Debug.Log($"[AILiveProbe] pause = {EditorApplication.isPaused}");
                break;

            case "worldScale":
                // 走 TimeManager 而不是散写 Time.timeScale（上下文包 §2.2）
                // 注意：TimeManager 的 API 是"按来源 Apply / Release"，没有 WorldScale 属性
                //（旧版那种 manager.WorldScale 字段在并入 dev 后已不存在）
                TimeManager.Release(TimeManager.TimeLayer.World, ScaleOwner);
                if (command.value > 0.001f && command.value < 0.999f)
                {
                    TimeManager.Apply(TimeManager.TimeLayer.World, Mathf.Clamp01(command.value), 0f, ScaleOwner);
                }
                Debug.Log($"[AILiveProbe] 世界层速率 = {TimeManager.WorldRate:F2}（1.00 = 正常）");
                break;

            case "teleport":
            case "wanderTo":
                if (!TryFindWanderer(command.enemy, out EnemyNavWander target)) break;

                Vector3 point = new Vector3(command.x, command.y, command.z);
                if (command.op == "teleport")
                {
                    CharacterController controller = target.GetComponent<CharacterController>();
                    if (controller != null) controller.enabled = false;   // 直接改位置会被胶囊碰撞解算拉回去
                    target.transform.position = point;
                    if (controller != null) controller.enabled = true;
                    Debug.Log($"[AILiveProbe] {target.name}#{target.GetInstanceID()} 瞬移到 {point}");
                }
                else
                {
                    target.MoveTo(point);
                    Debug.Log($"[AILiveProbe] {target.name}#{target.GetInstanceID()} 收到 MoveTo {point}");
                }
                break;

            default:
                Debug.LogWarning($"[AILiveProbe] 未知命令：{command.op}");
                break;
        }

        WriteAtomic(path, JsonUtility.ToJson(new Command(), true));
    }

    // 按 InstanceID（纯数字）或名字找敌人；重名时明确报错并列出候选，不瞎猜第一个
    private static bool TryFindWanderer(string key, out EnemyNavWander found)
    {
        found = null;
        EnemyNavWander[] all = UnityEngine.Object.FindObjectsOfType<EnemyNavWander>();
        if (all.Length == 0) { Debug.LogWarning("[AILiveProbe] 场景里没有 EnemyNavWander"); return false; }

        if (string.IsNullOrEmpty(key))
        {
            found = all[0];
            return true;
        }

        if (int.TryParse(key, out int id))
        {
            foreach (EnemyNavWander candidate in all)
            {
                if (candidate != null && candidate.GetInstanceID() == id) { found = candidate; return true; }
            }

            Debug.LogWarning($"[AILiveProbe] 没有 InstanceID = {id} 的敌人");
            return false;
        }

        int matches = 0;
        foreach (EnemyNavWander candidate in all)
        {
            if (candidate == null || candidate.name != key) continue;
            matches++;
            if (found == null) found = candidate;
        }

        if (matches == 1) return true;

        if (matches > 1)
        {
            string list = "";
            foreach (EnemyNavWander candidate in all)
            {
                if (candidate != null && candidate.name == key) list += $" {candidate.GetInstanceID()}";
            }

            Debug.LogWarning($"[AILiveProbe] 「{key}」有 {matches} 个同名敌人，请用 InstanceID 指定：{list}");
            found = null;
            return false;
        }

        Debug.LogWarning($"[AILiveProbe] 找不到敌人：{key}");
        return false;
    }

    private static Transform FindPlayer()
    {
        PlayerMotor motor = UnityEngine.Object.FindObjectOfType<PlayerMotor>();
        return motor != null ? motor.transform : null;
    }

    private static MonoBehaviour FindBrain(Transform enemy)
    {
        foreach (MonoBehaviour behaviour in enemy.GetComponents<MonoBehaviour>())
        {
            if (behaviour != null && behaviour.GetType().Name == "EnemyBrain") return behaviour;
        }

        return null;
    }
}
