using UnityEngine;

// 门口触发器：目标房间已预加载时立即切换；未预加载时仍会等待加载完成后再卸载旧房间。
[DisallowMultipleComponent]
[RequireComponent(typeof(Collider))]
public sealed class MapRoomPortal : MonoBehaviour
{
    [Tooltip("门后房间的场景名；场景须加入 Build Settings")]
    [SerializeField] private string _targetRoomSceneName;
    [Tooltip("留空时卸载此门所在的房间场景")]
    [SerializeField] private string _sourceRoomSceneName;
    [SerializeField] private bool _onlyUseOnce = true;

    private bool _hasBeenUsed;

    private void Reset()
    {
        GetComponent<Collider>().isTrigger = true;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (_hasBeenUsed && _onlyUseOnce) return;
        if (other.GetComponentInParent<PlayerMotor>() == null) return;

        string sourceRoomSceneName = string.IsNullOrWhiteSpace(_sourceRoomSceneName)
            ? gameObject.scene.name
            : _sourceRoomSceneName;

        if (MapManager.Instance.TryEnterRoom(sourceRoomSceneName, _targetRoomSceneName))
        {
            _hasBeenUsed = true;
        }
    }
}
