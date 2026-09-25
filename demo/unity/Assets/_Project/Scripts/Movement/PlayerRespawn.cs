using UnityEngine;

// 灰盒保底：掉出世界（y 低于击杀线）瞬移回出生点并清空状态——高速测试的死亡惩罚应为零（幽灵行者式秒重生）
public class PlayerRespawn : MonoBehaviour
{
    [SerializeField] private float _killY = -10f;

    private Vector3 _spawnPosition;
    private PlayerMotor _motor;

    private void Awake()
    {
        _spawnPosition = transform.position;
        _motor = GetComponent<PlayerMotor>();
    }

    private void FixedUpdate()
    {
        if (transform.position.y >= _killY) return;
        transform.position = _spawnPosition;
        if (_motor != null) _motor.ResetState();
    }
}
