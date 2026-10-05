using UnityEditor;
using UnityEngine;

namespace GameJam.Actions.Editor
{
    public static class ActionTimelineView
    {
        public static void Draw(ActionDefinition action, double cursor, int selectedWindow, System.Action<int> selectWindow)
        {
            if (action == null || action.TotalFrames <= 0) return;
            int windows = action.CancelWindows?.Count ?? 0;
            Rect rect = GUILayoutUtility.GetRect(200, 64 + windows * 25, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, new Color(0.1f, 0.12f, 0.15f));
            float labelWidth = Mathf.Min(150, rect.width * 0.3f);
            float left = rect.x + labelWidth;
            float width = Mathf.Max(1, rect.width - labelWidth - 10);
            int total = action.TotalFrames;
            GUI.Label(new Rect(rect.x + 6, rect.y + 5, labelWidth, 20), "动作时间轴 / 60Hz", EditorStyles.whiteLabel);
            int step = Mathf.Max(1, Mathf.CeilToInt(total / Mathf.Max(1, width / 50)));
            for (int tick = 0; tick <= total; tick += step)
            {
                float x = left + width * tick / total;
                EditorGUI.DrawRect(new Rect(x, rect.y + 23, 1, rect.height - 28), new Color(0.24f, 0.27f, 0.3f));
                GUI.Label(new Rect(x - 5, rect.y + 3, 45, 18), tick.ToString(), EditorStyles.whiteMiniLabel);
            }
            int start = 0;
            foreach (ActionSegment segment in action.Timeline)
            {
                if (segment == null || segment.DurationFrames <= 0) continue;
                Color color = segment.Phase == ActionPhase.Startup ? new Color(0.3f, 0.52f, 0.8f) :
                    segment.Phase == ActionPhase.Active ? new Color(0.8f, 0.4f, 0.3f) : new Color(0.4f, 0.65f, 0.46f);
                Rect segmentRect = new Rect(left + width * start / total, rect.y + 27, width * segment.DurationFrames / total - 1, 23);
                EditorGUI.DrawRect(segmentRect, color);
                GUI.Label(segmentRect, new GUIContent(segment.DisplayName, ActionAuthoringGUI.PhaseLabel(segment.Phase) + " [" + start + "," + (start + segment.DurationFrames) + ")"), EditorStyles.whiteMiniLabel);
                if (segment.Events != null)
                    foreach (ActionFrameEvent marker in segment.Events)
                        if (marker != null && marker.Frame >= 0 && marker.Frame < segment.DurationFrames)
                        {
                            float x = left + width * (start + marker.Frame) / total;
                            EditorGUI.DrawRect(new Rect(x, rect.y + 26, 2, 25), Color.yellow);
                        }
                start += segment.DurationFrames;
            }
            for (int i = 0; i < windows; i++)
            {
                ActionCancelWindow window = action.CancelWindows[i];
                float y = rect.y + 56 + i * 25;
                GUI.Label(new Rect(rect.x + 6, y, labelWidth - 9, 20), window?.DisplayName ?? "空窗口", EditorStyles.whiteMiniLabel);
                if (!action.TryGetWindowRange(window, out int begin, out int end))
                {
                    GUI.Label(new Rect(left, y, width, 20), "锚点失效 / 区间无效", EditorStyles.whiteMiniLabel);
                    continue;
                }
                Rect bar = new Rect(left + width * begin / total, y, width * (end - begin) / total, 20);
                Color color = i == selectedWindow ? new Color(0.3f, 0.75f, 1) : new Color(0.43f, 0.54f, 0.76f);
                EditorGUI.DrawRect(bar, color);
                if (GUI.Button(bar, new GUIContent("[" + begin + "," + end + ")", window.DisplayName), EditorStyles.whiteMiniLabel)) selectWindow?.Invoke(i);
            }
            if (cursor >= 0)
            {
                float x = left + width * Mathf.Clamp((float)cursor, 0, total) / total;
                EditorGUI.DrawRect(new Rect(x, rect.y + 23, 2, rect.height - 27), Color.white);
            }
        }
    }
}
