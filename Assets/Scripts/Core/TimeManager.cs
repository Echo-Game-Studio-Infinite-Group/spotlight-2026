using UnityEngine;

[DefaultExecutionOrder(-100)]
public sealed class TimeManager : MonoBehaviour
{
    private static TimeManager _instance;
    [Range(0f, 1f)] public float WorldScale = 1f;
    [Range(0f, 1f)] public float PlayerScale = 1f;
    private float _hitStopUntil;
    private float _hitStopScale = 1f;
    private float _playerTime;

    private float FreezeScale => Time.unscaledTime < _hitStopUntil ? _hitStopScale : 1f;
    public static float PlayerRate => _instance != null ? _instance.PlayerScale * _instance.FreezeScale : 1f;
    public static float PlayerDeltaTime => Time.unscaledDeltaTime * PlayerRate;
    public static float PlayerFixedDeltaTime => Time.fixedDeltaTime * PlayerRate;
    public static float WorldDeltaTime => Time.unscaledDeltaTime * (_instance != null ? _instance.WorldScale * _instance.FreezeScale : 1f);
    public static float UnscaledDeltaTime => Time.unscaledDeltaTime;
    public static float UnscaledTime => Time.unscaledTime;
    public static float PlayerTime => _instance != null ? _instance._playerTime : Time.time;
    public static bool InHitStop => _instance != null && Time.unscaledTime < _instance._hitStopUntil;

    private void Awake() => _instance = this;
    private void OnDestroy() { if (_instance == this) _instance = null; }
    private void FixedUpdate() => _playerTime += PlayerFixedDeltaTime;

    public static void HitStop(float seconds, float scale = 0.05f)
    {
        if (_instance == null) return;
        bool alreadyFrozen = InHitStop;
        _instance._hitStopUntil = Mathf.Max(_instance._hitStopUntil, Time.unscaledTime + Mathf.Max(0f, seconds));
        _instance._hitStopScale = alreadyFrozen ? Mathf.Min(_instance._hitStopScale, Mathf.Clamp01(scale)) : Mathf.Clamp01(scale);
    }
}
