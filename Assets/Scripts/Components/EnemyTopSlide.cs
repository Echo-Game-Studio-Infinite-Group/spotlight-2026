using UnityEngine;

[DisallowMultipleComponent, RequireComponent(typeof(CharacterController))]
public sealed class EnemyTopSlide : MonoBehaviour
{
    [SerializeField, Min(0f), Tooltip("玩家落到敌人顶部时，沿身体外侧滑落的速度（米/秒）")]
    private float _slideSpeed = 2.5f;
    private CharacterController _body;

    private void Awake() => _body = GetComponent<CharacterController>();

    public bool TryGetSlideVelocity(CharacterController player, Vector3 normal, Vector3 incoming, out Vector3 velocity)
    {
        velocity = Vector3.zero;
        if (!isActiveAndEnabled || !_body.enabled || _slideSpeed <= 0f || incoming.y > 0f || normal.y <= .1f
            || player.bounds.min.y < _body.bounds.center.y) return false;
        Vector3 outward = Vector3.ProjectOnPlane(player.bounds.center - _body.bounds.center, Vector3.up);
        float reach = player.radius + _body.radius + player.skinWidth + _body.skinWidth;
        if (outward.sqrMagnitude > reach * reach) return false;
        // 正好落在轴线上没有径向方向，固定向玩家侧面滑，避免每帧随机换向。
        Vector3 direction = outward.sqrMagnitude > .0001f ? outward.normalized : player.transform.right;
        velocity = direction * _slideSpeed;
        return true;
    }
}