using UnityEngine;

namespace HST
{
    /// <summary>
    /// 走廊大门: 双扇内开自动门(2026-10-11 用户令「门做成可以开关有门缝的门」)。
    /// 行为: 玩家水平距离 &lt; openRadius 即开门, 离开 closeRadius 后关门; 中间维持当前状态防抖。
    /// 说明: 本场景照明全烘焙, 动态件(门扇/玩家)走 HST_LightProbes 取 GI —— 因此门扇**不能**设静态。
    /// </summary>
    public class HstSwingDoor : MonoBehaviour
    {
        [Tooltip("左扇铰链(在门洞 -z 侧), 门扇为其子物体")]
        public Transform hingeL;
        [Tooltip("右扇铰链(在门洞 +z 侧)")]
        public Transform hingeR;
        [Tooltip("玩家 Transform; 留空则运行时按名字 HST_Player 查找")]
        public Transform player;

        [Tooltip("开门角度(度)")]
        public float openAngle = 95f;
        [Tooltip("进入该水平半径内开门(m)")]
        public float openRadius = 6f;
        [Tooltip("离开该水平半径外关门(m)")]
        public float closeRadius = 9f;
        [Tooltip("开合速度(每秒归一化进度)")]
        public float speed = 3.5f;

        float _t;   // 0=关 1=开

        void Update()
        {
            if (hingeL == null || hingeR == null) return;
            if (player == null)
            {
                var p = GameObject.Find("HST_Player");
                if (p != null) player = p.transform;
            }

            float want = _t;
            if (player != null)
            {
                Vector3 a = player.position, b = transform.position;
                float d = new Vector2(a.x - b.x, a.z - b.z).magnitude;
                if (d < openRadius) want = 1f;
                else if (d > closeRadius) want = 0f;
            }

            _t = Mathf.MoveTowards(_t, want, speed * Time.deltaTime);
            float ang = Mathf.SmoothStep(0f, openAngle, _t);
            // 两扇自由边都朝 +x(走廊内侧)摆开: Rx 绕 -y, Lx 绕 +y
            hingeR.localRotation = Quaternion.Euler(0f, -ang, 0f);
            hingeL.localRotation = Quaternion.Euler(0f, ang, 0f);
        }
    }
}
