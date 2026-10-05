using System;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
public static class AnimationDiag
{
  public static void Run(){Go();}

  public static void InspectCameraOnce() { EditorApplication.delayCall += () => {
  var sb = new StringBuilder();
  foreach (var a in Resources.FindObjectsOfTypeAll<Animator>()) {
   if (!a.gameObject.scene.IsValid()) continue;
   sb.AppendLine($"Animator {a.name} enabled={a.enabled} rootMotion={a.applyRootMotion} local={a.transform.localPosition} parent={a.transform.parent?.name}");
  }
  var model = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Art/Models/PlayerDummy.fbx");
  sb.AppendLine($"Model default rootMotion={model?.GetComponent<Animator>()?.applyRootMotion}");
  foreach (var v in Resources.FindObjectsOfTypeAll<Cinemachine.CinemachineVirtualCamera>()) {
   if (!v.gameObject.scene.IsValid()) continue;
   sb.AppendLine($"Vcam {v.name} active={v.isActiveAndEnabled} priority={v.Priority} follow={v.Follow?.name}");
  }
  var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab");
  var visualAnimator = prefab.transform.Find("PlayerDummy").GetComponent<Animator>();
  sb.AppendLine($"Saved prefab visual rootMotion={visualAnimator.applyRootMotion}");
  sb.AppendLine($"Checked at {DateTime.Now:O}");
  System.IO.File.WriteAllText("Temp/camera-diagnostic.txt", sb.ToString());
 }; }
 static void Go(){try{var sb=new StringBuilder();
  var prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab"); var a=prefab.GetComponentInChildren<Animator>(true); sb.AppendLine($"animator={a!=null} avatar={a?.avatar} valid={a?.avatar?.isValid} human={a?.avatar?.isHuman} controller={a?.runtimeAnimatorController}");
  var c=a?.runtimeAnimatorController as AnimatorController; if(c!=null){var l=c.layers[0]; foreach(var st in l.stateMachine.states){var m=st.state.motion; sb.AppendLine($"state={st.state.name} motion={m} type={m?.GetType().Name}"); if(m is BlendTree t)foreach(var ch in t.children)sb.AppendLine($" child={ch.motion} len={(ch.motion as AnimationClip)?.length} curves={(ch.motion as AnimationClip)?.humanMotion}"); if(m is AnimationClip clip)sb.AppendLine($" cliplen={clip.length} curves={AnimationUtility.GetCurveBindings(clip).Length}");}}
  foreach(var p in new[]{"Assets/Animations/fbx/Idle (1).fbx","Assets/Animations/fbx/Walking.fbx","Assets/Animations/fbx/Running.fbx","Assets/Animations/fbx/Jump.fbx","Assets/Animations/fbx/Soccer Tackle.fbx"}){var clips=AssetDatabase.LoadAllAssetsAtPath(p).OfType<AnimationClip>().Where(x=>!x.name.StartsWith("__preview__")).ToArray(); sb.AppendLine($"asset={p} clips={clips.Length} "+string.Join(";",clips.Select(x=>$"{x.name}:{x.length:F2}/{AnimationUtility.GetCurveBindings(x).Length}")));}
  System.IO.File.WriteAllText("Temp/animation-diag.txt",sb.ToString()); Debug.Log(sb.ToString()); }catch(Exception e){System.IO.File.WriteAllText("Temp/animation-diag.txt",e.ToString());Debug.LogException(e);}}
}
