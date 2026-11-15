using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

[InitializeOnLoad]
public static class PlayerModelReplacement
{
    static PlayerModelReplacement() { EditorApplication.delayCall += RunRequest; }
    private static void RunRequest()
    {
        const string request = "Library/PlayerModel.request";
        if (!File.Exists(request)) return;
        if (EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode)
        { EditorApplication.delayCall += RunRequest; return; }
        string command = File.ReadAllText(request).Trim();
        File.Delete(request);
        try
        {
            if (command == "validate") Validate();
            else Build();
            File.WriteAllText("Library/PlayerModel.result", "SUCCESS: model and humanoid animation validation completed.");

        }
        catch (Exception e) { File.WriteAllText("Library/PlayerModel.result", e.ToString()); Debug.LogException(e); }
    }

    [MenuItem("超高速行者/动画/替换主角为新 Character")]
    public static void Build()
    {
        const string path = "Assets/Art/Models/Character.fbx";
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        var importer = (ModelImporter)AssetImporter.GetAtPath(path);
        importer.animationType = ModelImporterAnimationType.Human;
        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        importer.SaveAndReimport();
        var avatar = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Avatar>().FirstOrDefault();
        if (avatar == null || !avatar.isValid || !avatar.isHuman)
            throw new InvalidOperationException("Character Humanoid Avatar 无效，未修改玩家预制体。");
        var player = PrefabUtility.LoadPrefabContents("Assets/Prefabs/Player.prefab");
        try
        {
            var animator = player.GetComponent<Animator>();
            if (player.transform.Find("RL_BoneRoot") != null)
            {
                animator.avatar = avatar;
                animator.applyRootMotion = false;
            }
            else
            {
                var oldRenderers = player.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                var model = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(path));
                PrefabUtility.UnpackPrefabInstance(model, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                model.transform.SetParent(player.transform, false);
                model.name = "CharacterVisual";
                Vector3 importedScale = model.transform.localScale;
                // Avatar 路径以 Animator 为根，直接展开导入模型的子节点。
                foreach (Transform child in model.transform.Cast<Transform>().ToArray())
                {
                    child.SetParent(player.transform, false);
                    child.localPosition = Vector3.Scale(child.localPosition, importedScale);
                    child.localScale = Vector3.Scale(child.localScale, importedScale);
                }
                UnityEngine.Object.DestroyImmediate(model);
                var newRenderers = player.GetComponentsInChildren<SkinnedMeshRenderer>(true).Except(oldRenderers).ToArray();
                if (newRenderers.Length == 0 || newRenderers.Any(r => r.bones.Length == 0))
                    throw new InvalidOperationException("新模型缺少蒙皮。");
                foreach (var r in oldRenderers) r.enabled = false;
                animator.avatar = avatar;
                animator.applyRootMotion = false;
                animator.Rebind();
                // 更新残影和描边的序列化网格引用，保持现有组件和事件接线。
                foreach (var component in player.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (component == null) continue;
                    var so = new SerializedObject(component);
                    var property = so.GetIterator();
                    while (property.Next(true))
                    {
                        if (property.isArray && property.propertyType != SerializedPropertyType.String && property.arraySize > 0)
                        {
                            var first = property.GetArrayElementAtIndex(0);
                            if (first.propertyType == SerializedPropertyType.ObjectReference && oldRenderers.Contains(first.objectReferenceValue as SkinnedMeshRenderer))
                            {
                                property.arraySize = newRenderers.Length;
                                for (int i = 0; i < newRenderers.Length; i++) property.GetArrayElementAtIndex(i).objectReferenceValue = newRenderers[i];
                            }
                        }
                    }
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
            }
            ConvertClips(player, animator);
            PrefabUtility.SaveAsPrefabAsset(player, "Assets/Prefabs/Player.prefab");
        }
        finally { PrefabUtility.UnloadPrefabContents(player); }
        foreach (string enemyPath in new[] { "Assets/Prefabs/EnemyTest.prefab", "Assets/Prefabs/EnemyHeavy.prefab" })
        {
            var enemy = PrefabUtility.LoadPrefabContents(enemyPath);
            try
            {
                var hitbox = enemy.GetComponentsInChildren<Transform>(true).First(t => t.name == "AttackHitbox");
                var position = hitbox.localPosition;
                position.z = enemyPath.Contains("Heavy") ? 1.6f : 1.1f;
                hitbox.localPosition = position;
                PrefabUtility.SaveAsPrefabAsset(enemy, enemyPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(enemy); }
        }
        AssetDatabase.SaveAssets();
    }
    private static void ConvertClips(GameObject player, Animator animator)
    {
        if (animator.runtimeAnimatorController.animationClips.All(c => c.humanMotion)) return;
        var avatar = animator.avatar;
        var oldAvatar = AssetDatabase.LoadAssetAtPath<Avatar>(AssetDatabase.GUIDToAssetPath("b831cd65aab42354c85dd6ac0e36ca9f"));
        if (oldAvatar == null) throw new InvalidOperationException("Missing source Avatar");
        animator.avatar = oldAvatar;
        animator.Rebind();
        // 从旧骨架采样肌肉曲线，保留资源 GUID、事件和循环设置。
        using (var handler = new HumanPoseHandler(oldAvatar, player.transform))
        foreach (var clip in animator.runtimeAnimatorController.animationClips.Distinct().Where(c => !c.humanMotion))
        {
            animator.Rebind();
            var curves = Enumerable.Range(0, HumanTrait.MuscleCount).Select(_ => new AnimationCurve()).ToArray();
            var pose = new HumanPose();
            int frames = Mathf.CeilToInt(clip.length * clip.frameRate);
            for (int frame = 0; frame <= frames; frame++)
            {
                float time = Mathf.Min(frame / clip.frameRate, clip.length);
                clip.SampleAnimation(player, time);
                handler.GetHumanPose(ref pose);
                for (int i = 0; i < curves.Length; i++) curves[i].AddKey(time, pose.muscles[i]);
            }
            var generated = new AnimationClip { name = clip.name, frameRate = clip.frameRate };
            for (int i = 0; i < curves.Length; i++)
                AnimationUtility.SetEditorCurve(generated, EditorCurveBinding.FloatCurve("", typeof(Animator), HumanTrait.MuscleName[i]), curves[i]);
            AnimationUtility.SetAnimationClipSettings(generated, AnimationUtility.GetAnimationClipSettings(clip));
            AnimationUtility.SetAnimationEvents(generated, AnimationUtility.GetAnimationEvents(clip));
            EditorUtility.CopySerialized(generated, clip);
            EditorUtility.SetDirty(clip);
            UnityEngine.Object.DestroyImmediate(generated);
        }
        animator.avatar = avatar;
        animator.Rebind();
        AssetDatabase.SaveAssets();
    }

    private static void Validate()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab");
        var player = UnityEngine.Object.Instantiate(prefab);
        try
        {
            var animator = player.GetComponent<Animator>();
            if (!animator.avatar.isValid || !animator.avatar.isHuman)
                throw new InvalidOperationException("玩家 Avatar 无效");
            animator.Rebind();
            ConvertClips(player, animator);
            var report = new System.Text.StringBuilder();
            foreach (var renderer in player.GetComponentsInChildren<SkinnedMeshRenderer>().Where(r => r.enabled))
            {
                if (renderer.sharedMaterials.Any(m => m == null)) throw new InvalidOperationException(renderer.name + " 缺失材质");
                report.AppendLine(renderer.name + " bounds=" + renderer.bounds + " scale=" + renderer.transform.lossyScale);
            }
            foreach (var clip in animator.runtimeAnimatorController.animationClips.Distinct())
            {
                if (!clip.humanMotion) throw new InvalidOperationException(clip.name + " 不是 Humanoid 动画");
                clip.SampleAnimation(player, clip.length * 0.4f);
                var mesh = new Mesh();
                foreach (var renderer in player.GetComponentsInChildren<SkinnedMeshRenderer>().Where(r => r.enabled))
                {
                    renderer.BakeMesh(mesh);
                    if (mesh.vertexCount == 0 || float.IsNaN(mesh.bounds.center.y))
                        throw new InvalidOperationException(clip.name + " 蒙皮采样失败");
                }
                UnityEngine.Object.DestroyImmediate(mesh);
                report.AppendLine("Humanoid sample OK: " + clip.name);
            }
            File.WriteAllText("Library/PlayerModel.validation", report.ToString());
        }
        finally { UnityEngine.Object.DestroyImmediate(player); }
    }

}
