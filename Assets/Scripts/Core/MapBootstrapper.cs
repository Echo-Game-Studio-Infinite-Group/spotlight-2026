using UnityEngine;

// 常驻场景只保存玩家、相机和全局系统；房间场景可被卸载而不会销毁这些运行时对象。
[DisallowMultipleComponent]
public sealed class MapBootstrapper : MonoBehaviour
{
    [Tooltip("游戏开始后以 Additive 方式加载的第一个房间场景名；场景须加入 Build Settings")]
    [SerializeField] private string _initialRoomSceneName;

    private void Start()
    {
        MapManager.Instance.TryLoadInitialRoom(_initialRoomSceneName);
    }
}
