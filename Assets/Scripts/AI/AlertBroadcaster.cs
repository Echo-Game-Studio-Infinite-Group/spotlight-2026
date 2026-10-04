using System.Collections.Generic;
using UnityEngine;

// 群体警觉：一个敌人发现玩家后，把警觉扩散给附近的同伴
// 策划案 8.(1) 要求医疗机器人「成群出现」，第 9 节群体 AI 目前仍是占位——这里是它的最小实现
// 约定：本类只负责通知，不移动、不转向任何单位。收到广播后怎么反应是各敌人自己的事
public class AlertBroadcaster : MonoBehaviour
{
    [Header("传播范围")]
    public float AlertRadius = 12f;
    [Header("单次最多叫醒几个")]
    public int MaxReceiversPerAlert = 8;      // 防止一帧内遍历全场地，同屏几十个怪时保帧率

    private static AlertBroadcaster _instance;

    private readonly List<IAlertReceiver> _receivers = new List<IAlertReceiver>();
    private readonly List<Vector3> _receiverPositions = new List<Vector3>();

    public static AlertBroadcaster Instance => _instance;

    // 用 MonoBehaviour 反查位置：注册方只需实现接口，不必额外维护一份坐标表
    public void Register(IAlertReceiver receiver, MonoBehaviour owner)
    {
        if (receiver == null || owner == null) return;
        if (_receivers.Contains(receiver)) return;

        _receivers.Add(receiver);
        _receiverPositions.Add(owner.transform.position);
    }

    public void Unregister(IAlertReceiver receiver)
    {
        if (receiver == null) return;

        int index = _receivers.IndexOf(receiver);
        if (index < 0) return;

        _receivers.RemoveAt(index);
        _receiverPositions.RemoveAt(index);
    }

    // 由发现玩家的那个敌人调用。返回本次叫醒了几个，方便调试时看传播规模
    public int RaiseAlert(Vector3 sourcePosition)
    {
        int alerted = 0;
        float radiusSquared = AlertRadius * AlertRadius;

        for (int i = 0; i < _receivers.Count; i++)
        {
            if (alerted >= MaxReceiversPerAlert) break;

            // 位置每帧刷新一次：敌人一直在动，用注册时的旧坐标会算错半径
            _receiverPositions[i] = ((MonoBehaviour)_receivers[i]).transform.position;

            if ((_receiverPositions[i] - sourcePosition).sqrMagnitude > radiusSquared) continue;

            _receivers[i].OnAlerted(sourcePosition);
            alerted++;
        }

        return alerted;
    }

    private void Awake()
    {
        _instance = this;
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, AlertRadius);
    }
}
