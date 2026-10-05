using System;
using System.Collections.Generic;
using GameJam.Actions;
using UnityEngine;

/// <summary>
/// Data-driven adapter for existing player conditions. Each rule maps an
/// ActionId to a condition key already understood by PlayerActionRunner.
/// New actions should usually implement IDynamicAudioActionSource directly.
/// </summary>
[DisallowMultipleComponent]
public sealed class PlayerDynamicAudioActionSource :
    MonoBehaviour,
    IDynamicAudioActionSource
{
    [Serializable]
    public sealed class Rule
    {
        public string ActionId = "action";
        public string ConditionKey = "sliding";
        [Range(0f, 1f)] public float ContactIntensity = 1f;
    }

    [SerializeField]
    private Rule[] _rules =
    {
        new Rule
        {
            ActionId = "player_slide",
            ConditionKey = "sliding",
            ContactIntensity = 1f
        },
        new Rule
        {
            ActionId = "wall_slide",
            ConditionKey = "wall_sliding",
            ContactIntensity = 1f
        }
    };

    private PlayerMotor _motor;
    private PlayerActionRunner _actionRunner;
    private bool[] _lastActive;

    private void Awake()
    {
        _motor = GetComponent<PlayerMotor>();
        _actionRunner = GetComponent<PlayerActionRunner>();
        _lastActive = new bool[_rules != null ? _rules.Length : 0];
    }

    public void CollectDynamicAudioActions(
        List<DynamicAudioActionRequest> output)
    {
        if (_motor == null || output == null || _rules == null) return;
        if (_lastActive == null || _lastActive.Length != _rules.Length)
        {
            _lastActive = new bool[_rules.Length];
        }

        float speed = NormalizedSpeed();
        float direction = Direction();
        for (int i = 0; i < _rules.Length; i++)
        {
            Rule rule = _rules[i];
            if (rule == null || string.IsNullOrEmpty(rule.ConditionKey))
            {
                continue;
            }

            bool active = Evaluate(rule.ConditionKey);
            if (active && !_lastActive[i])
            {
                output.Add(DynamicAudioActionRequest.Start(
                    rule.ActionId,
                    speed,
                    rule.ContactIntensity,
                    direction));
            }

            if (active)
            {
                output.Add(DynamicAudioActionRequest.Update(
                    rule.ActionId,
                    speed,
                    rule.ContactIntensity,
                    direction));
            }
            else if (_lastActive[i])
            {
                output.Add(DynamicAudioActionRequest.Stop(
                    rule.ActionId,
                    DynamicAudioActionStopMode.Release));
            }

            _lastActive[i] = active;
        }
    }

    private bool Evaluate(string conditionKey)
    {
        if (_actionRunner != null)
        {
            return _actionRunner.CheckCondition(
                conditionKey,
                default,
                default);
        }

        switch (conditionKey)
        {
            case "sliding":
                return _motor.IsSliding;
            case "wall_sliding":
                return _motor.IsWallSliding;
            case "grounded":
                return _motor.IsGrounded;
            case "airborne":
                return !_motor.IsGrounded;
            default:
                return false;
        }
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
