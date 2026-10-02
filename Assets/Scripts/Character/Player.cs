using UnityEngine;

// 玩家身份组件：挂在玩家 GameObject 上，作为 GameManager 与具体移动实现之间的连接点
// 约束：速度唯一真值是 PlayerMotor
public class Player : MonoBehaviour
{
    private PlayerMotor _motor;

    // 当前水平速度，供 GameManager 聚合；无移动组件时退化为 0，不抛异常
    public float Speed => _motor != null ? _motor.HorizontalSpeed : 0f;

    private void Awake()
    {
        // 移动组件可有可无（备选移动实现下取不到），因此不做 RequireComponent
        _motor = GetComponent<PlayerMotor>();
    }

    private void Start()
    {
        // GameManager 按需自建，直接播放 TestScene 也能拿到实例
        GameManager.Instance.RegisterPlayer(this);
    }
}
