using UnityEngine;

public class VisibilityChecker : MonoBehaviour
{
    public static bool isVisible(Camera c, Transform t, float _dist = 0.5f, float _padding = 0.1f)
    {
        float far = c.farClipPlane * _dist;
        Vector3 v = c.WorldToViewportPoint(t.position);
        if (v.x < _padding || v.x > (1 - _padding) || v.y < _padding || v.y > (1 - _padding))
        {
            return false;
        }
        if(v.z < 0 || v.z > far)
        {
            return false;
        }
        return true;
    }
}
