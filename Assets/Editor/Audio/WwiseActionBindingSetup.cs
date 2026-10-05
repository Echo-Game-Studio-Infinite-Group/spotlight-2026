using System;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class WwiseActionBindingSetup
{
    private const string BindingAssetPath =
        "Assets/Resources/Audio/WwiseActionBindings.asset";

    [MenuItem("超高速行者/音频/创建或更新 Wwise 动作绑定")]
    public static void Ensure()
    {
        AudioActionDefinition slide = FindDefinition("Slide");
        AudioActionDefinition wallSlide = FindDefinition("WallSlide");
        if (slide == null)
        {
            throw new InvalidOperationException(
                "Slide AudioActionDefinition was not found.");
        }
        if (wallSlide == null)
        {
            throw new InvalidOperationException(
                "WallSlide AudioActionDefinition was not found.");
        }

        string directory = Path.GetDirectoryName(BindingAssetPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        WwiseActionBindings bindings =
            AssetDatabase.LoadAssetAtPath<WwiseActionBindings>(BindingAssetPath);
        if (bindings == null)
        {
            bindings = ScriptableObject.CreateInstance<WwiseActionBindings>();
            AssetDatabase.CreateAsset(bindings, BindingAssetPath);
        }

        bindings.Entries = new[]
        {
            new WwiseActionBindings.Entry
            {
                ActionId = "slide",
                Definition = slide,
                PlayEvent = "Play_SlideSinePlugin",
                StopEvent = "Stop_SlideSinePlugin",
                SourceRelativePath = EnsureStreamingSource(slide)
            },
            new WwiseActionBindings.Entry
            {
                ActionId = "wall_slide",
                Definition = wallSlide,
                PlayEvent = "Play_SlideSinePlugin",
                StopEvent = "Stop_SlideSinePlugin",
                SourceRelativePath = EnsureStreamingSource(wallSlide)
            }
        };

        EditorUtility.SetDirty(bindings);
        AssetDatabase.Refresh();
        AssetDatabase.SaveAssets();
        Debug.Log($"Wwise action bindings updated at {BindingAssetPath}");
    }

    private static string EnsureStreamingSource(AudioActionDefinition definition)
    {
        if (definition == null || definition.Clip == null)
        {
            return string.Empty;
        }

        string sourcePath = AssetDatabase.GetAssetPath(definition.Clip);
        if (string.IsNullOrEmpty(sourcePath) ||
            !string.Equals(
                Path.GetExtension(sourcePath),
                ".wav",
                StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        string fileName = Path.GetFileName(sourcePath);
        string sourceDirectory = Path.Combine(
            Application.streamingAssetsPath,
            "Audio",
            "Source");
        Directory.CreateDirectory(sourceDirectory);

        string targetPath = Path.Combine(sourceDirectory, fileName);
        FileInfo sourceInfo = new FileInfo(sourcePath);
        FileInfo targetInfo = new FileInfo(targetPath);
        if (!targetInfo.Exists ||
            targetInfo.Length != sourceInfo.Length ||
            targetInfo.LastWriteTimeUtc < sourceInfo.LastWriteTimeUtc)
        {
            File.Copy(sourcePath, targetPath, true);
            AssetDatabase.Refresh();
        }

        return $"Audio/Source/{fileName}";
    }

    private static AudioActionDefinition FindDefinition(string assetName)
    {
        string[] guids = AssetDatabase.FindAssets("t:AudioActionDefinition");
        for (int i = 0; i < guids.Length; i++)
        {
            string path = AssetDatabase.GUIDToAssetPath(guids[i]);
            if (Path.GetFileNameWithoutExtension(path) != assetName)
            {
                continue;
            }

            AudioActionDefinition definition =
                AssetDatabase.LoadAssetAtPath<AudioActionDefinition>(path);
            if (definition != null)
            {
                return definition;
            }
        }

        return null;
    }
}
