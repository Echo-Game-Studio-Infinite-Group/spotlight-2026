using System.IO;
using UnityEditor;
using UnityEngine;

public static class AudioCatalogWizard
{
    [MenuItem("超高速行者/音频/创建音频库和玩家映射")]
    public static void CreateCatalog()
    {
        string catalogPath = EditorUtility.SaveFilePanelInProject(
            "创建音频库", "GameAudioCatalog", "asset",
            "选择 GameAudioCatalog 的保存位置");
        if (string.IsNullOrEmpty(catalogPath)) return;

        string folder = Path.GetDirectoryName(catalogPath)?.Replace('\\', '/');
        if (string.IsNullOrEmpty(folder)) folder = "Assets";
        string profilePath = AssetDatabase.GenerateUniqueAssetPath(folder + "/PlayerAudioProfile.asset");

        GameAudioCatalog catalog = ScriptableObject.CreateInstance<GameAudioCatalog>();
        PlayerAudioProfile profile = ScriptableObject.CreateInstance<PlayerAudioProfile>();
        AssetDatabase.CreateAsset(catalog, catalogPath);
        AssetDatabase.CreateAsset(profile, profilePath);
        EditorUtility.SetDirty(catalog);
        EditorUtility.SetDirty(profile);
        AssetDatabase.SaveAssets();
        Selection.activeObject = catalog;
        EditorGUIUtility.PingObject(catalog);
        Debug.Log($"[AudioCatalogWizard] 已创建音频库: {catalogPath}\n玩家映射: {profilePath}");
    }
}
