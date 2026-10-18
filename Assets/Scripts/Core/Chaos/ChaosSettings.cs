using UnityEngine;

[CreateAssetMenu(menuName = "超高速行者/混乱度规则")]
public sealed class ChaosSettings : ScriptableObject
{
    [Header("流程定稿参数")]
    [SerializeField] private int _initialValue;
    [SerializeField, Min(1)] private int _normalMaximum = 100;
    [SerializeField, Min(0)] private int _deathPenalty = 10;
    [SerializeField] private int _lowLoopMaximum = 30;
    [SerializeField] private int _highLoopMinimum = 80;
    [Header("未定稿的联调参数，可由策划调整")]
    [SerializeField, Min(0f)] private float _fallDamage = 20f;
    [SerializeField, Range(.01f, 1f)] private float _reviveHealthRatio = 1f;
    [Tooltip("默认保留已碎灯，避免死亡回点后反复获取同一盏灯的混乱度。")]
    [SerializeField] private bool _restoreLampsOnDeath;
    public int InitialValue => _initialValue;
    public int NormalMaximum => Mathf.Max(1, _normalMaximum);
    public int DeathPenalty => Mathf.Max(0, _deathPenalty);
    public int LowLoopMaximum => _lowLoopMaximum;
    public int HighLoopMinimum => _highLoopMinimum;
    public float FallDamage => Mathf.Max(0f, _fallDamage);
    public float ReviveHealthRatio => Mathf.Clamp(_reviveHealthRatio, .01f, 1f);
    public bool RestoreLampsOnDeath => _restoreLampsOnDeath;
}
