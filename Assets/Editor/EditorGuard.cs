using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// 编辑器装配工具统一守卫（各工具共用，不再各写一套）：
//   · RunWhenEditing：Play 中点菜单 → 自动退出播放并重试（OpenScene 在 Play 中必抛 InvalidOperationException，装配会中途夭折留半成品）
//   · ConfirmSaveModifiedScenes：OpenScene(Single) 前征求未保存改动的处理意愿，杜绝静默丢弃用户改动
public static class EditorGuard
{
    public static bool IsEditing =>
        !EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling;

    /// <summary>编辑模式直接执行；Play 中自动退出播放、回到编辑模式后重试一次</summary>
    public static void RunWhenEditing(Action action, string toolName)
    {
        if (IsEditing) { action(); return; }
        Debug.LogWarning($"[{toolName}] Play 模式：场景装配只能在编辑模式做，正在退出播放模式后重试…");
        EditorApplication.playModeStateChanged += OnStateChanged;
        EditorApplication.isPlaying = false;

        void OnStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredEditMode) return;
            EditorApplication.playModeStateChanged -= OnStateChanged;
            // 退出播放后场景会重新加载，等一帧再动，否则拿到的还是播放态的场景对象
            EditorApplication.delayCall += () => action();
        }
    }

    /// <summary>OpenScene 前调用；返回 false = 用户取消，调用方必须中止</summary>
    public static bool ConfirmSaveModifiedScenes() =>
        EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo();
}
