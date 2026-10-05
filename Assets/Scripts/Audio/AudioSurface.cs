using UnityEngine;

[DisallowMultipleComponent]
public sealed class AudioSurface : MonoBehaviour
{
    [SerializeField] private AudioCueDefinition _footstepCue;

    public AudioCueDefinition FootstepCue
    {
        get => _footstepCue;
        set => _footstepCue = value;
    }

    public static AudioCueDefinition ResolveCue(Collider collider)
    {
        if (collider == null) return null;
        AudioSurface surface = collider.GetComponentInParent<AudioSurface>();
        return surface != null ? surface.FootstepCue : null;
    }

    public static AudioCueDefinition ResolveCue(RaycastHit hit)
    {
        return hit.collider != null ? ResolveCue(hit.collider) : null;
    }
}
