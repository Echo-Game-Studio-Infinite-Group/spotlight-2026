using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace GameJam.Flow
{
    [DisallowMultipleComponent, RequireComponent(typeof(TextMeshPro))]
    public sealed class FlowTextVisual : MonoBehaviour
    {
        private sealed class Surface
        {
            public Renderer Renderer;
            public Texture Atlas;
            public readonly Dictionary<Material, Material> Materials = new Dictionary<Material, Material>();
        }

        private readonly List<Surface> _surfaces = new List<Surface>();
        private TextMeshPro _text;
        private Vector3 _position, _scale, _euler;
        private bool _initialized, _configured, _persistent;
        private FlowTextAnimation _appearance, _disappearance;
        private float _hold, _appearanceSpeed, _disappearanceSpeed, _elapsed;
        private int _phase;
        private Material _layoutMaterial;
        public bool IsPlaying => _phase > 0 && _phase < 4;
        public bool IsVisible => _surfaces.Count > 0 && _surfaces[0].Renderer.enabled;
        public string Content => _text != null ? _text.text : string.Empty;
        public Bounds WorldBounds
        {
            get
            {
                if (_text == null) return new Bounds(transform.position, Vector3.zero);
                // 隐藏期间 Renderer.bounds 可能尚未被渲染系统刷新，触发检测必须使用实际排版范围。
                Bounds local = _text.textBounds; Vector3 extents = local.extents;
                Vector3 x = transform.TransformVector(Vector3.right * extents.x);
                Vector3 y = transform.TransformVector(Vector3.up * extents.y);
                Vector3 z = transform.TransformVector(Vector3.forward * extents.z);
                return new Bounds(transform.TransformPoint(local.center), new Vector3(
                    Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x),
                    Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y),
                    Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z)) * 2f);
            }
        }

        private void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            _text = GetComponent<TextMeshPro>();
            _position = transform.localPosition; _scale = transform.localScale; _euler = transform.localEulerAngles;
        }

        public void Configure(FlowTextBinding binding)
        {
            Initialize(); Hide(); ReleaseMaterials();
            _text.text = binding.Content ?? string.Empty;
            _text.fontSize = binding.FontSize;
            _text.color = binding.Color;
            _text.enableAutoSizing = false;
            _text.raycastTarget = false;
            if (_text.font == null) { Debug.LogError("[地图文字] 缺少字体资产", this); return; }
            // TMP 先用自己的字体材质生成字形，随后为每张图集绑定独立的效果材质。
            _layoutMaterial = new Material(_text.font.material) { hideFlags = HideFlags.HideAndDontSave };
            _text.fontSharedMaterial = _layoutMaterial;
            _text.ForceMeshUpdate(true, true);
            _surfaces.Add(new Surface { Renderer = _text.renderer, Atlas = _text.font.atlasTexture });
            foreach (TMP_SubMesh submesh in GetComponentsInChildren<TMP_SubMesh>(true))
                _surfaces.Add(new Surface { Renderer = submesh.renderer, Atlas = submesh.sharedMaterial.mainTexture });
            _configured = true; SetVisible(false);
        }

        public void Play(FlowTextAnimation appearance, FlowTextAnimation disappearance, float hold, bool persistent,
            float appearanceSpeed = 1f, float disappearanceSpeed = 1f)
        {
            if (!_configured) return;
            Hide(); _appearance = appearance; _disappearance = disappearance;
            _hold = Mathf.Max(0f, hold); _persistent = persistent;
            _appearanceSpeed = Mathf.Max(.01f, appearanceSpeed); _disappearanceSpeed = Mathf.Max(.01f, disappearanceSpeed);
            _phase = 1; _elapsed = 0f; SetVisible(true); Sample(_appearance, 0f);
        }

        public void ShowFinal(FlowTextAnimation appearance)
        {
            if (!_configured) return;
            _appearance = appearance; _phase = 4; Sample(appearance, appearance != null ? appearance.Duration : 0f); SetVisible(true);
        }

        public void Hide()
        {
            Initialize(); _phase = 0; _elapsed = 0f; SetVisible(false);
            transform.localPosition = _position; transform.localScale = _scale; transform.localEulerAngles = _euler;
            _text.renderer.enabled = false;
        }

        private void Update() => Advance(TimeManager.IsPaused ? 0f : TimeManager.UnscaledDeltaTime);

        public void Advance(float seconds)
        {
            if (!_configured || _phase == 0 || _phase == 4 || seconds <= 0f) return;
            _elapsed += seconds;
            // 允许一帧跨过多个阶段，低帧率时不额外延长停留时间。
            for (int step = 0; step < 3; step++)
            {
                FlowTextAnimation effect = _phase == 1 ? _appearance : _disappearance;
                float speed = _phase == 1 ? _appearanceSpeed : _disappearanceSpeed;
                float duration = _phase == 2 ? _hold : effect != null ? effect.Duration / speed : 0f;
                if (_phase != 2) Sample(effect, Mathf.Min(_elapsed * speed, effect != null ? effect.Duration : 0f));
                if (_elapsed < duration) break;
                _elapsed -= duration;
                if (_phase == 1 && _persistent) { _phase = 4; break; }
                if (_phase == 3) { Hide(); break; }
                _phase++;
            }
        }

        public void Sample(FlowTextAnimation effect, float time)
        {
            if (!_configured) return;
            Material source = _layoutMaterial;
            if (_appearance != null)
                foreach (FlowTextAnimation.MaterialKey key in _appearance.Materials)
                    if (key.Material != null) source = key.Material;
            if (effect != null)
                foreach (FlowTextAnimation.MaterialKey key in effect.Materials)
                    if (key.Material != null && key.Time <= time) source = key.Material;
            foreach (Surface surface in _surfaces)
            {
                if (!surface.Materials.TryGetValue(source, out Material material))
                {
                    material = new Material(source) { hideFlags = HideFlags.HideAndDontSave };
                    material.mainTexture = surface.Atlas; surface.Materials.Add(source, material);
                }
                surface.Renderer.sharedMaterial = material;
                Bounds bounds = _text.textBounds;
                if (material.HasProperty("_FlowTextBounds")) material.SetVector("_FlowTextBounds", new Vector4(bounds.min.x, bounds.max.x, bounds.min.y, bounds.max.y));
                if (material.HasProperty("_FlowTextUseBounds")) material.SetFloat("_FlowTextUseBounds", 1f);
                if (effect == null) continue;
                Vector3 position = _position, scale = _scale, euler = _euler;
                foreach (FlowTextAnimation.FloatTrack track in effect.Tracks)
                {
                    float value = track.Curve.Evaluate(time);
                    if (track.Property.StartsWith("material."))
                    {
                        string property = track.Property.Substring("material.".Length);
                        if (property == "_CutoffHeight") value = Mathf.LerpUnclamped(bounds.min.y, bounds.max.y,
                            Mathf.InverseLerp(effect.CutoffMinimum, effect.CutoffMaximum, value));
                        if (material.HasProperty(property)) material.SetFloat(property, value);
                    }
                    else ApplyTransform(track.Property, value, ref position, ref scale, ref euler);
                }
                transform.localPosition = position; transform.localScale = scale; transform.localEulerAngles = euler;
            }
        }

        private static void ApplyTransform(string property, float value, ref Vector3 position, ref Vector3 scale, ref Vector3 euler)
        {
            switch (property)
            {
                case "m_LocalPosition.x": position.x += value; break;
                case "m_LocalPosition.y": position.y += value; break;
                case "m_LocalPosition.z": position.z += value; break;
                case "m_LocalScale.x": scale.x *= value; break;
                case "m_LocalScale.y": scale.y *= value; break;
                case "m_LocalScale.z": scale.z *= value; break;
                case "localEulerAnglesRaw.x": euler.x += value; break;
                case "localEulerAnglesRaw.y": euler.y += value; break;
                case "localEulerAnglesRaw.z": euler.z += value; break;
            }
        }

        private void SetVisible(bool visible) { foreach (Surface surface in _surfaces) if (surface.Renderer != null) surface.Renderer.enabled = visible; }
        private void OnDisable() { if (_initialized) Hide(); }
        private void OnDestroy() => ReleaseMaterials();
        private void ReleaseMaterials()
        {
            foreach (Surface surface in _surfaces)
                foreach (Material material in surface.Materials.Values) Release(material);
            _surfaces.Clear(); Release(_layoutMaterial); _layoutMaterial = null; _configured = false;
        }
        private static void Release(Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) Destroy(value); else DestroyImmediate(value);
        }
    }
}
