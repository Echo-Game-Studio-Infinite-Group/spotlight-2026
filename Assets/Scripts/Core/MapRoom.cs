using UnityEngine;

// 仅约定加载接口；关卡顺序也可由配表工具在运行时调用 MapManager 指定。
[DisallowMultipleComponent]
public sealed class MapRoom : MonoBehaviour
{
    [SerializeField] private string _nextRoomSceneName;
    public string NextRoomSceneName => _nextRoomSceneName;
    public void Configure(string nextRoomSceneName) => _nextRoomSceneName = nextRoomSceneName;
}
