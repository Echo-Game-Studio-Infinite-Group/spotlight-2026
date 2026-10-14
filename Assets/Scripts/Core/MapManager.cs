using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// 地图流式加载的唯一入口：房间场景始终以 Additive 方式加载，避免切场景时销毁仍在使用的相邻房间。
[DisallowMultipleComponent]
public sealed class MapManager : MonoBehaviour
{
    private sealed class RoomLoadRequest
    {
        public Coroutine Routine;
    }

    private static MapManager _instance;
    private readonly Dictionary<string, RoomLoadRequest> _loadingRequests =
        new Dictionary<string, RoomLoadRequest>(StringComparer.Ordinal);
    private Coroutine _transitionRoutine;

    public static MapManager Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = new GameObject("MapManager").AddComponent<MapManager>();
            }

            return _instance;
        }
    }

    public bool IsTransitioning => _transitionRoutine != null;
    public event Action<string> RoomLoaded;
    public event Action<string> RoomUnloaded;

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnDestroy()
    {
        if (_instance == this) _instance = null;
    }

    // 启动场景使用。sourceRoomSceneName 为空时，不会卸载 Boot 等常驻场景。
    public bool TryLoadInitialRoom(string roomSceneName)
    {
        return TryEnterRoom(string.Empty, roomSceneName);
    }

    // 在玩家进入当前房间时调用；只加载资源，不改活动场景，因此不会抢走输入与光照上下文。
    public bool PreloadRoom(string roomSceneName)
    {
        if (!CanLoadRoom(roomSceneName)) return false;
        BeginRoomLoad(roomSceneName);
        return true;
    }

    // 目标房间必须先完整加载，才会切换活动场景并卸载来源房间，门口不会出现空场景。
    public bool TryEnterRoom(string sourceRoomSceneName, string targetRoomSceneName)
    {
        if (IsTransitioning)
        {
            Debug.LogWarning("[MapManager] 正在切换房间，忽略重复请求", this);
            return false;
        }

        if (!CanLoadRoom(targetRoomSceneName)) return false;

        _transitionRoutine = StartCoroutine(EnterRoomRoutine(sourceRoomSceneName, targetRoomSceneName));
        return true;
    }

    public bool TryUnloadRoom(string roomSceneName)
    {
        if (string.IsNullOrWhiteSpace(roomSceneName)) return false;

        Scene room = SceneManager.GetSceneByName(roomSceneName);
        if (!room.IsValid() || !room.isLoaded) return false;

        if (room == SceneManager.GetActiveScene())
        {
            Debug.LogWarning($"[MapManager] 房间「{roomSceneName}」仍是活动场景，不能卸载", this);
            return false;
        }

        StartCoroutine(UnloadRoomRoutine(room));
        return true;
    }

    private IEnumerator EnterRoomRoutine(string sourceRoomSceneName, string targetRoomSceneName)
    {
        RoomLoadRequest request = BeginRoomLoad(targetRoomSceneName);
        if (request != null) yield return request.Routine;

        Scene targetRoom = SceneManager.GetSceneByName(targetRoomSceneName);
        if (!targetRoom.IsValid() || !targetRoom.isLoaded)
        {
            Debug.LogError($"[MapManager] 房间「{targetRoomSceneName}」加载失败，保留当前房间", this);
            _transitionRoutine = null;
            yield break;
        }

        SceneManager.SetActiveScene(targetRoom);

        if (!string.IsNullOrWhiteSpace(sourceRoomSceneName) &&
            !string.Equals(sourceRoomSceneName, targetRoomSceneName, StringComparison.Ordinal))
        {
            Scene sourceRoom = SceneManager.GetSceneByName(sourceRoomSceneName);
            if (sourceRoom.IsValid() && sourceRoom.isLoaded)
            {
                yield return UnloadRoomRoutine(sourceRoom);
            }
        }

        _transitionRoutine = null;
    }

    private RoomLoadRequest BeginRoomLoad(string roomSceneName)
    {
        if (IsRoomLoaded(roomSceneName)) return null;

        if (_loadingRequests.TryGetValue(roomSceneName, out RoomLoadRequest existingRequest))
        {
            return existingRequest;
        }

        var request = new RoomLoadRequest();
        _loadingRequests.Add(roomSceneName, request);
        request.Routine = StartCoroutine(LoadRoomRoutine(roomSceneName, request));
        return request;
    }

    private IEnumerator LoadRoomRoutine(string roomSceneName, RoomLoadRequest request)
    {
        AsyncOperation load = SceneManager.LoadSceneAsync(roomSceneName, LoadSceneMode.Additive);
        if (load == null)
        {
            Debug.LogError($"[MapManager] 无法异步加载房间「{roomSceneName}」", this);
            FinishLoadRequest(roomSceneName, request);
            yield break;
        }

        yield return load;

        Scene room = SceneManager.GetSceneByName(roomSceneName);
        if (room.IsValid() && room.isLoaded)
        {
            RoomLoaded?.Invoke(roomSceneName);
        }
        else
        {
            Debug.LogError($"[MapManager] 房间「{roomSceneName}」异步加载结束但未处于已加载状态", this);
        }

        FinishLoadRequest(roomSceneName, request);
    }

    private IEnumerator UnloadRoomRoutine(Scene room)
    {
        string roomSceneName = room.name;
        AsyncOperation unload = SceneManager.UnloadSceneAsync(room);
        if (unload == null)
        {
            Debug.LogError($"[MapManager] 无法卸载房间「{roomSceneName}」", this);
            yield break;
        }

        yield return unload;
        RoomUnloaded?.Invoke(roomSceneName);
    }

    private bool CanLoadRoom(string roomSceneName)
    {
        if (string.IsNullOrWhiteSpace(roomSceneName))
        {
            Debug.LogError("[MapManager] 房间场景名不能为空", this);
            return false;
        }

        if (IsRoomLoaded(roomSceneName)) return true;

        if (!Application.CanStreamedLevelBeLoaded(roomSceneName))
        {
            Debug.LogError($"[MapManager] 房间「{roomSceneName}」不在 Build Settings 中", this);
            return false;
        }

        return true;
    }

    private static bool IsRoomLoaded(string roomSceneName)
    {
        Scene room = SceneManager.GetSceneByName(roomSceneName);
        return room.IsValid() && room.isLoaded;
    }

    private void FinishLoadRequest(string roomSceneName, RoomLoadRequest request)
    {
        if (_loadingRequests.TryGetValue(roomSceneName, out RoomLoadRequest activeRequest) &&
            ReferenceEquals(activeRequest, request))
        {
            _loadingRequests.Remove(roomSceneName);
        }
    }
}
