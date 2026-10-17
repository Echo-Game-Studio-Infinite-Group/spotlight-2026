using UnityEngine;

// Resources 配置引用原始预制体，使直接播放场景与打包后的装配方式一致。
[CreateAssetMenu(menuName = "超高速行者/场景 UI 配置")]
public sealed class SceneUISettings : ScriptableObject
{
    public GameObject HUD;
    public GameObject PauseMenu;
}
