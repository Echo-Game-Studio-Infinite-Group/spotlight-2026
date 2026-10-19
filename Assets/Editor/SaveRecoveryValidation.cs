using System.IO;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

[InitializeOnLoad]
public static class SaveRecoveryValidation
{
    private const string RequestPath = "Library/SaveRecoveryValidation.request";
    private const string RunningKey = "SaveRecoveryValidation.Running";
    private static readonly TestRunnerApi Runner;

    static SaveRecoveryValidation()
    {
        Runner = ScriptableObject.CreateInstance<TestRunnerApi>();
        Runner.RegisterCallbacks(new Results());
        EditorApplication.update += CheckRequest;
    }

    private static void CheckRequest()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating ||
            EditorApplication.isPlayingOrWillChangePlaymode || !File.Exists(RequestPath)) return;
        string filter = File.ReadAllText(RequestPath).Trim();
        File.Delete(RequestPath);
        Run(string.IsNullOrEmpty(filter) ? "PlayerReviveTests" : filter);
    }

    [MenuItem("超高速行者/验证/存档与复活")]
    public static void Run() => Run("PlayerReviveTests");

    private static void Run(string filter)
    {
        SessionState.SetBool(RunningKey, true);
        Runner.Execute(new ExecutionSettings(new Filter
        {
            testMode = TestMode.PlayMode,
            testNames = filter.Split(';')
        }));
    }

    private sealed class Results : ICallbacks
    {
        public void RunStarted(ITestAdaptor testsToRun) { }
        public void TestStarted(ITestAdaptor test) { }
        public void TestFinished(ITestResultAdaptor result) { }
        public void RunFinished(ITestResultAdaptor result)
        {
            if (!SessionState.GetBool(RunningKey, false)) return;
            SessionState.SetBool(RunningKey, false);
            Directory.CreateDirectory("Logs/SaveRecoveryValidation");
            File.WriteAllText("Logs/SaveRecoveryValidation/results.xml", result.ToXml().OuterXml);
            Debug.Log($"[SaveRecoveryValidation] 通过={result.PassCount} 失败={result.FailCount}");
        }
    }
}
