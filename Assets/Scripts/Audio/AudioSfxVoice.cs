using UnityEngine;

public sealed class AudioSfxVoice : MonoBehaviour
{
    private AudioSource _source;
    private AudioLowPassFilter _lowpass;
    private AudioHighPassFilter _highpass;
    private AudioEchoFilter _echo;
    private AudioDistortionFilter _distortion;
    private StutterGateFilter _stutter;
    private BitcrushFilter _bitcrush;
    private Transform _follow;
    private float _glitchRemaining;

    public bool IsPlaying => _source != null && _source.isPlaying;

    public void Initialize()
    {
        if (_source != null) return;
        _source = gameObject.AddComponent<AudioSource>();
        _source.playOnAwake = false;
        _source.loop = false;
        _source.dopplerLevel = 0f;
        _source.rolloffMode = AudioRolloffMode.Linear;
        _lowpass = gameObject.AddComponent<AudioLowPassFilter>();
        _highpass = gameObject.AddComponent<AudioHighPassFilter>();
        _echo = gameObject.AddComponent<AudioEchoFilter>();
        _echo.enabled = false;
        _distortion = gameObject.AddComponent<AudioDistortionFilter>();
        _distortion.enabled = false;
        _stutter = gameObject.AddComponent<StutterGateFilter>();
        _bitcrush = gameObject.AddComponent<BitcrushFilter>();
    }

    public void Play(
        AudioClip clip,
        Vector3 position,
        Transform follow,
        float volumeDb,
        float pitch,
        float spatialBlend,
        float minDistance,
        float maxDistance,
        int priority,
        float lowpassHz,
        float highpassHz,
        bool glitch,
        float glitchSeconds,
        float stutterRate,
        float crushBits)
    {
        Initialize();
        _follow = follow;
        transform.position = follow != null ? follow.position : position;
        _source.Stop();
        _source.clip = clip;
        _source.volume = AudioCurveUtility.DbToLinear(volumeDb);
        _source.pitch = Mathf.Clamp(pitch, 0.35f, 2.5f);
        _source.spatialBlend = follow != null || position != Vector3.zero ? spatialBlend : 0f;
        _source.minDistance = minDistance;
        _source.maxDistance = maxDistance;
        _source.priority = priority;
        _lowpass.cutoffFrequency = Mathf.Clamp(lowpassHz, 120f, 22000f);
        _highpass.cutoffFrequency = Mathf.Clamp(highpassHz, 10f, 20000f);
        ResetGlitchState();
        if (glitch && clip != null)
        {
            _glitchRemaining = Mathf.Max(glitchSeconds, 0.02f);
            _echo.enabled = true;
            _echo.delay = 62f;
            _echo.decayRatio = 0.42f;
            _echo.dryMix = 0.72f;
            _echo.wetMix = 0.48f;
            _distortion.enabled = true;
            _distortion.distortionLevel = 0.35f;
            _stutter.Trigger(_glitchRemaining, stutterRate, 0.9f);
            _bitcrush.Trigger(_glitchRemaining, crushBits, 3);
        }
        _source.Play();
    }

    public bool Tick(float deltaTime)
    {
        if (_source == null || !_source.isPlaying) return false;
        if (_follow != null) transform.position = _follow.position;
        if (_glitchRemaining <= 0f) return true;
        _glitchRemaining -= deltaTime;
        if (_glitchRemaining <= 0f) ResetGlitchState();
        return true;
    }

    public void Stop()
    {
        if (_source != null) _source.Stop();
        ResetGlitchState();
        _follow = null;
    }

    private void ResetGlitchState()
    {
        _glitchRemaining = 0f;
        if (_echo != null) _echo.enabled = false;
        if (_distortion != null) _distortion.enabled = false;
    }
}
