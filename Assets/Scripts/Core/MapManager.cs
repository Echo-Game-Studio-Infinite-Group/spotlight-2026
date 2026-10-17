using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

[DisallowMultipleComponent]
public sealed partial class MapManager : MonoBehaviour
{
    private static MapManager _instance;
    private readonly Dictionary<string, AsyncOperation> _loads = new Dictionary<string, AsyncOperation>();
    private readonly HashSet<string> _unloads = new HashSet<string>();
    private readonly HashSet<int> _persistentScenes = new HashSet<int>();
    private bool _isRestarting;
    public static bool Exists => _instance != null;
    public string BootstrapSceneName { get; private set; } = string.Empty;

    public static MapManager Instance
    {
        get
        {
            if (_instance == null) _instance = new GameObject("MapManager").AddComponent<MapManager>();
            return _instance;
        }
    }

    public bool IsTransitioning { get; private set; }
    public string CurrentRoomSceneName { get; private set; } = string.Empty;
    public string LastError { get; private set; } = string.Empty;
    public event Action<string> RoomLoaded;
    public event Action<string> RoomEntered;
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
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        if (_instance == this) _instance = null;
    }

    public void RegisterPersistentScene(Scene scene)
    {
        if (!scene.IsValid()) return;
        _persistentScenes.Add(scene.handle);
        if (string.IsNullOrEmpty(BootstrapSceneName)) BootstrapSceneName = scene.name;
    }

    public bool TryRestartMap()
    {
        if (IsTransitioning || string.IsNullOrEmpty(BootstrapSceneName)) return false;
        IsTransitioning = true;
        StartCoroutine(RestartMap());
        return true;
    }

    private IEnumerator RestartMap()
    {
        // 等待所有后台任务完成，避免 Single 重启后旧的预加载任务把旧房间重新加回来。
        while (_loads.Count > 0 || _unloads.Count > 0) yield return null;
        _isRestarting = true;
        AsyncOperation load = null;
        try { load = SceneManager.LoadSceneAsync(BootstrapSceneName, LoadSceneMode.Single); }
        catch (Exception exception) { Fail("无法重开地图：" + exception.Message); }
        if (load == null)
        {
            _isRestarting = false;
            IsTransitioning = false;
            yield break;
        }
        while (!load.isDone) yield return null;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!_isRestarting || mode != LoadSceneMode.Single || scene.name != BootstrapSceneName) return;
        _persistentScenes.Clear();
        _persistentScenes.Add(scene.handle);
        CurrentRoomSceneName = string.Empty;
        LastError = string.Empty;
        _isRestarting = false;
        // sceneLoaded 早于新场景的 Start，在 Bootstrapper 发起首房加载前解除重启锁。
        IsTransitioning = false;
    }

    public bool IsRoomReady(string sceneName)
    {
        if (string.IsNullOrWhiteSpace(sceneName)) return false;
        Scene scene = SceneManager.GetSceneByName(sceneName);
        return scene.IsValid() && scene.isLoaded && !_unloads.Contains(sceneName);
    }

    public bool TryLoadInitialRoom(string sceneName) => TryEnterRoom(string.Empty, sceneName);

    public bool PreloadRoom(string sceneName)
    {
        if (!CanLoadRoom(sceneName)) return false;
        return IsRoomReady(sceneName) || BeginLoad(sceneName) != null;
    }

    public bool TryEnterRoom(string sourceSceneName, string targetSceneName)
    {
        if (IsTransitioning || !CanLoadRoom(targetSceneName)) return false;
        if (!string.IsNullOrEmpty(sourceSceneName) && sourceSceneName != targetSceneName)
        {
            Scene source = SceneManager.GetSceneByName(sourceSceneName);
            if (source.IsValid() && _persistentScenes.Contains(source.handle))
                return Fail($"场景「{sourceSceneName}」是常驻场景，不能作为待卸载房间");
        }

        // 在启动协程前设锁；已预加载的目标可能在首个 MoveNext 内完成，不能用 Coroutine 赋值当锁。
        LastError = string.Empty;
        IsTransitioning = true;
        StartCoroutine(EnterRoom(sourceSceneName, targetSceneName));
        return true;
    }

    public bool TryUnloadRoom(string sceneName)
    {
        if (string.IsNullOrWhiteSpace(sceneName)) return false;
        if (IsTransitioning || _loads.ContainsKey(sceneName) || !IsRoomReady(sceneName)) return false;
        Scene scene = SceneManager.GetSceneByName(sceneName);
        if (_persistentScenes.Contains(scene.handle) || scene == SceneManager.GetActiveScene())
            return Fail($"场景「{sceneName}」仍在使用，不能卸载");
        StartCoroutine(UnloadRoom(scene));
        return true;
    }

    private IEnumerator EnterRoom(string sourceSceneName, string targetSceneName)
    {
        try
        {
            AsyncOperation load = IsRoomReady(targetSceneName) ? null : BeginLoad(targetSceneName);
            if (load != null)
            {
                while (!load.isDone) yield return null;
                // 场景激活回调可能仍在同帧执行，下一帧再查询场景列表。
                yield return null;
            }
            if (!IsRoomReady(targetSceneName))
            {
                Fail($"房间「{targetSceneName}」加载失败，保留原房间");
                yield break;
            }

            Scene target = SceneManager.GetSceneByName(targetSceneName);
            if (SceneManager.GetActiveScene() != target && !SceneManager.SetActiveScene(target))
            {
                Fail($"无法激活房间「{targetSceneName}」");
                yield break;
            }
            CurrentRoomSceneName = targetSceneName;

            // 只有真正进入的房间才预加载后继；预加载房间自身不会递归加载整张地图。
            foreach (GameObject root in target.GetRootGameObjects())
                foreach (MapRoom room in root.GetComponentsInChildren<MapRoom>(true))
                    if (!string.IsNullOrWhiteSpace(room.NextRoomSceneName)) PreloadRoom(room.NextRoomSceneName);

            if (!string.IsNullOrWhiteSpace(sourceSceneName) && sourceSceneName != targetSceneName)
            {
                Scene source = SceneManager.GetSceneByName(sourceSceneName);
                if (source.IsValid() && source.isLoaded) yield return UnloadRoom(source);
            }
        }
        finally
        {
            IsTransitioning = false;
        }
        if (CurrentRoomSceneName == targetSceneName) RoomEntered?.Invoke(targetSceneName);
    }

    private AsyncOperation BeginLoad(string sceneName)
    {
        if (_loads.TryGetValue(sceneName, out AsyncOperation pending)) return pending;
        AsyncOperation load;
        try
        {
            // 不使用 allowSceneActivation=false，它会阻塞 Unity 后面的异步加载/卸载队列。
            load = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
        }
        catch (Exception exception)
        {
            Fail($"无法加载「{sceneName}」：{exception.Message}");
            return null;
        }
        if (load == null)
        {
            Fail($"无法加载「{sceneName}」");
            return null;
        }
        _loads.Add(sceneName, load);
        StartCoroutine(ObserveLoad(sceneName, load));
        return load;
    }

    private IEnumerator ObserveLoad(string sceneName, AsyncOperation load)
    {
        while (!load.isDone) yield return null;
        yield return null;
        _loads.Remove(sceneName);
        if (IsRoomReady(sceneName)) RoomLoaded?.Invoke(sceneName);
        else Fail($"房间「{sceneName}」加载结束，但场景不可用");
    }

    private IEnumerator UnloadRoom(Scene scene)
    {
        string sceneName = scene.name;
        if (!_unloads.Add(sceneName)) yield break;
        try
        {
            AsyncOperation unload = SceneManager.UnloadSceneAsync(scene);
            if (unload == null)
            {
                Fail($"无法卸载房间「{sceneName}」");
                yield break;
            }
            yield return unload;
        }
        finally
        {
            _unloads.Remove(sceneName);
        }
        RoomUnloaded?.Invoke(sceneName);
    }

    private bool CanLoadRoom(string sceneName)
    {
        if (string.IsNullOrWhiteSpace(sceneName)) return Fail("房间场景名不能为空");
        if (_unloads.Contains(sceneName)) return false;
        if (IsRoomReady(sceneName) || _loads.ContainsKey(sceneName)) return true;
        if (!Application.CanStreamedLevelBeLoaded(sceneName))
            return Fail($"房间「{sceneName}」不在 Build Settings 中");
        return true;
    }

    private bool Fail(string error)
    {
        LastError = error;
        Debug.LogError($"[MapManager] {error}", this);
        return false;
    }
}
