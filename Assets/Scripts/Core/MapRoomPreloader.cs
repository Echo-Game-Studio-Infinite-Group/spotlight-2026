using UnityEngine;

// 预加载触发器与门分开设置，避免新房间刚被 Additive 加载就递归预加载整张地图。
[DisallowMultipleComponent]
[RequireComponent(typeof(BoxCollider), typeof(Rigidbody))]
public sealed class MapRoomPreloader : MonoBehaviour
{
    [Tooltip("玩家进入此房间时预加载的下一房间场景名；场景须加入 Build Settings")]
    [SerializeField] private string _nextRoomSceneName;
    [SerializeField] private bool _onlyPreloadOnce = true;

    private bool _hasPreloaded;

    private void Reset()
    {
        GetComponent<Collider>().isTrigger = true;
        GetComponent<Rigidbody>().isKinematic = true;
        GetComponent<Rigidbody>().useGravity = false;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (_hasPreloaded && _onlyPreloadOnce) return;
        if (other.GetComponentInParent<PlayerMotor>() == null) return;

        if (MapManager.Instance.PreloadRoom(_nextRoomSceneName))
        {
            _hasPreloaded = true;
        }
    }
}
