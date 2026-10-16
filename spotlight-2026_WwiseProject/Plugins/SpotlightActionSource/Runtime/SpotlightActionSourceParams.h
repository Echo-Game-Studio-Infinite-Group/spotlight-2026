#pragma once

#include <AK/SoundEngine/Common/IAkPlugin.h>

class CSpotlightActionSourceParams final : public AK::IAkPluginParam
{
public:
    CSpotlightActionSourceParams();
    CSpotlightActionSourceParams(const CSpotlightActionSourceParams& in_rCopy);
    virtual ~CSpotlightActionSourceParams();

    virtual AK::IAkPluginParam* Clone(AK::IAkPluginMemAlloc* in_pAllocator) override;
    virtual AKRESULT Init(
        AK::IAkPluginMemAlloc* in_pAllocator,
        const void* in_pParamsBlock,
        AkUInt32 in_uBlockSize) override;
    virtual AKRESULT Term(AK::IAkPluginMemAlloc* in_pAllocator) override;
    virtual AKRESULT SetParamsBlock(
        const void* in_pParamsBlock,
        AkUInt32 in_uBlockSize) override;
    virtual AKRESULT SetParam(
        AkPluginParamID in_paramId,
        const void* in_pValue,
        AkUInt32 in_uParamSize) override;
};

