#if UNITY_EDITOR
// 文件投递触发器 (2026-10-10): 当项目被 GUI 编辑器占用(批处理拿不到锁)时,
// 用"投递文件"代替 -executeMethod —— 编辑器下次刷新/重新编译时自动执行城市构建。
// 用后即删请求文件; 有守卫(编译中/刷新中/播放中/用户场景有未保存改动则不跑)。
// 用法: 在 project-0/Temp/ 放一个 hst_city_build.request 空文件 → 焦点回到 Unity 即触发。
using System.IO;
using UnityEditor;
using UnityEngine;

namespace HST
{
    public static class HstCityBuildTrigger
    {
        const string REQ = "Temp/hst_city_build.request";
        // 触发版本: 每次改动本行(=内容变化)会强制一次重编译, 从而跑一遍 Check ——
        // 这是"编辑器占用项目时投递构建请求"的可靠路径(改 mtime 对 2022.3 不一定触发刷新)。
        const int TriggerRev = 8;

        [InitializeOnLoadMethod]
        static void OnLoad()
        {
            EditorApplication.delayCall += Check;
        }

        static void Check()
        {
            if (!File.Exists(REQ)) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.delayCall += Check;   // 忙, 下次再看
                return;
            }
            File.Delete(REQ);
            Debug.Log("[CITY-REQ] 收到构建请求(文件投递通道), 开始构建…");
            HstCityBuilder.BuildCity();
        }
    }
}
#endif
