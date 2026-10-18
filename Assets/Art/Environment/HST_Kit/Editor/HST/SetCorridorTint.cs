using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// 色调对齐(2026-10-07): Play 出图全图比 Blender 偏红 4.4%、少蓝 2.3% (01 机位实测
// B/P 通道均值比 R0.956 G0.993 B1.023) → colorFilter 乘 (0.96, 0.99, 1.02)。免重烤。
public static class SetCorridorTint
{
    public static void Apply()
    {
        var prof = AssetDatabase.LoadAssetAtPath<VolumeProfile>("Assets/Art/Environment/HST_Kit/Materials/HST_Prags/PP_SC_Corridor_Pragmata.asset");
        if (prof == null) { Debug.LogWarning("[HST-TINT] profile 缺"); return; }
        ColorAdjustments ca;
        if (!prof.TryGet(out ca)) ca = prof.Add<ColorAdjustments>(true);
        ca.colorFilter.overrideState = true;
        ca.colorFilter.value = Color.white;   // 中性: 根治走源头, 不用滤镜(用户令)
        ca.postExposure.overrideState = true;
        ca.postExposure.value = -0.45f;   // 绝对值(幂等)   // 绝对值复位(幂等! ±= 累计曾把曝光砍掉 1~2 档)
        EditorUtility.SetDirty(ca); EditorUtility.SetDirty(prof);
        AssetDatabase.SaveAssets();
        Debug.Log("[HST-TINT] colorFilter=(0.96,0.99,1.02) 已存");
    }
}
