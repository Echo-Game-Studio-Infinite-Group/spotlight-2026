using System.Collections.Generic;
using UnityEngine;

namespace GameJam.Environment
{
    /// <summary>
    /// 防爆门"靠近即开"玩法接线 —— 与 TechBlastDoorController 挂在同一个门根节点上, 零手工接线。
    ///
    /// 职责只有两件事(开合动画本身全在控制器里, 本组件不碰门扇 Transform):
    ///   1. 触发区(自动创建子物体 ProximityZone): 横跨门两侧的 BoxCollider(isTrigger)。
    ///      玩家进入 → controller.Open(); 最后一人离开且过了 closeDelaySeconds → controller.Close();
    ///      关门倒计时里再有人进来则取消关门。占用计数用集合去重 —— 玩家挂多个碰撞体也只算一人。
    ///   2. 通行闸(自动创建子物体 PassageBlock): 一块正好堵住门洞的 BoxCollider。
    ///      ★ 开/关全程只是动画 —— 十六扇门与框体都没有碰撞体, 全门唯一的物理就是这块闸板:
    ///      整体进度没到 passableAtProgress 时启用(挡人), 到了就禁用(放行)。
    ///      "门基本彻底打开后才能通过"由它精确保证 —— 玩家就算贴着门口等, 也撞不到还在动的门扇。
    ///
    /// 触发区深度怎么取(换算公式, IMPORT.md 同款):
    ///   门全开耗时 = 外层装饰门时长 × bcdStartFraction + 中间三组最长层时长
    ///              = 3.90/1.5 × 0.5 + 3.28/1.5 ≈ 1.30 + 2.19 ≈ 3.49 秒
    ///              (门速 1.5 米/秒 = 冻结规格 1.0 的 1.50 倍 —— 试验场按用户令"比 GIF 快 50%");
    ///   想让玩家"跑到门口门刚好开完": triggerDepthEachSide ≥ 玩家最高跑速 × 门全开耗时。
    ///   默认 14 米/侧适合中低速走廊; 高速 bhop 段位按上式加深(如 10 m/s 给 35 米/侧)。
    /// </summary>
    [RequireComponent(typeof(TechBlastDoorController))]
    public class TechBlastDoorProximity : MonoBehaviour
    {
        [Header("触发 · 算作玩家的 Tag(玩家预制体的 Tag 要与此一致)")]
        [SerializeField] private string triggerTag = "Player";

        [Header("触发 · 触发区尺寸(米): 总宽 / 高 / 单侧纵深(以门为中心前后各伸多远)")]
        [SerializeField, Min(1f)] private float triggerWidth = 12f;
        [SerializeField, Min(1f)] private float triggerHeight = 7f;
        [SerializeField, Min(2f)] private float triggerDepthEachSide = 14f;

        [Header("触发 · 最后一人离开后隔多久开始关门(秒); 倒计时里再进来则取消关门")]
        [SerializeField, Min(0f)] private float closeDelaySeconds = 1f;

        [Header("通行闸 · 门洞包盒(门根局部坐标): 中心与尺寸 —— 比洞口(宽6.5×高6.0)略大一圈防贴边钻缝")]
        [SerializeField] private Vector3 blockerCenter = new Vector3(0f, 3f, 0f);
        [SerializeField] private Vector3 blockerSize = new Vector3(6.7f, 6.1f, 1.6f);

        [Header("通行闸 · 整体进度达到多少算基本开完放行(1 = 四层全开完才放行)")]
        [SerializeField, Range(0.5f, 1f)] private float passableAtProgress = 0.95f;

        private TechBlastDoorController _controller;
        private BoxCollider _blocker;
        private readonly HashSet<Collider> _occupants = new HashSet<Collider>();
        private float _closeAtTime = -1f;   // 关门倒计时的截止时刻; <0 = 没有在倒计时

        private void Awake()
        {
            _controller = GetComponent<TechBlastDoorController>();
            BuildTriggerZone();
            BuildPassageBlock();
        }

        private void Update()
        {
            // 占用者若被销毁(死亡/卸载), OnTriggerExit 不会再发 —— 清掉防止门永远不关
            if (_occupants.RemoveWhere(c => c == null) > 0 && _occupants.Count == 0 && _closeAtTime < 0f)
            {
                _closeAtTime = Time.time + closeDelaySeconds;
            }

            if (_closeAtTime >= 0f && Time.time >= _closeAtTime)
            {
                if (_occupants.Count == 0)
                {
                    _controller.Close();
                }
                _closeAtTime = -1f;
            }

            // 通行闸只在状态翻转时改 enabled, 不每帧赋值
            bool shouldBlock = _controller.Progress < passableAtProgress;
            if (_blocker.enabled != shouldBlock)
            {
                _blocker.enabled = shouldBlock;
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!other.CompareTag(triggerTag) || !_occupants.Add(other))
            {
                return;
            }
            _closeAtTime = -1f;   // 取消正在倒计时的关门
            _controller.Open();   // 已开着也无副作用(目标状态相同, 时间线不动)
        }

        private void OnTriggerExit(Collider other)
        {
            if (!other.CompareTag(triggerTag) || !_occupants.Remove(other))
            {
                return;
            }
            if (_occupants.Count == 0)
            {
                _closeAtTime = Time.time + closeDelaySeconds;
            }
        }

        /// <summary>建触发区: isTrigger 盒 + kinematic Rigidbody —— 我方带了刚体后, 玩家无论用 CharacterController 还是 Rigidbody 移动都必然产生触发事件。</summary>
        private void BuildTriggerZone()
        {
            GameObject zone = new GameObject("ProximityZone");
            zone.transform.SetParent(transform, false);
            zone.transform.localPosition = new Vector3(0f, triggerHeight * 0.5f, 0f);

            BoxCollider box = zone.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = new Vector3(triggerWidth, triggerHeight, triggerDepthEachSide * 2f);

            Rigidbody body = zone.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
        }

        /// <summary>建通行闸: 全门唯一参与物理的碰撞体, 启用=挡人, 禁用=放行(状态在 Update 里跟整体进度对齐)。</summary>
        private void BuildPassageBlock()
        {
            GameObject block = new GameObject("PassageBlock");
            block.transform.SetParent(transform, false);
            block.transform.localPosition = blockerCenter;

            _blocker = block.AddComponent<BoxCollider>();
            _blocker.size = blockerSize;
            _blocker.enabled = false;   // 初值任意, 第一帧 Update 按实际进度对齐
        }

        /// <summary>选中门根时在 Scene 视图画出触发区(青)与通行闸(橙), 方便肉眼核对覆盖范围。</summary>
        private void OnDrawGizmosSelected()
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(0f, 1f, 1f, 0.6f);
            Gizmos.DrawWireCube(new Vector3(0f, triggerHeight * 0.5f, 0f),
                                new Vector3(triggerWidth, triggerHeight, triggerDepthEachSide * 2f));
            Gizmos.color = new Color(1f, 0.5f, 0f, 0.9f);
            Gizmos.DrawWireCube(blockerCenter, blockerSize);
        }
    }
}
