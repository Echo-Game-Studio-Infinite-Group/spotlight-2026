using UnityEngine;

/// <summary>
/// 音频系统的静态门面。目前只剩动态连续动作音效；
/// 一次性音效 / 音乐 / 故障效果后续由 Wwise 提供入口。
/// </summary>
public static class GameAudio
{
    public static bool IsReady => AudioSystem.Instance != null;

    public static AudioActionHandle PlayAction(
        AudioActionDefinition definition, Transform follow, string surfaceId = "")
    {
        return AudioSystem.Instance != null
            ? AudioSystem.Instance.PlayAction(definition, follow, surfaceId)
            : null;
    }
}
