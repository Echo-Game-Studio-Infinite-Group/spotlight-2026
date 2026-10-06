using UnityEngine;

[CreateAssetMenu(fileName = "PlayerAudioProfile", menuName = "超高速行者/音频/玩家音频映射")]
public sealed class PlayerAudioProfile : ScriptableObject
{
    [Header("连续动作")]
    public AudioActionDefinition SlideAction;
    public AudioActionDefinition WallSlideAction;

    [Header("移动")]
    public AudioCueDefinition DefaultFootstep;
    public AudioCueDefinition Jump;

    [Header("战斗")]
    public AudioCueDefinition DrawSword;
    public AudioCueDefinition SwordSwing;
    public AudioCueDefinition Hit;
    public AudioCueDefinition Hurt;

    [Header("故障和特殊效果")]
    public AudioCueDefinition Glitch;
    public AudioCueDefinition Overdrive;

    [Header("全局层")]
    public AudioClip Music;
    public AudioClip SpeedLayer;
}
