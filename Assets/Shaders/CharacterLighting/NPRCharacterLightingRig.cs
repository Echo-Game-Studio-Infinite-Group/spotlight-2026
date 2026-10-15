using UnityEngine;

namespace Spotlight.Rendering
{
    [ExecuteAlways, DisallowMultipleComponent]
    public sealed class NPRCharacterLightingRig : MonoBehaviour
    {
        [ColorUsage(false, true)] public Color FillColor = new Color(.90f, .95f, 1);
        [Range(0, 1)] public float FillStrength = .24f;
        [Range(0, .5f)] public float MinimumLight = .08f;
        [Tooltip("相机视空间方向，+Z朝向相机；不改变主光和脸部SDF方位。")]
        public Vector3 FillDirection = new Vector3(.3f, .4f, 1);
        public Renderer[] Renderers;
        static readonly int ColorId = Shader.PropertyToID("_CharacterFillColor");
        static readonly int StrengthId = Shader.PropertyToID("_CharacterFillStrength");
        static readonly int MinimumId = Shader.PropertyToID("_CharacterMinLight");
        static readonly int DirectionId = Shader.PropertyToID("_CharacterFillDirectionVS");
        struct Previous { public Renderer Renderer; public Color Color; public float Strength, Minimum; public Vector4 Direction; }
        Previous[] previous;
        MaterialPropertyBlock block;
        void OnEnable() { block = new MaterialPropertyBlock(); Apply(); }
        void OnValidate() { if (isActiveAndEnabled) Apply(); }
        public void Apply()
        {
            if (block == null) block = new MaterialPropertyBlock();
            if (previous != null && TargetsChanged()) { Restore(); previous = null; }
            if (previous == null)
            {
                if (Renderers == null || Renderers.Length == 0) Renderers = GetComponentsInChildren<Renderer>(true);
                previous = new Previous[Renderers.Length];
                for (int i = 0; i < Renderers.Length; i++)
                {
                    var r = Renderers[i]; if (!r) continue;
                    r.GetPropertyBlock(block); var m = r.sharedMaterial;
                    previous[i] = new Previous { Renderer = r,
                        Color = block.HasColor(ColorId) ? block.GetColor(ColorId) : m && m.HasProperty(ColorId) ? m.GetColor(ColorId) : Color.white,
                        Strength = block.HasFloat(StrengthId) ? block.GetFloat(StrengthId) : m && m.HasProperty(StrengthId) ? m.GetFloat(StrengthId) : 0,
                        Minimum = block.HasFloat(MinimumId) ? block.GetFloat(MinimumId) : m && m.HasProperty(MinimumId) ? m.GetFloat(MinimumId) : 0,
                        Direction = block.HasVector(DirectionId) ? block.GetVector(DirectionId) : m && m.HasProperty(DirectionId) ? m.GetVector(DirectionId) : new Vector4(.3f,.4f,1,0) };
                }
            }
            // 视空间补光由每个相机的 shader 矩阵转换，角色移动/镜头旋转不需要CPU每帧改灯。
            foreach (var saved in previous)
            {
                if (!saved.Renderer) continue;
                saved.Renderer.GetPropertyBlock(block);
                block.SetColor(ColorId, FillColor); block.SetFloat(StrengthId, FillStrength);
                block.SetFloat(MinimumId, MinimumLight); block.SetVector(DirectionId, FillDirection.normalized);
                saved.Renderer.SetPropertyBlock(block);
            }
        }
        bool TargetsChanged()
        {
            if (Renderers == null || Renderers.Length != previous.Length) return true;
            for (int i = 0; i < Renderers.Length; i++) if (Renderers[i] != previous[i].Renderer) return true;
            return false;
        }
        void OnDisable() { Restore(); previous = null; }
        void Restore()
        {
            if (previous == null) return;
            foreach (var saved in previous)
            {
                if (!saved.Renderer) continue;
                saved.Renderer.GetPropertyBlock(block);
                // 只还原本组件拥有的四个键，不能 Clear 掉运动/描边等系统写入的其它参数。
                block.SetColor(ColorId, saved.Color); block.SetFloat(StrengthId, saved.Strength);
                block.SetFloat(MinimumId, saved.Minimum); block.SetVector(DirectionId, saved.Direction);
                saved.Renderer.SetPropertyBlock(block);
            }
            previous = null;
        }
    }
}
