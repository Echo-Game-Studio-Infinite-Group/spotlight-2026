using UnityEditor;
using UnityEngine;
public static class LmStat
{
    public static void Run()
    {
        string p = "Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_Pragmata/Lightmap-0_comp_light.exr";
        var imp = AssetImporter.GetAtPath(p) as TextureImporter;
        if (imp != null && !imp.isReadable) { imp.isReadable = true; imp.SaveAndReimport(); }
        var t = AssetDatabase.LoadAssetAtPath<Texture2D>(p);
        var px = t.GetPixels();
        float mean = 0, mx = 0; double var2 = 0;
        foreach (var c in px) { float l = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b; mean += l; mx = Mathf.Max(mx, l); }
        mean /= px.Length;
        foreach (var c in px) { float l = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b; var2 += (l - mean) * (l - mean); }
        int over1 = 0, over03 = 0, under005 = 0;
        foreach (var c in px) { float l = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b; if (l > 1f) over1++; else if (l > 0.3f) over03++; else if (l < 0.05f) under005++; }
        Debug.Log($"[HST-LM] 光照贴图 {t.width}x{t.height}: mean={mean:F3} max={mx:F3} std={Mathf.Sqrt((float)(var2 / px.Length)):F3} " +
                  $">1占{100f * over1 / px.Length:F1}% >0.3占{100f * over03 / px.Length:F1}% <0.05占{100f * under005 / px.Length:F1}%");
    }
}
