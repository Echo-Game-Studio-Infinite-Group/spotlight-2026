using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

// 仅内部显式调用装配，避免脚本重载覆盖用户手工调整。
public static class PlayerDummySetup
{
    private const string ModelPath = "Assets/Art/Models/PlayerDummy.fbx";
    private const string RunPath = "Assets/Animations/fbx/Running.fbx";
    public static void Install()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("请先退出播放模式");
        var importer = (ModelImporter)AssetImporter.GetAtPath(ModelPath);
        importer.animationType = ModelImporterAnimationType.Human;
        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        importer.importAnimation = false;
        importer.SaveAndReimport();
        GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
        Avatar avatar = AssetDatabase.LoadAllAssetsAtPath(ModelPath).OfType<Avatar>().FirstOrDefault();
        if (avatar == null || !avatar.isValid || !avatar.isHuman)
            throw new InvalidOperationException("PlayerDummy Humanoid Avatar 无效");

        var run = (ModelImporter)AssetImporter.GetAtPath(RunPath);
        run.animationType = ModelImporterAnimationType.Human;
        run.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        // 新 FBX 不得沿用旧动作保存的参考骨架和截帧区间。
        run.humanDescription = new HumanDescription
        {
            human = Array.Empty<HumanBone>(), skeleton = Array.Empty<SkeletonBone>(),
            upperArmTwist = 0.5f, lowerArmTwist = 0.5f,
            upperLegTwist = 0.5f, lowerLegTwist = 0.5f,
            armStretch = 0.05f, legStretch = 0.05f
        };
        // defaultClipAnimations 反映的是「上一次导入」的片段表：改了 animationType / humanDescription
        // 之后必须先按新设置重导一次，否则读到的是旧 FBX 遗留的结果（换过文件的场景尤其明显）
        run.clipAnimations = Array.Empty<ModelImporterClipAnimation>();
        run.SaveAndReimport();
        run = (ModelImporter)AssetImporter.GetAtPath(RunPath);

        var clips = run.defaultClipAnimations;
        if (clips.Length != 1)
            throw new InvalidOperationException($"新版 Running 应包含一个片段，实际 {clips.Length} 个");
        clips[0].loopTime = true;
        clips[0].loopPose = true;
        run.clipAnimations = clips;
        run.SaveAndReimport();
        AnimationClip clip = AssetDatabase.LoadAllAssetsAtPath(RunPath).OfType<AnimationClip>()
            .First(c => !c.name.StartsWith("__preview__"));
        var controller = PlayerAnimationSetup.EnsureController();
        var tree = controller.layers[0].stateMachine.states.First(s => s.state.name == "Move")
            .state.motion as UnityEditor.Animations.BlendTree;
        if (tree == null) throw new InvalidOperationException("Move 缺少 BlendTree");
        var children = tree.children;
        if (children.Length != 2) throw new InvalidOperationException("Walk Run 应包含两个片段");
        children[1].motion = clip;
        tree.children = children;
        EditorUtility.SetDirty(tree);

        GameObject contents = PrefabUtility.LoadPrefabContents(MovementSceneSetup.CharacterPath);
        try
        {
            Transform visual = contents.transform.Find("PlayerDummy");
            if (visual == null)
            {
                GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(model, contents.scene);
                instance.name = "PlayerDummy";
                instance.transform.SetParent(contents.transform, false);
                visual = instance.transform;
            }
            Animator animator = visual.GetComponent<Animator>();
            if (animator == null) animator = visual.gameObject.AddComponent<Animator>();
            animator.avatar = avatar;
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;
            foreach (Transform child in visual.GetComponentsInChildren<Transform>(true))
            {
                child.gameObject.tag = "Player";
                PrefabUtility.RecordPrefabInstancePropertyModifications(child.gameObject);
            }
            PrefabUtility.RecordPrefabInstancePropertyModifications(animator);
            contents.GetComponent<PlayerAnimation>().Configure(animator);
            PrefabUtility.SaveAsPrefabAsset(contents, MovementSceneSetup.CharacterPath);
        }
        finally { PrefabUtility.UnloadPrefabContents(contents); }
        AssetDatabase.SaveAssets();
        GameObject saved = AssetDatabase.LoadAssetAtPath<GameObject>(MovementSceneSetup.CharacterPath);
        if (saved.GetComponentInChildren<Animator>().avatar != avatar || saved.GetComponent<PlayerMotor>() == null)
            throw new InvalidOperationException("预制体落盘验证失败");
        Debug.Log($"[PlayerDummySetup] SUCCESS bones={avatar.isHuman}, Running={clip.length:F3}s, frames={clips[0].firstFrame}..{clips[0].lastFrame}");
    }
}
