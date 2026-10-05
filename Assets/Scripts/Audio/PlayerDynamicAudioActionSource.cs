using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Transitional adapter for the existing PlayerMotor state. It is the first
/// consumer of IDynamicAudioActionSource and reports the two continuous slide
/// actions without requiring changes in the movement, combat or character code.
/// </summary>
[DisallowMultipleComponent]
public sealed class PlayerDynamicAudioActionSource :
    MonoBehaviour,
    IDynamicAudioActionSource
{
    [SerializeField] private string _slideActionId = "slide";
    [SerializeField] private string _wallSlideActionId = "wall_slide";

    private PlayerMotor _motor;
    private bool _lastSliding;
    private bool _lastWallSliding;

    private void Awake()
    {
        _motor = GetComponent<PlayerMotor>();
    }

    public void CollectDynamicAudioActions(
        List<DynamicAudioActionRequest> output)
    {
        if (_motor == null || output == null) return;

        float speed = NormalizedSpeed();
        bool sliding = _motor.IsSliding;
        if (sliding && !_lastSliding)
        {
            output.Add(DynamicAudioActionRequest.Start(
                _slideActionId,
                speed,
                1f,
                Direction()));
        }
        if (sliding)
        {
            output.Add(DynamicAudioActionRequest.Update(
                _slideActionId,
                speed,
                1f,
                Direction()));
        }
        else if (_lastSliding)
        {
            output.Add(DynamicAudioActionRequest.Stop(
                _slideActionId,
                DynamicAudioActionStopMode.Release));
        }

        bool wallSliding = _motor.IsWallSliding;
        float wallContact = Mathf.Clamp01(
            _motor.WallApproachAngle / 90f);
        if (wallSliding && !_lastWallSliding)
        {
            output.Add(DynamicAudioActionRequest.Start(
                _wallSlideActionId,
                speed,
                wallContact,
                Direction()));
        }
        if (wallSliding)
        {
            output.Add(DynamicAudioActionRequest.Update(
                _wallSlideActionId,
                speed,
                wallContact,
                Direction()));
        }
        else if (_lastWallSliding)
        {
            output.Add(DynamicAudioActionRequest.Stop(
                _wallSlideActionId,
                DynamicAudioActionStopMode.Release));
        }

        _lastSliding = sliding;
        _lastWallSliding = wallSliding;
    }

    private float NormalizedSpeed()
    {
        float threshold = _motor.Params != null &&
                          _motor.Params.GroundSpeedThreshold > 0f
            ? _motor.Params.GroundSpeedThreshold
            : 10f;
        return Mathf.Clamp01(
            _motor.HorizontalSpeed / Mathf.Max(1f, threshold * 3f));
    }

    private float Direction()
    {
        Vector3 horizontal = new Vector3(
            _motor.Velocity.x,
            0f,
            _motor.Velocity.z);
        if (horizontal.sqrMagnitude <= 0.0001f) return 0f;
        return Mathf.Clamp(
            Vector3.Dot(horizontal.normalized, transform.right),
            -1f,
            1f);
    }
}
