using UnityEngine;

namespace Spotlight.Rendering
{
    [ExecuteAlways, DisallowMultipleComponent]
    public sealed class CharacterLightOrbit : MonoBehaviour
    {
        public Light TargetLight;
        public Transform CharacterPivot;
        public Vector3 PivotOffset = new Vector3(0, 1.3f, 0);
        public Vector3 Axis = Vector3.up;
        [Range(-60, 60)] public float DegreesPerSecond = 12;
        [Min(0), Tooltip("0 保留灯到角色的原始距离；仅点光与聚光灯使用。")]
        public float Radius;
        public bool RotateInPlayMode = true;
        public bool PreviewInEditMode;

        Light capturedLight;
        Vector3 initialPosition, initialOffset;
        Quaternion initialRotation;
        float angle;
        bool orbiting;
#if UNITY_EDITOR
        double editorTime;
#endif
        void OnEnable()
        {
#if UNITY_EDITOR
            editorTime = UnityEditor.EditorApplication.timeSinceStartup;
            UnityEditor.EditorApplication.update += EditorTick;
#endif
        }
        void Update()
        {
            if (Application.isPlaying) Tick(Time.deltaTime, RotateInPlayMode);
        }
#if UNITY_EDITOR
        void EditorTick()
        {
            double now = UnityEditor.EditorApplication.timeSinceStartup;
            float delta = (float)(now - editorTime);
            editorTime = now;
            if (!Application.isPlaying) Tick(Mathf.Min(delta, .1f), PreviewInEditMode);
        }
#endif
        void Tick(float deltaTime, bool run)
        {
            if (!run || !TargetLight || !CharacterPivot)
            {
                Restore();
                return;
            }
            if (orbiting && capturedLight != TargetLight) Restore();
            Vector3 pivot = CharacterPivot.TransformPoint(PivotOffset);
            if (!orbiting)
            {
                capturedLight = TargetLight;
                initialPosition = capturedLight.transform.position;
                initialRotation = capturedLight.transform.rotation;
                initialOffset = initialPosition - pivot;
                angle = 0;
                orbiting = true;
            }
            angle = Mathf.Repeat(angle + DegreesPerSecond * Mathf.Max(deltaTime, 0), 360);
            Quaternion rotation = Quaternion.AngleAxis(angle, Axis.sqrMagnitude > .0001f ? Axis.normalized : Vector3.up);
            if (capturedLight.type == LightType.Directional)
                capturedLight.transform.rotation = rotation * initialRotation;
            else
            {
                Vector3 offset = initialOffset;
                if (Radius > .001f) offset = (offset.sqrMagnitude > .0001f ? offset.normalized : Vector3.forward) * Radius;
                capturedLight.transform.position = pivot + rotation * offset;
                Vector3 towardPivot = pivot - capturedLight.transform.position;
                if (towardPivot.sqrMagnitude > .0001f)
                    capturedLight.transform.rotation = Quaternion.LookRotation(towardPivot, Vector3.up);
            }
        }
        void Restore()
        {
            if (orbiting && capturedLight)
                capturedLight.transform.SetPositionAndRotation(initialPosition, initialRotation);
            orbiting = false;
            capturedLight = null;
        }
        void OnDisable()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= EditorTick;
#endif
            Restore();
        }
        void OnDestroy() => Restore();
    }
}
