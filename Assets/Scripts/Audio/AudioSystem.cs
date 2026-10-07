using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 只负责"动态连续动作音效"的运行期容器：动作声源池 + 播放句柄 + 驱动自动挂载。
///
/// 一次性音效、脚步、战斗音效、主题曲、风声、故障效果已移除，后续交给 Wwise 实现。
/// </summary>
[DefaultExecutionOrder(-40)]
[DisallowMultipleComponent]
public sealed class AudioSystem : MonoBehaviour
{
    [SerializeField, Range(1, 8)] private int _actionPoolSize = 4;

    private static AudioSystem _instance;
    private readonly List<ActionAudioVoice> _actionPool = new List<ActionAudioVoice>();
    private readonly List<AudioActionHandle> _activeActions = new List<AudioActionHandle>();
    private readonly List<AudioActionHandle> _actionRemoveBuffer = new List<AudioActionHandle>();
    private AudioDebugOverlay _debugOverlay;
    private PlayerInputReader _boundInput;
    private float _playerScanTimer;
    private bool _playerBound;
    private System.Random _random;

    public static AudioSystem Instance => _instance;
    public int ActiveActionCount => _activeActions.Count;
    public IReadOnlyList<ActionAudioVoice> ActionVoices => _actionPool;

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }
        _instance = this;
        DontDestroyOnLoad(gameObject);
        _random = new System.Random(System.Environment.TickCount);
        BuildActionPool();
        _debugOverlay = gameObject.AddComponent<AudioDebugOverlay>();
    }

    private void OnDestroy()
    {
        if (_instance == this) _instance = null;
        if (_boundInput != null) _boundInput.ToggleHUD -= ToggleDebug;
    }

    private void Update()
    {
        float dt = TimeManager.UnscaledDeltaTime;
        TickActions(dt);
        FindPlayer();
    }

    /// <summary>开始一个连续动作音效；返回句柄用于持续喂状态和松手。</summary>
    public AudioActionHandle PlayAction(AudioActionDefinition definition, Transform follow, string surfaceId)
    {
        if (definition == null || definition.Clip == null) return null;
        ActionAudioVoice voice = AcquireActionVoice();
        if (voice == null) return null;
        int seed = NextSeed();
        voice.Initialize(definition, follow, surfaceId, seed);
        voice.Begin();
        AudioActionHandle handle = new AudioActionHandle(this, voice);
        _activeActions.Add(handle);
        return handle;
    }

    public void ToggleDebug()
    {
        if (_debugOverlay != null) _debugOverlay.Visible = !_debugOverlay.Visible;
    }

    private void TickActions(float deltaTime)
    {
        _actionRemoveBuffer.Clear();
        for (int i = 0; i < _activeActions.Count; i++)
            if (!_activeActions[i].Tick(deltaTime)) _actionRemoveBuffer.Add(_activeActions[i]);
        for (int i = 0; i < _actionRemoveBuffer.Count; i++)
            _activeActions.Remove(_actionRemoveBuffer[i]);
    }

    private void FindPlayer()
    {
        _playerScanTimer -= TimeManager.UnscaledDeltaTime;
        if (_playerScanTimer > 0f) return;
        _playerScanTimer = 0.5f;
        if (_playerBound) return;
        PlayerMotor motor = FindObjectOfType<PlayerMotor>();
        if (motor == null) return;
        if (motor.gameObject.GetComponent<WwiseActionDriver>() == null)
            motor.gameObject.AddComponent<WwiseActionDriver>();
        _playerBound = true;
        PlayerInputReader input = motor.GetComponent<PlayerInputReader>();
        if (input != null && input != _boundInput)
        {
            if (_boundInput != null) _boundInput.ToggleHUD -= ToggleDebug;
            _boundInput = input;
            _boundInput.ToggleHUD += ToggleDebug;
        }
    }

    private void BuildActionPool()
    {
        for (int i = 0; i < _actionPoolSize; i++)
        {
            GameObject go = new GameObject("ActionVoice_" + i);
            go.transform.SetParent(transform, false);
            ActionAudioVoice voice = go.AddComponent<ActionAudioVoice>();
            go.SetActive(false);
            _actionPool.Add(voice);
        }
    }

    private ActionAudioVoice AcquireActionVoice()
    {
        for (int i = 0; i < _actionPool.Count; i++)
        {
            if (_actionPool[i].IsActive) continue;
            _actionPool[i].gameObject.SetActive(true);
            return _actionPool[i];
        }
        return null;
    }

    internal void ReleaseActionVoice(ActionAudioVoice voice)
    {
        if (voice == null) return;
        voice.Finish();
        voice.gameObject.SetActive(false);
    }

    private int NextSeed()
    {
        return _random != null ? _random.Next(int.MinValue, int.MaxValue) : Random.Range(int.MinValue, int.MaxValue);
    }
}
