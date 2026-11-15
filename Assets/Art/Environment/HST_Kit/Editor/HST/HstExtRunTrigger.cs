#if UNITY_EDITOR
// 走廊扩建投递触发器 (2026-10-10, 结构照抄 HstCityBuildTrigger):
// 当项目被 GUI 编辑器占用(批处理拿不到锁)时, 用"投递文件"代替 -executeMethod。
// 与城市触发器的差别: 本触发器用 EditorApplication.update 轮询请求文件(2 秒一查),
// 不依赖"改代码强制重编译"来激活 —— 触发器类只要编译过一次, 之后的请求文件随时能接。
// 用法: 在 project-0/Temp/ 放一个 hst_ext_run.request 空文件 → 空闲时自动执行走廊扩建全流程。
// 守卫: 编译中/刷新中/播放中/场景有未保存改动时不跑(等干净了再跑, 不弹保存对话框)。
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace HST
{
    [InitializeOnLoad]
    public static class HstExtRunTrigger
    {
        const string REQ = "Temp/hst_ext_run.request";
        static double _nextCheck;

        static HstExtRunTrigger()
        {
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            if (EditorApplication.timeSinceStartup < _nextCheck) return;
            _nextCheck = EditorApplication.timeSinceStartup + 2.0;
            if (!File.Exists(REQ)) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode) return;
            foreach (var s in EditorSceneManager.GetAllScenes())
                if (s.isDirty)
                { Debug.Log("[EXT-REQ] 场景有未保存改动, 扩建请求挂起(保存后自动继续)"); return; }

            File.Delete(REQ);   // 先删再跑, 防重入
            Debug.Log("[EXT-REQ] 收到扩建请求(文件投递通道), 开始全流程…");
            HstCorridorExtend.RunAll();
        }
    }
}
#endif
