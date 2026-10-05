using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 把素材的循环区预渲染成一条“已经做过交叠淡化”的循环片段。
///
/// 做法对齐 DSP Action 的思路：先把 buffer 处理好，再交给播放器循环。
/// 交叠淡化的定义（xf 为交叠长度，L = loopEnd - loopStart）：
///   新循环长度 P = L - xf
///   j ∈ [0, P - xf) : x[loopStart + j]                       —— 从 LoopStart 直接开始
///   j ∈ [P - xf, P) : x[loopEnd - xf + k] * (1 - w) + x[loopStart + k] * w
/// 其中 k = j - (P - xf)，w 是升余弦权重，权重对和恒为 1。
///
/// 关键：**入口是 x[loopStart]**，所以"启动段结束于 LoopStart → 循环从 LoopStart 开始"
/// 是连续的。接缝（LoopEnd → LoopStart）用尾部交叠淡化盖住。
/// 直接交给 AudioSource.loop 循环，不依赖每帧改音量。
/// </summary>
public static class ActionClipRenderer
{
    private struct LoopKey
    {
        public int ClipId;
        public int LoopStart;
        public int LoopEnd;
        public int Crossfade;
        public int GrainFrames;
        public int SpacingFrames;
        public int RandomStartPermille;
        public int TuneCents;
        public int FadeCurve;
        public int Seed;

        public bool Equals(LoopKey other) =>
            ClipId == other.ClipId && LoopStart == other.LoopStart &&
            LoopEnd == other.LoopEnd && Crossfade == other.Crossfade &&
            GrainFrames == other.GrainFrames && SpacingFrames == other.SpacingFrames &&
            RandomStartPermille == other.RandomStartPermille &&
            TuneCents == other.TuneCents &&
            FadeCurve == other.FadeCurve && Seed == other.Seed;

        public override bool Equals(object obj) => obj is LoopKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + ClipId;
                hash = hash * 31 + LoopStart;
                hash = hash * 31 + LoopEnd;
                hash = hash * 31 + Crossfade;
                hash = hash * 31 + GrainFrames;
                hash = hash * 31 + SpacingFrames;
                hash = hash * 31 + RandomStartPermille;
                hash = hash * 31 + TuneCents;
                hash = hash * 31 + FadeCurve;
                hash = hash * 31 + Seed;
                return hash;
            }
        }
    }

    private static readonly Dictionary<LoopKey, AudioClip> Cache =
        new Dictionary<LoopKey, AudioClip>();
    /// <summary>解码后的素材采样缓存：避免每次渲染循环 buffer 都重新 GetData 一遍整条素材。</summary>
    private static readonly Dictionary<int, float[]> SourceCache =
        new Dictionary<int, float[]>();

    /// <summary>缓存上限。超过就整体清空，避免每次触发都新建一条 buffer 把内存堆满。</summary>
    private const int MaxCacheEntries = 64;
    /// <summary>颗粒排布的变体数量。按 seed 取模，既有多样性又不会无限增长。</summary>
    private const int GrainVariants = 8;

    private static float[] GetSourceSamples(AudioClip clip)
    {
        int id = clip.GetInstanceID();
        if (SourceCache.TryGetValue(id, out float[] cached) && cached != null) return cached;
        var data = new float[clip.samples * clip.channels];
        if (!clip.GetData(data, 0)) return null;
        SourceCache[id] = data;
        return data;
    }

    public static AudioClip GetCrossfadeLoop(
        AudioClip clip, int loopStartSample, int loopEndSample, int crossfadeSamples)
    {
        if (clip == null || clip.samples <= 0 || clip.channels <= 0) return null;
        if (clip.loadType == AudioClipLoadType.Streaming) return null;

        var key = new LoopKey
        {
            ClipId = clip.GetInstanceID(),
            LoopStart = loopStartSample,
            LoopEnd = loopEndSample,
            Crossfade = crossfadeSamples
        };
        if (Cache.TryGetValue(key, out AudioClip cached) && cached != null) return cached;

        float[] pcm = GetCrossfadeLoopPcm(
            clip,
            loopStartSample,
            loopEndSample,
            crossfadeSamples);
        if (pcm == null) return null;

        AudioClip built = CreateClip(
            $"{clip.name}_xfade_{loopStartSample}_{loopEndSample}_{crossfadeSamples}",
            pcm,
            clip.channels,
            clip.frequency);
        if (built != null) Cache[key] = built;
        return built;
    }

    /// <summary>
    /// 纯 PCM 版本，不创建 AudioClip。Wwise 搬运路径和离线测试使用这里，
    /// 因此不要求 Unity 音频系统处于启用状态。
    /// </summary>
    public static float[] GetCrossfadeLoopPcm(
        AudioClip clip, int loopStartSample, int loopEndSample, int crossfadeSamples)
    {
        if (clip == null || clip.samples <= 0 || clip.channels <= 0) return null;
        if (clip.loadType == AudioClipLoadType.Streaming) return null;

        return GetCrossfadeLoopPcm(
            GetSourceSamples(clip),
            clip.channels,
            clip.samples,
            loopStartSample,
            loopEndSample,
            crossfadeSamples);
    }

    /// <summary>纯 PCM 源数据版本，供 Wwise 搬运路径和无 Unity 音频环境使用。</summary>
    public static float[] GetCrossfadeLoopPcm(
        float[] source,
        int channels,
        int totalFrames,
        int loopStartSample,
        int loopEndSample,
        int crossfadeSamples)
    {
        if (source == null || source.Length == 0 || channels <= 0 ||
            totalFrames <= 0)
        {
            return null;
        }

        loopStartSample = Mathf.Clamp(loopStartSample, 0, totalFrames - 1);
        loopEndSample = Mathf.Clamp(loopEndSample, loopStartSample + 2, totalFrames);
        int loopLength = loopEndSample - loopStartSample;
        int crossfade = Mathf.Clamp(crossfadeSamples, 0, loopLength / 2);
        int outputLength = loopLength - crossfade;
        if (outputLength <= 1) return null;

        var output = new float[outputLength * channels];
        int crossfadeStart = outputLength - crossfade;
        // 交叠权重只跟帧号有关，先算成表，避免逐采样重算 cos。
        var headWeights = new float[crossfade];
        for (int k = 0; k < crossfade; k++)
        {
            float u = crossfade > 1 ? k / (float)(crossfade - 1) : 1f;
            headWeights[k] = 0.5f - 0.5f * Mathf.Cos(Mathf.PI * u);
        }
        for (int j = 0; j < outputLength; j++)
        {
            if (crossfade > 0 && j >= crossfadeStart)
            {
                int k = j - crossfadeStart;
                float headWeight = headWeights[k];
                int tailFrame = loopEndSample - crossfade + k;
                int headFrame = loopStartSample + k;
                for (int c = 0; c < channels; c++)
                {
                    output[j * channels + c] =
                        source[tailFrame * channels + c] * (1f - headWeight)
                        + source[headFrame * channels + c] * headWeight;
                }
            }
            else
            {
                int frame = loopStartSample + j;
                for (int c = 0; c < channels; c++)
                    output[j * channels + c] = source[frame * channels + c];
            }
        }

        return output;
    }

    /// <summary>
    /// 颗粒模式的循环片段：在循环区里铺一串带淡入淡出窗、随机读取起点的颗粒，
    /// 重叠相加成一条固定长度的 buffer。颗粒尾巴越过末尾时回写到开头，
    /// 所以整条 buffer 首尾是同一个颗粒的两半 —— 交给 AudioSource.loop 就没有接缝。
    ///
    /// 参数语义对齐 GameSynth 的 GranularPlayer：
    /// Duration = grainSeconds，Fading Duration/Curve = 颗粒窗，Start = 随机读取起点。
    /// </summary>
    public static AudioClip GetGranularLoop(
        AudioClip clip,
        int loopStartSample,
        int loopEndSample,
        float grainSeconds,
        float spacingSeconds,
        float randomStart01,
        float tuneCents,
        AudioCurveMapping fadeCurve,
        int seed)
    {
        if (clip == null || clip.samples <= 0 || clip.channels <= 0) return null;
        if (clip.loadType == AudioClipLoadType.Streaming) return null;

        int loopLength = Mathf.Max(2, loopEndSample - loopStartSample);
        int grainFrames = Mathf.Clamp(Mathf.RoundToInt(grainSeconds * clip.frequency), 64, loopLength);
        int spacingFrames = Mathf.Clamp(
            Mathf.RoundToInt(spacingSeconds * clip.frequency), 1, grainFrames);
        int grainCount = Mathf.Clamp(
            // buffer 拉长到 ~2.5s：颗粒排布的重复周期更长，不容易听出"循环感"。
            Mathf.CeilToInt(2.5f / Mathf.Max(0.005f, spacingSeconds)), 4, 512);
        int totalFrames = grainCount * spacingFrames;
        if (totalFrames <= 1) return null;

        var key = new LoopKey
        {
            ClipId = clip.GetInstanceID(),
            LoopStart = loopStartSample,
            LoopEnd = loopEndSample,
            Crossfade = 0,
            GrainFrames = grainFrames,
            SpacingFrames = spacingFrames,
            RandomStartPermille = Mathf.RoundToInt(randomStart01 * 1000f),
            TuneCents = Mathf.RoundToInt(tuneCents),
            FadeCurve = (int)fadeCurve,
            // 量化成固定几个变体：既能"每遍不完全一样"，又不会每次都多占一份内存。
            Seed = (seed & 0x7fffffff) % GrainVariants
        };
        if (Cache.TryGetValue(key, out AudioClip cached) && cached != null) return cached;

        float[] output = GetGranularLoopPcm(
            clip,
            loopStartSample,
            loopEndSample,
            grainSeconds,
            spacingSeconds,
            randomStart01,
            tuneCents,
            fadeCurve,
            seed);
        if (output == null) return null;

        var result = CreateClip(
            $"{clip.name}_grain_{loopStartSample}_{loopEndSample}_{grainFrames}_{spacingFrames}",
            output,
            clip.channels,
            clip.frequency);
        if (result == null) return null;
        if (Cache.Count >= MaxCacheEntries)
        {
            Cache.Clear();
            // 解码缓存跟渲染结果一起回收，避免素材采样长期占内存。
            SourceCache.Clear();
        }
        Cache[key] = result;
        return result;
    }

    /// <summary>
    /// 纯 PCM 版本，不创建 AudioClip。颗粒参数和缓存键与 AudioClip 版本一致。
    /// </summary>
    public static float[] GetGranularLoopPcm(
        AudioClip clip,
        int loopStartSample,
        int loopEndSample,
        float grainSeconds,
        float spacingSeconds,
        float randomStart01,
        float tuneCents,
        AudioCurveMapping fadeCurve,
        int seed)
    {
        if (clip == null || clip.samples <= 0 || clip.channels <= 0) return null;
        if (clip.loadType == AudioClipLoadType.Streaming) return null;

        return GetGranularLoopPcm(
            GetSourceSamples(clip),
            clip.channels,
            clip.frequency,
            loopStartSample,
            loopEndSample,
            grainSeconds,
            spacingSeconds,
            randomStart01,
            tuneCents,
            fadeCurve,
            seed);
    }

    /// <summary>纯 PCM 源数据版本，供 Wwise 搬运路径和无 Unity 音频环境使用。</summary>
    public static float[] GetGranularLoopPcm(
        float[] source,
        int channels,
        int sampleRate,
        int loopStartSample,
        int loopEndSample,
        float grainSeconds,
        float spacingSeconds,
        float randomStart01,
        float tuneCents,
        AudioCurveMapping fadeCurve,
        int seed)
    {
        if (source == null || source.Length == 0 || channels <= 0 ||
            sampleRate <= 0)
        {
            return null;
        }

        int loopLength = Mathf.Max(2, loopEndSample - loopStartSample);
        int grainFrames = Mathf.Clamp(
            Mathf.RoundToInt(grainSeconds * sampleRate),
            64,
            loopLength);
        int spacingFrames = Mathf.Clamp(
            Mathf.RoundToInt(spacingSeconds * sampleRate),
            1,
            grainFrames);
        int grainCount = Mathf.Clamp(
            Mathf.CeilToInt(2.5f / Mathf.Max(0.005f, spacingSeconds)),
            4,
            512);
        int totalFrames = grainCount * spacingFrames;
        if (totalFrames <= 1) return null;

        var output = new float[totalFrames * channels];
        int fadeFrames = Mathf.Clamp(Mathf.RoundToInt(grainFrames * 0.45f), 1, grainFrames / 2);
        int maxOffset = Mathf.Max(0, Mathf.RoundToInt((loopLength - grainFrames) * randomStart01));
        var random = new System.Random(seed == 0 ? 12345 : seed);
        // 颗粒窗对所有颗粒都一样，先算成表：省掉每采样一次 Map()（SCurve 内含 Mathf.Sin）。
        var windowTable = new float[grainFrames];
        for (int k = 0; k < grainFrames; k++)
            windowTable[k] = GrainWindow(k, grainFrames, fadeFrames, fadeCurve);

        for (int g = 0; g < grainCount; g++)
        {
            int offset = maxOffset > 0 ? random.Next(0, maxOffset + 1) : 0;
            int readStart = loopStartSample + offset;
            int writeStart = g * spacingFrames;
            // 每颗随机失谐一点：同一素材重叠时相位被打散，避免梳状滤波（金属/电子味）。
            double tuneRatio = System.Math.Pow(2.0,
                (random.NextDouble() * 2.0 - 1.0) * tuneCents / 1200.0);
            // 读指针写成增量，避免每采样做一次 `readStart + k * ratio` 的浮点乘 + 取整。
            double readPosition = readStart;
            // 写指针增量 + 越界归零，替掉每采样的整数取模。
            int dstFrame = writeStart % totalFrames;
            for (int k = 0; k < grainFrames; k++)
            {
                float window = windowTable[k];
                int frame0 = (int)readPosition;
                float frac = (float)(readPosition - frame0);
                int frame1 = frame0 + 1;
                // tuneRatio 恒在 1 附近，每步最多跨过 loopEnd 一次，减一次就够（不必 while）。
                if (frame0 >= loopEndSample) frame0 -= loopLength;
                if (frame1 >= loopEndSample) frame1 -= loopLength;

                if (window > 0f)
                {
                    int s0 = frame0 * channels;
                    int s1 = frame1 * channels;
                    int d = dstFrame * channels;
                    for (int c = 0; c < channels; c++)
                    {
                        float a = source[s0 + c];
                        output[d + c] += (a + (source[s1 + c] - a) * frac) * window;
                    }
                }
                readPosition += tuneRatio;
                if (++dstFrame >= totalFrames) dstFrame = 0;
            }
        }

        // 响度对齐：颗粒重叠相加会让电平比素材高很多（重叠越多抬得越高），
        // 只按峰值归一化不够 —— 那样 sustain 会比启动段/尾音明显更响。
        // 这里把颗粒缓冲的 RMS 压到与循环区素材本身的 RMS 一致，
        // 再额外检查峰值不削顶。这样三段响度才是连续的。
        float sourceRms = RmsOfRange(source, loopStartSample, loopEndSample, channels);
        float bufferRms = RmsOfAll(output);
        if (bufferRms > 1e-6f && sourceRms > 1e-6f)
        {
            float gain = sourceRms / bufferRms;
            for (int i = 0; i < output.Length; i++) output[i] *= gain;
        }

        float peak = 0f;
        for (int i = 0; i < output.Length; i++)
        {
            float magnitude = Mathf.Abs(output[i]);
            if (magnitude > peak) peak = magnitude;
        }
        if (peak > 0.95f)
        {
            float scale = 0.95f / peak;
            for (int i = 0; i < output.Length; i++) output[i] *= scale;
        }

        return output;
    }

    private static AudioClip CreateClip(
        string name, float[] pcm, int channels, int sampleRate)
    {
        if (pcm == null || pcm.Length == 0 || channels <= 0 || sampleRate <= 0)
            return null;

        int frames = pcm.Length / channels;
        if (frames <= 0) return null;

        var clip = AudioClip.Create(name, frames, channels, sampleRate, false);
        clip.SetData(pcm, 0);
        return clip;
    }

    private static float GrainWindow(
        int index, int grainFrames, int fadeFrames, AudioCurveMapping curve)
    {
        if (index < fadeFrames)
            return AudioCurveUtility.Map(index / (float)fadeFrames, curve);
        int fromEnd = grainFrames - 1 - index;
        if (fromEnd < fadeFrames)
            return AudioCurveUtility.Map(fromEnd / (float)fadeFrames, curve);
        return 1f;
    }

    private static int WrapIntoLoop(int frame, int loopStart, int loopEnd, int loopLength)
    {
        while (frame >= loopEnd) frame -= loopLength;
        while (frame < loopStart) frame += loopLength;
        return frame;
    }

    private static float RmsOfRange(float[] data, int fromFrame, int toFrame, int channels)
    {
        double sum = 0.0;
        int count = 0;
        int last = Mathf.Min(toFrame, data.Length / Mathf.Max(1, channels));
        for (int frame = Mathf.Max(0, fromFrame); frame < last; frame++)
        {
            int offset = frame * channels;
            for (int c = 0; c < channels; c++)
            {
                double v = data[offset + c];
                sum += v * v;
                count++;
            }
        }
        return count == 0 ? 0f : (float)System.Math.Sqrt(sum / count);
    }

    private static float RmsOfAll(float[] data)
    {
        if (data.Length == 0) return 0f;
        double sum = 0.0;
        for (int i = 0; i < data.Length; i++)
        {
            double v = data[i];
            sum += v * v;
        }
        return (float)System.Math.Sqrt(sum / data.Length);
    }
}
