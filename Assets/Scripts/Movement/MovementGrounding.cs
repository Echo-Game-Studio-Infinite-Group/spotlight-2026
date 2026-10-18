using UnityEngine;

// 探地和跨阶共用胶囊几何；查询不能写入墙面事件，也不能修改运动速度。
public sealed class MovementGrounding
{
    private readonly CharacterController _controller;
    private readonly MovementParams _params;
    private readonly RaycastHit[] _hits = new RaycastHit[32];
    private readonly RaycastHit[] _rays = new RaycastHit[16];
    private readonly Collider[] _overlaps = new Collider[32];
    private float Padding => _params.GroundTolerance;
    private float Radius => Mathf.Max(.01f, _controller.radius - Mathf.Min(_controller.skinWidth, _controller.radius * .4f));
    public bool IsWalkable(Vector3 normal) => normal.y >= Mathf.Cos(_params.MaxGroundAngle * Mathf.Deg2Rad);

    public MovementGrounding(CharacterController controller, MovementParams parameters)
    { _controller = controller; _params = parameters; }

    public bool TrySupport(Vector3 feet, float rise, float drop, out GroundSupport support, GroundSupport previous = default)
    {
        support = default;
        float radius = _controller.radius;
        float castRise = Mathf.Max(rise, _params.MaxStepUp);
        Vector3 origin = feet + Vector3.up * (radius + castRise + Padding);
        int count = Physics.SphereCastNonAlloc(origin, radius, Vector3.down, _hits,
            castRise + drop + radius + Padding * 2f, _params.CollisionMask, QueryTriggerInteraction.Ignore);
        if (count == _hits.Length) return false;
        for (int i = 0; i < count; i++)
        {
            RaycastHit hit = _hits[i];
            if (Own(hit.transform) || hit.distance <= 0f) continue;
            Vector3 normal = hit.normal;
            Vector3 point = hit.point;
            // 球扫在台阶棱边返回分离方向；用同一实体上的短射线确认真实台面，不能把竖墙当斜坡。
            Vector3 inward = -MovementMath.Horizontal(normal).normalized * Padding;
            if (TrySurface(hit.point + inward + Vector3.up * Padding, Padding * 3f, hit.collider, out RaycastHit surface))
            {
                normal = surface.normal;
                point = surface.point;
            }
            if (!IsWalkable(normal)) continue;
            float planeY = point.y - (normal.x * (feet.x - point.x) + normal.z * (feet.z - point.z)) / normal.y;
            float rootHeight = planeY + radius * (1f / normal.y - 1f);
            if (rootHeight > feet.y + rise + Padding || rootHeight < feet.y - drop - Padding) continue;
            if (support.IsValid && rootHeight <= support.RootHeight) continue;
            support = new GroundSupport(hit.collider, point, normal, rootHeight);
        }
        // 同一个 MeshCollider 的球扫只返回最先碰到的台面；补中心和前沿射线才能识别连续楼梯的下一阶。
        for (int i = 0; i < 3; i++)
        {
            if (i == 2 && (!previous.IsValid || MovementMath.Horizontal(previous.Point - feet).magnitude > radius + _controller.skinWidth + Padding * 2f)) continue;
            Vector3 sample = i == 2 ? previous.Point : feet + (i == 0 ? Vector3.zero : _controller.transform.forward * (radius + _controller.skinWidth));
            sample.y = feet.y + castRise + Padding;
            if (!TrySurface(sample, castRise + drop + Padding * 2f, i == 2 ? previous.Collider : null, out RaycastHit surface) || !IsWalkable(surface.normal)) continue;
            Vector3 normal = surface.normal;
            float planeY = surface.point.y - (normal.x * (feet.x - surface.point.x) + normal.z * (feet.z - surface.point.z)) / normal.y;
            float rootHeight = planeY + radius * (1f / normal.y - 1f);
            if (rootHeight > feet.y + rise + Padding || rootHeight < feet.y - drop - Padding
                || support.IsValid && rootHeight <= support.RootHeight) continue;
            support = new GroundSupport(surface.collider, surface.point, normal, rootHeight);
        }
        return support.IsValid;
    }

    public bool TrySurface(Vector3 origin, float distance, Collider required, out RaycastHit surface)
    {
        surface = default;
        int count = Physics.RaycastNonAlloc(origin, Vector3.down, _rays, distance,
            _params.CollisionMask, QueryTriggerInteraction.Ignore);
        if (count == _rays.Length) return false;
        float nearest = float.PositiveInfinity;
        for (int i = 0; i < count; i++)
        {
            RaycastHit hit = _rays[i];
            if (Own(hit.transform) || required != null && hit.collider != required || hit.distance >= nearest) continue;
            surface = hit; nearest = hit.distance;
        }
        return surface.collider != null;
    }

    public bool TryStep(Vector3 displacement, float height, out float lift, out GroundSupport landing)
    {
        lift = 0f;
        landing = default;
        Vector3 horizontal = MovementMath.Horizontal(displacement);
        float distance = horizontal.magnitude;
        if (distance < .000001f || _params.MaxStepUp <= 0f) return false;
        Vector3 feet = _controller.transform.position;
        TrySupport(feet + horizontal, _params.MaxStepUp, _params.MaxStepDown, out landing);
        // 原生胶囊在棱边的接触余量大于球扫半径；沿实际位移补前沿台面，避免先卡住再也探不到下一阶。
        Vector3 front = feet + horizontal + horizontal / distance * (_controller.radius + _controller.skinWidth * 2f + Padding);
        front.y = feet.y + _params.MaxStepUp + Padding;
        if (TrySurface(front, _params.MaxStepUp + _params.MaxStepDown + Padding * 2f, null, out RaycastHit ahead)
            && ahead.normal.y > .999f)
        {
            float planeY = ahead.point.y - Vector3.Dot(MovementMath.Horizontal(ahead.normal), feet + horizontal - ahead.point) / ahead.normal.y;
            float rootHeight = planeY + _controller.radius * (1f / ahead.normal.y - 1f);
            if (rootHeight <= feet.y + _params.MaxStepUp + .001f && (!landing.IsValid || rootHeight > landing.RootHeight))
                landing = new GroundSupport(ahead.collider, ahead.point, ahead.normal, rootHeight);
        }
        if (Blocked(feet, height, horizontal / distance, distance + _controller.skinWidth, out RaycastHit obstacle, true))
        {
            if (obstacle.collider == null) return false;
            Vector3 probe = obstacle.point - MovementMath.Horizontal(obstacle.normal).normalized * Padding;
            probe.y = feet.y + _params.MaxStepUp + Padding;
            if (MovementMath.Horizontal(probe - feet - horizontal).magnitude <= _controller.radius + _controller.skinWidth + Padding
                && TrySurface(probe, _params.MaxStepUp + _params.MaxStepDown + Padding * 2f, obstacle.collider, out RaycastHit top)
                && IsWalkable(top.normal))
            {
                float planeY = top.point.y - Vector3.Dot(MovementMath.Horizontal(top.normal), feet + horizontal - top.point) / top.normal.y;
                float rootHeight = planeY + _controller.radius * (1f / top.normal.y - 1f);
                if (!landing.IsValid || rootHeight > landing.RootHeight)
                    landing = new GroundSupport(top.collider, top.point, top.normal, rootHeight);
            }
        }
        if (!landing.IsValid) return false;
        lift = landing.RootHeight - feet.y;
        if (lift <= Mathf.Max(0f, displacement.y) + Padding || lift > _params.MaxStepUp + .001f) return false;
        float up = lift + Padding;
        if (Blocked(feet, height, Vector3.up, up, out _) || !CanOccupy(feet + Vector3.up * up, height)) return false;
        Vector3 elevated = feet + Vector3.up * up;
        if (Blocked(elevated, height, horizontal / distance, distance, out _)) return false;
        return CanOccupy(elevated + horizontal, height);
    }

    public bool CanOccupy(Vector3 feet, float height)
    {
        Capsule(feet, height, out Vector3 bottom, out Vector3 top);
        int count = Physics.OverlapCapsuleNonAlloc(bottom, top, Radius, _overlaps,
            _params.CollisionMask, QueryTriggerInteraction.Ignore);
        if (count == _overlaps.Length) return false;
        for (int i = 0; i < count; i++) if (!Own(_overlaps[i].transform)) return false;
        return true;
    }

    public bool Blocked(Vector3 feet, float height, Vector3 direction, float distance, out RaycastHit obstacle, bool includeSkin = false)
    {
        obstacle = default;
        Capsule(feet, height, out Vector3 bottom, out Vector3 top);
        float radius = includeSkin ? _controller.radius + _controller.skinWidth : Radius;
        if (includeSkin)
        {
            // 台阶与地板共用网格时，膨胀查询要先离开地板，避免初始重叠遮掉后续立面。
            Vector3 clearance = Vector3.up * (_controller.skinWidth + Padding * .1f);
            bottom += clearance; top += clearance;
        }
        int count = Physics.CapsuleCastNonAlloc(bottom, top, radius, direction, _hits,
            distance, _params.CollisionMask, QueryTriggerInteraction.Ignore);
        if (count == _hits.Length) return true;
        float nearest = float.PositiveInfinity;
        for (int i = 0; i < count; i++)
        {
            RaycastHit hit = _hits[i];
            if (Own(hit.transform) || hit.distance <= 0f || Vector3.Dot(direction, hit.normal) >= -.00001f || hit.distance >= nearest) continue;
            obstacle = hit; nearest = hit.distance;
        }
        return obstacle.collider != null;
    }

    private bool Own(Transform value) => value.IsChildOf(_controller.transform);
    private void Capsule(Vector3 feet, float height, out Vector3 bottom, out Vector3 top)
    {
        bottom = feet + Vector3.up * _controller.radius;
        top = feet + Vector3.up * Mathf.Max(_controller.radius, height - _controller.radius);
    }
}
