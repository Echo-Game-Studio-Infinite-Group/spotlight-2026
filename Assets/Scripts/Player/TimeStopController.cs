using UnityEngine;

// 时停：按住期间把 world 层冻住，每 tick 从能量账户扣费；能量扣干就自己结束。
//
// 分界：
//   · 把 world 停住/放开是 TimeManager.SetTimeStop 的事（执行序 -100，本类不碰时间本身）
//   · 每 tick 扣多少在 EnergyParams.TimeStopCostPerTick
//   · 「右键按住 / 松开」的判定归按键系统 —— 本类只提供 BeginTimeStop / EndTimeStop 两个接入点
//
// ⚠️ 必须成对：SetTimeStop(true) 是"按住型"来源，不带到期时刻，
//    没有 EndTimeStop 收尾世界会一直停着（所以 OnDisable / 能量耗尽都要自己收）。
public class TimeStopController : MonoBehaviour
{
    [Tooltip("能量与时间效果参数（时停每 tick 耗能）")]
    [SerializeField] private EnergyParams _params;

    [Tooltip("唯一能量账户（同一物体上的 VectorEnergy）")]
    [SerializeField] private VectorEnergy _energy;

    // 不足 1 tick 的耗能先攒着，防止每帧扣一点点被舍成 0
    private float _costAccrued;

    /// <summary>当前是否处于时停中</summary>
    public bool IsTimeStopping { get; private set; }

    /// <summary>按键系统接入点：按住时调用</summary>
    public void BeginTimeStop()
    {
        if (IsTimeStopping) return;
        if (_energy == null || _params == null || !enabled) return;

        // 起手至少要付得起一个 tick，否则按下去也维持不住
        if (_energy.CurrentEnergy < _params.TimeStopCostPerTick) return;

        IsTimeStopping = true;
        _costAccrued = 0f;
        TimeManager.SetTimeStop(true);
    }

    /// <summary>按键系统接入点：松开 / 被打断时调用</summary>
    public void EndTimeStop()
    {
        if (!IsTimeStopping) return;

        IsTimeStopping = false;
        _costAccrued = 0f;
        TimeManager.SetTimeStop(false);
    }

    private void Update()
    {
        if (!IsTimeStopping) return;

        // 时停不压 player 层，所以这里读到的就是玩家时间
        float tickNum = Time.fixedDeltaTime > 0f ? TimeManager.PlayerDeltaTime / Time.fixedDeltaTime : 0f;
        _costAccrued += _params.TimeStopCostPerTick * tickNum;

        // 只扣"整数个 tick"的量，余下的零头下一帧继续攒
        if (_costAccrued < 1f) return;

        float whole = Mathf.Floor(_costAccrued);
        if (!_energy.TrySpend(whole))
        {
            EndTimeStop();   // 能量不够 → 时停自己结束
            return;
        }
        _costAccrued -= whole;
    }

    // 组件被禁用 / 对象销毁时必须收尾，否则世界永远停着
    private void OnDisable()
    {
        EndTimeStop();
    }
}
