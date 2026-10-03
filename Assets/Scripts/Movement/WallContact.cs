using UnityEngine;

public readonly struct WallContact
{
    public readonly Vector3 Point;
    public readonly Vector3 Normal;
    public readonly Vector3 IncomingVelocity;
    public readonly float ApproachAngle;

    public WallContact(Vector3 point, Vector3 normal, Vector3 incomingVelocity, float approachAngle)
    {
        Point = point;
        Normal = normal;
        IncomingVelocity = incomingVelocity;
        ApproachAngle = approachAngle;
    }
}
