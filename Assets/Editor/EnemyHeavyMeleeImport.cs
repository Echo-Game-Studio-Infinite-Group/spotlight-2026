using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

// 只处理 Blender 新制作的两个动作，避免重建模型时覆盖已经手调的控制器。
public sealed class EnemyHeavyMeleeImport : AssetPostprocessor
{
    private const string Folder = "Assets/Animations/EnemyHeavy/";
    private static readonly string[] Names = { "HeavyHorizontalSwing", "HeavySwingRecover" };
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
            (Names.Any(name => !File.Exists(Folder + name + ".anim")) ||
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

    [MenuItem("超高速行者/动画/提取并检查重型敌人横挥动作")]
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
            var clip = new AnimationClip { name = name, frameRate = 30f };
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
            settings.stopTime = i == 0 ? 3f : 2f;
            settings.loopTime = false;
            settings.keepOriginalPositionXZ = true;
            settings.keepOriginalPositionY = true;
            settings.keepOriginalOrientation = true;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            string path = Folder + name + ".anim";
            var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (existing == null) AssetDatabase.CreateAsset(clip, path);
            else
            {
                EditorUtility.CopySerialized(clip, existing);
                UnityEngine.Object.DestroyImmediate(clip);
                clip = existing;
                EditorUtility.SetDirty(clip);
            }
            clips[i] = clip;
            if (Mathf.Abs(clip.length - (i == 0 ? 3f : 2f)) > .001f)
                throw new InvalidOperationException(name + " 动作时长不正确：" + clip.length);
            report.AppendLine($"{name}: {clip.length:F3}s, {AnimationUtility.GetCurveBindings(clip).Length} curves");
        }

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/EnemyHeavy.prefab");
        var instance = UnityEngine.Object.Instantiate(prefab);
        instance.SetActive(false);
        try
        {
            var model = instance.GetComponentInChildren<Animator>(true).gameObject;
            foreach (var clip in clips)
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                    if (model.transform.Find(binding.path) == null)
                        throw new InvalidOperationException("模型缺少动画绑定：" + binding.path);
            var transforms = model.GetComponentsInChildren<Transform>(true);
            var idle = AssetDatabase.LoadAssetAtPath<AnimationClip>(Folder + "Idle.anim");
            idle.SampleAnimation(model, 0f);
            var idlePositions = transforms.Select(t => t.localPosition).ToArray();
            var idleRotations = transforms.Select(t => t.localRotation).ToArray();
            clips[0].SampleAnimation(model, 0f);
            float idlePosition = 0f, idleAngle = 0f;
            for (int i = 0; i < transforms.Length; i++)
            {
                idlePosition = Mathf.Max(idlePosition, Vector3.Distance(idlePositions[i], transforms[i].localPosition));
                idleAngle = Mathf.Max(idleAngle, Quaternion.Angle(idleRotations[i], transforms[i].localRotation));
            }
            // 原 Idle 使用 0.5° 压缩误差，往返比较允许 1°，交接仍使用下方的严格容差。
            if (idlePosition > .001f || idleAngle > 1f)
                throw new InvalidOperationException($"Blender 往返后待机姿势不匹配：{idlePosition} units / {idleAngle} degrees");
            report.AppendLine($"Original idle to ready: {idlePosition:F7} units / {idleAngle:F5} degrees.");
            clips[0].SampleAnimation(model, 3f);
            var endPositions = transforms.Select(t => t.localPosition).ToArray();
            var endRotations = transforms.Select(t => t.localRotation).ToArray();
            clips[1].SampleAnimation(model, 0f);
            float maxPosition = 0f, maxAngle = 0f;
            for (int i = 0; i < transforms.Length; i++)
            {
                maxPosition = Mathf.Max(maxPosition, Vector3.Distance(endPositions[i], transforms[i].localPosition));
                maxAngle = Mathf.Max(maxAngle, Quaternion.Angle(endRotations[i], transforms[i].localRotation));
            }
            if (maxPosition > .0001f || maxAngle > .05f)
                throw new InvalidOperationException($"交接处不连续：{maxPosition} units / {maxAngle} degrees");
            report.AppendLine($"Seam: {maxPosition:F7} units / {maxAngle:F5} degrees; all bindings resolve on EnemyHeavy.");
            clips[0].SampleAnimation(model, 0f);
            var readyPositions = transforms.Select(t => t.localPosition).ToArray();
            var readyRotations = transforms.Select(t => t.localRotation).ToArray();
            clips[1].SampleAnimation(model, 2f);
            float returnPosition = 0f, returnAngle = 0f;
            for (int i = 0; i < transforms.Length; i++)
            {
                returnPosition = Mathf.Max(returnPosition, Vector3.Distance(readyPositions[i], transforms[i].localPosition));
                returnAngle = Mathf.Max(returnAngle, Quaternion.Angle(readyRotations[i], transforms[i].localRotation));
            }
            if (returnPosition > .0001f || returnAngle > .05f)
                throw new InvalidOperationException($"收回未回到起始姿势：{returnPosition} units / {returnAngle} degrees");
            report.AppendLine($"Recovery to ready: {returnPosition:F7} units / {returnAngle:F5} degrees.");
        }
        finally { UnityEngine.Object.DestroyImmediate(instance); }
        AssetDatabase.SaveAssets();
        report.AppendLine("SUCCESS");
        File.WriteAllText(ReportPath, report.ToString());
        Debug.Log("[EnemyHeavyMelee] " + report);
    }
}
