#pragma once

#include <AK/Wwise/Plugin.h>

class SpotlightActionSourceAuthoring final
    : public AK::Wwise::Plugin::AudioPlugin
    , public AK::Wwise::Plugin::RequestHost
    , public AK::Wwise::Plugin::Source
{
public:
    virtual bool GetBankParameters(
        const GUID& in_guidPlatform,
        AK::Wwise::Plugin::DataWriter& in_dataWriter) const override;

    virtual bool GetSourceDuration(
        double& out_dblMinDuration,
        double& out_dblMaxDuration) const override;
};

AK_DECLARE_PLUGIN_CONTAINER(SpotlightActionSource);

