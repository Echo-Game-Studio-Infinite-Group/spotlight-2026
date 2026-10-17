using System.Collections.Generic;
using UnityEngine;

// 池化火花实例：挂在每个克隆出的特效节点上，负责「播放」与「播完自我隐藏」。
// 池的登记簿在 CombatSparks 里，本组件不知道池的存在——播完把自己藏起来就算归还。
//
// 层（Layer）= 模板里的一个粒子系统子节点（闪/环/火花/电弧各一层）。
// ScaleWithStrength 只给「随伤害力度变化」的层（命中火花层）打开：
// parry 是恒定强度的反馈，不受力度缩放。
[DisallowMultipleComponent]
public sealed class SparkInstance : MonoBehaviour
{
    /// <summary>种类标记，由 CombatSparks 在克隆时写入；池按它分桶复用。</summary>
    public CombatSparksInternal.Kind Kind { get; internal set; }

    // [System.Serializable] + SerializeField 的唯一目的：让 Instantiate 克隆模板时把层清单复制过去。
    // 普通 private 字段不参与序列化，克隆体拿到的会是空表，播放会静默失效——这里靠它活着。
    [System.Serializable]
    public sealed class Layer
    {
        public ParticleSystem System;
        public bool ScaleWithStrength;
    }

    [SerializeField] private List<Layer> _layers = new List<Layer>(4);
    private float _busyUntil;

    public bool IsBusy => isActiveAndEnabled && Time.time < _busyUntil;

    internal void Register(ParticleSystem system, bool scaleWithStrength)
    {
        _layers.Add(new Layer { System = system, ScaleWithStrength = scaleWithStrength });
    }

    /// <summary>从隐藏状态唤醒并播放。strength 0~1 缩放「随力度变化」的层（0.85~1.2 倍），
    /// parry 层不受影响、保持恒定反馈。</summary>
    public void Play(float strength)
    {
        float scale = Mathf.Lerp(0.85f, 1.2f, Mathf.Clamp01(strength));
        float longest = 0f;
        for (int i = 0; i < _layers.Count; i++)
        {
            Layer layer = _layers[i];
            ParticleSystem ps = layer.System;
            if (ps == null) continue; // 模板层被删时按缺层跳过，不影响其余层
            // 复用前先清干净上一轮的残留粒子，再按各自模板配置重放
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ParticleSystem.MainModule main = ps.main;
            if (layer.ScaleWithStrength)
            {
                main.startSizeMultiplier = scale;
                main.startSpeedMultiplier = scale;
            }
            ps.Play(true);
            longest = Mathf.Max(longest, main.startLifetime.constantMax);
        }
        // 稍留余量再判「播完」；下一轮 Play 会先重置它
        _busyUntil = Time.time + longest + 0.4f;
    }

    private void Update()
    {
        // 轮询代替 StopAction 回调：多层粒子要等全部熄灭才算完，回调在手动 Stop 时还有边界情况
        for (int i = 0; i < _layers.Count; i++)
        {
            if (_layers[i].System != null && _layers[i].System.IsAlive(true)) return;
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
