using UnityEngine;

public readonly struct AttackVolumePose
{
    public readonly Vector3 Center, Extents, Root;
    public readonly Quaternion Rotation;

    public AttackVolumePose(Vector3 center, Vector3 extents, Quaternion rotation, Vector3 root)
    { Center = center; Extents = extents; Rotation = rotation; Root = root; }
}
