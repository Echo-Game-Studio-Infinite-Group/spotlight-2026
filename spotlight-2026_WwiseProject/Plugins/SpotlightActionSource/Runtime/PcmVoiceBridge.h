#pragma once

#include <cstdint>

#ifdef _WIN32
#define SPOTLIGHT_BRIDGE_API extern "C" __declspec(dllexport)
#else
#define SPOTLIGHT_BRIDGE_API extern "C" __attribute__((visibility("default")))
#endif

constexpr std::uint32_t SpotlightActionVoiceMagic = 0x53504C31u;

struct SpotlightActionVoiceInfo
{
    std::uint32_t Magic;
    std::uint32_t VoiceId;
    std::uint32_t Channels;
    std::uint32_t SampleRate;
};

SPOTLIGHT_BRIDGE_API bool SpotlightAction_CreateVoice(
    std::uint32_t voiceId,
    std::uint32_t channels,
    std::uint32_t sampleRate);

SPOTLIGHT_BRIDGE_API void SpotlightAction_DestroyVoice(
    std::uint32_t voiceId);

SPOTLIGHT_BRIDGE_API std::uint32_t SpotlightAction_GetAvailableFrames(
    std::uint32_t voiceId);

SPOTLIGHT_BRIDGE_API std::uint32_t SpotlightAction_GetConsumedFrames(
    std::uint32_t voiceId);

SPOTLIGHT_BRIDGE_API std::uint32_t SpotlightAction_PushPcm(
    std::uint32_t voiceId,
    const float* interleavedData,
    std::uint32_t frames,
    std::uint32_t channels);

SPOTLIGHT_BRIDGE_API void SpotlightAction_MarkFinished(
    std::uint32_t voiceId);

SPOTLIGHT_BRIDGE_API void SpotlightAction_ClearVoice(
    std::uint32_t voiceId);

bool SpotlightAction_ReadPcm(
    std::uint32_t voiceId,
    float* interleavedOutput,
    std::uint32_t frames,
    std::uint32_t channels,
    bool& outFinished);
