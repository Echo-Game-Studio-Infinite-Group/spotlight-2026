using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class MapStreamingHUD : MonoBehaviour
{
    private void OnGUI()
    {
        var manager = MapManager.Instance;
        var text = new StringBuilder();
        text.AppendLine("地图无缝加载验收 / 沿彩色通道向前走");
        text.AppendLine("WASD 移动  /  鼠标转向  /  空格跳跃  /  Esc 释放鼠标");
        text.AppendLine("当前房间：" + manager.CurrentRoomSceneName);
        text.AppendLine(manager.IsTransitioning ? "正在切换房间…" : "状态：可以前进");
        text.AppendLine("已加载场景：");
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            text.AppendLine("  " + scene.name + (scene == SceneManager.GetActiveScene() ? " [活动]" : ""));
        }
        if (!string.IsNullOrEmpty(manager.LastError)) text.AppendLine("错误：" + manager.LastError);
        text.AppendLine("进入下一段后：旧房间消失、玩家保留；到第 3 房验收结束。");
        GUI.Box(new Rect(12f, 12f, 530f, 230f), GUIContent.none);
        var style = new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true };
        GUI.Label(new Rect(24f, 22f, 510f, 210f), text.ToString(), style);
    }
}
