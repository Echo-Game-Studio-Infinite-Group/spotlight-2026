#if UNITY_EDITOR
// 屋顶封边棱(2026-10-11, 用户令"那就两个棱吧, 中间一个, 然后屋顶封边一个"):
//   · 中间棱 = 现有腰线 ARCH_Trim_*_hi(3.36), 不动。
//   · 屋顶封边棱 = 挑高时的 _top 克隆(5.153)。实测问题: 顶棱上沿 5.190 离天花 5.192 只差 2mm,
//     贴死天花读不出来(用户观感"封边没有跟着上升"); 且只存在于侧墙, 门墙/端墙没有 → 一圈不闭合。
// 修法: 顶棱整体下移到中心 5.06(上沿 5.097, 与天花拉开 9.5cm 阴影缝, 成为一个独立可读的棱),
//   并克隆两段封住门墙(x=-17.918)与端墙(x=40.713), 棱环闭合。
// 与走廊各克隆件同口径: 网格顶点烤死世界坐标, 位置一律 AlignBoundsCenter; 缩放必须乘不是赋值。
// 幂等: 见到 HST_TRIMRING_DONE 即中止。
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace HST
{
    public static class HstTrimRing
    {
        const string SceneDir = "Assets/Scenes";
        const string SceneName = "SC_Corridor_Pragmata";
        const float TopTrimY = 5.06f;      // 顶棱新中心(原 5.153)
        const float EndLen = 7.9f;         // 端墙段长(z ±3.95, 与侧墙棱内面对齐)
        const float TrimLen = 18.2f;       // 棱网格原长

        [MenuItem("HST/屋顶封边棱-两棱方案")]
        public static void Run()
        {
            var scene = EditorSceneManager.OpenScene(SceneDir + "/" + SceneName + ".unity", OpenSceneMode.Single);
            if (GameObject.Find("HST_TRIMRING_DONE") != null)
            { Debug.LogError("[HST-TRIM] 已做过封边棱(检测到标记)。重跑请先还原场景。"); return; }

            // ---- 1. 侧墙顶棱 8 件下移到 5.06 ----
            int nm = 0;
            GameObject tpl = null;
            foreach (var mr in HstCorridorExtend.AllMRs().ToArray())
            {
                if (!mr.name.StartsWith("ARCH_Trim_") || !mr.name.EndsWith("_top")) continue;
                var b = mr.bounds;
                HstCorridorExtend.AlignBoundsCenter(mr.gameObject,
                    new Vector3(b.center.x, TopTrimY, b.center.z));
                if (tpl == null) tpl = mr.gameObject;
                nm++;
            }
            if (tpl == null) { Debug.LogError("[HST-TRIM] 找不到 ARCH_Trim_*_top, 中止"); return; }

            // ---- 2. 门墙/端墙补段(旋转 90°, 长度压到 7.9, 突出面朝走廊) ----
            var tplRot = tpl.transform.rotation;
            var s0 = tpl.transform.localScale;
            int ne = 0;
            // 门墙面 x=-17.955 朝 +x → 棱中心 = 面 + 0.037(棱截面半厚); 端墙面 40.75 朝 -x 同理
            ne += EndPiece(tpl, tplRot, s0, "HST_TrimRing_DoorWall", -17.918f);
            ne += EndPiece(tpl, tplRot, s0, "HST_TrimRing_EndWall", 40.713f);

            var marker = new GameObject("HST_TRIMRING_DONE");
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            HstCorridorExtend.Log("封边棱: 侧墙顶棱下移 " + nm + " 件 → 中心 " + TopTrimY +
                                  ", 端墙补段 " + ne + " 件, 棱环闭合, 已存盘");

            // ---- 3. 重烤链(与盖板删除轮同序) ----
            HstCorridorExtend.StripExtStatic();
            HstCorridorStage2.ResetDoorLights();
            SceneBuilder._indirectOverride = 4096;
            SceneBuilder._skipProbeBake = true;
            SceneBuilder.BakeCorridorOnly();
            SceneBuilder._skipProbeBake = false;
            SceneBuilder._indirectOverride = 0;
            SceneBuilder.Polish();
            SceneBuilder.BakeProbeManual();
            SceneBuilder.Shoot();
            SceneBuilder.DiagLinear();
            HstCorridorExtend.Log("封边棱: 全流程完成");
        }

        // 端墙段: 克隆侧墙顶棱 → 转 90°(长轴指向 z) → 长度压到 7.9 → bounds 校准到目标面
        static int EndPiece(GameObject tpl, Quaternion baseRot, Vector3 s0, string name, float wx)
        {
            var c = HstCorridorExtend.CloneMesh(tpl, tpl.transform.parent, name);
            c.transform.rotation = Quaternion.Euler(0f, 90f, 0f) * baseRot;
            c.transform.localScale = new Vector3(s0.x * (EndLen / TrimLen), s0.y, s0.z);
            HstCorridorExtend.AlignBoundsCenter(c, new Vector3(wx, TopTrimY, 0f));
            return 1;
        }
    }
}
#endif
