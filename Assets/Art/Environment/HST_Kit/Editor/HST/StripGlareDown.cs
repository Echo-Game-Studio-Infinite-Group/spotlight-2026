#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace HST
{
    // 灯带降眩(2026-10-09, 用户令"降低炫光本身在视野中的强度"):
    // 灯带/门框发光条的自发光 6.65 → 0.85(默认后处理下从"削峰死白"降到"柔白"),
    // 只动观感、不动照明(它们的光照在光照贴图里, 不受此影响)。幂等可逆(值写死)。
    public static class StripGlareDown
    {
        [MenuItem("HST/灯带降眩(自发光6.65→0.85)")]
        public static void Apply()
        {
            string[] mats = { "M_ARCH_PaneEmit", "M_ARCH_FrameEmit" };
            foreach (var name in mats)
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>($"Assets/Art/Environment/HST_Kit/Materials/HST_Prags/{name}.mat");
                if (m == null || !m.HasProperty("_EmissionColor")) continue;
                var e = m.GetColor("_EmissionColor");
                float mx = Mathf.Max(e.r, Mathf.Max(e.g, e.b));
                if (mx < 0.01f) continue;
                float k = 0.85f / mx;
                m.SetColor("_EmissionColor", new Color(e.r * k, e.g * k, e.b * k, 1f));
                EditorUtility.SetDirty(m);
                Debug.Log($"[GLARE] {name}: {mx:F2} → 0.85");
            }
            AssetDatabase.SaveAssets();
            Debug.Log("[GLARE] 完成(照明不受影响; 想回原亮度把值改回 6.65 量级再 SetDirty 即可)");
        }
    }
}
#endif