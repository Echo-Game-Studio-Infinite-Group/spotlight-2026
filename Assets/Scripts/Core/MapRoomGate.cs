using UnityEngine;

[DisallowMultipleComponent]
public sealed class MapRoomGate : MonoBehaviour
{
    [SerializeField] private string _targetRoomSceneName;
    [SerializeField] private GameObject _door;
    [SerializeField] private bool _closeAfterEntering;
    public bool IsOpen => _door != null && !_door.activeSelf;

    public void Configure(string targetRoomSceneName, GameObject door, bool closeAfterEntering = false)
    {
        _targetRoomSceneName = targetRoomSceneName;
        _door = door;
        _closeAfterEntering = closeAfterEntering;
    }

    private void Update()
    {
        if (_door == null) return;
        var manager = MapManager.Instance;
        bool open = _closeAfterEntering
            ? manager.CurrentRoomSceneName != _targetRoomSceneName
            : manager.IsRoomReady(_targetRoomSceneName);
        if (_door.activeSelf == open) _door.SetActive(!open);
    }
}
