using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MoveWithCamera : MonoBehaviour
{
    private Transform _cameraTransform;
    private Vector3 _defaultPos;
    private float _lastY, _lastP, _curY, _curP;

    public float xOffsetRate = 1.0f, yOffsetRate = 1.0f;
    public float xReturnRate = 1.0f, yReturnRate = 1.0f;

    private void Awake()
    {
        _cameraTransform = GameObject.FindWithTag("MainCamera").transform;
        _defaultPos = transform.position;

        GetYawAndPitch(out _lastY, out _lastP);
    }

    private void LateUpdate()
    {
        GetYawAndPitch(out _curY, out _curP);

        float dxOffset = Mathf.DeltaAngle(_curY, _lastY);
        float dyOffset = Mathf.DeltaAngle(_curP, _lastP);

        float dxReturn = _defaultPos.x - transform.position.x;
        float dyReturn = _defaultPos.y - transform.position.y;

        Vector3 dPos =
            new Vector3(dxOffset * xOffsetRate, dyOffset * yOffsetRate, 0.0f) * 0.3f
            + new Vector3(dxReturn * xReturnRate, dyReturn * yReturnRate, 0.0f) * Mathf.Abs(dxReturn + dyReturn) * 0.02f;

        transform.position += dPos;

        _lastY = _curY;
        _lastP = _curP;
    }

    private void GetYawAndPitch(out float yaw, out float pitch)
    {
        Vector3 forward = _cameraTransform.transform.forward;
        Vector3 flat = new Vector3(forward.x, 0f, forward.z);
        if (flat.sqrMagnitude < 1e-6f)
        {
            yaw = 0f;
        }
        else
        {
            yaw = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
        }
        pitch = Mathf.Asin(Mathf.Clamp(forward.y, -1f, 1f)) * Mathf.Rad2Deg;
    }
}
