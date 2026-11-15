#if UNITY_EDITOR
// 屋顶封边棱·修正轮(2026-10-11, 用户三条反馈):
//   ① "楼顶封边和楼顶面视觉上有距离" → 上一轮为可读性下移到 5.06 拉的 9.5cm 缝用户不要,
//      顶棱回升到上沿贴齐天花面(5.192), 中心 = 5.192-0.037(半厚) = 5.155;
//   ② "腰线不全" + ③ "门那面墙好像没有" → 腰线只走侧墙, 门墙/端墙没有 → 克隆侧墙腰线
//      转 90° 补两段(y=3.361 与侧墙同高), 腰线环闭合。
// 前置: 本工具是 HstTrimRing 的后继轮, 场景里已有 HST_TRIMRING_DONE 标记与 HST_TrimRing_* 两段。
// 幂等: 见到 HST_TRIMRING2_DONE 即中止。
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace HST
{
    public static class HstTrimRing2
    {
        const string SceneDir = "Assets/Scenes";
        const string SceneName = "SC_Corridor_Pragmata";
        const float TopTrimY = 5.155f;     // 顶棱新中心(上沿贴齐天花 5.192)
        const float WaistY = 3.361f;       // 腰线高度(与侧墙 ARCH_Trim_*_hi 同高)
        const float EndLen = 7.9f;         // 端墙段长(z ±3.95)
        const float TrimLen = 18.2f;       // 棱网格原长

        [MenuItem("HST/屋顶封边棱-修正轮(贴顶+腰线闭环)")]
        public static void Run()
        {
            var scene = EditorSceneManager.OpenScene(SceneDir + "/" + SceneName + ".unity", OpenSceneMode.Single);
            if (GameObject.Find("HST_TRIMRING2_DONE") != null)
            { Debug.LogError("[HST-TRIM2] 已做过修正轮(检测到标记)。重跑请先还原场景。"); return; }

            // ---- 1. 顶棱全部(侧墙 8 + 上轮端墙段 2)回升贴天花 ----
            int nu = 0;
            foreach (var mr in HstCorridorExtend.AllMRs().ToArray())
            {
                bool isTop = (mr.name.StartsWith("ARCH_Trim_") && mr.name.EndsWith("_top"))
                             || mr.name.StartsWith("HST_TrimRing_");
                if (!isTop) continue;
                var b = mr.bounds;
                HstCorridorExtend.AlignBoundsCenter(mr.gameObject,
                    new Vector3(b.center.x, TopTrimY, b.center.z));
                nu++;
            }

            // ---- 2. 腰线闭环: 门墙/端墙各补一段(与侧墙腰线同网格同高) ----
            var tpl = GameObject.Find("ARCH_Trim_nY_hi");
            if (tpl == null) { Debug.LogError("[HST-TRIM2] 找不到腰线模板 ARCH_Trim_nY_hi, 中止"); return; }
            int nw = 0;
            nw += EndPiece(tpl, "HST_TrimRing_Waist_DoorWall", -17.918f);
            nw += EndPiece(tpl, "HST_TrimRing_Waist_EndWall", 40.713f);

            var marker = new GameObject("HST_TRIMRING2_DONE");
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            HstCorridorExtend.Log("封边棱修正: 顶棱回升 " + nu + " 件 → 中心 " + TopTrimY +
                                  "(贴天花), 腰线补段 " + nw + " 件, 两环全闭合, 已存盘");

            // ---- 3. 重烤链(与封边棱轮同序) ----
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
            HstCorridorExtend.Log("封边棱修正: 全流程完成");
        }

        // 端墙段: 克隆侧墙棱 → 转 90°(长轴指向 z) → 长度压到 7.9 → bounds 校准
        static int EndPiece(GameObject tpl, string name, float wx)
        {
            var c = HstCorridorExtend.CloneMesh(tpl, tpl.transform.parent, name);
            c.transform.rotation = Quaternion.Euler(0f, 90f, 0f) * tpl.transform.rotation;
            var s0 = tpl.transform.localScale;
            c.transform.localScale = new Vector3(s0.x * (EndLen / TrimLen), s0.y, s0.z);
            HstCorridorExtend.AlignBoundsCenter(c, new Vector3(wx,
                name.Contains("Waist") ? WaistY : TopTrimY, 0f));
            return 1;
        }
    }
}
#endif
