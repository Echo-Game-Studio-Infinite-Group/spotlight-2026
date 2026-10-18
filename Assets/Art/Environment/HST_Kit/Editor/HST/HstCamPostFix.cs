#if UNITY_EDITOR
// CAM_06 后处理开关修正 (2026-10-12)
// 诊断: 6 台机位里只有 CAM_06_Spawn 的 renderPostProcessing = False(其余全 True) →
// 出图时只有它不经 Tonemapping, 天花板 43 的原始 HDR 值被直接 sRGB 编码 → 死白"晃眼";
// 其余机位被压到 0.992 → 过曝 0.00%。游戏用 HST_FollowCamera(post=True), 故 Play 无此现象。
// 本工具: 幂等确保所有 CAM_* 机位后处理开启 → 存盘 → 重出图(不重烤; 材质/光照数据未变)。
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace HST
{
    public static class HstCamPostFix
    {
        const string SceneDir = "Assets/Scenes";
        const string SceneName = "SC_Corridor_Pragmata";

        [MenuItem("HST/CAM后处理开关统一+重出图")]
        public static void Run()
        {
            var scene = EditorSceneManager.OpenScene(SceneDir + "/" + SceneName + ".unity", OpenSceneMode.Single);
            int fixedN = 0, total = 0;
            foreach (var cam in Object.FindObjectsOfType<Camera>(true).OrderBy(c => c.name))
            {
                if (!cam.name.StartsWith("CAM_")) continue;
                total++;
                var d = cam.GetUniversalAdditionalCameraData();
                if (!d.renderPostProcessing)
                {
                    d.renderPostProcessing = true;
                    fixedN++;
                    HstCorridorExtend.Log("后处理开关: " + cam.name + " False → True");
                }
            }
            if (fixedN > 0)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }
            HstCorridorExtend.Log($"后处理开关: {total} 台机位, 本次修正 {fixedN} 台, 已存盘");

            // 重出图(不重烤: 光照贴图与材质均未改动)
            SceneBuilder.Shoot();
            SceneBuilder.DiagLinear();
            HstCorridorExtend.Log("后处理开关: 重出图完成");
        }
    }
}
#endif
