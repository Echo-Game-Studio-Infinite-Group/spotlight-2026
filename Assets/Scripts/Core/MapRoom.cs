using UnityEngine;

// 仅约定加载接口；关卡顺序也可由配表工具在运行时调用 MapManager 指定。
[DisallowMultipleComponent]
public sealed class MapRoom : MonoBehaviour
{
    [SerializeField] private string _nextRoomSceneName;
    [SerializeField] private Transform _entryPoint;
    [SerializeField] private Transform _exitPoint;
    [SerializeField] private Transform _respawnPoint;
    [SerializeField] private float _fallDeathY = -10f;
    public string NextRoomSceneName => _nextRoomSceneName;
    public Transform EntryPoint => _entryPoint;
    public Transform ExitPoint => _exitPoint;
    public Transform RespawnPoint => _respawnPoint;
    public float FallDeathY => _fallDeathY;
    public void Configure(string nextRoomSceneName) => _nextRoomSceneName = nextRoomSceneName;
    public void Configure(string nextRoomSceneName, Transform entryPoint, Transform exitPoint)
    {
        _nextRoomSceneName = nextRoomSceneName;
        _entryPoint = entryPoint;
        _exitPoint = exitPoint;
    }
    public void ConfigureRespawn(Transform respawnPoint, float fallDeathY)
    {
        _respawnPoint = respawnPoint;
        _fallDeathY = fallDeathY;
    }
}
