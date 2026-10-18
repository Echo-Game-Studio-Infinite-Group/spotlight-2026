using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

[InitializeOnLoad]
public static class AttackPoseDiagnostic
{
    private const string Request = "Library/AttackPoseDiagnostic.request";
    static AttackPoseDiagnostic() => EditorApplication.update += Poll;

    private static void Poll()
    {
        if (EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode ||
            !File.Exists(Request) || File.ReadAllText(Request).Trim() != "run") return;
        File.WriteAllText(Request, "running");
        try { Run(); File.WriteAllText(Request, "done"); }
        catch (Exception e) { File.WriteAllText("Library/AttackPoseDiagnostic.txt", e.ToString()); }
    }

    [MenuItem("超高速行者/动画/诊断攻击脚部")]
    public static void Run()
    {
        var report = new StringBuilder();
        var scene = EditorSceneManager.NewPreviewScene();
        try
        {
            foreach (string path in new[] { "Assets/Art/Models/Character.fbx", "Assets/Prefabs/Player.prefab" })
            foreach (bool ik in new[] { false, true })
            {
                var root = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(path), scene);
                foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true)) behaviour.enabled = false;
                var animator = root.GetComponentInChildren<Animator>();
                animator.runtimeAnimatorController = null;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                var graph = PlayableGraph.Create("AttackPoseDiagnostic");
                try
                {
                    graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                    var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/Animations/Player/Attack.anim");
                    var playable = AnimationClipPlayable.Create(graph, clip);
                    playable.SetApplyFootIK(ik);
                    var output = AnimationPlayableOutput.Create(graph, "Pose", animator);
                    output.SetSourcePlayable(playable);
                    graph.Play();
                    report.AppendLine($"{path} FootIK={ik} avatar={animator.avatar.name}");
                    for (int i = 0; i <= 10; i++)
                    {
                        playable.SetTime(i * clip.length / 10d);
                        graph.Evaluate(0);
                        Vector3 left = root.transform.InverseTransformPoint(animator.GetBoneTransform(HumanBodyBones.LeftFoot).position);
                        Vector3 right = root.transform.InverseTransformPoint(animator.GetBoneTransform(HumanBodyBones.RightFoot).position);
                        report.AppendLine($"{i/10f:F1} left={left:F4} right={right:F4} width={right.x-left.x:F4}");
                    }
                }
                finally { graph.Destroy(); UnityEngine.Object.DestroyImmediate(root); }
            }
        }
        finally { EditorSceneManager.ClosePreviewScene(scene); }
        File.WriteAllText("Library/AttackPoseDiagnostic.txt", report.ToString());
    }
}
