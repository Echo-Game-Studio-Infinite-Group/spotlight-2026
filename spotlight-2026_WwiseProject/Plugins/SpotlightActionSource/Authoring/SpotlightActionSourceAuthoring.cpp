#include "SpotlightActionSourceAuthoring.h"

#include <AK/AkWwiseSDKVersion.h>

bool SpotlightActionSourceAuthoring::GetBankParameters(
    const GUID&,
    AK::Wwise::Plugin::DataWriter&) const
{
    return true;
}

bool SpotlightActionSourceAuthoring::GetSourceDuration(
    double& out_dblMinDuration,
    double& out_dblMaxDuration) const
{
    out_dblMinDuration = 0.0;
    out_dblMaxDuration = 3600.0;
    return false;
}

AK_DEFINE_PLUGIN_CONTAINER(SpotlightActionSource);
AK_EXPORT_PLUGIN_CONTAINER(SpotlightActionSource);
AK_ADD_PLUGIN_CLASS_TO_CONTAINER(
    SpotlightActionSource,
    SpotlightActionSourceAuthoring,
    SpotlightActionSource);

DEFINE_PLUGIN_REGISTER_HOOK;
DEFINEDUMMYASSERTHOOK;

