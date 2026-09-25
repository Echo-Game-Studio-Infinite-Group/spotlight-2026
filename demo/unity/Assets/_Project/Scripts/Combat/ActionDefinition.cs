using UnityEngine;

// 动作帧数据：启动/判定/恢复三段帧数（按 60fps 逻辑帧换算秒）+ 可取消动作列表
// 帧数据格式惯例参考 Ikemen GO / OpenBOR（见算法清单第二节），骨架取最小子集
[CreateAssetMenu(fileName = "ActionDefinition", menuName = "超高速行者/ActionDefinition")]
public class ActionDefinition : ScriptableObject
{
    public const int LogicFps = 60; // 与 Fixed Timestep = 1/60 一致

    [SerializeField] private string _actionName;
    [Tooltip("意图枚举：动作状态机用它在输入缓冲的仲裁结果中查表")]
    [SerializeField] private InputBuffer.Intent _intent;
    [SerializeField] private int _startupFrames;
    [SerializeField] private int _activeFrames;
    [SerializeField] private int _recoveryFrames;
    [Tooltip("取消表 to 侧：本动作后摇可被取消到的动作集合")]
    [SerializeField] private ActionDefinition[] _cancelableActions;
    [Tooltip("判定窗使用的判定框 profile，可为空")]
    [SerializeField] private HitboxProfile _hitbox;

    public string ActionName => _actionName;
    public InputBuffer.Intent Intent => _intent;
    public ActionDefinition[] CancelableActions => _cancelableActions;
    public HitboxProfile Hitbox => _hitbox;

    public float StartupTime => _startupFrames / (float)LogicFps;
    public float ActiveTime => _activeFrames / (float)LogicFps;
    public float RecoveryTime => _recoveryFrames / (float)LogicFps;
}
