using System;
using UnityEngine;

/// <summary>
/// 编辑器侧的素材能量分析：把整段 AudioClip 切成固定窗口，统计每窗 RMS。
/// 用来在 Inspector 里画出波形、找出有声区间、以及在循环区落在静音段时给出修正建议。
/// 只读采样，不改素材；分析结果由调用方缓存。
/// </summary>
public sealed class AudioActionClipAnalysis
{
    private const float WindowSeconds = 0.02f;
    private const int ChunkFrames = 1 << 15;
    private const float AudibleFloorRatio = 0.02f;

    public float[] WindowRms = Array.Empty<float>();
    /// <summary>单声道混合采样，供循环点互相关搜索使用（过长素材不保留）。</summary>
    public float[] Mono;
    public AudioClip Clip;
    public float Duration;
    public float PeakRms;
    public float PeakAmplitude;
    public float Peak01;
    public float FirstAudible01;
    public float LastAudible01;
    public bool HasAudibleContent;
    public string Error = string.Empty;

    public bool Success => string.IsNullOrEmpty(Error) && WindowRms.Length > 0;

    public static AudioActionClipAnalysis Analyze(AudioClip clip)
    {
        var result = new AudioActionClipAnalysis();
        if (clip == null)
        {
            result.Error = "没有绑定 AudioClip。";
            return result;
        }
        if (clip.loadType == AudioClipLoadType.Streaming)
        {
            result.Error = "该素材是 Streaming 导入，编辑器无法读取采样做分析。请改成 Decompress On Load。";
            return result;
        }

        int channels = clip.channels;
        int samples = clip.samples;
        if (channels <= 0 || samples <= 0)
        {
            result.Error = "AudioClip 没有可用采样数据。";
            return result;
        }

        int windowFrames = Mathf.Max(1, Mathf.RoundToInt(clip.frequency * WindowSeconds));
        int windowCount = Mathf.CeilToInt(samples / (float)windowFrames);
        var energy = new float[windowCount];
        var buffer = new float[ChunkFrames * channels];
        result.Clip = clip;
        // 互相关搜索需要原始采样；超过 30 秒的素材不保留，避免占内存。
        float[] mono = samples <= clip.frequency * 30 ? new float[samples] : null;

        try
        {
            int frame = 0;
            while (frame < samples)
            {
                int frames = Mathf.Min(ChunkFrames, samples - frame);
                if (!clip.GetData(buffer, frame))
                {
                    result.Error = "当前导入设置不允许读取采样数据。";
                    return result;
                }
                for (int f = 0; f < frames; f++)
                {
                    int window = (frame + f) / windowFrames;
                    if (window >= windowCount) break;
                    int baseIndex = f * channels;
                    double sum = 0.0;
                    float monoSample = 0f;
                    for (int c = 0; c < channels; c++)
                    {
                        float value = buffer[baseIndex + c];
                        sum += (double)value * value;
                        monoSample += value;
                        float magnitude = Mathf.Abs(value);
                        if (magnitude > result.PeakAmplitude) result.PeakAmplitude = magnitude;
                    }
                    energy[window] += (float)(sum / channels);
                    if (mono != null) mono[frame + f] = monoSample / channels;
                }
                frame += frames;
            }
        }
        catch (Exception e)
        {
            result.Error = $"{e.GetType().Name}: {e.Message}";
            return result;
        }

        result.WindowRms = new float[windowCount];
        result.Mono = mono;
        result.Duration = samples / (float)clip.frequency;
        for (int w = 0; w < windowCount; w++)
        {
            int framesInWindow = Mathf.Min(windowFrames, samples - w * windowFrames);
            if (framesInWindow <= 0) continue;
            float rms = Mathf.Sqrt(energy[w] / framesInWindow);
            result.WindowRms[w] = rms;
            if (rms > result.PeakRms)
            {
                result.PeakRms = rms;
                result.Peak01 = (w * windowFrames + framesInWindow * 0.5f) / samples;
            }
        }

        float floor = Mathf.Max(result.PeakRms * AudibleFloorRatio, 1e-5f);
        int first = -1;
        int last = -1;
        for (int w = 0; w < windowCount; w++)
        {
            if (result.WindowRms[w] < floor) continue;
            if (first < 0) first = w;
            last = w;
        }

        result.HasAudibleContent = first >= 0;
        if (result.HasAudibleContent)
        {
            result.FirstAudible01 = (first * windowFrames) / (float)samples;
            result.LastAudible01 = Mathf.Min(1f, ((last + 1) * windowFrames) / (float)samples);
        }
        return result;
    }

    /// <summary>归一化区间 [start01, end01] 内的平均 RMS。</summary>
    public float AverageRms01(float start01, float end01)
    {
        if (!Success) return 0f;
        int count = WindowRms.Length;
        int from = Mathf.Clamp(Mathf.FloorToInt(Mathf.Clamp01(start01) * count), 0, count - 1);
        int to = Mathf.Clamp(Mathf.CeilToInt(Mathf.Clamp01(end01) * count) - 1, from, count - 1);
        double sum = 0.0;
        for (int i = from; i <= to; i++) sum += WindowRms[i];
        return (float)(sum / (to - from + 1));
    }

    /// <summary>在有声区间内给一个可循环的中段：跳过起音瞬态，保留到有声区间结束。</summary>
    public bool TrySuggestLoopRegion(out float start01, out float end01)
    {
        start01 = 0f;
        end01 = 1f;
        if (!Success || !HasAudibleContent) return false;

        float span = LastAudible01 - FirstAudible01;
        if (span <= 0.01f) return false;

        // 起段会完整播放 StartPosition→LoopStart，所以循环点要落在起音瞬态“之后”，
        // 否则每一轮循环都会重新触发那个瞬态，听起来像机关枪。
        int peakWindow = Mathf.Clamp(
            Mathf.RoundToInt(Peak01 * WindowRms.Length), 0, WindowRms.Length - 1);
        float transientFloor = PeakRms * 0.6f;
        float afterTransient01 = Peak01;
        for (int w = peakWindow; w < WindowRms.Length; w++)
        {
            if (WindowRms[w] >= transientFloor) continue;
            afterTransient01 = w / (float)WindowRms.Length;
            break;
        }

        start01 = Mathf.Max(FirstAudible01 + span * 0.25f, afterTransient01);
        // 同时保证循环区至少占整个有声区间的四分之一，避免压成一条缝。
        start01 = Mathf.Min(start01, Mathf.Max(FirstAudible01, LastAudible01 - span * 0.25f));
        end01 = Mathf.Clamp01(LastAudible01);
        if (end01 - start01 < 0.02f) start01 = Mathf.Max(0f, end01 - 0.02f);
        return end01 > start01;
    }

    /// <summary>循环接缝的瞬时跳变幅度：|起点的首采样 - 终点的末采样|，多声道取最大。</summary>
    public float SeamStep01(float start01, float end01)
    {
        if (Clip == null || !Success || Clip.channels <= 0) return 0f;
        int last = Clip.samples - 1;
        int startSample = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(start01) * last), 0, last);
        int endSample = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(end01) * last) - 1, 0, last);
        var first = new float[Clip.channels];
        var final = new float[Clip.channels];
        if (!Clip.GetData(first, startSample)) return 0f;
        if (!Clip.GetData(final, endSample)) return 0f;
        float step = 0f;
        for (int c = 0; c < Clip.channels; c++)
            step = Mathf.Max(step, Mathf.Abs(first[c] - final[c]));
        return step;
    }

    /// <summary>
    /// 用归一化互相关在素材里找"最像接得上"的一对循环点。
    ///
    /// 判据：LoopEnd 前面那段波形 与 LoopStart 前面那段波形 越像，
    /// 从 LoopEnd 回到 LoopStart 就越不容易听出接缝。
    /// 除了波形相关性，还要求两段 RMS 接近，避免把"响的接静的"选进来。
    /// </summary>
    public bool TryFindSeamlessLoopRegion(
        out float start01, out float end01, out float score, out float seamStep)
    {
        start01 = 0f;
        end01 = 1f;
        score = 0f;
        seamStep = 0f;
        if (Mono == null || !HasAudibleContent || Clip == null) return false;

        int rate = Clip.frequency;
        int window = Mathf.Clamp(rate / 50, 64, 4096);        // ~20 ms 比较窗
        int first = Mathf.Clamp(Mathf.RoundToInt(FirstAudible01 * Mono.Length), 0, Mono.Length - 1);
        int last = Mathf.Clamp(Mathf.RoundToInt(LastAudible01 * Mono.Length), 0, Mono.Length - 1);
        // 循环长度限制在 0.25~1.2 s：太短会有明显周期感，太长就没有"持续"的意义。
        int minLoop = Mathf.Max(window * 2, Mathf.RoundToInt(rate * 0.25f));
        int span = last - first;
        int maxLoop = Mathf.Clamp(Mathf.RoundToInt(rate * 1.2f), minLoop, Mathf.Max(minLoop, span));
        if (span <= minLoop + window) return false;

        int bestStart = -1;
        int bestEnd = -1;
        float bestScore = float.NegativeInfinity;
        // 搜索量封顶，避免长素材卡死编辑器：最多 400 × 40 组合。
        int endSpan = Mathf.Max(1, last - (first + minLoop + window));
        int endStep = Mathf.Max(1, endSpan / 400);
        int lengthSpan = Mathf.Max(1, maxLoop - minLoop);
        int lengthStep = Mathf.Max(rate / 20, lengthSpan / 40);

        for (int end = first + minLoop + window; end <= last; end += endStep)
        {
            for (int length = minLoop; length <= maxLoop; length += lengthStep)
            {
                int start = end - length;
                if (start < first + window) break;

                float rmsA = Rms(Mono, start - window, window);
                float rmsB = Rms(Mono, end - window, window);
                float levelMatch = 1f - Mathf.Clamp01(
                    Mathf.Abs(rmsA - rmsB) / Mathf.Max(1e-5f, Mathf.Max(rmsA, rmsB)));
                // 接缝跳变（相对峰值）越小越好；交叠淡化能削掉一部分，但相对跳变仍要控制。
                float jump = Mathf.Abs(Mono[end - 1] - Mono[start]);
                float jumpScore = 1f - Mathf.Clamp01(jump / Mathf.Max(1e-5f, PeakAmplitude));
                // 波形相关只当加分项：噪声型素材相关本身就很低，不能让它主导。
                float corr = NormalizedCorrelation(Mono, start - window, end - window, window);
                float corrBonus = 0.5f + 0.5f * Mathf.Clamp01(corr);

                float candidate = levelMatch * jumpScore * corrBonus;
                if (candidate <= bestScore) continue;
                bestScore = candidate;
                bestStart = start;
                bestEnd = end;
            }
        }

        if (bestStart < 0) return false;
        start01 = bestStart / (float)Mono.Length;
        end01 = bestEnd / (float)Mono.Length;
        score = bestScore;
        seamStep = SeamStep01(start01, end01);
        return true;
    }

    private static float NormalizedCorrelation(float[] data, int a, int b, int length)
    {
        if (a < 0 || b < 0 || a + length > data.Length || b + length > data.Length) return -1f;
        double dot = 0.0, na = 0.0, nb = 0.0;
        for (int i = 0; i < length; i++)
        {
            double x = data[a + i];
            double y = data[b + i];
            dot += x * y;
            na += x * x;
            nb += y * y;
        }
        double denom = System.Math.Sqrt(na * nb);
        return denom <= 1e-12 ? -1f : (float)(dot / denom);
    }

    private static float Rms(float[] data, int from, int length)
    {
        if (from < 0 || from + length > data.Length) return 0f;
        double sum = 0.0;
        for (int i = 0; i < length; i++)
        {
            double v = data[from + i];
            sum += v * v;
        }
        return (float)System.Math.Sqrt(sum / length);
    }
}
