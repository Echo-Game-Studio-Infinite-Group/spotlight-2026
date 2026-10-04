using System.IO;
using UnityEditor;
using UnityEngine;

namespace GameJam.Actions.Editor
{
    public static class ActionSequenceBatchValidation
    {
        public static void CreateAndCheckExamples()
        {
            ActionCatalog catalog = ActionSequenceExamples.Create();
            var issues = ActionCatalogValidator.Validate(catalog);
            foreach (ActionValidationIssue issue in issues)
                if (issue.Level == ActionValidationIssue.Severity.Error) throw new System.InvalidOperationException(issue.Message);
            var graph = new ActionCancelGraphView();
            var edges = ActionCancelGraph.Build(catalog);
            graph.Refresh(catalog, edges);
            // Unity 退出时会清理工程 Temp，报告必须放在工程根下以便批处理取回。
            string reports = Path.GetFullPath(Path.Combine(Application.dataPath, "../ActionSequenceReports"));
            Directory.CreateDirectory(reports);
            File.WriteAllText(Path.Combine(reports, "cancel-chain.svg"), graph.ExportSvg());
            Debug.Log("[ActionSequence] 示例动作 " + catalog.Actions.Count + " 个，取消边 " + edges.Count + " 条，配置校验通过。SVG：" + Path.Combine(reports, "cancel-chain.svg"));
        }
    }
}
