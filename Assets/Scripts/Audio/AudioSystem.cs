using System.Collections.Generic;
using UnityEngine;

[DefaultExecutionOrder(-40)]
[DisallowMultipleComponent]
public sealed class AudioSystem : MonoBehaviour
{
    [SerializeField, Range(4, 32)] private int _sfxPoolSize = 20;
    [SerializeField, Range(1, 8)] private int _actionPoolSize = 4;
    [SerializeField, Min(0f)] private float _musicFade = 1.2f;
    [SerializeField] private bool _playMusicOnConfigure = true;

    private static AudioSystem _instance;
    private readonly List<AudioSfxVoice> _sfxPool = new List<AudioSfxVoice>();
    private readonly List<ActionAudioVoice> _actionPool = new List<ActionAudioVoice>();
    private readonly List<AudioActionHandle> _activeActions = new List<AudioActionHandle>();
    private readonly List<AudioActionHandle> _actionRemoveBuffer = new List<AudioActionHandle>();
    private GameAudioCatalog _catalog;
    private PlayerAudioProfile _profile;
    private MusicDirector _music;
    private AudioDebugOverlay _debugOverlay;
    private AudioSource _windSource;
    private AudioLowPassFilter _windLowpass;
    private PlayerInputReader _boundInput;
    private PlayerAudioDriver _driver;
    private float _playerScanTimer;
    private float _speedRatio;
    private System.Random _random;

    public static AudioSystem Instance => _instance;
    public GameAudioCatalog Catalog => _catalog;
    public PlayerAudioProfile Profile => _profile;
    public int ActiveActionCount => _activeActions.Count;
    public int ActiveSfxCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _sfxPool.Count; i++)
                if (_sfxPool[i].IsPlaying) count++;
            return count;
        }
    }

    public float SpeedRatio => _speedRatio;
    public AudioClip CurrentMusic => _music != null ? _music.CurrentClip : null;
    public PlayerAudioDriver Driver => _driver;
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
        BuildPools();
        BuildMusic();
        BuildWindLayer();
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
        TickSfx(dt);
        FindPlayer();
        UpdateWindLayer();
    }

    public void Configure(GameAudioCatalog catalog, PlayerAudioProfile profile)
    {
        _catalog = catalog;
        _profile = profile;
        if (_windSource != null)
        {
            AudioClip speedLayer = profile != null ? profile.SpeedLayer : null;
            if (speedLayer == null && catalog != null) speedLayer = catalog.SpeedLayer;
            _windSource.clip = speedLayer;
            if (_windSource.clip != null && !_windSource.isPlaying) _windSource.Play();
        }
        if (_music != null && _playMusicOnConfigure)
        {
            AudioClip music = profile != null ? profile.Music : null;
            if (music == null && catalog != null) music = catalog.Music;
            if (music != null) _music.Play(music, _musicFade);
        }
    }

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

    public void PlaySfx(AudioCueDefinition cue, Vector3 position, float volumeDbOffset = 0f)
    {
        PlaySfx(cue, position, null, volumeDbOffset, false, 0f, 40f, 8f);
    }

    public void PlaySfx(AudioCueDefinition cue, Transform follow, float volumeDbOffset = 0f)
    {
        PlaySfx(cue, follow != null ? follow.position : Vector3.zero, follow, volumeDbOffset, false, 0f, 40f, 8f);
    }

    public void TriggerGlitch(Vector3 position, float intensity = 0.8f, float duration = 0.09f)
    {
        AudioCueDefinition cue = _profile != null ? _profile.Glitch : null;
        if (cue == null && _catalog != null) cue = _catalog.FindCue("glitch");
        if (cue == null) return;
        PlaySfx(cue, position, null, -4f, true, duration,
            Mathf.Lerp(28f, 68f, intensity), Mathf.Lerp(7f, 4f, intensity));
    }

    public void PlayMusic(AudioClip clip, float fadeSeconds = -1f)
    {
        if (_music != null) _music.Play(clip, fadeSeconds >= 0f ? fadeSeconds : _musicFade);
    }

    public void StopMusic(float fadeSeconds = -1f)
    {
        if (_music != null) _music.Stop(fadeSeconds >= 0f ? fadeSeconds : _musicFade);
    }

    public void SetSpeedRatio(float ratio)
    {
        _speedRatio = Mathf.Max(0f, ratio);
    }

    public void ToggleDebug()
    {
        if (_debugOverlay != null) _debugOverlay.Visible = !_debugOverlay.Visible;
    }

    private void PlaySfx(
        AudioCueDefinition cue,
        Vector3 position,
        Transform follow,
        float volumeDbOffset,
        bool glitch,
        float glitchSeconds,
        float stutterRate,
        float crushBits)
    {
        if (cue == null || !cue.HasClips) return;
        AudioClip clip = cue.PickClip(_random);
        if (clip == null) return;
        AudioSfxVoice voice = AcquireSfxVoice();
        if (voice == null) return;
        float volumeDb = cue.GainDbRange.Value(_random) + volumeDbOffset;
        float pitch = AudioCurveUtility.SemitoneToRatio(cue.PitchSemitoneRange.Value(_random));
        voice.Play(clip, position, follow, volumeDb, pitch, cue.SpatialBlend,
            cue.MinDistance, cue.MaxDistance, cue.Priority, cue.LowpassHz, cue.HighpassHz,
            glitch, glitchSeconds, stutterRate, crushBits);
    }

    private void TickActions(float deltaTime)
    {
        _actionRemoveBuffer.Clear();
        for (int i = 0; i < _activeActions.Count; i++)
            if (!_activeActions[i].Tick(deltaTime)) _actionRemoveBuffer.Add(_activeActions[i]);
        for (int i = 0; i < _actionRemoveBuffer.Count; i++)
            _activeActions.Remove(_actionRemoveBuffer[i]);
    }

    private void TickSfx(float deltaTime)
    {
        for (int i = 0; i < _sfxPool.Count; i++) _sfxPool[i].Tick(deltaTime);
    }

    private void FindPlayer()
    {
        _playerScanTimer -= TimeManager.UnscaledDeltaTime;
        if (_playerScanTimer > 0f) return;
        _playerScanTimer = 0.5f;
        if (_driver != null) return;
        PlayerMotor motor = FindObjectOfType<PlayerMotor>();
        if (motor == null) return;
        _driver = motor.gameObject.GetComponent<PlayerAudioDriver>();
        if (_driver == null) _driver = motor.gameObject.AddComponent<PlayerAudioDriver>();
        PlayerInputReader input = motor.GetComponent<PlayerInputReader>();
        if (input != null && input != _boundInput)
        {
            if (_boundInput != null) _boundInput.ToggleHUD -= ToggleDebug;
            _boundInput = input;
            _boundInput.ToggleHUD += ToggleDebug;
        }
    }

    private void BuildPools()
    {
        for (int i = 0; i < _sfxPoolSize; i++)
        {
            GameObject go = new GameObject("SfxVoice_" + i);
            go.transform.SetParent(transform, false);
            AudioSfxVoice voice = go.AddComponent<AudioSfxVoice>();
            voice.Initialize();
            _sfxPool.Add(voice);
        }
        for (int i = 0; i < _actionPoolSize; i++)
        {
            GameObject go = new GameObject("ActionVoice_" + i);
            go.transform.SetParent(transform, false);
            ActionAudioVoice voice = go.AddComponent<ActionAudioVoice>();
            go.SetActive(false);
            _actionPool.Add(voice);
        }
    }

    private void BuildMusic()
    {
        GameObject go = new GameObject("MusicDirector");
        go.transform.SetParent(transform, false);
        _music = go.AddComponent<MusicDirector>();
        _music.Initialize();
    }

    private void BuildWindLayer()
    {
        GameObject go = new GameObject("SpeedLayer");
        go.transform.SetParent(transform, false);
        _windSource = go.AddComponent<AudioSource>();
        _windSource.playOnAwake = false;
        _windSource.loop = true;
        _windSource.spatialBlend = 0f;
        _windSource.volume = 0f;
        _windLowpass = go.AddComponent<AudioLowPassFilter>();
        _windLowpass.cutoffFrequency = 22000f;
    }

    private void UpdateWindLayer()
    {
        if (_windSource == null || _windSource.clip == null) return;
        float t = Mathf.Clamp01((_speedRatio - 0.8f) / 2.2f);
        _windSource.volume = AudioCurveUtility.DbToLinear(Mathf.Lerp(-80f, -12f, t));
        _windSource.pitch = Mathf.Lerp(0.92f, 1.18f, t);
        _windLowpass.cutoffFrequency = AudioCurveUtility.LerpFrequency(16000f, 4200f, t);
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

    private AudioSfxVoice AcquireSfxVoice()
    {
        for (int i = 0; i < _sfxPool.Count; i++)
            if (!_sfxPool[i].IsPlaying) return _sfxPool[i];
        return null;
    }

    private int NextSeed()
    {
        return _random != null ? _random.Next(int.MinValue, int.MaxValue) : Random.Range(int.MinValue, int.MaxValue);
    }
}
