using UnityEngine;

// 警觉接收方的统一入口。框架 4.4 只在跨模块处建接口，这里正是：
// 广播器不该知道敌人是医疗机器人还是护卫机器人，只知道「它会被叫醒」
public interface IAlertReceiver
{
    // sourcePosition 是首个发现玩家的位置，收到的人据此判断往哪边警戒
    void OnAlerted(Vector3 sourcePosition);
}
