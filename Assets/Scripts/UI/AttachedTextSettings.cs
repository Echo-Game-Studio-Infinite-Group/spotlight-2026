using TMPro;
using UnityEngine;

[RequireComponent(typeof(TextMeshPro))]
public class AttachedTextSettings : MonoBehaviour
{
    private enum Direction { _north, _south, _east, _west, _top, _bottom };

    [SerializeField] private Direction _direction = Direction._north;
    [SerializeField] private float _scale = 1.0f;
    [SerializeField] private Vector3 _relativePosition = new Vector3(0.0f, 0.0f, 0.0f);
    [SerializeField] private Quaternion _relativeRotation = Quaternion.Euler(0.0f, 0.0f, 0.0f);

    private void Awake()
    {
        ApplySettings(_direction, _scale, _relativePosition, _relativeRotation);
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (!Application.isPlaying)
        {
            ApplySettings(_direction, _scale, _relativePosition, _relativeRotation);
        }
    }
#endif

    private void ApplySettings(Direction direction, float scale, Vector3 position, Quaternion rotation)
    {
        if (transform.parent == null)
        {
            return;
        }
        Vector3 pScale = transform.parent.localScale;
        Vector3 exPos = new Vector3();
        Quaternion exRot = new Quaternion();
        Vector3 chScale = new Vector3();

        if (direction == Direction._north)
        {
            exPos = new Vector3(0.0f, 0.0f, 0.51f);
            exRot = Quaternion.Euler(0.0f, 180.0f, 0.0f);
            chScale = new Vector3(scale / pScale.x, scale / pScale.y, 1);
        }
        else if (direction == Direction._south)
        {
            exPos = new Vector3(0.0f, 0.0f, -0.51f);
            exRot = Quaternion.Euler(0.0f, 0.0f, 0.0f);
            chScale = new Vector3(scale / pScale.x, scale / pScale.y, 1);
        }
        else if (direction == Direction._east)
        {
            exPos = new Vector3(0.51f, 0.0f, 0.0f);
            exRot = Quaternion.Euler(0.0f, -90.0f, 0.0f);
            chScale = new Vector3(scale / pScale.z, scale / pScale.y, 1);
        }
        else if (direction == Direction._west)
        {
            exPos = new Vector3(-0.51f, 0.0f, 0.0f);
            exRot = Quaternion.Euler(0.0f, 90.0f, 0.0f);
            chScale = new Vector3(scale / pScale.z, scale / pScale.y, 1);
        }
        else if (direction == Direction._top)
        {
            exPos = new Vector3(0.0f, 0.51f, 0.0f);
            exRot = Quaternion.Euler(90.0f, 0.0f, 0.0f);
            chScale = new Vector3(scale / pScale.x, scale / pScale.z, 1);
        }
        else if (direction == Direction._bottom)
        {
            exPos = new Vector3(0.0f, -0.51f, 0.0f);
            exRot = Quaternion.Euler(-90.0f, 0.0f, 0.0f);
            chScale = new Vector3(scale / pScale.x, scale / pScale.z, 1);
        }

        transform.localPosition = position + exPos;
        transform.localRotation = Quaternion.Euler(rotation.eulerAngles + exRot.eulerAngles);
        transform.localScale = chScale;
    }
}
