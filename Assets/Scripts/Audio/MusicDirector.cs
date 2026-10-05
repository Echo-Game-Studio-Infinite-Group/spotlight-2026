using System.Collections;
using UnityEngine;

public sealed class MusicDirector : MonoBehaviour
{
    private AudioSource _channelA;
    private AudioSource _channelB;
    private AudioSource _current;
    private Coroutine _fade;
    private float _volume = 1f;

    public AudioClip CurrentClip => _current != null ? _current.clip : null;

    public void Initialize()
    {
        _channelA = CreateChannel("MusicA");
        _channelB = CreateChannel("MusicB");
    }

    public void Play(AudioClip clip, float fadeSeconds)
    {
        if (clip == null)
        {
            Stop(fadeSeconds);
            return;
        }
        if (_current != null && _current.clip == clip && _current.isPlaying) return;

        AudioSource from = _current;
        AudioSource to = from == _channelA ? _channelB : _channelA;
        to.clip = clip;
        to.loop = true;
        to.volume = 0f;
        to.Play();
        _current = to;
        StartFade(from, to, fadeSeconds);
    }

    public void Stop(float fadeSeconds)
    {
        if (_current == null) return;
        StartFade(_current, null, fadeSeconds);
        _current = null;
    }

    public void SetVolume(float linear01)
    {
        _volume = Mathf.Clamp01(linear01);
        if (_channelA != null) ApplyVolume(_channelA, 1f);
        if (_channelB != null) ApplyVolume(_channelB, 1f);
    }

    private AudioSource CreateChannel(string channelName)
    {
        GameObject go = new GameObject(channelName);
        go.transform.SetParent(transform, false);
        AudioSource source = go.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = true;
        source.spatialBlend = 0f;
        source.ignoreListenerPause = true;
        source.dopplerLevel = 0f;
        return source;
    }

    private void StartFade(AudioSource from, AudioSource to, float seconds)
    {
        if (_fade != null) StopCoroutine(_fade);
        _fade = StartCoroutine(Crossfade(from, to, Mathf.Max(0.01f, seconds)));
    }

    private IEnumerator Crossfade(AudioSource from, AudioSource to, float seconds)
    {
        float elapsed = 0f;
        while (elapsed < seconds)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / seconds);
            if (from != null) ApplyVolume(from, 1f - t);
            if (to != null) ApplyVolume(to, t);
            yield return null;
        }

        if (from != null)
        {
            from.Stop();
            ApplyVolume(from, 0f);
        }
        if (to != null) ApplyVolume(to, 1f);
        _fade = null;
    }

    private void ApplyVolume(AudioSource source, float envelope)
    {
        if (source != null) source.volume = envelope * _volume;
    }
}
