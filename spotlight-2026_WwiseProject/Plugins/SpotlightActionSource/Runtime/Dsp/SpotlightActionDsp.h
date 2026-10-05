#pragma once

#include <AK/SoundEngine/Common/AkTypes.h>

#include <cstdint>
#include <vector>

namespace SpotlightActionDsp
{
enum class CurveMapping
{
    Linear,
    Slow,
    Fast,
    SCurve
};

struct AdsrEnvelope
{
    float AttackSeconds = 0.01f;
    float DecaySeconds = 0.05f;
    float SustainLevel = 1.0f;
    CurveMapping AttackCurve = CurveMapping::SCurve;
    CurveMapping DecayCurve = CurveMapping::Fast;
    CurveMapping ReleaseCurve = CurveMapping::Fast;
};

struct RmsPeakMetrics
{
    double Rms = 0.0;
    float Peak = 0.0f;
    float First = 0.0f;
    float Last = 0.0f;
    float SeamDelta = 0.0f;
    std::uint32_t Frames = 0;
};

struct DifferenceMetrics
{
    float MaxAbsDifference = 0.0f;
    double RmsDifference = 0.0;
    std::uint32_t Frames = 0;
};

float MapCurve(float value, CurveMapping mapping);
float EvaluateHold(const AdsrEnvelope& envelope, float timeSincePress);
float EvaluateRelease(
    const AdsrEnvelope& envelope,
    float timeSinceRelease,
    float startLevel,
    float duration);

std::vector<float> RenderCrossfadeLoop(
    const std::vector<float>& source,
    int loopStartFrame,
    int loopEndFrame,
    int crossfadeFrames);

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
    std::uint32_t sampleRate = 48000);

RmsPeakMetrics ComputeMetrics(const std::vector<float>& samples);
DifferenceMetrics Compare(
    const std::vector<float>& reference,
    const std::vector<float>& candidate);

std::uint32_t NextRandom(std::uint32_t& state);
float RandomUnit(std::uint32_t& state);
}
