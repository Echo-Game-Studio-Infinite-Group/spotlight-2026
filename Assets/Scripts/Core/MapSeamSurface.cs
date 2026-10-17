using UnityEngine;
using UnityEngine.SceneManagement;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public sealed class MapSeamSurface : MonoBehaviour
{
    [SerializeField] private string _neighborSceneName;
    [SerializeField] private Mesh _ownerOnlyMesh;
    [SerializeField] private Mesh _withNeighborMesh;
    private MeshFilter _filter;

    public Mesh OwnerOnlyMesh => _ownerOnlyMesh;
    public Mesh NeighborLoadedMesh => _withNeighborMesh;
    public string NeighborSceneName => _neighborSceneName;

    public void Configure(string neighborSceneName, Mesh ownerOnlyMesh, Mesh withNeighborMesh)
    {
        _neighborSceneName = neighborSceneName;
        _ownerOnlyMesh = ownerOnlyMesh;
        _withNeighborMesh = withNeighborMesh;
        // 场景文件保存单房间的完整补面，运行时再按邻房存在与否选择。
        GetComponent<MeshFilter>().sharedMesh = ownerOnlyMesh;
    }

    private void Awake() => _filter = GetComponent<MeshFilter>();

    private void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
        SceneManager.sceneUnloaded += OnSceneUnloaded;
        Refresh();
    }

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneUnloaded -= OnSceneUnloaded;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (scene.name == _neighborSceneName) Refresh();
    }

    private void OnSceneUnloaded(Scene scene)
    {
        if (scene.name == _neighborSceneName) Refresh();
    }

    private void Refresh()
    {
        // 邻房原地板可能越过门沿；卸载后恢复属于本房的补面，不能留下视觉空洞。
        _filter.sharedMesh = SceneManager.GetSceneByName(_neighborSceneName).isLoaded ? _withNeighborMesh : _ownerOnlyMesh;
    }
}
