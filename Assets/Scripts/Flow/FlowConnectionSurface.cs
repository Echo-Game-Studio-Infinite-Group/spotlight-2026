using UnityEngine;
using UnityEngine.SceneManagement;

namespace GameJam.Flow
{
    [RequireComponent(typeof(MeshFilter))]
    public sealed class FlowConnectionSurface : MonoBehaviour
    {
        private MeshFilter _filter;
        private Mesh _withSource, _withoutSource;
        private int _sourceHandle;
        private void Awake() => _filter = GetComponent<MeshFilter>();
        public void Configure(FlowRoomInstance source, FlowRoomInstance target)
        {
            _sourceHandle = source.Scene.handle;
            _withSource = FlowFloorMesh.Create(transform, source.Root, target.Root);
            _withoutSource = FlowFloorMesh.Create(transform, target.Root);
            _filter.sharedMesh = _withSource;
        }
        private void OnEnable() => SceneManager.sceneUnloaded += OnSceneUnloaded;
        private void OnDisable() => SceneManager.sceneUnloaded -= OnSceneUnloaded;
        private void OnSceneUnloaded(Scene scene) { if (scene.handle == _sourceHandle) _filter.sharedMesh = _withoutSource; }
        private void OnDestroy()
        {
            if (_withSource != null) Destroy(_withSource);
            if (_withoutSource != null) Destroy(_withoutSource);
        }
    }
}
