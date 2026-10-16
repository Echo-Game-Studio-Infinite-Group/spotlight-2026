#include "SpotlightActionSourceParams.h"

CSpotlightActionSourceParams::CSpotlightActionSourceParams()
{
}

CSpotlightActionSourceParams::CSpotlightActionSourceParams(
    const CSpotlightActionSourceParams&)
{
}

CSpotlightActionSourceParams::~CSpotlightActionSourceParams()
{
}

AK::IAkPluginParam* CSpotlightActionSourceParams::Clone(
    AK::IAkPluginMemAlloc* in_pAllocator)
{
    return AK_PLUGIN_NEW(in_pAllocator, CSpotlightActionSourceParams(*this));
}

AKRESULT CSpotlightActionSourceParams::Init(
    AK::IAkPluginMemAlloc*,
    const void*,
    AkUInt32)
{
    return AK_Success;
}

AKRESULT CSpotlightActionSourceParams::Term(
    AK::IAkPluginMemAlloc* in_pAllocator)
{
    AK_PLUGIN_DELETE(in_pAllocator, this);
    return AK_Success;
}

AKRESULT CSpotlightActionSourceParams::SetParamsBlock(
    const void*,
    AkUInt32)
{
    return AK_Success;
}

AKRESULT CSpotlightActionSourceParams::SetParam(
    AkPluginParamID,
    const void*,
    AkUInt32)
{
    return AK_InvalidParameter;
}

