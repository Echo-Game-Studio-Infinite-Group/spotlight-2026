using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Volume settings for the showcase GTAO pass.
/// This version intentionally keeps only gray-screen AO and one full-screen debug output.
/// </summary>
[Serializable]
public class GroundTruthAmbientOcclusion : VolumeComponent, IPostProcessComponent
{
    [Header("GTAO")]
    [Tooltip("Apply GTAO through URP's _ScreenSpaceOcclusionTexture path.")]
    public BoolParameter enableAO = new BoolParameter(true);

    [Tooltip("Show the final blurred AO texture directly on screen.")]
    public BoolParameter debugAOToScreen = new BoolParameter(false);

    [Tooltip("1 = full resolution, 2 = half resolution, 4 = quarter resolution.")]
    public ClampedIntParameter downsamplingFactor = new ClampedIntParameter(1, 1, 4);

    [Tooltip("AO strength. Keep this moderate for lookdev; 0.5-1.2 is usually a good range.")]
    public ClampedFloatParameter intensity = new ClampedFloatParameter(1.0f, 0.0f, 4.0f);

    [Tooltip("How much AO affects direct lighting. 0 is physically safer; higher is more stylized.")]
    public ClampedFloatParameter directLightingStrength = new ClampedFloatParameter(0.25f, 0.0f, 1.0f);

    [Tooltip("World-space sampling radius.")]
    public ClampedFloatParameter radius = new ClampedFloatParameter(1.0f, 0.01f, 5.0f);

    [Tooltip("Higher values concentrate samples closer to the shaded point.")]
    public ClampedFloatParameter distributionPower = new ClampedFloatParameter(2.0f, 1.0f, 5.0f);

    [Tooltip("Fraction of the radius used for far-sample falloff.")]
    public ClampedFloatParameter falloffRange = new ClampedFloatParameter(0.1f, 0.01f, 1.0f);

    [Tooltip("Raises the horizon angle slightly to reduce flat-surface self-occlusion.")]
    public ClampedFloatParameter horizonBias = new ClampedFloatParameter(0.1f, 0.0f, 0.5f);

    [Header("Temporal Denoise")]
    [Tooltip("Optional temporal accumulation. Leave off first when tuning the AO shape.")]
    public BoolParameter temporalEnabled = new BoolParameter(false);

    [Tooltip("History blend weight. Higher is smoother but can leave trails.")]
    public ClampedFloatParameter temporalBlend = new ClampedFloatParameter(0.9f, 0.0f, 0.98f);

    [Tooltip("Variance clipping strength for the temporal history.")]
    public ClampedFloatParameter varianceClamp = new ClampedFloatParameter(1.0f, 0.25f, 4.0f);

    [Header("Multi-Bounce / SSDO")]
    [Tooltip("Color-correct the existing GTAO so occluded areas keep more material color instead of going gray-black.")]
    public BoolParameter enableMultiBounce = new BoolParameter(false);

    [Tooltip("Strength of the multi-bounce color recovery. This is safest between 0.25 and 0.7.")]
    public ClampedFloatParameter multiBounceStrength = new ClampedFloatParameter(0.5f, 0.0f, 1.0f);

    [Tooltip("Add a lightweight screen-space diffuse bounce on top of the lit scene.")]
    public BoolParameter enableSSDO = new BoolParameter(false);

    [Tooltip("SSDO bounce intensity. Keep low first because it is an additive approximation.")]
    public ClampedFloatParameter ssdoIntensity = new ClampedFloatParameter(0.25f, 0.0f, 3.0f);

    [Tooltip("SSDO sampling radius in screen pixels.")]
    public ClampedFloatParameter ssdoRadius = new ClampedFloatParameter(24.0f, 2.0f, 96.0f);

    [Tooltip("Number of SSDO taps. 4-8 is usually enough for a visible debug difference.")]
    public ClampedIntParameter ssdoSampleCount = new ClampedIntParameter(6, 2, 12);

    [Tooltip("Hard clamp for additive SSDO contribution, preventing white-out when the source color is very bright.")]
    public ClampedFloatParameter ssdoMaxContribution = new ClampedFloatParameter(0.25f, 0.02f, 2.0f);

    [Tooltip("Show only the SSDO bounce term on screen. Useful when judging whether SSDO is actually doing anything.")]
    public BoolParameter debugSSDOToScreen = new BoolParameter(false);

    public bool RequiresComposite()
    {
        return debugSSDOToScreen.value
            || (enableAO.value && enableMultiBounce.value && multiBounceStrength.value > 0.0f)
            || (enableSSDO.value && ssdoIntensity.value > 0.0f);
    }

    public bool IsActive()
    {
        return active && (debugAOToScreen.value || (enableAO.value && intensity.value > 0.0f) || RequiresComposite());
    }

    public bool IsTileCompatible()
    {
        return false;
    }
}
