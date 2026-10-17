using System;
using UnityEngine;

namespace GameJam.Flow
{
    public sealed class RoomVariantController
    {
        private readonly FlowContentGroup[] _groups;
        private readonly Light[] _lights;
        private readonly Color[] _colors;
        private readonly float[] _intensities;
        private readonly TextMesh[] _narratives;
        private RoomVariantDefinition _variant;
        public RoomVariantController(FlowRoomTemplate template)
        {
            _groups = template.GetComponentsInChildren<FlowContentGroup>(true);
            _lights = template.GetComponentsInChildren<Light>(true);
            _colors = new Color[_lights.Length]; _intensities = new float[_lights.Length];
            for (int i = 0; i < _lights.Length; i++) { _colors[i] = _lights[i].color; _intensities[i] = _lights[i].intensity; }
            _narratives = template.Narratives;
        }
        public void Apply(RoomVariantDefinition variant, bool current)
        {
            _variant = variant;
            foreach (FlowContentGroup group in _groups)
                group.gameObject.SetActive(Array.IndexOf(variant.ContentGroups, group.Id) >= 0 && (!group.GameplayOnly || current));
            for (int i = 0; i < _lights.Length; i++)
            {
                _lights[i].color = variant.OverrideLightColor ? variant.LightColor : _colors[i];
                _lights[i].intensity = _intensities[i] * variant.LightIntensityMultiplier;
            }
            foreach (TextMesh text in _narratives) if (text != null) text.text = variant.Narrative;
        }
        public void SetCurrent(bool current) { if (_variant != null) Apply(_variant, current); }
    }
}
