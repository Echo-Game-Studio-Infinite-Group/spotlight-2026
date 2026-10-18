using UnityEngine;

// 回调只采集碰撞，Motor 在 Move 完成后统一决定状态，避免同一帧的回调顺序重复授予蹬墙。
public sealed class MovementContacts
{
    private readonly CharacterController _controller;
    private readonly MovementParams _params;
    private readonly MovementGrounding _grounding;
    private readonly RaycastHit[] _hits = new RaycastHit[16];
    private readonly Collider[] _overlaps = new Collider[32];
    private readonly Vector3[] _normals = new Vector3[16];
    private readonly WallContact[] _wallContacts = new WallContact[16];
    private int _normalCount;
    private int _wallContactCount;
    private Vector3 _incoming;
    private bool _sliding;
    private bool _collectHits = true;
    private bool _supported;
    private GroundSupport _support;
    private Vector3 _frontSupportFeet;
    private bool _frontSupported;

    public bool Grounded { get; private set; }
    public bool HasWall { get; private set; }
    // 不可滑接触会结束划墙；是否触发撞击惩罚由表面标签分别决定。
    public bool HasNonSlideWall { get; private set; }
    public Vector3 WallNormal { get; private set; }
    public Vector3 WallPoint { get; private set; }
    public Vector3 WallIncoming { get; private set; }
    public float WallAngle { get; private set; }
    public GroundSupport Support => _support;
    public float StepHeightDelta { get; private set; }

    public MovementContacts(CharacterController controller, MovementParams parameters)
    {
        _controller = controller;
        _params = parameters;
        _grounding = new MovementGrounding(controller, parameters);
    }

    public void RecordHit(ControllerColliderHit hit)
    {
        if (!_collectHits || ((_params.CollisionMask.value & (1 << hit.gameObject.layer)) == 0)) return;
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
        bool sliding = false, float slideForwardExtent = 0f, bool wasGrounded = false)
    {
        HasWall = false;
        HasNonSlideWall = false;
        WallAngle = -1f;
        _wallContactCount = 0;
        _sliding = sliding;
        StepHeightDelta = 0f;
        float contactRange = _params.GroundTolerance + _controller.skinWidth;
        // 短暂离地后落回近地范围，也必须先恢复支撑，再处理台阶；否则同帧的立面接触会先裁速。
        // 空中只接受脚下台面，不能借前方较高的台阶提前吸附。
        bool moving = velocity.x != 0f || velocity.z != 0f;
        _supported = (wasGrounded || moving) && velocity.y <= 0f &&
            _grounding.TrySupport(_controller.transform.position, wasGrounded ? contactRange : 0f,
                contactRange, out _support, _support);
        float segmentLength = Mathf.Max(0.01f, _controller.radius * _params.MoveSegmentRadiusRatio);
        int count = Mathf.Clamp(Mathf.CeilToInt(velocity.magnitude * dt / segmentLength), 1, _params.MaxMoveSegments);
        CollisionFlags last = CollisionFlags.None;
        for (int i = 0; i < count; i++)
        {
            _normalCount = 0;
            _incoming = velocity;
            Vector3 displacement = velocity * (dt / count);
            if (_supported)
            {
                Vector3 horizontal = MovementMath.Horizontal(displacement);
                displacement.y = -Vector3.Dot(horizontal, _support.Normal) / _support.Normal.y;
                if (_grounding.TryStep(displacement, _controller.height, out float lift, out GroundSupport landing))
                {
                    float before = _controller.transform.position.y;
                    AuxiliaryMove(Vector3.up * (lift + _params.GroundTolerance), pathPoint);
                    StepHeightDelta += _controller.transform.position.y - before;
                    displacement.y = 0f;
                    _support = landing;
                }
            }
            if (slideForwardExtent > _controller.radius)
                GuardSlideFront(ref displacement, ref velocity, slideForwardExtent);
            last = _controller.Move(displacement);
            pathPoint?.Invoke(_controller.transform.position);
            for (int j = 0; j < _normalCount; j++)
            {
                Vector3 normal = _normals[j];
                if (IsBlockingSurface(normal))
                {
                    Vector3 clipped = MovementMath.ClipIntoSurface(MovementMath.Horizontal(velocity), MovementMath.Horizontal(normal).normalized);
                    velocity.x = clipped.x;
                    velocity.z = clipped.z;
                }
            }
            if ((last & CollisionFlags.Above) != 0 && velocity.y > 0f) velocity.y = 0f;
            Vector3 feet = _controller.transform.position;
            float drop = _supported ? _params.MaxStepDown : contactRange;
            bool hasSupport = velocity.y <= 0f && _grounding.TrySupport(feet, contactRange, drop, out _support, _support);
            if (hasSupport && _supported)
            {
                float correction = Mathf.Min(0f, _support.RootHeight - feet.y);
                if (correction < 0f)
                {
                    AuxiliaryMove(Vector3.up * correction, pathPoint);
                    // 坡面高度已经包含在运动位移里，只有离散阶差才交给表现层消抖。
                    if (_support.Normal.y > .999f) StepHeightDelta += _controller.transform.position.y - feet.y;
                }
                hasSupport = _grounding.TrySupport(_controller.transform.position, contactRange, contactRange, out _support, _support);
            }
            _supported = hasSupport && (_supported || (last & CollisionFlags.Below) != 0);
        }
        Grounded = _supported && velocity.y <= 0f;
        if (!Grounded) _support = default;
    }

    private void AuxiliaryMove(Vector3 displacement, System.Action<Vector3> pathPoint)
    {
        _collectHits = false;
        _controller.Move(displacement);
        _collectHits = true;
        pathPoint?.Invoke(_controller.transform.position);
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
                if (TraversableFront(hit.collider, hitPoint)) continue;
                if (hitNormal == Vector3.zero || !IsSlideBlockingSurface(hit.collider, hitPoint, hitNormal)
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
                if (TraversableFront(collider, closest)) continue;
                Vector3 outward = (start - closest).normalized;
                if (outward == Vector3.zero || !IsSlideBlockingSurface(collider, closest, outward)
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
            Vector3 closest = collider.ClosestPoint(start);
            if (TraversableFront(collider, closest)) continue;
            Vector3 outward = (start - closest).normalized;
            if (outward == Vector3.zero || IsSlideBlockingSurface(collider, closest, outward)) return false;
        }
        return true;
    }

    private void SlideCapsule(float extent, out Vector3 start, out Vector3 end, out float radius)
    {
        radius = _controller.radius;
        start = _controller.transform.position + Vector3.up * radius;
        end = start + _controller.transform.forward * Mathf.Max(0f, extent - radius);
        _frontSupported = false;
        Vector3 feet = _controller.transform.position;
        float range = _params.GroundTolerance + _controller.skinWidth;
        if (!_grounding.TrySupport(feet, range, range, out GroundSupport cursor, _support)) return;
        feet.y = cursor.RootHeight;
        float length = Mathf.Max(0f, extent - radius);
        int samples = Mathf.Max(1, Mathf.CeilToInt(length / Mathf.Max(radius, .1f)));
        for (int i = 1; i <= samples; i++)
        {
            Vector3 next = _controller.transform.position + _controller.transform.forward * (length * i / samples);
            next.y = feet.y;
            if (!_grounding.TrySupport(next, _params.MaxStepUp, _params.MaxStepDown, out GroundSupport support, cursor)) return;
            if (support.RootHeight > feet.y + _params.MaxStepUp + .001f) return;
            feet = new Vector3(next.x, support.RootHeight, next.z);
            cursor = support;
        }
        _frontSupportFeet = feet;
        _frontSupported = true;
        end.y = feet.y + radius;
    }

    private bool TraversableFront(Collider collider, Vector3 point)
    {
        if (!_frontSupported) return false;
        float top = Mathf.Max(_controller.transform.position.y, _frontSupportFeet.y) + _params.MaxStepUp;
        Vector3 origin = point + _controller.transform.forward * _params.GroundTolerance;
        origin.y = top + _params.GroundTolerance;
        if (!_grounding.TrySurface(origin, top - point.y + _params.GroundTolerance * 2f, collider, out RaycastHit surface)
            || !_grounding.IsWalkable(surface.normal)) return false;
        Vector3 feet = new Vector3(origin.x, surface.point.y + _params.MaxStepUp + _params.GroundTolerance, origin.z);
        return _grounding.CanOccupy(feet, _params.SlideCapsuleHeight);
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
    private bool IsBlockingSurface(Vector3 normal) => IsWall(normal) || normal.y >= 0f && !_grounding.IsWalkable(normal);
    private bool IsSlideBlockingSurface(Collider collider, Vector3 point, Vector3 normal)
    {
        if (IsWall(normal)) return true;
        if (normal.y < 0f || _grounding.IsWalkable(normal)) return false;
        // 胶囊在台阶棱边的分离方向可能很陡；陡坡阻挡只接受真实表面法线。
        Vector3 origin = point - MovementMath.Horizontal(normal).normalized * _params.GroundTolerance
            + Vector3.up * _params.GroundTolerance;
        return _grounding.TrySurface(origin, _params.GroundTolerance * 3f, collider, out RaycastHit surface)
            && !_grounding.IsWalkable(surface.normal);
    }

    private void Capsule(float height, float radius, out Vector3 bottom, out Vector3 top, out float queryRadius)
    {
        queryRadius = Mathf.Max(0.01f, radius - _controller.skinWidth);
        Vector3 feet = _controller.transform.position;
        bottom = feet + Vector3.up * radius;
        top = feet + Vector3.up * Mathf.Max(radius, height - radius);
    }
}
