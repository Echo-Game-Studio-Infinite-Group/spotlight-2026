using UnityEngine;

public sealed class StutterGateFilter : MonoBehaviour
{
    private int _remainingSamples;
    private float _phase;
    private float _rate = 40f;
    private float _depth = 0.85f;

    public bool Active => _remainingSamples > 0;

    public void Trigger(float duration, float rate, float depth)
    {
        int sampleRate = Mathf.Max(1, AudioSettings.outputSampleRate);
        _remainingSamples = Mathf.Max(1, Mathf.RoundToInt(Mathf.Max(0.001f, duration) * sampleRate));
        _rate = Mathf.Max(1f, rate);
        _depth = Mathf.Clamp01(depth);
        _phase = 0f;
    }

    private void OnAudioFilterRead(float[] data, int channels)
    {
        if (_remainingSamples <= 0) return;
        int sampleRate = Mathf.Max(1, AudioSettings.outputSampleRate);
        float increment = _rate / sampleRate;
        int frames = data.Length / channels;
        for (int frame = 0; frame < frames; frame++)
        {
            bool open = _phase % 1f < 0.5f;
            float gain = open ? 1f : 1f - _depth;
            for (int channel = 0; channel < channels; channel++)
                data[frame * channels + channel] *= gain;
            _phase += increment;
            _remainingSamples--;
            if (_remainingSamples <= 0) return;
        }
    }
}

public sealed class BitcrushFilter : MonoBehaviour
{
    private int _remainingSamples;
    private int _holdFrames = 2;
    private int _holdCounter;
    private float _steps = 32f;
    private float[] _held;

    public bool Active => _remainingSamples > 0;

    private void OnEnable()
    {
        _held ??= new float[2];
    }

    public void Trigger(float duration, float bits, int holdFrames)
    {
        int sampleRate = Mathf.Max(1, AudioSettings.outputSampleRate);
        _remainingSamples = Mathf.Max(1, Mathf.RoundToInt(Mathf.Max(0.001f, duration) * sampleRate));
        _steps = Mathf.Pow(2f, Mathf.Clamp(bits, 2f, 12f) - 1f);
        _holdFrames = Mathf.Max(1, holdFrames);
        _holdCounter = 0;
    }

    private void OnAudioFilterRead(float[] data, int channels)
    {
        if (_remainingSamples <= 0) return;
        int frames = data.Length / channels;
        for (int frame = 0; frame < frames; frame++)
        {
            if (_holdCounter == 0)
            {
                for (int channel = 0; channel < channels; channel++)
                {
                    float value = data[frame * channels + channel];
                    _held[channel] = Mathf.Round(value * _steps) / _steps;
                }
            }

            for (int channel = 0; channel < channels; channel++)
                data[frame * channels + channel] = _held[channel];

            _holdCounter++;
            if (_holdCounter >= _holdFrames) _holdCounter = 0;
            _remainingSamples--;
            if (_remainingSamples <= 0) return;
        }
    }
}
