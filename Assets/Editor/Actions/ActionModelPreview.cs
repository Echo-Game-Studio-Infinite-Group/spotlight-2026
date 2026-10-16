using System;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace GameJam.Actions.Editor
{
    public sealed class ActionModelPreview : IDisposable
    {
        private PreviewRenderUtility _preview;
        private GameObject _model;
        private ActionAnimatorBridge _bridge;
        private ActionDefinition _action;
        private long _instance;
        private Vector2 _orbit = new Vector2(150f, 10f);
        private Vector3 _center;
        private float _distance = 4f;
        private string _error;
        private double _frame;
        private readonly System.Collections.Generic.List<GameObject> _volumeObjects = new System.Collections.Generic.List<GameObject>();
        private Mesh _wireBox;
        private Material _wireMaterial;
        public bool Loaded => _model != null;
        public Animator Animator => _bridge != null ? _bridge.Animator : null;

        public void Load(GameObject prefab, AnimatorController controller)
        {
            Dispose(); _error = null;
            if (prefab == null || controller == null) { _error = "请选择玩家预制体和 Controller"; return; }
            _preview = new PreviewRenderUtility();
            _model = UnityEngine.Object.Instantiate(prefab);
            _model.hideFlags = HideFlags.HideAndDontSave;
            _preview.AddSingleGO(_model);
            foreach (Behaviour behaviour in _model.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
            foreach (Collider collider in _model.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            foreach (ParticleSystem particles in _model.GetComponentsInChildren<ParticleSystem>(true)) particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            _model.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            Animator animator = _model.GetComponentInChildren<Animator>(true);
            PlayerAnimation locomotion = _model.GetComponentInChildren<PlayerAnimation>(true);
            if (animator == null || locomotion == null) { _error = "预制体缺少 Animator 或 PlayerAnimation"; return; }
            animator.runtimeAnimatorController = controller;
            _bridge = _model.GetComponent<ActionAnimatorBridge>() ?? _model.AddComponent<ActionAnimatorBridge>();
            _bridge.Configure(animator, locomotion);
            if (!_bridge.TryAcquire(this, out _error)) return;
            Bounds bounds = new Bounds(Vector3.up, Vector3.one);
            foreach (Renderer renderer in _model.GetComponentsInChildren<Renderer>(true))
                if (renderer is SkinnedMeshRenderer) bounds.Encapsulate(renderer.bounds);
            _center = bounds.center;
            _distance = Mathf.Max(3f, bounds.size.magnitude * 1.4f);
            _preview.camera.fieldOfView = 35f;
            _preview.camera.nearClipPlane = 0.01f;
            _preview.camera.farClipPlane = 100f;
            _preview.lights[0].intensity = 1.3f;
            _preview.lights[0].transform.rotation = Quaternion.Euler(40f, 40f, 0f);
            _preview.lights[1].intensity = 0.8f;
            _preview.ambientColor = Color.gray;
        }
        public void Sample(ActionExecutionState state, double frames, bool entered)
        {
            if (_bridge == null || !_bridge.IsOwned) return;
            if (state.Action == null)
            {
                if (_action != null) _bridge.EndAction(_instance, true);
                _action = null;
                _instance = 0;
                _bridge.Sample(default, (float)(frames / ActionSequencePlayer.FramesPerSecond));
                return;
            }
            if (!_bridge.CanPlay(state.Segment.Animation)) { _error = "预览 Controller 中不存在状态：" + state.Segment.Animation.AnimatorState; return; }
            if (entered || _action != state.Action) _bridge.EnterSegment(state);
            _action = state.Action;
            _frame = state.FrameProgress;
            _instance = state.InstanceId;
            _bridge.Sample(state, (float)(frames / ActionSequencePlayer.FramesPerSecond));
        }
        public void Scrub(ActionDefinition action, float frame)
        {
            if (action == null || action.Timeline.Count == 0) return;
            double progress = Math.Min(Math.Max(0, frame), Math.Max(0, action.TotalFrames - 0.0001));
            int index = 0, start = 0;
            while (index + 1 < action.Timeline.Count && progress >= start + action.Timeline[index].DurationFrames)
                start += action.Timeline[index++].DurationFrames;
            ActionExecutionState state = new ActionExecutionState(action, 1, index, progress, start, default);
            if (_bridge == null || !_bridge.IsOwned || !_bridge.CanPlay(state.Segment.Animation)) return;
            // 拖帧没有经过时间，应直接定位姿态，不能停在混合权重为零的起点。
            _bridge.EnterSegment(state, true);
            _action = action; _instance = 1;
            _frame = progress;
            _bridge.Sample(state, 0f);
        }
        public void Draw()
        {
            if (!string.IsNullOrEmpty(_error)) EditorGUILayout.HelpBox(_error, MessageType.Warning);
            if (_preview == null || _model == null) return;
            Rect rect = GUILayoutUtility.GetRect(260f, 260f, GUILayout.ExpandWidth(true));
            Event current = Event.current;
            if (current.type == EventType.MouseDrag && rect.Contains(current.mousePosition))
            { _orbit += current.delta * 0.5f; _orbit.y = Mathf.Clamp(_orbit.y, -70f, 70f); current.Use(); }
            if (current.type != EventType.Repaint) return;
            UpdateVolumes();
            Quaternion rotation = Quaternion.Euler(_orbit.y, _orbit.x, 0f);
            _preview.camera.transform.SetPositionAndRotation(_center - rotation * Vector3.forward * _distance, rotation);
            _preview.BeginPreview(rect, GUIStyle.none);
            _preview.Render(true);
            GUI.DrawTexture(rect, _preview.EndPreview(), ScaleMode.StretchToFill, false);
        }
        public void Dispose()
        {
            _volumeObjects.Clear();
            if (_wireBox != null) UnityEngine.Object.DestroyImmediate(_wireBox);
            if (_wireMaterial != null) UnityEngine.Object.DestroyImmediate(_wireMaterial);
            _bridge?.Release(this);
            _bridge = null;
            _action = null;
            _preview?.Cleanup();
            _preview = null;
            if (_model != null) UnityEngine.Object.DestroyImmediate(_model);
            _model = null;
        }
        private void UpdateVolumes()
        {
            int count = _action?.Combat?.HitVolumes?.Count ?? 0;
            if (count > 0 && _wireBox == null)
            {
                _wireBox = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                _wireBox.vertices = new[] { new Vector3(-.5f,-.5f,-.5f), new Vector3(.5f,-.5f,-.5f), new Vector3(.5f,.5f,-.5f), new Vector3(-.5f,.5f,-.5f),
                    new Vector3(-.5f,-.5f,.5f), new Vector3(.5f,-.5f,.5f), new Vector3(.5f,.5f,.5f), new Vector3(-.5f,.5f,.5f) };
                _wireBox.SetIndices(new[] {0,1,1,2,2,3,3,0,4,5,5,6,6,7,7,4,0,4,1,5,2,6,3,7}, MeshTopology.Lines, 0);
                _wireMaterial = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
                _wireMaterial.SetInt("_ZWrite", 0);
            }
            while (_volumeObjects.Count < count)
            {
                var box = new GameObject("判定框预览", typeof(MeshFilter), typeof(MeshRenderer)) { hideFlags = HideFlags.HideAndDontSave };
                box.transform.SetParent(_model.transform, false);
                box.GetComponent<MeshFilter>().sharedMesh = _wireBox;
                box.GetComponent<MeshRenderer>().sharedMaterial = _wireMaterial;
                _volumeObjects.Add(box);
            }
            for (int i = 0; i < _volumeObjects.Count; i++)
            {
                GameObject box = _volumeObjects[i];
                bool valid = i < count;
                ActionHitVolume volume = valid ? _action.Combat.HitVolumes[i] : null;
                Vector3 center = default, extents = default; Quaternion rotation = Quaternion.identity;
                valid = valid && volume.TryGetPose(_model.transform, out center, out extents, out rotation);
                box.SetActive(valid);
                if (!valid) continue;
                box.transform.SetPositionAndRotation(center, rotation); box.transform.localScale = extents * 2f;
                var properties = new MaterialPropertyBlock();
                properties.SetColor("_Color", volume.IsActive(_action, _frame) ? new Color(1f, .65f, .1f) : Color.gray);
                box.GetComponent<MeshRenderer>().SetPropertyBlock(properties);
            }
        }
    }
}
