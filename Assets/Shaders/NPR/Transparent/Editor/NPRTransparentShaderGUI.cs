using UnityEditor;
using UnityEngine;
public sealed class NPRTransparentShaderGUI : ShaderGUI
{
 public override void OnGUI(MaterialEditor editor, MaterialProperty[] properties)
 {
  EditorGUILayout.HelpBox("独立透明母材质：Alpha 混合、关闭深度写入。透明不进入当前不透明 SSR/GTAO/屏幕描边；重叠透明物体受排序限制。Emission 会经过现有 Bloom，透明度为 0 时不可见。可用 Unity 原生 Create > Material Variant 继承母材质。",MessageType.Info);
  EditorGUI.BeginChangeCheck();
  editor.PropertiesDefaultGUI(properties);
  if(EditorGUI.EndChangeCheck()) foreach(var item in editor.targets) ValidateMaterial((Material)item);
  editor.RenderQueueField();editor.EnableInstancingField();
 }
 public override void ValidateMaterial(Material material)
 {
  SetKeyword(material,"_EMISSION",material.GetFloat("_EmissionEnabled")>.5f && material.GetFloat("_EmissionIntensity")>0 && material.GetColor("_EmissionColor").maxColorComponent>0);
  SetKeyword(material,"_ROUGHNESSMAP",material.GetTexture("_RoughnessMap")!=null);
  SetKeyword(material,"_NORMALMAP",material.GetTexture("_BumpMap")!=null);
  SetKeyword(material,"_METALLICSPECGLOSSMAP",material.GetTexture("_MetallicGlossMap")!=null);
  SetKeyword(material,"_OCCLUSIONMAP",material.GetTexture("_OcclusionMap")!=null);
 }
 static void SetKeyword(Material material,string keyword,bool enabled)
 {if(material.IsKeywordEnabled(keyword)==enabled)return;if(enabled)material.EnableKeyword(keyword);else material.DisableKeyword(keyword);}
}
