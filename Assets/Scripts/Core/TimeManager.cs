using UnityEngine;

// 分层时间：worldScale（时停/时缓）× playerScale（时停中玩家仍可行动）× hitstop（帧冻结）
// 约束（AGENTS.md 第 5 条）：游戏逻辑只读本类提供的缩放时间；UI/相机走 unscaled；禁止散写 Time.timeScale
[DefaultExecutionOrder(-100)]
public class TimeManager : MonoBehaviour
{
    private static TimeManager _instance;

    [Range(0f, 1f)] public float WorldScale = 1f;
    [Range(0f, 1f)] public float PlayerScale = 1f;

    private float _worldDeltaTime;
    private float _playerDeltaTime;
    private float _playerTime;
    private float _hitStopTimer;
    private float _hitStopScale = 1f;

    // 无实例时退化为未缩放时间，保证骨架脚本在无 TimeManager 的场景中也能运行
    public static float WorldDeltaTime => _instance != null ? _instance._worldDeltaTime : Time.deltaTime;
    public static float PlayerDeltaTime => _instance != null ? _instance._playerDeltaTime : Time.deltaTime;
    public static float UnscaledDeltaTime => Time.deltaTime;
    public static float UnscaledTime => Time.unscaledTime;
    public static float PlayerTime => _instance != null ? _instance._playerTime : Time.time;
    public static bool InHitStop => _instance != null && _instance._hitStopTimer > 0f;

    private void Awake()
    {
        // 单场景单实例；跨场景复用由后续 Bootstrap 负责，骨架不做 DontDestroyOnLoad 迁移
        _instance = this;
    }

    private void OnDestroy()
    {
        if (_instance == this) _instance = null;
    }

    private void Update()
    {
        float unscaled = Time.deltaTime;

        if (_hitStopTimer > 0f)
        {
            _hitStopTimer -= unscaled; // hitstop 时长走真实时间轴，不随缩放自锁
            if (_hitStopTimer <= 0f)
            {
                _hitStopTimer = 0f;
                _hitStopScale = 1f;
            }
        }

        float freeze = _hitStopTimer > 0f ? _hitStopScale : 1f;
        _worldDeltaTime = unscaled * WorldScale * freeze;
        _playerDeltaTime = unscaled * PlayerScale * freeze;
        _playerTime += _playerDeltaTime;
    }

    // 打击帧冻结：seconds 内 world/player 时间同步降到 scale；重复调用取更长冻结与更小 scale
    public static void HitStop(float seconds, float scale = 0.05f)
    {
        if (_instance == null)
        {
            Debug.LogWarning("[TimeManager] 场景中无 TimeManager，HitStop 未生效");
            return;
        }
        if (seconds > _instance._hitStopTimer)
        {
            _instance._hitStopTimer = seconds;
            _instance._hitStopScale = scale;
        }
        else if (_instance._hitStopTimer > 0f)
        {
            _instance._hitStopScale = Mathf.Min(_instance._hitStopScale, scale);
        }
    }
}
