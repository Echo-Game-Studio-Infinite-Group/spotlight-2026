using UnityEngine;

// 调试用自由观察相机：Play 模式下按住鼠标右键拖动转视角，WASD 平移，Q/E 升降
// 约束（代码框架设计 4.3）：相机属于不缩放时间层，必须读 UnscaledDeltaTime。
// 否则一旦 Ysir 那边接了时停 / 时缓，调试视角会跟着一起变慢，反而看不清发生了什么
// 约束（AGENTS.md 第 4 条）：输入直读旧版 Input Manager，不引入新 Input System
public class FlyCameraDebug : MonoBehaviour
{
    [Header("速度")]
    public float MoveSpeed = 8f;
    public float BoostMultiplier = 3f;
    public float LookSensitivity = 3f;

    private float _yaw;
    private float _pitch;

    private void Awake()
    {
        Vector3 angles = transform.eulerAngles;
        _yaw = angles.y;
        _pitch = angles.x;
    }

    private void Update()
    {
        if (Input.GetMouseButton(1))
        {
            _yaw += Input.GetAxis("Mouse X") * LookSensitivity;
            _pitch -= Input.GetAxis("Mouse Y") * LookSensitivity;
            _pitch = Mathf.Clamp(_pitch, -89f, 89f);
            transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }

        float delta = TimeManager.UnscaledDeltaTime;
        float speed = MoveSpeed * (Input.GetKey(KeyCode.LeftShift) ? BoostMultiplier : 1f);

        Vector3 move = Vector3.zero;
        if (Input.GetKey(KeyCode.W)) move += Vector3.forward;
        if (Input.GetKey(KeyCode.S)) move += Vector3.back;
        if (Input.GetKey(KeyCode.A)) move += Vector3.left;
        if (Input.GetKey(KeyCode.D)) move += Vector3.right;
        if (Input.GetKey(KeyCode.E)) move += Vector3.up;
        if (Input.GetKey(KeyCode.Q)) move += Vector3.down;

        if (move.sqrMagnitude > 0f)
        {
            transform.Translate(move.normalized * speed * delta, Space.Self);
        }
    }
}
