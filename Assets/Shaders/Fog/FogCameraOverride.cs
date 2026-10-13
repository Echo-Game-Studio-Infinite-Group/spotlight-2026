using UnityEngine;
/// <summary>Optional per-camera density and opacity; all other shared fog settings stay inherited.</summary>
[DisallowMultipleComponent, RequireComponent(typeof(Camera))]
public sealed class FogCameraOverride : MonoBehaviour
{
    [Min(0)] public float density = .004f;
    [Range(0,1)] public float maxOpacity = .035f;
}