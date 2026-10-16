#pragma once

#include "PcmVoiceBridge.h"
#include "SpotlightActionSourceParams.h"

#include <cstdint>

class CSpotlightActionSource final : public AK::IAkSourcePlugin
{
public:
    CSpotlightActionSource();
    virtual ~CSpotlightActionSource();

    virtual AKRESULT Init(
        AK::IAkPluginMemAlloc* in_pAllocator,
        AK::IAkSourcePluginContext* in_pSourcePluginContext,
        AK::IAkPluginParam* in_pParams,
        AkAudioFormat& io_rFormat) override;
    virtual AKRESULT Term(AK::IAkPluginMemAlloc* in_pAllocator) override;
    virtual AKRESULT Reset() override;
    virtual AKRESULT GetPluginInfo(AkPluginInfo& out_rPluginInfo) override;
    virtual void Execute(AkAudioBuffer* io_pBuffer) override;
    virtual AkReal32 GetDuration() const override;
    virtual AKRESULT StopLooping() override;
    virtual AKRESULT TimeSkip(AkUInt32& io_uFrames) override;

private:
    std::uint32_t m_voiceId = 0;
    std::uint32_t m_channels = 0;
    std::uint32_t m_sampleRate = 0;
    bool m_voiceValid = false;
    bool m_finished = false;
};

