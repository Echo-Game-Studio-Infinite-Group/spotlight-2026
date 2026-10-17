using UnityEngine;

// 回调只采集碰撞，Motor 在 Move 完成后统一决定状态，避免同一帧的回调顺序重复授予蹬墙。
public sealed class MovementContacts
{
    private readonly CharacterController _controller;
    private readonly MovementParams _params;
    private readonly RaycastHit[] _hits = new RaycastHit[16];
    private readonly Collider[] _overlaps = new Collider[32];
    private readonly Vector3[] _normals = new Vector3[16];
    private readonly WallContact[] _wallContacts = new WallContact[16];
    private int _normalCount;
    private int _wallContactCount;
    private Vector3 _incoming;
    private bool _sliding;

    public bool Grounded { get; private set; }
    public bool HasWall { get; private set; }
    // 不可滑接触会结束划墙；是否触发撞击惩罚由表面标签分别决定。
    public bool HasNonSlideWall { get; private set; }
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
        RecordWall(hit.point, hit.normal, hit.collider);
    }

    private void RecordWall(Vector3 point, Vector3 hitNormal, Collider surface)
    {
        if (!AllowsWallSlide(surface)) HasNonSlideWall = true;
        // 轻型敌人仍阻挡位移，但不能授予划墙或触发撞墙瘫倒；滑铲前伸检测也走此入口。
        if (HasTagInParents(surface, "NoWallInteraction")) return;
        Vector3 normal = MovementMath.Horizontal(hitNormal).normalized;
        float angle = MovementMath.ApproachAngle(_incoming, normal);
        // 同一面墙会在分段 Move 中反复回调；保留不同法线，避免旧墙覆盖墙角接触。
        for (int i = 0; i < _wallContactCount; i++)
            if (Vector3.Dot(_wallContacts[i].Normal, normal) > 0.9999f)
            {
                if (Vector3.Dot(_incoming, normal) < Vector3.Dot(_wallContacts[i].IncomingVelocity, normal))
                    _wallContacts[i] = new WallContact(point, normal, _incoming, angle, _sliding);
                UpdatePrimaryWall(point, normal, angle);
                return;
            }
        if (_wallContactCount < _wallContacts.Length)
            _wallContacts[_wallContactCount++] = new WallContact(point, normal, _incoming, angle, _sliding);
        UpdatePrimaryWall(point, normal, angle);
    }

    private void UpdatePrimaryWall(Vector3 point, Vector3 normal, float angle)
    {
        if (angle < 0f || (HasWall && angle >= WallAngle)) return;
        HasWall = true;
        WallAngle = angle;
        WallNormal = normal;
        WallPoint = point;
        WallIncoming = _incoming;
    }

    public void Move(ref Vector3 velocity, float dt, System.Action<Vector3> pathPoint = null,
        bool sliding = false, float slideForwardExtent = 0f)
    {
        HasWall = false;
        HasNonSlideWall = false;
        WallAngle = -1f;
        _wallContactCount = 0;
        _sliding = sliding;
        float segmentLength = Mathf.Max(0.01f, _controller.radius * _params.MoveSegmentRadiusRatio);
        int count = Mathf.Clamp(Mathf.CeilToInt(velocity.magnitude * dt / segmentLength), 1, _params.MaxMoveSegments);
        CollisionFlags last = CollisionFlags.None;
        for (int i = 0; i < count; i++)
        {
            _normalCount = 0;
            _incoming = velocity;
            Vector3 displacement = velocity * (dt / count);
            if (slideForwardExtent > _controller.radius)
                GuardSlideFront(ref displacement, ref velocity, slideForwardExtent);
            last = _controller.Move(displacement);
            pathPoint?.Invoke(_controller.transform.position);
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

    private void GuardSlideFront(ref Vector3 displacement, ref Vector3 velocity, float extent)
    {
        SlideCapsule(extent, out Vector3 start, out Vector3 end, out float radius);
        // 水平面最多需要两个独立墙面约束；保留沿墙位移，不能把斜擦墙也当成正撞清速。
        for (int plane = 0; plane < 2; plane++)
        {
            Vector3 horizontal = MovementMath.Horizontal(displacement);
            float distance = horizontal.magnitude;
            if (distance < 0.000001f) break;
            Vector3 direction = horizontal / distance;
            float allowed = distance;
            Vector3 normal = Vector3.zero, point = Vector3.zero;
            Collider surface = null;
            int count = Physics.CapsuleCastNonAlloc(start, end, radius, direction, _hits,
                distance + _controller.skinWidth, _params.CollisionMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = _hits[i];
                if (hit.transform.IsChildOf(_controller.transform)) continue;
                Vector3 hitPoint = hit.point, hitNormal = hit.normal;
                if (hit.distance <= 0f)
                {
                    hitPoint = hit.collider.ClosestPoint(start);
                    hitNormal = (start - hitPoint).normalized;
                }
                if (hitNormal == Vector3.zero || !IsWall(hitNormal)
                    || Vector3.Dot(direction, hitNormal) >= -0.000001f) continue;
                float travel = Mathf.Max(0f, hit.distance - _controller.skinWidth);
                if (travel >= allowed) continue;
                allowed = travel;
                normal = MovementMath.Horizontal(hitNormal).normalized;
                point = hitPoint;
                surface = hit.collider;
            }
            // 起手或转向后前伸部分可能已接触墙；补重叠查询，并使用真实外法线允许后退脱困。
            count = Physics.OverlapCapsuleNonAlloc(start, end, radius, _overlaps,
                _params.CollisionMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                Collider collider = _overlaps[i];
                if (collider.transform.IsChildOf(_controller.transform)) continue;
                Vector3 closest = collider.ClosestPoint(start);
                Vector3 outward = (start - closest).normalized;
                if (outward == Vector3.zero || !IsWall(outward)
                    || Vector3.Dot(direction, outward) >= -0.000001f) continue;
                allowed = 0f;
                normal = MovementMath.Horizontal(outward).normalized;
                point = closest;
                surface = collider;
            }
            if (normal == Vector3.zero) break;
            RecordWall(point, normal, surface);
            Vector3 remainder = horizontal * (1f - allowed / distance);
            horizontal -= remainder;
            horizontal += MovementMath.ClipIntoSurface(remainder, normal);
            displacement.x = horizontal.x; displacement.z = horizontal.z;
            Vector3 clipped = MovementMath.ClipIntoSurface(MovementMath.Horizontal(velocity), normal);
            velocity.x = clipped.x; velocity.z = clipped.z;
        }
    }

    public bool HasSlideFrontSpace(float extent)
    {
        if (extent <= _controller.radius) return true;
        SlideCapsule(extent, out Vector3 start, out Vector3 end, out float radius);
        int count = Physics.OverlapCapsuleNonAlloc(start, end, radius, _overlaps,
            _params.CollisionMask, QueryTriggerInteraction.Ignore);
        if (count == _overlaps.Length) return false;
        for (int i = 0; i < count; i++)
        {
            Collider collider = _overlaps[i];
            if (collider.transform.IsChildOf(_controller.transform)) continue;
            Vector3 outward = (start - collider.ClosestPoint(start)).normalized;
            if (outward == Vector3.zero || IsWall(outward)) return false;
        }
        return true;
    }

    private void SlideCapsule(float extent, out Vector3 start, out Vector3 end, out float radius)
    {
        radius = _controller.radius;
        start = _controller.transform.position + Vector3.up * radius;
        end = start + _controller.transform.forward * Mathf.Max(0f, extent - radius);
    }

    public bool TryGetWallImpact(float minimumAngle, out WallContact impact)
    {
        impact = default;
        float strongest = 0f;
        for (int i = 0; i < _wallContactCount; i++)
        {
            WallContact contact = _wallContacts[i];
            if (contact.ApproachAngle < 0f || contact.ApproachAngle + 0.01f < minimumAngle) continue;
            float into = -Vector3.Dot(MovementMath.Horizontal(contact.IncomingVelocity), contact.Normal);
            if (into <= strongest) continue;
            strongest = into;
            impact = contact;
        }
        return strongest > 0f;
    }

    public bool TryGetWallTransition(Vector3 currentNormal, float seamAngle, out WallContact transition)
    {
        transition = default;
        bool found = false;
        for (int i = 0; i < _wallContactCount; i++)
        {
            WallContact contact = _wallContacts[i];
            if (Vector3.Angle(currentNormal, contact.Normal) <= seamAngle || contact.ApproachAngle < 0f)
                continue;
            // 多墙同时接触时，正面撞上的墙优先决定这次是否能继续滑行。
            if (!found || transition.ApproachAngle < contact.ApproachAngle)
            { transition = contact; found = true; }
        }
        return found;
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
            if (!MatchesWall(hit, normal, alignment)) continue;
            if (hit.distance >= nearest) continue;
            nearest = hit.distance;
            contact = hit;
        }
        if (nearest < float.PositiveInfinity) return true;

        // 胶囊已贴住墙时 CapsuleCast 的起始体积会与墙重叠，投射可能没有命中。
        // 沿胶囊轴线补射线，只接受朝向与当前墙一致的近距离实体墙。
        float reach = _controller.radius + distance + _controller.skinWidth * 2f;
        Vector3 middle = (bottom + top) * 0.5f;
        for (int j = 0; j < 3; j++)
        {
            Vector3 origin = j == 0 ? middle : j == 1 ? bottom : top;
            count = Physics.RaycastNonAlloc(origin, -normal, _hits, reach,
                _params.CollisionMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = _hits[i];
                if (!MatchesWall(hit, normal, alignment)) continue;
                if (hit.distance >= nearest) continue;
                nearest = hit.distance;
                contact = hit;
            }
        }
        return nearest < float.PositiveInfinity;
    }

    private bool MatchesWall(RaycastHit hit, Vector3 normal, float alignment)
    {
        if (hit.collider == null || hit.transform.IsChildOf(_controller.transform) || !IsWall(hit.normal)) return false;
        return Vector3.Dot(MovementMath.Horizontal(hit.normal).normalized, normal) >= alignment;
    }

    public bool CanResize(float height, float radius)
    {
        Capsule(height, radius, out Vector3 bottom, out Vector3 top, out float queryRadius);
        return IsCapsuleFree(bottom, top, queryRadius);
    }

    public bool HasHeadroom(float standingHeight)
    {
        if (standingHeight <= _controller.height) return true;
        Capsule(_controller.height, _controller.radius, out _, out Vector3 currentTop, out float queryRadius);
        Vector3 standingTop = _controller.transform.position + Vector3.up
            * Mathf.Max(_controller.radius, standingHeight - _controller.radius);
        // 只检查当前头顶到站立高度的空间，侧边墙体不能授予免滑铲摩擦。
        return IsCapsuleFree(currentTop, standingTop, queryRadius);
    }

    private bool IsCapsuleFree(Vector3 bottom, Vector3 top, float queryRadius)
    {
        int count = Physics.OverlapCapsuleNonAlloc(bottom, top, queryRadius, _overlaps,
            _params.CollisionMask, QueryTriggerInteraction.Ignore);
        if (count == _overlaps.Length) return false;
        for (int i = 0; i < count; i++)
            if (!_overlaps[i].transform.IsChildOf(_controller.transform)) return false;
        return true;
    }

    public static bool AllowsWallSlide(Collider surface)
    {
        // 根物体的标记覆盖子碰撞体，保留 Enemy Layer 供战斗和索敌使用。
        return !HasTagInParents(surface, "NoWallInteraction") && !HasTagInParents(surface, "NoWallSlide");
    }

    private static bool HasTagInParents(Collider surface, string tag)
    {
        for (Transform current = surface.transform; current != null; current = current.parent)
            if (current.CompareTag(tag)) return true;
        return false;
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
