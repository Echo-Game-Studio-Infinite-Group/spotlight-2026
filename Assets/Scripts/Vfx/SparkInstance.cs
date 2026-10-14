using System.Collections.Generic;
using UnityEngine;

// 池化火花实例：挂在每个克隆出的特效节点上，负责「播放」与「播完自我隐藏」。
// 池的登记簿在 CombatSparks 里，本组件不知道池的存在——播完把自己藏起来就算归还。
[DisallowMultipleComponent]
public sealed class SparkInstance : MonoBehaviour
{
    /// <summary>种类标记，由 CombatSparks 在克隆时写入；池按它分桶复用。</summary>
    public CombatSparksInternal.Kind Kind { get; internal set; }

    // SerializeField 的唯一目的：让 Instantiate 克隆模板时把这份清单一并复制过去。
    // 普通 private 字段不参与序列化，克隆体拿到的会是空表，播放会静默失效——这里靠它活着。
    [SerializeField] private List<ParticleSystem> _systems = new List<ParticleSystem>(2);
    private float _busyUntil;
    private float _scale = 1f;

    public bool IsBusy => isActiveAndEnabled && Time.time < _busyUntil;

    internal void Register(ParticleSystem system) => _systems.Add(system);

    /// <summary>从隐藏状态唤醒并播放。strength 0~1，缩放火花的大小与速度（伤害力度表意），不影响闪光类。</summary>
    public void Play(float strength)
    {
        _scale = Mathf.Lerp(0.8f, 1.35f, strength);
        float longest = 0f;
        for (int i = 0; i < _systems.Count; i++)
        {
            ParticleSystem ps = _systems[i];
            if (ps == null) continue;
            // 复用前先清干净上一轮的残留粒子，再按各自模板配置重放
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ParticleSystem.MainModule main = ps.main;
            // 闪光层（Count=1）不吃力度缩放：parry 反馈强度应恒定，只有火花层随伤害变化
            bool isFlash = i == 0 && _systems.Count > 1;
            if (!isFlash)
            {
                main.startSizeMultiplier = _scale;
                main.startSpeedMultiplier = _scale;
            }
            ps.Play(true);
            longest = Mathf.Max(longest, main.startLifetime.constantMax);
        }
        // 稍留余量再判「播完」；下一轮 Play 会先重置它
        _busyUntil = Time.time + longest + 0.5f;
    }

    private void Update()
    {
        // 轮询代替 StopAction 回调：多层粒子要等全部熄灭才算完，回调在手动 Stop 时还有边界情况
        for (int i = 0; i < _systems.Count; i++)
        {
            if (_systems[i] != null && _systems[i].IsAlive(true)) return;
        }
        // 全部熄灭：隐藏待复用（Update 随之停跑，闲置实例零开销）
        gameObject.SetActive(false);
    }
}

/// <summary>
/// CombatSparks 的池分桶键。放独立类型是为了让 SparkInstance 能公开持有它，
/// 又不用把 CombatSparks 自己的私有枚举暴露出去。
/// </summary>
public static class CombatSparksInternal
{
    public enum Kind { Hit, Parry, ParryProjectile }
}
