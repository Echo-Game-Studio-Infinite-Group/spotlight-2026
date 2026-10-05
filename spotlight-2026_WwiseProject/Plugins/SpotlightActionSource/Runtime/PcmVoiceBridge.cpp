#include "PcmVoiceBridge.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <cstring>
#include <limits>
#include <vector>

namespace
{
constexpr std::uint32_t MaxVoices = 16;
constexpr std::uint32_t RingCapacityFrames = 1u << 17;
constexpr std::uint64_t RingMask = RingCapacityFrames - 1u;

struct PcmVoice
{
    std::vector<float> Ring;
    std::atomic<std::uint64_t> ProducedFrames{0};
    std::atomic<std::uint64_t> ConsumedFrames{0};
    std::atomic<bool> Active{false};
    std::atomic<bool> Finished{false};
    std::uint32_t VoiceId = 0;
    std::uint32_t Channels = 0;
    std::uint32_t SampleRate = 0;
};

std::array<PcmVoice, MaxVoices> Voices;

PcmVoice* FindVoice(std::uint32_t voiceId)
{
    for (PcmVoice& voice : Voices)
    {
        if (voice.Active.load(std::memory_order_acquire) &&
            voice.VoiceId == voiceId)
        {
            return &voice;
        }
    }
    return nullptr;
}

PcmVoice* FindOrCreateSlot(std::uint32_t voiceId)
{
    for (PcmVoice& voice : Voices)
    {
        if (voice.VoiceId == voiceId &&
            !voice.Active.load(std::memory_order_acquire))
        {
            return &voice;
        }
    }

    for (PcmVoice& voice : Voices)
    {
        if (!voice.Active.load(std::memory_order_acquire))
        {
            return &voice;
        }
    }

    return nullptr;
}
}

bool SpotlightAction_CreateVoice(
    std::uint32_t voiceId,
    std::uint32_t channels,
    std::uint32_t sampleRate)
{
    if (voiceId == 0 || channels == 0 || channels > 2 || sampleRate == 0)
    {
        return false;
    }

    PcmVoice* voice = FindOrCreateSlot(voiceId);
    if (voice == nullptr)
    {
        return false;
    }

    voice->Active.store(false, std::memory_order_release);
    voice->VoiceId = voiceId;
    voice->Channels = channels;
    voice->SampleRate = sampleRate;
    voice->ProducedFrames.store(0, std::memory_order_relaxed);
    voice->ConsumedFrames.store(0, std::memory_order_relaxed);
    voice->Finished.store(false, std::memory_order_relaxed);
    voice->Ring.assign(
        static_cast<std::size_t>(RingCapacityFrames) * channels,
        0.0f);
    voice->Active.store(true, std::memory_order_release);
    return true;
}

void SpotlightAction_DestroyVoice(std::uint32_t voiceId)
{
    if (PcmVoice* voice = FindVoice(voiceId))
    {
        voice->Active.store(false, std::memory_order_release);
        voice->Finished.store(true, std::memory_order_relaxed);
        voice->ProducedFrames.store(0, std::memory_order_relaxed);
        voice->ConsumedFrames.store(0, std::memory_order_relaxed);
    }
}

std::uint32_t SpotlightAction_GetAvailableFrames(std::uint32_t voiceId)
{
    PcmVoice* voice = FindVoice(voiceId);
    if (voice == nullptr)
    {
        return 0;
    }

    const std::uint64_t produced =
        voice->ProducedFrames.load(std::memory_order_acquire);
    const std::uint64_t consumed =
        voice->ConsumedFrames.load(std::memory_order_acquire);
    return produced >= consumed
        ? static_cast<std::uint32_t>(produced - consumed)
        : 0;
}

std::uint32_t SpotlightAction_GetConsumedFrames(std::uint32_t voiceId)
{
    PcmVoice* voice = FindVoice(voiceId);
    if (voice == nullptr)
    {
        return 0;
    }

    const std::uint64_t consumed =
        voice->ConsumedFrames.load(std::memory_order_acquire);
    return static_cast<std::uint32_t>(
        std::min<std::uint64_t>(
            consumed,
            std::numeric_limits<std::uint32_t>::max()));
}

std::uint32_t SpotlightAction_PushPcm(
    std::uint32_t voiceId,
    const float* interleavedData,
    std::uint32_t frames,
    std::uint32_t channels)
{
    if (interleavedData == nullptr || frames == 0)
    {
        return 0;
    }

    PcmVoice* voice = FindVoice(voiceId);
    if (voice == nullptr || channels != voice->Channels || voice->Ring.empty())
    {
        return 0;
    }

    const std::uint64_t produced =
        voice->ProducedFrames.load(std::memory_order_relaxed);
    const std::uint64_t consumed =
        voice->ConsumedFrames.load(std::memory_order_acquire);
    const std::uint64_t available =
        produced >= consumed ? produced - consumed : 0;
    const std::uint64_t freeFrames = RingCapacityFrames > available
        ? RingCapacityFrames - available
        : 0;
    const std::uint32_t framesToWrite = static_cast<std::uint32_t>(
        std::min<std::uint64_t>(frames, freeFrames));

    for (std::uint32_t frame = 0; frame < framesToWrite; ++frame)
    {
        const std::uint64_t ringFrame = (produced + frame) & RingMask;
        const float* source = interleavedData + frame * channels;
        float* destination = voice->Ring.data() + ringFrame * channels;
        std::memcpy(destination, source, channels * sizeof(float));
    }

    voice->ProducedFrames.store(
        produced + framesToWrite,
        std::memory_order_release);
    return framesToWrite;
}

void SpotlightAction_MarkFinished(std::uint32_t voiceId)
{
    if (PcmVoice* voice = FindVoice(voiceId))
    {
        voice->Finished.store(true, std::memory_order_release);
    }
}

void SpotlightAction_ClearVoice(std::uint32_t voiceId)
{
    if (PcmVoice* voice = FindVoice(voiceId))
    {
        voice->ConsumedFrames.store(
            voice->ProducedFrames.load(std::memory_order_acquire),
            std::memory_order_release);
        voice->Finished.store(false, std::memory_order_relaxed);
    }
}

bool SpotlightAction_ReadPcm(
    std::uint32_t voiceId,
    float* interleavedOutput,
    std::uint32_t frames,
    std::uint32_t channels,
    bool& outFinished)
{
    outFinished = true;
    if (interleavedOutput == nullptr || frames == 0 || channels == 0)
    {
        return false;
    }

    PcmVoice* voice = FindVoice(voiceId);
    if (voice == nullptr || channels != voice->Channels || voice->Ring.empty())
    {
        std::memset(
            interleavedOutput,
            0,
            static_cast<std::size_t>(frames) * channels * sizeof(float));
        return true;
    }

    const std::uint64_t produced =
        voice->ProducedFrames.load(std::memory_order_acquire);
    const std::uint64_t consumed =
        voice->ConsumedFrames.load(std::memory_order_relaxed);
    const std::uint64_t available =
        produced >= consumed ? produced - consumed : 0;
    const std::uint32_t framesToRead = static_cast<std::uint32_t>(
        std::min<std::uint64_t>(frames, available));

    for (std::uint32_t frame = 0; frame < framesToRead; ++frame)
    {
        const std::uint64_t ringFrame = (consumed + frame) & RingMask;
        const float* source = voice->Ring.data() + ringFrame * channels;
        float* destination = interleavedOutput + frame * channels;
        std::memcpy(destination, source, channels * sizeof(float));
    }

    if (framesToRead < frames)
    {
        std::memset(
            interleavedOutput + framesToRead * channels,
            0,
            static_cast<std::size_t>(frames - framesToRead) *
                channels * sizeof(float));
    }

    voice->ConsumedFrames.store(
        consumed + framesToRead,
        std::memory_order_release);

    const bool finished =
        voice->Finished.load(std::memory_order_acquire) &&
        framesToRead == available;
    outFinished = finished;
    return true;
}
