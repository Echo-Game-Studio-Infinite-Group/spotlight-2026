using UnityEngine;

// 回调只采集碰撞，Motor 在 Move 完成后统一决定状态，避免同一帧的回调顺序重复授予蹬墙。
public sealed class MovementContacts
{
    private readonly CharacterController _controller;
    private readonly MovementParams _params;
    private readonly RaycastHit[] _hits = new RaycastHit[16];
    private readonly Collider[] _overlaps = new Collider[32];
    private readonly Vector3[] _normals = new Vector3[16];
    private int _normalCount;
    private Vector3 _incoming;

    public bool Grounded { get; private set; }
    public bool HasWall { get; private set; }
    public Vector3 WallNormal { get; private set; }
    public Vector3 WallPoint { get; private set; }
    public Vector3 WallIncoming { get; private set; }
    public float WallAngle { get; private set; }

    public MovementContacts(CharacterController controller, MovementParams parameters)
    {
        _controller = controller;
        _params = parameters;
    }

    public void RecordHit(ControllerColliderHit hit)
    {
        if (_normalCount < _normals.Length) _normals[_normalCount++] = hit.normal;
        if (!IsWall(hit.normal) || ((_params.CollisionMask.value & (1 << hit.gameObject.layer)) == 0)) return;
        Vector3 normal = MovementMath.Horizontal(hit.normal).normalized;
        float angle = MovementMath.ApproachAngle(_incoming, normal);
        if (HasWall && angle <= WallAngle) return;
        HasWall = true;
        WallAngle = angle;
        WallNormal = normal;
        WallPoint = hit.point;
        WallIncoming = _incoming;
    }

    public void Move(ref Vector3 velocity, float dt)
    {
        HasWall = false;
        WallAngle = -1f;
        float segmentLength = Mathf.Max(0.01f, _controller.radius * _params.MoveSegmentRadiusRatio);
        int count = Mathf.Clamp(Mathf.CeilToInt(velocity.magnitude * dt / segmentLength), 1, _params.MaxMoveSegments);
        CollisionFlags last = CollisionFlags.None;
        for (int i = 0; i < count; i++)
        {
            _normalCount = 0;
            _incoming = velocity;
            last = _controller.Move(velocity * (dt / count));
            for (int j = 0; j < _normalCount; j++)
            {
                Vector3 normal = _normals[j];
                if (IsWall(normal))
                {
                    Vector3 clipped = MovementMath.ClipIntoSurface(MovementMath.Horizontal(velocity), MovementMath.Horizontal(normal).normalized);
                    velocity.x = clipped.x;
                    velocity.z = clipped.z;
                }
            }
            if ((last & CollisionFlags.Above) != 0 && velocity.y > 0f) velocity.y = 0f;
        }
        Grounded = (last & CollisionFlags.Below) != 0 && velocity.y <= 0f;
    }

    public bool ProbeWall(Vector3 normal, float distance, out RaycastHit contact)
    {
        Capsule(_controller.height, _controller.radius, out Vector3 bottom, out Vector3 top, out float radius);
        int count = Physics.CapsuleCastNonAlloc(bottom, top, radius, -normal, _hits,
            distance + _controller.skinWidth * 2f, _params.CollisionMask, QueryTriggerInteraction.Ignore);
        contact = default;
        float nearest = float.PositiveInfinity;
        float alignment = Mathf.Cos(_params.WallSeamAngle * Mathf.Deg2Rad);
        for (int i = 0; i < count; i++)
        {
            RaycastHit hit = _hits[i];
            if (hit.transform.IsChildOf(_controller.transform) || !IsWall(hit.normal)) continue;
            if (Vector3.Dot(MovementMath.Horizontal(hit.normal).normalized, normal) < alignment) continue;
            if (hit.distance >= nearest) continue;
            nearest = hit.distance;
            contact = hit;
        }
        return nearest < float.PositiveInfinity;
    }

    public bool CanResize(float height, float radius)
    {
        Capsule(height, radius, out Vector3 bottom, out Vector3 top, out float queryRadius);
        int count = Physics.OverlapCapsuleNonAlloc(bottom, top, queryRadius, _overlaps,
            _params.CollisionMask, QueryTriggerInteraction.Ignore);
        if (count == _overlaps.Length) return false;
        for (int i = 0; i < count; i++)
            if (!_overlaps[i].transform.IsChildOf(_controller.transform)) return false;
        return true;
    }

    private bool IsWall(Vector3 normal) => Mathf.Abs(normal.y) <= _params.WallNormalMaxUpDot;

    private void Capsule(float height, float radius, out Vector3 bottom, out Vector3 top, out float queryRadius)
    {
        queryRadius = Mathf.Max(0.01f, radius - _controller.skinWidth);
        Vector3 feet = _controller.transform.position;
        bottom = feet + Vector3.up * radius;
        top = feet + Vector3.up * Mathf.Max(radius, height - radius);
    }
}
