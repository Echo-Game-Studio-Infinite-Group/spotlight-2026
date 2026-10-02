using UnityEngine;

// CharacterController 撞不动物体（Move 只查碰撞，不算力），所以碰到刚体时在这里手动推开
// PlayerMotor / CharacterMovement 都走 CharacterController，这个组件对两套移动都生效
[RequireComponent(typeof(CharacterController))]
public class RigidbodyPusher : MonoBehaviour
{
    [Tooltip("把物体推开的速度。玩家跑得越快，撞得越远")]
    [SerializeField, Min(0f)] private float _pushSpeed = 2f;

    private void OnControllerColliderHit(ControllerColliderHit hit)
    {
        Rigidbody body = hit.collider.attachedRigidbody;

        // 静态物体和运动学刚体推不动，直接跳过
        if (body == null || body.isKinematic) return;

        // 从头顶往下踩不算推，免得落地把脚底下的东西踩飞
        if (hit.moveDirection.y < -0.3f) return;

        Vector3 pushDir = new Vector3(hit.moveDirection.x, 0f, hit.moveDirection.z);
        if (pushDir.sqrMagnitude < 0.0001f) return;
        pushDir.Normalize();

        // 推出速度取“设定值”和“玩家当前速度”中较大的那个
        Vector3 v = hit.controller.velocity;
        float playerSpeed = new Vector3(v.x, 0f, v.z).magnitude;
        float speed = Mathf.Max(_pushSpeed, playerSpeed);

        // 只改水平速度，竖直方向留给重力
        Vector3 bodyVelocity = body.velocity;
        body.velocity = new Vector3(pushDir.x * speed, bodyVelocity.y, pushDir.z * speed);
    }
}
