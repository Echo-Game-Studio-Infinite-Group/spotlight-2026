using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(PlayerInputReader), typeof(PlayerMotor))]
public sealed class PlayerMarkChop : MonoBehaviour
{
    [SerializeField] private PlayerInputReader _input;
    [SerializeField] private PlayerMotor _motor;
    [SerializeField, Min(0f)] private float _range = 3f;
    [SerializeField, Min(0f)] private float _radius = 0.65f;
    [SerializeField, Min(0f)] private float _highSpeedThreshold = 15f;
    [SerializeField, Min(0f)] private float _highSpeedRange = 4.5f;
    [SerializeField, Min(0f)] private float _highSpeedRadius = 1f;
    [SerializeField] private LayerMask _queryMask = ~0;
    private void OnEnable() => _input.MarkPressed += Chop;
    private void OnDisable() => _input.MarkPressed -= Chop;
    private void Chop()
    {
        bool fast = _motor.HorizontalSpeed >= _highSpeedThreshold;
        float range = fast ? _highSpeedRange : _range;
        float radius = fast ? _highSpeedRadius : _radius;
        Vector3 origin = transform.position;
        Vector3 forward = transform.forward;
        EnemyMark closest = null;
        float best = float.PositiveInfinity;
        foreach (Collider hit in Physics.OverlapCapsule(origin, origin + forward * range, radius, _queryMask, QueryTriggerInteraction.Ignore))
        {
            EnemyMark target = hit.GetComponentInParent<EnemyMark>();
            if (target == null || !target.isActiveAndEnabled) continue;
            Vector3 point = hit.ClosestPoint(origin);
            Vector3 delta = point - origin;
            if (Vector3.Dot(delta, forward) < 0f || delta.sqrMagnitude >= best) continue;
            bool blocked = false;
            // 标记可以透墙显示，但手刀不能穿过场景碰撞体选中敌人。
            foreach (RaycastHit obstruction in Physics.RaycastAll(origin, delta.normalized, delta.magnitude, _queryMask, QueryTriggerInteraction.Ignore))
            {
                if (obstruction.transform.IsChildOf(transform) || obstruction.collider.GetComponentInParent<EnemyMark>() == target) continue;
                blocked = true;
                break;
            }
            if (blocked) continue;
            closest = target;
            best = delta.sqrMagnitude;
        }
        if (closest != null) closest.Toggle();
    }
}
