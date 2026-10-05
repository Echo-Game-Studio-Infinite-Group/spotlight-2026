#include "SpotlightActionDsp.h"

#include <algorithm>
#include <cmath>
#include <limits>

namespace SpotlightActionDsp
{
namespace
{
constexpr float Pi = 3.14159265358979323846f;
constexpr float TwoPi = Pi * 2.0f;

float Clamp01(float value)
{
    return std::max(0.0f, std::min(1.0f, value));
}

float Clamp(float value, float minimum, float maximum)
{
    return std::max(minimum, std::min(maximum, value));
}

float GrainWindow(
    int index,
    int grainFrames,
    int fadeFrames,
    CurveMapping curve)
{
    if (index < fadeFrames)
    {
        return MapCurve(index / static_cast<float>(fadeFrames), curve);
    }

    const int fromEnd = grainFrames - 1 - index;
    if (fromEnd < fadeFrames)
    {
        return MapCurve(fromEnd / static_cast<float>(fadeFrames), curve);
    }

    return 1.0f;
}

double RmsOfRange(
    const std::vector<float>& data,
    int fromFrame,
    int toFrame)
{
    double sum = 0.0;
    int count = 0;
    const int last = std::max(
        0,
        std::min(toFrame, static_cast<int>(data.size())));
    for (int frame = std::max(0, fromFrame); frame < last; ++frame)
    {
        const double value = data[frame];
        sum += value * value;
        ++count;
    }

    return count == 0
        ? 0.0
        : std::sqrt(sum / static_cast<double>(count));
}

double RmsOfAll(const std::vector<float>& data)
{
    if (data.empty())
    {
        return 0.0;
    }

    double sum = 0.0;
    for (float value : data)
    {
        sum += static_cast<double>(value) * value;
    }

    return std::sqrt(sum / static_cast<double>(data.size()));
}

int WrapIntoLoop(
    int frame,
    int loopStartFrame,
    int loopEndFrame,
    int loopLength)
{
    while (frame >= loopEndFrame)
    {
        frame -= loopLength;
    }
    while (frame < loopStartFrame)
    {
        frame += loopLength;
    }
    return frame;
}
}

float MapCurve(float value, CurveMapping mapping)
{
    const float x = Clamp01(value);
    switch (mapping)
    {
    case CurveMapping::Slow:
        return x * x * x;
    case CurveMapping::Fast:
        return 1.0f - std::pow(1.0f - x, 3.0f);
    case CurveMapping::SCurve:
        return 0.5f + 0.5f * std::sin(Pi * x - Pi * 0.5f);
    default:
        return x;
    }
}

float EvaluateHold(const AdsrEnvelope& envelope, float timeSincePress)
{
    const float time = std::max(0.0f, timeSincePress);
    if (envelope.AttackSeconds > 0.0f && time < envelope.AttackSeconds)
    {
        return MapCurve(time / envelope.AttackSeconds, envelope.AttackCurve);
    }

    const float decayTime = time - envelope.AttackSeconds;
    if (envelope.DecaySeconds > 0.0f && decayTime < envelope.DecaySeconds)
    {
        return 1.0f + (envelope.SustainLevel - 1.0f)
            * MapCurve(decayTime / envelope.DecaySeconds, envelope.DecayCurve);
    }

    return envelope.SustainLevel;
}

float EvaluateRelease(
    const AdsrEnvelope& envelope,
    float timeSinceRelease,
    float startLevel,
    float duration)
{
    if (duration <= 0.0f)
    {
        return 0.0f;
    }

    const float normalized = Clamp01(std::max(0.0f, timeSinceRelease) / duration);
    return Clamp01(startLevel)
        * (1.0f - MapCurve(normalized, envelope.ReleaseCurve));
}

std::vector<float> RenderCrossfadeLoop(
    const std::vector<float>& source,
    int loopStartFrame,
    int loopEndFrame,
    int crossfadeFrames)
{
    if (source.empty())
    {
        return {};
    }

    const int totalFrames = static_cast<int>(source.size());
    loopStartFrame = std::max(0, std::min(loopStartFrame, totalFrames - 1));
    loopEndFrame = std::max(
        loopStartFrame + 2,
        std::min(loopEndFrame, totalFrames));

    const int loopLength = loopEndFrame - loopStartFrame;
    const int crossfade = std::max(
        0,
        std::min(crossfadeFrames, loopLength / 2));
    const int outputLength = loopLength - crossfade;
    if (outputLength <= 1)
    {
        return {};
    }

    std::vector<float> output(static_cast<std::size_t>(outputLength), 0.0f);
    std::vector<float> headWeights(static_cast<std::size_t>(crossfade), 0.0f);
    for (int index = 0; index < crossfade; ++index)
    {
        const float normalized = crossfade > 1
            ? index / static_cast<float>(crossfade - 1)
            : 1.0f;
        headWeights[index] =
            0.5f - 0.5f * std::cos(Pi * normalized);
    }

    const int crossfadeStart = outputLength - crossfade;
    for (int outputFrame = 0; outputFrame < outputLength; ++outputFrame)
    {
        if (crossfade > 0 && outputFrame >= crossfadeStart)
        {
            const int index = outputFrame - crossfadeStart;
            const float headWeight = headWeights[index];
            const int tailFrame = loopEndFrame - crossfade + index;
            const int headFrame = loopStartFrame + index;
            output[outputFrame] =
                source[tailFrame] * (1.0f - headWeight)
                + source[headFrame] * headWeight;
        }
        else
        {
            output[outputFrame] = source[loopStartFrame + outputFrame];
        }
    }

    return output;
}

std::vector<float> RenderGranularLoop(
    const std::vector<float>& source,
    int loopStartFrame,
    int loopEndFrame,
    float grainSeconds,
    float spacingSeconds,
    float randomStart01,
    float tuneCents,
    CurveMapping fadeCurve,
    std::uint32_t seed,
    std::uint32_t sampleRate)
{
    if (source.empty())
    {
        return {};
    }

    const int sampleRateInt = static_cast<int>(sampleRate);
    const int totalSourceFrames = static_cast<int>(source.size());
    loopStartFrame = std::max(0, std::min(loopStartFrame, totalSourceFrames - 1));
    loopEndFrame = std::max(
        loopStartFrame + 2,
        std::min(loopEndFrame, totalSourceFrames));

    const int loopLength = std::max(2, loopEndFrame - loopStartFrame);
    const int grainFrames = std::max(
        64,
        std::min(
            loopLength,
            static_cast<int>(std::round(grainSeconds * sampleRateInt))));
    const int spacingFrames = std::max(
        1,
        std::min(
            grainFrames,
            static_cast<int>(std::round(spacingSeconds * sampleRateInt))));
    const int grainCount = std::max(
        4,
        std::min(
            512,
            static_cast<int>(std::ceil(
                2.5f / std::max(0.005f, spacingSeconds)))));
    const int totalFrames = grainCount * spacingFrames;
    if (totalFrames <= 1)
    {
        return {};
    }

    std::vector<float> output(static_cast<std::size_t>(totalFrames), 0.0f);
    const int fadeFrames = std::max(
        1,
        std::min(
            grainFrames / 2,
            static_cast<int>(std::round(grainFrames * 0.45f))));
    const int maxOffset = std::max(
        0,
        static_cast<int>(
            std::round((loopLength - grainFrames) * Clamp01(randomStart01))));

    std::uint32_t randomState = seed == 0 ? 12345u : seed;
    std::vector<float> windowTable(static_cast<std::size_t>(grainFrames), 0.0f);
    for (int index = 0; index < grainFrames; ++index)
    {
        windowTable[index] =
            GrainWindow(index, grainFrames, fadeFrames, fadeCurve);
    }

    for (int grain = 0; grain < grainCount; ++grain)
    {
        const int offset = maxOffset > 0
            ? static_cast<int>(NextRandom(randomState)
                % static_cast<std::uint32_t>(maxOffset + 1))
            : 0;
        const int readStart = loopStartFrame + offset;
        const int writeStart = grain * spacingFrames;
        const double tuneRatio = std::pow(
            2.0,
            (static_cast<double>(RandomUnit(randomState)) * 2.0 - 1.0)
                * tuneCents / 1200.0);

        double readPosition = readStart;
        int destinationFrame = writeStart % totalFrames;
        for (int index = 0; index < grainFrames; ++index)
        {
            const float window = windowTable[index];
            const int frame0 = static_cast<int>(readPosition);
            const float fraction =
                static_cast<float>(readPosition - frame0);
            const int frame1 = WrapIntoLoop(
                frame0 + 1,
                loopStartFrame,
                loopEndFrame,
                loopLength);
            const int wrappedFrame0 = WrapIntoLoop(
                frame0,
                loopStartFrame,
                loopEndFrame,
                loopLength);

            if (window > 0.0f)
            {
                const float first = source[wrappedFrame0];
                output[destinationFrame] +=
                    (first + (source[frame1] - first) * fraction) * window;
            }

            readPosition += tuneRatio;
            if (++destinationFrame >= totalFrames)
            {
                destinationFrame = 0;
            }
        }
    }

    const double sourceRms = RmsOfRange(source, loopStartFrame, loopEndFrame);
    const double bufferRms = RmsOfAll(output);
    if (bufferRms > 1e-6 && sourceRms > 1e-6)
    {
        const float gain = static_cast<float>(sourceRms / bufferRms);
        for (float& sample : output)
        {
            sample *= gain;
        }
    }

    float peak = 0.0f;
    for (float sample : output)
    {
        peak = std::max(peak, std::abs(sample));
    }
    if (peak > 0.95f)
    {
        const float scale = 0.95f / peak;
        for (float& sample : output)
        {
            sample *= scale;
        }
    }

    return output;
}

RmsPeakMetrics ComputeMetrics(const std::vector<float>& samples)
{
    RmsPeakMetrics metrics;
    metrics.Frames = static_cast<std::uint32_t>(samples.size());
    if (samples.empty())
    {
        return metrics;
    }

    double sum = 0.0;
    for (float sample : samples)
    {
        sum += static_cast<double>(sample) * sample;
        metrics.Peak = std::max(metrics.Peak, std::abs(sample));
    }

    metrics.Rms = std::sqrt(sum / static_cast<double>(samples.size()));
    metrics.First = samples.front();
    metrics.Last = samples.back();
    metrics.SeamDelta = std::abs(metrics.Last - metrics.First);
    return metrics;
}

DifferenceMetrics Compare(
    const std::vector<float>& reference,
    const std::vector<float>& candidate)
{
    DifferenceMetrics metrics;
    const std::size_t count = std::min(reference.size(), candidate.size());
    metrics.Frames = static_cast<std::uint32_t>(count);
    if (count == 0)
    {
        return metrics;
    }

    double sumSquared = 0.0;
    for (std::size_t index = 0; index < count; ++index)
    {
        const float difference = candidate[index] - reference[index];
        metrics.MaxAbsDifference = std::max(
            metrics.MaxAbsDifference,
            std::abs(difference));
        sumSquared += static_cast<double>(difference) * difference;
    }

    metrics.RmsDifference = std::sqrt(sumSquared / static_cast<double>(count));
    return metrics;
}

std::uint32_t NextRandom(std::uint32_t& state)
{
    std::uint32_t value = state;
    value ^= value << 13;
    value ^= value >> 17;
    value ^= value << 5;
    state = value;
    return value;
}

float RandomUnit(std::uint32_t& state)
{
    return static_cast<float>(
        NextRandom(state) / static_cast<double>(
            std::numeric_limits<std::uint32_t>::max()));
}
}
