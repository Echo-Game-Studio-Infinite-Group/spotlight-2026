#if UNITY_EDITOR
// 大厅 NPR 出图投递触发器 (2026-10-12, 结构照抄 HstExtRunTrigger):
// 项目被 GUI 编辑器占用(批处理拿不到锁)时, 用"投递文件"代替 -executeMethod ——
// 本触发器用 EditorApplication.update 每 2 秒轮询请求文件, 不依赖"改代码强制重编译"来激活。
// 用法: 在 project-0/Temp/ 放一个 hst_hall_npr.request 空文件 → 空闲时自动跑两厅 NPR 全流程。
// 守卫: 编译中/刷新中/播放中/场景有未保存改动时不跑(等干净了再跑, 不弹保存对话框)。
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace HST
{
    [InitializeOnLoad]
    public static class HstHallNprTrigger
    {
        const string REQ = "Temp/hst_hall_npr.request";
        static double _nextCheck;

        static HstHallNprTrigger()
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
                { Debug.Log("[HNP-REQ] 场景有未保存改动, 请求挂起(保存后自动继续)"); return; }

            File.Delete(REQ);   // 先删再跑, 防重入
            Debug.Log("[HNP-REQ] 收到大厅 NPR 出图请求(文件投递通道), 开始…");
            HstHallNprShoot.ShootOnly();   // 材质/打光已持久写入场景, 这里只读场出图
            Debug.Log("[HNP-REQ] 请求完成");
        }
    }
}
#endif
