using UnityEngine;

// RuinsFireFlyby —— 演示/验收用的"高速掠过"相机（跑酷路边火的正确验收姿势）
//
// 调研 §6.11 定了验收纪律：**不能站在火前看细节**，要走"以 20 m/s 掠过一堆火时看到的那 1 秒"。
// 这个组件就是把那句话变成可跑的机位：沿跑道匀速前进、带轻微横向摆动与俯仰，
// 让人真的用"玩家速度"看这些火。Sample(time) 是纯函数 → 出图脚本可以逐帧指定时刻，结果可复现。
[DisallowMultipleComponent]
public sealed class RuinsFireFlyby : MonoBehaviour
{
    [Tooltip("前进速度（m/s）。项目高速档量级：20 m/s 每秒掠过 20m 场景")]
    public float speed = 20f;

    public float startZ = -8f;
    public float endZ = 128f;
    public float height = 1.65f;

    [Tooltip("横向摆动幅度（像跑动中的重心左右换脚）")]
    public float lateralSway = 0.75f;

    public float swaySpeed = 0.41f;

    [Tooltip("视线前瞻距离")]
    public float lookAhead = 14f;

    [Tooltip("视线在前方的横向偏移（把火排带进画面一侧）")]
    public float lookSideOffset = -1.6f;

    void Update()
    {
        Sample(Time.time, out Vector3 pos, out Quaternion rot);
        transform.SetPositionAndRotation(pos, rot);
    }

    public void Sample(float time, out Vector3 pos, out Quaternion rot)
    {
        float span = Mathf.Max(1f, endZ - startZ);
        float z = startZ + Mathf.Repeat(speed * time, span);

        float sway = Mathf.Sin(time * swaySpeed * 6.2831853f) * lateralSway;
        float bob = Mathf.Sin(time * 1.93f) * 0.035f;          // 极轻的上下起伏（跑动的呼吸）
        pos = new Vector3(sway, height + bob, z);

        // 看向前方偏侧一点的点，并随摆动轻微横移 → 画面里有"速度感"，但火排始终在视野里
        Vector3 target = new Vector3(sway * 0.35f + lookSideOffset, height - 0.42f, z + lookAhead);
        rot = Quaternion.LookRotation((target - pos).normalized, Vector3.up);
        // 轻微侧倾（跑动的 roll），幅度给得很小，避免影响可读性
        rot *= Quaternion.AngleAxis(Mathf.Sin(time * 0.37f) * 2.2f, Vector3.forward);
    }
}
