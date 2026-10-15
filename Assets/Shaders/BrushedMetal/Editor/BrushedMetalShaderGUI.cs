using UnityEditor;
using UnityEngine;

namespace Spotlight.BrushedMetal.Editor
{
    public sealed class BrushedMetalShaderGUI : ShaderGUI
    {
        public override void OnGUI(MaterialEditor editor, MaterialProperty[] properties)
        {
            EditorGUILayout.HelpBox("独立拉丝金属材质，不继承角色光照模型。Deferred 通过已挂载的 NPRLightingFeature 共享捕获协议；烘焙法线通常保持 Scale = 1。", MessageType.Info);
            EditorGUI.BeginChangeCheck();
            editor.TexturePropertySingleLine(new GUIContent("Base Color"), FindProperty("_BaseMap", properties), FindProperty("_BaseColor", properties));
            editor.TextureScaleOffsetProperty(FindProperty("_BaseMap", properties));
            editor.TexturePropertySingleLine(new GUIContent("Baked Normal / Strength", "与模型原始硬法线配套烘焙的法线贴图通常保持强度 1，降低强度可能暴露底层三角面。"), FindProperty("_BumpMap", properties), FindProperty("_BumpScale", properties));
            editor.TexturePropertySingleLine(new GUIContent("Metallic R / Smoothness A"), FindProperty("_MetallicGlossMap", properties));
            Draw(editor, properties, "_Metallic", "Metallic (No Metallic Map)");
            editor.TexturePropertySingleLine(new GUIContent("Roughness R (Linear)"), FindProperty("_RoughnessMap", properties));
            Draw(editor, properties, "_Smoothness", "Environment / SSR Smoothness (No Roughness Map)");
            editor.TexturePropertySingleLine(new GUIContent("Occlusion G"), FindProperty("_OcclusionMap", properties), FindProperty("_OcclusionStrength", properties));
            EditorGUILayout.Space(); EditorGUILayout.LabelField("Anisotropic Reflection", EditorStyles.boldLabel);
            Draw(editor, properties, "_MetalFlowMapEnabled", "Use Metal Direction / Brushing Map");
            if (FindProperty("_MetalFlowMapEnabled", properties).floatValue > 0.5f)
                editor.TexturePropertySingleLine(new GUIContent("Direction RG / Brush Response B (Linear)"), FindProperty("_MetalFlowMap", properties));
            foreach (string name in new[] { "_MetalAnisotropy", "_MetalReflectanceOverride", "_MetalReflectanceColor", "_MetalRoughness", "_MetalRoughnessScale", "_MetalBrushScale", "_MetalBrushStrength", "_MetalSpecBandStrength", "_MetalSpecBandThreshold", "_MetalSpecBandSoftness" })
                Draw(editor, properties, name, FindProperty(name, properties).displayName);
            EditorGUILayout.HelpBox("艺术反射颜色与粗糙度倍率调整直接高光，不重写 GBuffer / GI / SSR 的原始 PBR 数据。", MessageType.Info);
            EditorGUILayout.Space(); EditorGUILayout.LabelField("Weapon Readability", EditorStyles.boldLabel);
            foreach (string name in new[] { "_MetalFillColor", "_MetalFillStrength", "_MetalFillDirectionVS", "_MetalMinLight" })
                Draw(editor, properties, name, FindProperty(name, properties).displayName);
            EditorGUILayout.Space(); Draw(editor, properties, "_EmissionEnabled", "Emission Enabled");
            if (FindProperty("_EmissionEnabled", properties).floatValue > 0.5f)
            {
                Draw(editor, properties, "_EmissionUseMap", "Use Emission Map");
                if (FindProperty("_EmissionUseMap", properties).floatValue > 0.5f)
                    editor.TexturePropertyWithHDRColor(new GUIContent("Emission Map / HDR Color"), FindProperty("_EmissionMap", properties), FindProperty("_EmissionColor", properties), false);
                else Draw(editor, properties, "_EmissionColor", "Emission HDR Color");
                Draw(editor, properties, "_EmissionIntensity", "Emission Intensity");
            }
            EditorGUILayout.Space(); EditorGUILayout.LabelField("Geometry Outline", EditorStyles.boldLabel);
            Draw(editor, properties, "_SmoothNormalOutline", "Smooth Normal Outline (UV3 Tangent Space)");
            Draw(editor, properties, "_OverrideOutline", "Override Outline");
            if (FindProperty("_OverrideOutline", properties).floatValue > 0.5f)
                foreach (string name in new[] { "_OutlineEnabled", "_OutlineWidth", "_OverrideOutlineColor", "_OutlineColor", "_OutlineBrightness", "_OutlineGeometryEdges" })
                    Draw(editor, properties, name, FindProperty(name, properties).displayName);
            Draw(editor, properties, "_AlphaClip", "Alpha Clip");
            if (FindProperty("_AlphaClip", properties).floatValue > 0.5f) Draw(editor, properties, "_Cutoff", "Cutoff");
            Draw(editor, properties, "_ReceiveShadows", "Receive Shadows");
            Draw(editor, properties, "_EnvironmentReflections", "Environment Reflections");
            if (EditorGUI.EndChangeCheck()) foreach (Object item in editor.targets) ValidateMaterial((Material)item);
        }
        static void Draw(MaterialEditor editor, MaterialProperty[] properties, string name, string label)
            => editor.ShaderProperty(FindProperty(name, properties), label);
        static void SetFloat(Material material, string name, float value)
        { if (material.GetFloat(name) != value) material.SetFloat(name, value); }
        static void Keyword(Material material, string name, bool enabled)
        { if (material.IsKeywordEnabled(name) == enabled) return; if (enabled) material.EnableKeyword(name); else material.DisableKeyword(name); }
        public override void ValidateMaterial(Material material)
        {
            // 母材质与原生 Variant 只同步关键字和表面派生状态，不复制父材质的贴图或颜色。
            SetFloat(material, "_Surface", 0); SetFloat(material, "_ZWrite", 1);
            SetFloat(material, "_SrcBlend", 1); SetFloat(material, "_DstBlend", 0);
            bool clip = material.GetFloat("_AlphaClip") > 0.5f;
            Keyword(material, "_ALPHATEST_ON", clip);
            Keyword(material, "_NORMALMAP", material.GetTexture("_BumpMap") != null);
            Keyword(material, "_ROUGHNESSMAP", material.GetTexture("_RoughnessMap") != null);
            Keyword(material, "_METALLICSPECGLOSSMAP", material.GetTexture("_MetallicGlossMap") != null);
            Keyword(material, "_OCCLUSIONMAP", material.GetTexture("_OcclusionMap") != null);
            Keyword(material, "_SPECULAR_SETUP", false);
            Keyword(material, "_RECEIVE_SHADOWS_OFF", material.GetFloat("_ReceiveShadows") == 0);
            Keyword(material, "_SPECULARHIGHLIGHTS_OFF", material.GetFloat("_SpecularHighlights") == 0);
            Keyword(material, "_ENVIRONMENTREFLECTIONS_OFF", material.GetFloat("_EnvironmentReflections") == 0);
            bool emits = material.GetFloat("_EmissionEnabled") > 0.5f && material.GetFloat("_EmissionIntensity") > 0 && material.GetColor("_EmissionColor").maxColorComponent > 0;
            Keyword(material, "_EMISSION", emits);
            var flags = emits ? material.globalIlluminationFlags & ~MaterialGlobalIlluminationFlags.EmissiveIsBlack : material.globalIlluminationFlags | MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            if (material.globalIlluminationFlags != flags) material.globalIlluminationFlags = flags;
            string renderType = clip ? "TransparentCutout" : "Opaque";
            if (material.GetTag("RenderType", false) != renderType) material.SetOverrideTag("RenderType", renderType);
            int queue = (clip ? 2450 : 2000) + (int)material.GetFloat("_QueueOffset");
            if (material.renderQueue != queue) material.renderQueue = queue;
        }
    }
}
