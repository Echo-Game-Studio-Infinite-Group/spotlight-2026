#include "SpotlightActionSourcePlugin.h"

#include <AK/AkWwiseSDKVersion.h>
#include <AK/SoundEngine/Common/IAkPlugin.h>

#include <cstring>

namespace
{
}

AK::IAkPlugin* CreateSpotlightActionSource(AK::IAkPluginMemAlloc* in_pAllocator)
{
    return AK_PLUGIN_NEW(in_pAllocator, CSpotlightActionSource());
}

AK::IAkPluginParam* CreateSpotlightActionSourceParams(
    AK::IAkPluginMemAlloc* in_pAllocator)
{
    return AK_PLUGIN_NEW(in_pAllocator, CSpotlightActionSourceParams());
}

AK_IMPLEMENT_PLUGIN_FACTORY(SpotlightActionSource, AkPluginTypeSource, 0, 4242)

CSpotlightActionSource::CSpotlightActionSource()
{
}

CSpotlightActionSource::~CSpotlightActionSource()
{
}

AKRESULT CSpotlightActionSource::Init(
    AK::IAkPluginMemAlloc*,
    AK::IAkSourcePluginContext* in_pSourcePluginContext,
    AK::IAkPluginParam*,
    AkAudioFormat& io_rFormat)
{
    void* customData = nullptr;
    AkUInt32 customDataSize = 0;
    in_pSourcePluginContext->GetPluginCustomGameData(
        customData,
        customDataSize);
    if (customData == nullptr ||
        customDataSize < sizeof(SpotlightActionVoiceInfo))
    {
        return AK_Fail;
    }

    SpotlightActionVoiceInfo voiceInfo;
    std::memcpy(&voiceInfo, customData, sizeof(voiceInfo));
    if (voiceInfo.Magic != SpotlightActionVoiceMagic ||
        voiceInfo.VoiceId == 0 ||
        voiceInfo.Channels == 0 ||
        voiceInfo.Channels > 2 ||
        voiceInfo.SampleRate == 0)
    {
        return AK_Fail;
    }

    m_voiceId = voiceInfo.VoiceId;
    m_channels = voiceInfo.Channels;
    m_sampleRate = voiceInfo.SampleRate;
    m_voiceValid = SpotlightAction_GetAvailableFrames(m_voiceId) > 0;
    m_finished = !m_voiceValid;
    io_rFormat.channelConfig.SetStandard(
        m_channels == 2 ? AK_SPEAKER_SETUP_STEREO : AK_SPEAKER_SETUP_MONO);
    io_rFormat.uSampleRate = m_sampleRate;
    return AK_Success;
}

AKRESULT CSpotlightActionSource::Term(AK::IAkPluginMemAlloc* in_pAllocator)
{
    AK_PLUGIN_DELETE(in_pAllocator, this);
    return AK_Success;
}

AKRESULT CSpotlightActionSource::Reset()
{
    m_finished = !m_voiceValid;
    return AK_Success;
}

AKRESULT CSpotlightActionSource::GetPluginInfo(AkPluginInfo& out_rPluginInfo)
{
    out_rPluginInfo.eType = AkPluginTypeSource;
    out_rPluginInfo.bIsInPlace = true;
    out_rPluginInfo.uBuildVersion = AK_WWISESDK_VERSION_COMBINED;
    return AK_Success;
}

void CSpotlightActionSource::Execute(AkAudioBuffer* io_pBuffer)
{
    const AkUInt32 maxFrames = io_pBuffer->MaxFrames();
    if (!m_voiceValid || m_channels == 0 || m_finished)
    {
        io_pBuffer->uValidFrames = 0;
        io_pBuffer->eState = AK_NoMoreData;
        return;
    }

    constexpr AkUInt32 StackFrames = 1024;
    float scratch[StackFrames * 2] = {};
    const AkUInt32 framesToRead = maxFrames < StackFrames
        ? maxFrames
        : StackFrames;
    bool finished = false;
    SpotlightAction_ReadPcm(
        m_voiceId,
        scratch,
        framesToRead,
        m_channels,
        finished);

    for (AkUInt32 channel = 0; channel < m_channels; ++channel)
    {
        AkSampleType* output = io_pBuffer->GetChannel(channel);
        if (output == nullptr)
        {
            continue;
        }

        for (AkUInt32 frame = 0; frame < framesToRead; ++frame)
        {
            output[frame] = scratch[frame * m_channels + channel];
        }
    }

    io_pBuffer->uValidFrames = static_cast<AkUInt16>(framesToRead);
    io_pBuffer->eState = finished ? AK_NoMoreData : AK_DataReady;
    m_finished = finished;
}

AkReal32 CSpotlightActionSource::GetDuration() const
{
    return 0.0f;
}

AKRESULT CSpotlightActionSource::StopLooping()
{
    return AK_Success;
}

AKRESULT CSpotlightActionSource::TimeSkip(AkUInt32&)
{
    return AK_Success;
}
