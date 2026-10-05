#include "../Runtime/Dsp/SpotlightActionDsp.h"

#include <cmath>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <string>
#include <vector>

namespace
{
constexpr int SampleRate = 48000;
constexpr int SourceFrames = 96000;
constexpr int LoopStartFrame = 12000;
constexpr int LoopEndFrame = 72000;
constexpr int CrossfadeFrames = 480;

std::vector<float> BuildFixture()
{
    std::vector<float> samples(SourceFrames, 0.0f);
    for (int frame = 0; frame < SourceFrames; ++frame)
    {
        const float time = frame / static_cast<float>(SampleRate);
        samples[frame] =
            0.60f * std::sin(2.0f * 3.14159265358979323846f * 220.0f * time)
            + 0.25f * std::sin(
                2.0f * 3.14159265358979323846f * 437.0f * time + 0.5f);
    }
    return samples;
}

void WriteFloatFile(
    const std::filesystem::path& path,
    const std::vector<float>& samples)
{
    std::filesystem::create_directories(path.parent_path());
    std::ofstream stream(path, std::ios::binary);
    stream.write(
        reinterpret_cast<const char*>(samples.data()),
        static_cast<std::streamsize>(samples.size() * sizeof(float)));
}

std::vector<float> ReadFloatFile(const std::filesystem::path& path)
{
    if (!std::filesystem::exists(path))
    {
        return {};
    }

    const auto size = std::filesystem::file_size(path);
    std::vector<float> samples(
        static_cast<std::size_t>(size / sizeof(float)));
    std::ifstream stream(path, std::ios::binary);
    stream.read(
        reinterpret_cast<char*>(samples.data()),
        static_cast<std::streamsize>(samples.size() * sizeof(float)));
    return samples;
}

void PrintMetrics(
    const char* label,
    const SpotlightActionDsp::RmsPeakMetrics& metrics)
{
    std::cout
        << label
        << " frames=" << metrics.Frames
        << " rms=" << metrics.Rms
        << " peak=" << metrics.Peak
        << " first=" << metrics.First
        << " last=" << metrics.Last
        << " seam=" << metrics.SeamDelta
        << "\n";
}

void PrintDifference(
    const char* label,
    const SpotlightActionDsp::DifferenceMetrics& difference)
{
    std::cout
        << label
        << " frames=" << difference.Frames
        << " max_abs=" << difference.MaxAbsDifference
        << " rms_diff=" << difference.RmsDifference
        << "\n";
}
}

int main(int argc, char** argv)
{
    const std::filesystem::path outputDirectory =
        argc > 1
            ? std::filesystem::path(argv[1])
            : std::filesystem::path("Logs/AudioDspNative");
    const std::filesystem::path referenceDirectory =
        argc > 2
            ? std::filesystem::path(argv[2])
            : std::filesystem::path("Logs/AudioDspReference");

    std::filesystem::create_directories(outputDirectory);

    const std::vector<float> source = BuildFixture();
    const std::vector<float> crossfade = SpotlightActionDsp::RenderCrossfadeLoop(
        source,
        LoopStartFrame,
        LoopEndFrame,
        CrossfadeFrames);
    const std::vector<float> granular = SpotlightActionDsp::RenderGranularLoop(
        source,
        LoopStartFrame,
        LoopEndFrame,
        0.18f,
        0.07f,
        1.0f,
        25.0f,
        SpotlightActionDsp::CurveMapping::SCurve,
        12345u);

    SpotlightActionDsp::AdsrEnvelope envelope;
    envelope.AttackSeconds = 0.05f;
    envelope.DecaySeconds = 0.08f;
    envelope.SustainLevel = 0.7f;

    const int holdFrames = static_cast<int>(SampleRate * 1.5f);
    const float releaseDuration =
        (SourceFrames - LoopEndFrame) / static_cast<float>(SampleRate);
    const int releaseFrames = static_cast<int>(
        std::round(releaseDuration * SampleRate));
    std::vector<float> hold(static_cast<std::size_t>(holdFrames), 0.0f);
    std::vector<float> release(static_cast<std::size_t>(releaseFrames), 0.0f);
    for (int frame = 0; frame < holdFrames; ++frame)
    {
        hold[frame] = SpotlightActionDsp::EvaluateHold(
            envelope,
            frame / static_cast<float>(SampleRate));
    }
    for (int frame = 0; frame < releaseFrames; ++frame)
    {
        release[frame] = SpotlightActionDsp::EvaluateRelease(
            envelope,
            frame / static_cast<float>(SampleRate),
            envelope.SustainLevel,
            releaseDuration);
    }

    WriteFloatFile(outputDirectory / "adsr_hold.raw", hold);
    WriteFloatFile(outputDirectory / "adsr_release.raw", release);
    WriteFloatFile(outputDirectory / "crossfade.raw", crossfade);
    WriteFloatFile(outputDirectory / "granular.raw", granular);

    PrintMetrics("crossfade", SpotlightActionDsp::ComputeMetrics(crossfade));
    PrintMetrics("granular", SpotlightActionDsp::ComputeMetrics(granular));

    const auto referenceHold = ReadFloatFile(referenceDirectory / "adsr_hold.raw");
    const auto referenceRelease =
        ReadFloatFile(referenceDirectory / "adsr_release.raw");
    const auto referenceCrossfade =
        ReadFloatFile(referenceDirectory / "crossfade.raw");
    const auto referenceGranular =
        ReadFloatFile(referenceDirectory / "granular.raw");

    if (!referenceHold.empty())
    {
        PrintDifference(
            "adsr_hold_diff",
            SpotlightActionDsp::Compare(referenceHold, hold));
    }
    if (!referenceRelease.empty())
    {
        PrintDifference(
            "adsr_release_diff",
            SpotlightActionDsp::Compare(referenceRelease, release));
    }
    if (!referenceCrossfade.empty())
    {
        PrintDifference(
            "crossfade_diff",
            SpotlightActionDsp::Compare(referenceCrossfade, crossfade));
    }
    if (!referenceGranular.empty())
    {
        const auto nativeMetrics =
            SpotlightActionDsp::ComputeMetrics(granular);
        const auto referenceMetrics =
            SpotlightActionDsp::ComputeMetrics(referenceGranular);
        PrintMetrics("granular_reference", referenceMetrics);
        std::cout
            << "granular_metric_delta"
            << " frames=" << (nativeMetrics.Frames - referenceMetrics.Frames)
            << " rms=" << (nativeMetrics.Rms - referenceMetrics.Rms)
            << " peak=" << (nativeMetrics.Peak - referenceMetrics.Peak)
            << " seam=" << (nativeMetrics.SeamDelta - referenceMetrics.SeamDelta)
            << "\n";
    }

    return 0;
}

