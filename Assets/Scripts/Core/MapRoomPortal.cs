using UnityEngine;

// 门口触发器：目标房间已预加载时立即切换；未预加载时仍会等待加载完成后再卸载旧房间。
[DisallowMultipleComponent]
[RequireComponent(typeof(BoxCollider), typeof(Rigidbody))]
public sealed class MapRoomPortal : MonoBehaviour
{
    [Tooltip("门后房间的场景名；场景须加入 Build Settings")]
    [SerializeField] private string _targetRoomSceneName;
    [Tooltip("留空时卸载此门所在的房间场景")]
    [SerializeField] private string _sourceRoomSceneName;
    [SerializeField] private bool _onlyUseOnce = true;

    private bool _hasBeenUsed;

    public string TargetRoomSceneName => _targetRoomSceneName;
    public void Configure(string sourceSceneName, string targetSceneName)
    {
        _sourceRoomSceneName = sourceSceneName;
        _targetRoomSceneName = targetSceneName;
        GetComponent<BoxCollider>().isTrigger = true;
        var body = GetComponent<Rigidbody>();
        body.isKinematic = true;
        body.useGravity = false;
    }

    private void Reset()
    {
        GetComponent<Collider>().isTrigger = true;
        GetComponent<Rigidbody>().isKinematic = true;
        GetComponent<Rigidbody>().useGravity = false;
    }

    private void OnTriggerEnter(Collider other)
    {
        TryEnter(other);
    }

    private void OnTriggerStay(Collider other)
    {
        TryEnter(other);
    }

    private void TryEnter(Collider other)
    {
        if (_hasBeenUsed && _onlyUseOnce) return;
        if (other.GetComponentInParent<PlayerMotor>() == null) return;
        if (MapManager.Instance.CurrentRoomSceneName == _targetRoomSceneName) return;

        string sourceRoomSceneName = string.IsNullOrWhiteSpace(_sourceRoomSceneName)
            ? gameObject.scene.name
            : _sourceRoomSceneName;

        if (MapManager.Instance.TryEnterRoom(sourceRoomSceneName, _targetRoomSceneName))
        {
            _hasBeenUsed = true;
        }
    }
}
