using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Wwise 插件激活器只依据 Wwise 工程 GeneratedSoundBanks 内的元数据判断自研
/// Source 插件是否被使用；本仓库只提交 StreamingAssets 里的 Bank，激活器可能
/// 把 SpotlightActionSource 误判为未使用并在构建前禁用 DLL。此守卫在 Wwise 的
/// 构建预处理（callbackOrder 0）之后强制恢复目标平台兼容，保证 DLL 进包。
/// </summary>
public sealed class WwisePluginDeploymentGuard : IPreprocessBuildWithReport
{
    private const string PluginAssetPath =
        "Assets/Wwise/API/Runtime/Plugins/Windows/x86_64/DSP/SpotlightActionSource.dll";

    public int callbackOrder => 1;

    public void OnPreprocessBuild(BuildReport report)
    {
        BuildTarget target = report.summary.platform;
        if (target != BuildTarget.StandaloneWindows &&
            target != BuildTarget.StandaloneWindows64)
        {
            return;
        }

        PluginImporter importer =
            AssetImporter.GetAtPath(PluginAssetPath) as PluginImporter;
        if (importer == null)
        {
            Debug.LogError($"Wwise 自研插件未找到：{PluginAssetPath}");
            return;
        }

        if (importer.GetCompatibleWithPlatform(target))
        {
            return;
        }

        importer.SetCompatibleWithPlatform(target, true);
        importer.SaveAndReimport();
        Debug.Log($"已强制启用 {PluginAssetPath} 于 {target}，避免自研 Source 插件被剔除。");
    }
}
