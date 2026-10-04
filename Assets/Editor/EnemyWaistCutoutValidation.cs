using System.IO;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

// 域重载后重新注册回调，测试结果不依赖临时的编辑器连接是否仍然在线。
[InitializeOnLoad]
public static class EnemyWaistCutoutValidation
{
    private static readonly TestRunnerApi Runner;

    static EnemyWaistCutoutValidation()
    {
        Runner = ScriptableObject.CreateInstance<TestRunnerApi>();
        Runner.RegisterCallbacks(new Results());
    }

    [MenuItem("超高速行者/验证/EnemyTest 腰斩与动画回归")]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
        SessionState.SetBool("EnemyWaistCutoutValidation.Running", true);
        Runner.Execute(new ExecutionSettings(new Filter
        {
            testMode = TestMode.PlayMode,
            assemblyNames = new[] { "GameJam.Tests.Enemy" }
        }));
    }

    [MenuItem("超高速行者/验证/Gib 预览与资源回收")]
    public static void ValidatePreview()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        var source = Object.FindObjectOfType<GibComponent>();
        if (source == null) throw new System.InvalidOperationException("当前场景没有 GibComponent");
        bool dirty = source.gameObject.scene.isDirty;
        string original = EditorJsonUtility.ToJson(source);
        var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
        try
        {
            Check(!source.BeginPreview(Vector3.forward), "普通场景必须拒绝预览旁路");
            var clone = Object.Instantiate(source.gameObject);
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(clone, scene);
            foreach (var behaviour in clone.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
            var animator = clone.GetComponentInChildren<Animator>();
            animator.enabled = true;
            animator.fireEvents = false;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.Rebind();
            animator.Update(0f);
            var gib = clone.GetComponent<GibComponent>();
            var settings = new SerializedObject(gib);
            settings.FindProperty("_lifetime").floatValue = 0f;
            settings.FindProperty("_deathChance").floatValue = 0f;
            settings.ApplyModifiedPropertiesWithoutUndo();
            Check(gib.BeginPreview(Vector3.forward), "预览应绕过概率");
            animator.SetBool("Dead", true);
            var roots = scene.GetRootGameObjects();
            var upper = System.Array.Find(roots, go => go.name == "WaistCut_Upper");
            var lower = System.Array.Find(roots, go => go.name == "WaistCut_Lower");
            Check(upper != null && lower != null, "必须生成两半");
            var a = upper.GetComponentInChildren<MeshRenderer>();
            var b = lower.GetComponentInChildren<MeshRenderer>();
            Check(a.sharedMaterial == b.sharedMaterial, "两半应共享材质");
            var block = new MaterialPropertyBlock();
            a.GetPropertyBlock(block);
            Check(block.GetFloat("_CutSide") == 1f, "上半身裁切方向");
            b.GetPropertyBlock(block);
            Check(block.GetFloat("_CutSide") == -1f, "下半身裁切方向");
            Check(block.GetFloat("_UseCutDistance") == 1f, "下半身应使用固定的裁切距离");
            var lowerMesh = b.GetComponent<MeshFilter>().sharedMesh;
            Vector3[] lowerBefore = lowerMesh.vertices;
            Vector3 lowerPosition = lower.transform.position;
            Vector3 start = upper.transform.position;
            gib.SimulatePreview(1f / 60f, -100f);
            Check(upper.transform.position != start, "动态预览必须推进运动");
            for (int i = 0; i < 600; i++)
            {
                animator.Update(1f / 60f);
                gib.SimulatePreview(1f / 60f, 0f);
            }
            Check(lower.transform.position == lowerPosition, "下半身不能被尸块物理移动");
            Vector3[] lowerAfter = lowerMesh.vertices;
            float movement = 0f;
            for (int i = 0; i < lowerAfter.Length; i++)
                movement = Mathf.Max(movement, (lowerAfter[i] - lowerBefore[i]).sqrMagnitude);
            Check(movement > 0.01f, "预览下半身必须随死亡动画倒下");
            Check(upper != null && lower != null, "Lifetime=0 不自动回收");
            var mesh = a.GetComponent<MeshFilter>().sharedMesh;
            var material = a.sharedMaterial;
            gib.ResetEffect();
            Check(upper == null && lower == null && mesh == null && material == null, "重置应回收全部运行时资源");
            Check(!gib.IsSliced && clone.GetComponentInChildren<SkinnedMeshRenderer>().enabled, "重置应恢复原模型");
            settings.Update();
            settings.FindProperty("_lifetime").floatValue = 0.1f;
            settings.ApplyModifiedPropertiesWithoutUndo();
            Check(gib.BeginPreview(Vector3.forward), "可重复预览");
            gib.SimulatePreview(0.2f, 0f);
            Check(scene.rootCount == 1, "有限寿命应自动回收尸块");
        }
        finally { UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene); }
        Check(EditorJsonUtility.ToJson(source) == original && source.gameObject.scene.isDirty == dirty,
            "预览不能修改源组件或场景保存状态");
        Debug.Log("[GibValidation] PASS：预览隔离、共享材质、互补裁切、下半身动画、零/有限寿命、重复预览与资源回收");
        GibPreviewWindow.Open(source);
    }

    private static void Check(bool passed, string message)
    {
        if (!passed) throw new System.InvalidOperationException("[GibValidation] " + message);
    }

    private sealed class Results : ICallbacks
    {
        public void RunStarted(ITestAdaptor testsToRun) { }
        public void TestStarted(ITestAdaptor test) { }
        public void TestFinished(ITestResultAdaptor result) { }
        public void RunFinished(ITestResultAdaptor result)
        {
            if (!SessionState.GetBool("EnemyWaistCutoutValidation.Running", false)) return;
            SessionState.SetBool("EnemyWaistCutoutValidation.Running", false);
            Directory.CreateDirectory("Temp/WaistCutValidation");
            File.WriteAllText("Temp/WaistCutValidation/results.xml", result.ToXml().OuterXml);
            Debug.Log($"[WaistCutValidation] 通过={result.PassCount} 失败={result.FailCount} 跳过={result.SkipCount}");
        }
    }
}
