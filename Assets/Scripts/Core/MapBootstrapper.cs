using System.Collections;
using UnityEngine;

// 常驻场景只保存玩家、相机和全局系统；房间场景可被卸载而不会销毁这些运行时对象。
[DisallowMultipleComponent]
public sealed class MapBootstrapper : MonoBehaviour
{
    [Tooltip("游戏开始后以 Additive 方式加载的第一个房间场景名；场景须加入 Build Settings")]
    [SerializeField] private string _initialRoomSceneName;
    [SerializeField] private PlayerMotor _playerMotor;
    private CharacterController _controller;
    private PlayerInputReader _input;
    private bool _controllerWasEnabled;
    public bool IsReady { get; private set; }
    public string InitialRoomSceneName => _initialRoomSceneName;

    public void Configure(string initialRoomSceneName, PlayerMotor playerMotor)
    {
        _initialRoomSceneName = initialRoomSceneName;
        _playerMotor = playerMotor;
    }

    private IEnumerator Start()
    {
        var manager = MapManager.Instance;
        manager.RegisterPersistentScene(gameObject.scene);
        // 动作序列已经拥有 Motor 的积分权；仅关闭碰撞控制器，加载完成前 Simulate 会直接返回。
        if (_playerMotor != null)
        {
            _controller = _playerMotor.GetComponent<CharacterController>();
            _controllerWasEnabled = _controller.enabled;
            _controller.enabled = false;
            _input = _playerMotor.GetComponent<PlayerInputReader>();
            if (_input != null) _input.SetGameplayEnabled(false);
        }
        if (!manager.TryLoadInitialRoom(_initialRoomSceneName)) yield break;
        while (manager.IsTransitioning) yield return null;
        if (manager.CurrentRoomSceneName != _initialRoomSceneName) yield break;
        IsReady = true;
        RestorePlayer();
    }

    private void OnDestroy()
    {
        RestorePlayer();
    }

    private void RestorePlayer()
    {
        if (_controller != null) _controller.enabled = _controllerWasEnabled;
        if (_input != null) _input.SetGameplayEnabled(true);
    }
}
