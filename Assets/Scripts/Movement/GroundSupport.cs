using UnityEngine;

public readonly struct GroundSupport
{
    public readonly Collider Collider;
    public readonly Vector3 Point;
    public readonly Vector3 Normal;
    public readonly float RootHeight;
    public bool IsValid => Collider != null;
    public float Angle => IsValid ? Vector3.Angle(Vector3.up, Normal) : 0f;

    public GroundSupport(Collider collider, Vector3 point, Vector3 normal, float rootHeight)
    { Collider = collider; Point = point; Normal = normal; RootHeight = rootHeight; }
}
