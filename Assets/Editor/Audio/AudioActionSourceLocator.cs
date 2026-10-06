using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Resolves one AudioActionDefinition to a readable WAV in StreamingAssets.
/// Bindings can override the path, but new assets do not need to be registered
/// before waveform analysis or audition.
/// </summary>
public static class AudioActionSourceLocator
{
    private const string BindingsPath = "Audio/WwiseActionBindings";
    private const string DefaultPreviewEvent = "Play_SlideSinePlugin";

    public static bool TryResolve(
        AudioActionDefinition definition,
        out string relativePath,
        out string error)
    {
        relativePath = string.Empty;
        error = string.Empty;
        if (definition == null || definition.Clip == null)
        {
            error = "缺少 AudioActionDefinition 或 Clip。";
            return false;
        }

        WwiseActionBindings bindings =
            Resources.Load<WwiseActionBindings>(BindingsPath);
        WwiseActionBindings.Entry entry = bindings != null
            ? bindings.Find(definition)
            : null;
        if (entry != null && !string.IsNullOrEmpty(entry.SourceRelativePath))
        {
            relativePath = entry.SourceRelativePath;
            return true;
        }

        return EnsureFromClip(
            definition,
            out relativePath,
            out error);
    }

    public static bool EnsureFromClip(
        AudioActionDefinition definition,
        out string relativePath,
        out string error)
    {
        relativePath = string.Empty;
        error = string.Empty;
        if (definition == null || definition.Clip == null)
        {
            error = "缺少 AudioActionDefinition 或 Clip。";
            return false;
        }

        string assetPath = AssetDatabase.GetAssetPath(definition.Clip);
        if (string.IsNullOrEmpty(assetPath) ||
            !string.Equals(
                Path.GetExtension(assetPath),
                ".wav",
                StringComparison.OrdinalIgnoreCase))
        {
            error = "只支持从 WAV 自动建立试听源；当前 Clip 不是 WAV。";
            return false;
        }

        string fileName = Path.GetFileName(assetPath);
        string guid = AssetDatabase.AssetPathToGUID(assetPath);
        string key = string.IsNullOrEmpty(guid)
            ? Mathf.Abs(fileName.GetHashCode()).ToString("x8")
            : guid.Substring(0, Mathf.Min(8, guid.Length));
        string generatedName = $"{key}_{fileName}";
        string generatedDirectory = Path.Combine(
            Application.streamingAssetsPath,
            "Audio",
            "Source",
            "Generated");
        Directory.CreateDirectory(generatedDirectory);

        string sourceFullPath = Path.Combine(
            Directory.GetParent(Application.dataPath).FullName,
            assetPath);
        string targetFullPath = Path.Combine(
            generatedDirectory,
            generatedName);
        FileInfo sourceInfo = new FileInfo(sourceFullPath);
        FileInfo targetInfo = new FileInfo(targetFullPath);
        if (!targetInfo.Exists ||
            targetInfo.Length != sourceInfo.Length ||
            targetInfo.LastWriteTimeUtc < sourceInfo.LastWriteTimeUtc)
        {
            File.Copy(sourceFullPath, targetFullPath, true);
            AssetDatabase.Refresh();
        }

        relativePath = $"Audio/Source/Generated/{generatedName}";
        return true;
    }

    public static string PreviewEventFor(AudioActionDefinition definition)
    {
        if (definition == null) return DefaultPreviewEvent;
        WwiseActionBindings bindings =
            Resources.Load<WwiseActionBindings>(BindingsPath);
        WwiseActionBindings.Entry entry = bindings != null
            ? bindings.Find(definition)
            : null;
        return entry != null && !string.IsNullOrEmpty(entry.PlayEvent)
            ? entry.PlayEvent
            : DefaultPreviewEvent;
    }
}
