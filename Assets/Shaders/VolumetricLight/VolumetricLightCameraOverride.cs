using UnityEngine;

/// <summary>Optional per-camera room visibility values; does not modify the shared feature.</summary>
[DisallowMultipleComponent, RequireComponent(typeof(Camera))]
public sealed class VolumetricLightCameraOverride : MonoBehaviour
{
    [Header("Optional camera-only scattering")]
    [Tooltip("Off inherits the original shared feature values. Enable only for this camera's lighting composition.")]
    public bool overrideScattering;
    [Min(0f)] public float intensity = 4f;
    [Min(0f)] public float density = 0.035f;
    [Range(-0.95f, 0.95f)] public float anisotropy = -0.839f;

    [Header("Camera visibility / occlusion")]
    [Range(0f, 1f), Tooltip("1 fully rejects shadowed scattering; the original feature values remain unchanged.")]
    public float shadowStrength = 1f;
    [Range(0f, 2f), Tooltip("World-space sample offset towards the main light. Large values can cross thin walls.")]
    public float shadowBias = 0.015f;
    [Min(1f)] public float maxDistance = 40f;
}
