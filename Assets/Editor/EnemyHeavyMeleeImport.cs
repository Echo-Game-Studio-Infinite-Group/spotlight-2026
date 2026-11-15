using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

// 仅覆盖指定动作的曲线，保留现有资产 GUID、事件与控制器引用。
public sealed class EnemyHeavyMeleeImport : AssetPostprocessor
{
    private const string Folder = "Assets/Animations/EnemyHeavy/";
    private static readonly string[] Names = { "HeavyHorizontalSwing", "HeavySwingRecover", "HeavyDeathBackward" };
    private static readonly string[] ClipNames = { "Attack", "AttackBack", "DieBack" };
    private const string ReportPath = "Tools/model-export/enemy-heavy/unity-validation.txt";
    private static bool _queued;

    private void OnPreprocessModel()
    {
        if (!Names.Any(name => assetPath == Folder + name + ".fbx")) return;
        var importer = (ModelImporter)assetImporter;
        importer.animationType = ModelImporterAnimationType.Generic;
        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        importer.animationCompression = ModelImporterAnimationCompression.Off;
        importer.optimizeGameObjects = false;
        importer.importAnimation = true;
        importer.importCameras = false;
        importer.importLights = false;
        importer.materialImportMode = ModelImporterMaterialImportMode.None;
    }

    [InitializeOnLoadMethod]
    private static void CheckNewClips()
    {
        if (Names.All(name => File.Exists(Folder + name + ".fbx")) &&
            (ClipNames.Any(name => !File.Exists(Folder + name + ".anim")) ||
             File.GetLastWriteTimeUtc(ReportPath) < File.GetLastWriteTimeUtc("Assets/Editor/EnemyHeavyMeleeImport.cs")))
            QueueExtraction();
    }

    private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] oldPaths)
    {
        if (imported.Any(path => Names.Any(name => path == Folder + name + ".fbx"))) QueueExtraction();
    }

    private static void QueueExtraction()
    {
        if (_queued) return;
        _queued = true;
        EditorApplication.delayCall += () =>
        {
            _queued = false;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                QueueExtraction();
                return;
            }
            if (!Names.All(name => File.Exists(Folder + name + ".fbx"))) return;
            try { ExtractAndValidate(); }
            catch (Exception error)
            {
                File.WriteAllText(ReportPath, error.ToString());
                Debug.LogException(error);
            }
        };
    }

    [MenuItem("超高速行者/动画/导出重型敌人攻击与死亡动作")]
    public static void ExtractAndValidate()
    {
        var report = new StringBuilder();
        var clips = new AnimationClip[Names.Length];
        for (int i = 0; i < Names.Length; i++)
        {
            string name = Names[i];
            string sourcePath = Folder + name + ".fbx";
            var source = AssetDatabase.LoadAllAssetsAtPath(sourcePath).OfType<AnimationClip>()
                .Single(clip => !clip.name.StartsWith("__preview__", StringComparison.Ordinal));
            var clip = new AnimationClip { name = ClipNames[i], frameRate = 30f };
            foreach (var binding in AnimationUtility.GetCurveBindings(source))
            {
                var target = binding;
                // Unity 会折叠仅含骨架的 FBX 外层；运行模型保留 Armature，显式补回绑定路径。
                if (target.path == "root" || target.path.StartsWith("root/", StringComparison.Ordinal))
                    target.path = "Armature/" + target.path;
                if (!target.path.StartsWith("Armature/root", StringComparison.Ordinal)) continue;
                var curve = AnimationUtility.GetEditorCurve(source, binding);
                var keys = curve.keys;
                // FBX 每帧烘焙静止骨骼；保留两端即可维持时长并避免资产膨胀。
                if (keys.Length > 2 && keys.All(key => Mathf.Abs(key.value - keys[0].value) < .0000001f))
                    curve = AnimationCurve.Linear(keys[0].time, keys[0].value, keys[keys.Length - 1].time, keys[0].value);
                AnimationUtility.SetEditorCurve(clip, target, curve);
            }
            clip.EnsureQuaternionContinuity();
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.startTime = 0f;
            settings.stopTime = i == 0 ? 1.8f : i == 1 ? 2f : 3f;
            settings.loopTime = false;
            settings.keepOriginalPositionXZ = true;
            settings.keepOriginalPositionY = true;
            settings.keepOriginalOrientation = true;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            string path = Folder + ClipNames[i] + ".anim";
            var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (existing != null)
                AnimationUtility.SetAnimationEvents(clip, AnimationUtility.GetAnimationEvents(existing));
            if (existing == null) AssetDatabase.CreateAsset(clip, path);
            else
            {
                EditorUtility.CopySerialized(clip, existing);
                UnityEngine.Object.DestroyImmediate(clip);
                clip = existing;
                EditorUtility.SetDirty(clip);
            }
            clips[i] = clip;
            if (Mathf.Abs(clip.length - (i == 0 ? 1.8f : i == 1 ? 2f : 3f)) > .001f)
                throw new InvalidOperationException(name + " 动作时长不正确：" + clip.length);
            report.AppendLine($"{name}: {clip.length:F3}s, {AnimationUtility.GetCurveBindings(clip).Length} curves");
        }

        AssetDatabase.SaveAssets();
        report.AppendLine("EXPORTED");
        File.WriteAllText(ReportPath, report.ToString());
        Debug.Log("[EnemyHeavyMelee] " + report);
    }
}
