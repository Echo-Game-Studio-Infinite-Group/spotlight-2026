using UnityEngine;

// 放在特效或特殊物件根节点，子节点即使使用普通不透明材质也不参与墙体淡出。
[DisallowMultipleComponent, AddComponentMenu("超高速行者/摄像机/排除遮挡透明化")]
public sealed class CameraWallFadeIgnore : MonoBehaviour
{
}
