using UnityEditor;
using UnityEngine;
namespace Spotlight.NPR.Editor
{
    public sealed class NPRShaderGUI : ShaderGUI
    {
        public override void OnGUI(MaterialEditor editor, MaterialProperty[] properties)
        {
            EditorGUILayout.HelpBox("Deferred Toon：支持主方向光、额外实时灯与阴影。场景连续描边由 NPRLightingFeature 提供，材质预览仅显示卡通光照。", MessageType.Info);
            EditorGUI.BeginChangeCheck();
            editor.TexturePropertySingleLine(new GUIContent("Base Map / Color"), FindProperty("_BaseMap", properties), FindProperty("_BaseColor", properties));
            editor.TextureScaleOffsetProperty(FindProperty("_BaseMap", properties));
            editor.TexturePropertySingleLine(new GUIContent("Normal Map", "Import texture as Normal Map; shared Base Map UV"), FindProperty("_BumpMap", properties), FindProperty("_BumpScale", properties));
            editor.TexturePropertySingleLine(new GUIContent("Roughness R", "Linear / non-sRGB; R=roughness, smoothness=1-R. Overrides metallic-map alpha when assigned."), FindProperty("_RoughnessMap", properties));
            var metallicMap = FindProperty("_MetallicGlossMap", properties);
            editor.TexturePropertySingleLine(new GUIContent("Metallic R / Smoothness A"), metallicMap);
            if (metallicMap.textureValue == null) Draw(editor, properties, "_Metallic", "Metallic");
            Draw(editor, properties, "_Smoothness", "Smoothness / SSR (map A multiplier)");
            Draw(editor, properties, "_EmissionEnabled", "Emission Enabled");
            if(FindProperty("_EmissionEnabled",properties).floatValue>.5f)
            {
                Draw(editor,properties,"_EmissionUseMap","Use Emission Map");
                if(FindProperty("_EmissionUseMap",properties).floatValue>.5f)
                    editor.TexturePropertyWithHDRColor(new GUIContent("Emission Map / HDR Color"),FindProperty("_EmissionMap",properties),FindProperty("_EmissionColor",properties),false);
                else Draw(editor,properties,"_EmissionColor","Emission HDR Color");
                Draw(editor,properties,"_EmissionIntensity","Emission Intensity");
            }
            Draw(editor, properties, "_AlphaClip", "Alpha Clip");
            if (FindProperty("_AlphaClip", properties).floatValue > .5f) Draw(editor, properties, "_Cutoff", "Cutoff");
            EditorGUILayout.Space(); EditorGUILayout.LabelField("Toon Lighting", EditorStyles.boldLabel);
            foreach (string name in new[] { "_ShadowColor", "_ToonThreshold", "_ToonSoftness", "_ToonSpecular", "_ToonSpecPower", "_ShadowPattern", "_Hatching", "_HatchScale" })
                editor.ShaderProperty(FindProperty(name, properties), FindProperty(name, properties).displayName);
            EditorGUILayout.Space(); Draw(editor, properties, "_OverrideOutline", "Override Outline");
            if (FindProperty("_OverrideOutline", properties).floatValue > .5f)
            { Draw(editor, properties, "_OutlineEnabled", "Outline Enabled"); Draw(editor,properties,"_OutlineGeometryEdges","Geometric Crease Edges"); Draw(editor, properties, "_OutlineWidth", "Width Pixels (clamped by Feature)");
              Draw(editor, properties, "_OverrideOutlineColor", "Custom Outline Color");
              if(FindProperty("_OverrideOutlineColor",properties).floatValue>.5f) {Draw(editor,properties,"_OutlineColor","Outline HDR Color");Draw(editor,properties,"_OutlineBrightness","Outline Brightness");} }
            if (EditorGUI.EndChangeCheck()) foreach (Object item in editor.targets) ValidateMaterial((Material)item);
        }
        static void SetFloatIfDifferent(Material material, string property, float value)
        { if(material.HasProperty(property) && material.GetFloat(property) != value) material.SetFloat(property, value); }
        static void SetKeyword(Material material, string keyword, bool enabled)
        { if(material.IsKeywordEnabled(keyword) == enabled) return; if(enabled) material.EnableKeyword(keyword); else material.DisableKeyword(keyword); }
        static void Draw(MaterialEditor editor, MaterialProperty[] properties, string name, string label) => editor.ShaderProperty(FindProperty(name, properties), label);
        public override void ValidateMaterial(Material material)
        {
            // 只同步派生状态，不复制旧 _Color/_MainTex，保留 Variant 的继承关系。
            SetFloatIfDifferent(material, "_Surface", 0);
            SetFloatIfDifferent(material, "_ZWrite", 1);
            SetFloatIfDifferent(material, "_SrcBlend", 1);
            SetFloatIfDifferent(material, "_DstBlend", 0);
            bool clip = material.GetFloat("_AlphaClip") > .5f;
            SetKeyword(material, "_ALPHATEST_ON", clip);
            SetKeyword(material, "_ROUGHNESSMAP", material.GetTexture("_RoughnessMap") != null);
            SetKeyword(material, "_NORMALMAP", material.GetTexture("_BumpMap") != null);
            SetKeyword(material, "_METALLICSPECGLOSSMAP", material.GetTexture("_MetallicGlossMap") != null);
            SetKeyword(material, "_OCCLUSIONMAP", material.GetTexture("_OcclusionMap") != null);
            SetKeyword(material, "_SPECULAR_SETUP", false);
            SetKeyword(material, "_RECEIVE_SHADOWS_OFF", material.GetFloat("_ReceiveShadows") == 0);
            SetKeyword(material, "_SPECULARHIGHLIGHTS_OFF", material.GetFloat("_SpecularHighlights") == 0);
            SetKeyword(material, "_ENVIRONMENTREFLECTIONS_OFF", material.GetFloat("_EnvironmentReflections") == 0);
            // 发光颜色控制实时发光，不要求材质已开启烘焙/实时 GI 标志。
            bool emits = material.GetFloat("_EmissionEnabled") > .5f && material.GetFloat("_EmissionIntensity") > 0 && material.GetColor("_EmissionColor").maxColorComponent > 0;
            var flags = emits ? material.globalIlluminationFlags & ~MaterialGlobalIlluminationFlags.EmissiveIsBlack : material.globalIlluminationFlags | MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            if(material.globalIlluminationFlags != flags) material.globalIlluminationFlags = flags;
            SetKeyword(material, "_EMISSION", emits);
            string renderType = clip ? "TransparentCutout" : "Opaque";
            if(material.GetTag("RenderType", false) != renderType) material.SetOverrideTag("RenderType", renderType);
            int queue = (clip ? 2450 : 2000) + (int)material.GetFloat("_QueueOffset");
            if(material.renderQueue != queue) material.renderQueue = queue;
        }
    }
}