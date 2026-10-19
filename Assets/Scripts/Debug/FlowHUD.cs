using GameJam.Flow;
using UnityEngine;

public sealed class FlowHUD : MonoBehaviour
{
    [SerializeField] private RunFlowController _flow;
    [SerializeField] private bool _showDiagnostics;
    public void Configure(RunFlowController flow, bool diagnostics) { _flow = flow; _showDiagnostics = diagnostics; }
    private void OnGUI()
    {
        if (!BuildInfo.IsDebugBuild() || _flow == null || _flow.Chaos == null) return;
        GUI.Label(new Rect(Screen.width - 190, 20, 180, 40), "混乱度  " + _flow.Chaos.Value);
        if (!_showDiagnostics) return;
        GUILayout.BeginArea(new Rect(12, Screen.height - 205, 650, 195), GUI.skin.box);
        FlowRoomInstance room = _flow.CurrentRoom;
        GUILayout.Label("流程与地图样例 / " + (_flow.IsComplete ? "通关" : _flow.IsBusy ? "恢复或装配中" : "运行中"));
        GUILayout.Label("当前：" + (room == null ? "—" : room.Id + " / " + room.Variant.Id));
        GUILayout.Label("后继：" + (_flow.NextRoom == null ? "准备中" : _flow.NextRoom.Variant.Id) +
            "　循环：" + _flow.Planner.LoopId + " / " + _flow.Planner.CycleIndex);
        GUILayout.Label("记录点：" + _flow.Checkpoints.Id + "　保留实例：" + MapManager.Instance.FlowRooms.Count);
        if (!string.IsNullOrEmpty(_flow.LastError)) GUILayout.Label("错误：" + _flow.LastError);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("重开本局")) _flow.RestartRun();
        if (GUILayout.Button("重试后继装配")) _flow.RetryPreparation();
        GUILayout.EndHorizontal(); GUILayout.EndArea();
    }
}
