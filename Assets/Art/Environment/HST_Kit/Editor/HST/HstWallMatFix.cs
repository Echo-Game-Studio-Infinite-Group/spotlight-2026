#if UNITY_EDITOR
// 门墙材质统一 (2026-10-11) —— 用户问"门墙左中右为什么是三个材质"。
// 取证结论: **材质其实是同一个 M_ARCH_WallM**, 三种观感来自三种**贴图密度**:
//   ① 墙板(下段, 走廊面板网格): 贴图 1:1 铺在 1.792×3.4 的面板模块上 —— 大块干净板;
//   ② 挑高上段(ARCH_Panel_*_band): 同一块面板网格被竖向压到 0.527 → 纹理密 1.9 倍;
//   ③ 门楣(HST_DoorHead)/门扇(ARCH_DoorLeaf_*): 用的是 **Unity 内置 Cube 网格**, UV 是
//      "整张贴图铺满一个面" → 贴图被挤进一个 1.7×2.8 / 0.75×2.4 的小面 → 细密十字格。
// 修法: 不动几何, 只给这三种尺寸各配一个**贴图缩放+偏移补偿**的材质变体, 把密度统一到
//       1.792×3.4 一个模数(offset 让裁切边落在模块的中段, 看起来就是"同一套墙板被裁短")。
// 说明: 只改 albedo 贴图参数 → 光照贴图不受影响, **无需重烤**, 跑完直接重出图。
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace HST
{
    public static class HstWallMatFix
    {
        const string MatDir = "Assets/Art/Environment/HST_Kit/Materials/HST_Prags";
        const float PanelW = 1.792f, PanelH = 3.4f;

        [MenuItem("HST/门墙材质统一+重出图")]
        public static void FixAndShoot()
        {
            Fix();
            SceneBuilder.Shoot();
        }

        [MenuItem("HST/门墙材质统一(只改材质, 不烤不拍)")]
        public static void Fix()
        {
            var scene = EditorSceneManager.OpenScene("Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            var src = AssetDatabase.LoadAssetAtPath<Material>(MatDir + "/M_ARCH_WallM.mat");
            if (src == null) { Debug.LogError("[HST-MATFIX] 找不到 M_ARCH_WallM"); return; }

            // 上段高带: 面 1.792 高, 显示整张面板的下段? 不 —— 面板自下往上 0→1, 上段是"裁短的那截",
            // 所以取上端 0.527(offset.y = 1-0.527), 顶部与面板顶对齐、底边落在模块中段 = 裁切边。
            float bandSy = 1.792f / PanelH;
            var mBand = Variant(src, "M_ARCH_WallM_Band", 1f, bandSy, 0f, 1f - bandSy);

            // 门楣: 1.708 宽 × 2.792 高(门洞顶 2.4 → 天花 5.192); 横向居中裁, 纵向上端对齐。
            float hSx = 1.708f / PanelW, hSy = 2.792f / PanelH;
            var mHead = Variant(src, "M_ARCH_WallM_DoorHead", hSx, hSy, (1f - hSx) * 0.5f, 1f - hSy);

            // 门扇: 0.75 宽 × 2.36 高, 底面与地板齐 → 取面板下端 0.694, 横向居中裁。
            float lSx = 0.75f / PanelW, lSy = 2.36f / PanelH;
            var mLeaf = Variant(src, "M_ARCH_WallM_DoorLeaf", lSx, lSy, (1f - lSx) * 0.5f, 0f);

            int nb = 0, nh = 0, nl = 0;
            foreach (var mr in Object.FindObjectsOfType<MeshRenderer>(true))
            {
                if (mr.name.EndsWith("_band")) { mr.sharedMaterial = mBand; nb++; }
                else if (mr.name == "HST_DoorHead") { mr.sharedMaterial = mHead; nh++; }
                else if (mr.name.StartsWith("ARCH_DoorLeaf")) { mr.sharedMaterial = mLeaf; nl++; }
            }
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[HST-MATFIX] 材质统一: 上段高带 " + nb + " 件 / 门楣 " + nh + " 件 / 门扇 " + nl + " 件" +
                      " (贴图密度对齐 1.792×" + PanelH + " 模数; 未动几何, 无需重烤)");
        }

        static Material Variant(Material src, string name, float sx, float sy, float ox, float oy)
        {
            var p = MatDir + "/" + name + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(p);
            if (m == null)
            {
                m = new Material(src) { name = name };
                AssetDatabase.CreateAsset(m, p);
            }
            else m.CopyPropertiesFromMaterial(src);   // 幂等: 每次从源材质重取, 只改贴图缩放
            var sc = new Vector2(sx, sy); var of = new Vector2(ox, oy);
            foreach (var prop in new[] { "_BaseMap", "_BumpMap", "_MainTex" })
                if (m.HasProperty(prop)) { m.SetTextureScale(prop, sc); m.SetTextureOffset(prop, of); }
            EditorUtility.SetDirty(m);
            AssetDatabase.SaveAssets();
            return m;
        }
    }
}
#endif
