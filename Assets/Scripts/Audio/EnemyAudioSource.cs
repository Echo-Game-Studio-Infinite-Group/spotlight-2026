using UnityEngine;

/// <summary>
/// Routes enemy hit and heavy laser events to one-shot Wwise events.
/// </summary>
[DisallowMultipleComponent]
public sealed class EnemyAudioSource : MonoBehaviour
{
    private CombatComponent _combat;
    private RangeComponent _range;
    private LaserAttack _laser;
    private bool _playHitSound;

    private void Awake()
    {
        CacheReferences();
    }

    private void OnEnable()
    {
        CacheReferences();
        if (_combat != null) _combat.Landed += OnLanded;
        if (_range != null) _range.Landed += OnLanded;
        if (_laser != null) _laser.Fired += OnLaserFired;
    }

    private void OnDisable()
    {
        if (_combat != null) _combat.Landed -= OnLanded;
        if (_range != null) _range.Landed -= OnLanded;
        if (_laser != null) _laser.Fired -= OnLaserFired;
    }

    private void CacheReferences()
    {
        _combat = GetComponent<CombatComponent>();
        _range = GetComponent<RangeComponent>();
        _laser = GetComponent<LaserAttack>();
        _playHitSound = _laser == null;
    }

    private void OnLanded(
        HealthComponent target,
        Vector3 point,
        Vector3 direction,
        float appliedDamage)
    {
        if (!_playHitSound || appliedDamage <= 0f) return;
        Play("enemy_hit");
    }

    private void OnLaserFired()
    {
        Play("enemy_laser");
    }

    private void Play(string eventId)
    {
        if (!AkUnitySoundEngine.IsInitialized()) return;
        if (GetComponent<AkGameObj>() == null)
            gameObject.AddComponent<AkGameObj>();
        WwiseEventBridge.Play(eventId, gameObject);
    }
}
