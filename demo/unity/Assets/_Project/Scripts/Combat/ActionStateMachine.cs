using UnityEngine;

// 帧驱动动作状态机骨架：启动 → 判定 → 恢复，按 ActionDefinition 帧数据推进，查取消表处理取消
// 不做具体攻击/伤害逻辑；hitbox 生成、位移派生、伤害结算均留 TODO
public class ActionStateMachine : MonoBehaviour
{
    public enum Phase { Idle, Startup, Active, Recovery }

    [Tooltip("空闲态默认动作，可为空；接入常态待机攻击逻辑时使用")]
    [SerializeField] private ActionDefinition _defaultAction;
    [Tooltip("意图 → 动作查表来源：同一意图取第一个匹配项")]
    [SerializeField] private ActionDefinition[] _actionLibrary;

    private readonly InputBuffer _input = new InputBuffer();

    private ActionDefinition _current;
    private Phase _phase = Phase.Idle;
    private float _phaseElapsed;

    public ActionDefinition Current => _current;
    public Phase CurrentPhase => _phase;
    public bool InActiveWindow => _phase == Phase.Active;

    private void Update()
    {
        _input.PollBattleKeys();
        Tick(TimeManager.PlayerDeltaTime);
    }

    public void Tick(float dt)
    {
        TryConsumeIntent();

        if (_phase == Phase.Idle || _current == null) return;

        _phaseElapsed += dt;
        switch (_phase)
        {
            case Phase.Startup:
                if (_phaseElapsed >= _current.StartupTime) EnterPhase(Phase.Active);
                break;
            case Phase.Active:
                if (_phaseElapsed >= _current.ActiveTime) EnterPhase(Phase.Recovery);
                break;
            case Phase.Recovery:
                if (_phaseElapsed >= _current.RecoveryTime) EnterPhase(Phase.Idle);
                break;
        }
    }

    private void TryConsumeIntent()
    {
        InputBuffer.Intent intent = _input.ConsumeCombo();
        if (intent == InputBuffer.Intent.None) return;

        ActionDefinition target = FindByIntent(intent);
        if (target != null)
        {
            TryStart(target);
        }
        // 意图消费后即失效、启动失败不回滚——重试策略待接取消表细化后定
    }

    // 启动条件：空闲，或当前动作的取消表允许取消到目标（cancelMatrix[from]→to）
    public bool TryStart(ActionDefinition next)
    {
        if (next == null) return false;
        if (_phase != Phase.Idle && !CanCancelTo(next)) return false;
        _current = next;
        EnterPhase(Phase.Startup);
        return true;
    }

    private bool CanCancelTo(ActionDefinition to)
    {
        if (_current == null || _current.CancelableActions == null) return false;
        return System.Array.IndexOf(_current.CancelableActions, to) >= 0;
    }

    private ActionDefinition FindByIntent(InputBuffer.Intent intent)
    {
        if (_actionLibrary == null) return null;
        for (int i = 0; i < _actionLibrary.Length; i++)
        {
            if (_actionLibrary[i] != null && _actionLibrary[i].Intent == intent) return _actionLibrary[i];
        }
        return null;
    }

    private void EnterPhase(Phase phase)
    {
        _phase = phase;
        _phaseElapsed = 0f;
        // TODO: 进入/退出 Active 时生成与回收 _current.Hitbox 对应的判定体
        // TODO: 高速普攻落地清空动量、派生跳转等与移动层的联动（策划案第 5 节）
    }

    private void OnDrawGizmosSelected()
    {
        if (_phase != Phase.Active || _current == null || _current.Hitbox == null) return;
        _current.Hitbox.DrawGizmos(transform, Color.magenta);
    }
}
