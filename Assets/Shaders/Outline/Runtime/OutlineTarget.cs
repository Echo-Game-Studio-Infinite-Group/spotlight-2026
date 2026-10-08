using System.Collections.Generic;
using UnityEngine;

namespace DeferredOutline
{
    [DisallowMultipleComponent]
    [ExecuteAlways]
    [DefaultExecutionOrder(10000)]
    public sealed class OutlineTarget : MonoBehaviour
    {
        public enum PlaybackState { Off, Appearing, On, Disappearing }
        public enum MotionInput { TransformDelta, ExternalWorldVelocity }

        [Header("Binding")]
        public OutlineGroup group;
        public OutlinePreset presetOverride;
        public bool includeChildren = true;
        public Renderer[] explicitRenderers;

        [Header("Decoupled outline material")]
        [Tooltip("旧版模板兼容字段。RenderingLayer / DepthOnly 路径不使用此模板，直接使用物体原材质 DepthOnly pass。")]
        [HideInInspector]
        public Material outlineMaterialTemplate;

        [Header("Simple outline appearance")]
        [Tooltip("Use these four controls on this character. When off, the preset and legacy layer colors remain in effect.")]
        public bool overrideOutlineAppearance;
        [Range(1f, 20f), Tooltip("Single mask-edge sampling radius in pixels. Black edge and white core share this radius.")]
        public float outlineWidthPixels = 5.59f;
        [Range(0f, 4f), Tooltip("Multiplier for current-frame UV distortion. 1 matches the distortion magnitude; 0 disables distortion. No history or tail copies.")]
        public float flameStrength = 1f;
        [Range(-3f, 3f), Tooltip("扰动强度取绝对值，1.85为1倍角色比例外伸预算，0关闭；正负兼容旧资产，不再反转方向。")]
        public float outlineJitterPixels = -1.85f;
        [HideInInspector]
        public bool overrideRhythmFrequency;
        [Min(0f), Tooltip("Used when Override Rhythm Frequency is enabled; otherwise inherit the preset. Cycles per second. Changing frequency preserves the current phase.")]
        [HideInInspector]
        public float rhythmFrequencyHz = 0.39788736f;
        [ColorUsage(true, true)] public Color innerOutlineColor = Color.black;
        [ColorUsage(true, true)] public Color outerOutlineColor = new Color(4.916183f, 6.9198833f, 7.8718348f, 1f);

        [Header("Per-object color override")]
        public bool overrideColors;
        [ColorUsage(true,true)] public Color color0 = Color.black;
        [ColorUsage(true,true)] public Color color1 = new Color(4.916183f,6.9198833f,7.8718348f,1f);
        [HideInInspector]
        public Color color2 = Color.blue;
        [HideInInspector]
        public Color color3 = Color.magenta;

        [Header("World motion input")]
        public MotionInput motionInput = MotionInput.TransformDelta;
        [Min(0.01f), Tooltip("外部速度超时后按零速度平滑释放，调用方应持续提交世界空间速度。")]
        public float externalVelocityTimeout = 0.15f;
        [Min(0.001f)] public float motionSmoothingSeconds = 0.10f;
        [Min(0f), Tooltip("单帧位移超过此距离时重置运动采样，避免传送尖峰；0禁用。") ]
        public float teleportDistance = 5f;

        [Header("State machine")]
        public bool startOn = true;
        public bool loopInDemo;
        [Min(0.1f)] public float loopHold = 2f;

        static readonly HashSet<OutlineTarget> ActiveSet = new HashSet<OutlineTarget>();
        static readonly List<OutlineTarget> SnapshotList = new List<OutlineTarget>();

        readonly List<Renderer> renderers = new List<Renderer>();
        PlaybackState state;
        float visibility;
        float transitionProgress;
        float holdTimer;
        Vector3 previousWorldPosition;
        Vector3 motionDelta;
        Vector3 motionDirection;
        Vector3 motionVelocity;
        Vector3 externalVelocity;
        double externalVelocityTime = double.NegativeInfinity;
        float motionSpeed;
        Material outlineMaterialInstance;
        Material instanceTemplate;
        bool outlineMaterialDirty;
        // Private serializable fields survive script hot reload, but are not scene settings.
        bool runtimeInitialized;
        bool runtimeWasPlaying;
        double rhythmCycles;
        double previousEffectTime;
#if UNITY_EDITOR
        double previousPreviewTime;
#endif
        Vector3 flowOffsetWorld;
        Vector3 flowDirectionWorld = Vector3.up;
        double lastDirectionSampleTime = double.NegativeInfinity;
        bool flowReferenceValid;
        Vector3 flowReferenceCenterLocal;
        float flowReferenceHeightLocal;
        List<Renderer> flowReferenceRenderers = new List<Renderer>();

        public PlaybackState State => state;
        public float Visibility => visibility;
        public Vector3 PreviousWorldPosition => previousWorldPosition;
        public Vector3 MotionDelta => motionDelta;
        public Vector3 MotionDirection => motionDirection;
        public Vector3 MotionVelocity => motionVelocity;
        public float MotionSpeed => motionSpeed;
        public float RhythmPhase => (float)(rhythmCycles * System.Math.PI * 2.0);
        public Vector3 FlowOffsetWorld => flowOffsetWorld;
        public Vector3 FlowDirectionWorld => flowDirectionWorld;
        public Vector3 FlowReferenceCenter => transform.TransformPoint(flowReferenceCenterLocal);
        public float FlowReferenceHeight => Mathf.Max(0.01f, flowReferenceHeightLocal * Mathf.Abs(transform.lossyScale.y));
        public IReadOnlyList<Renderer> Renderers => renderers;
        public OutlinePreset ResolvedPreset => presetOverride != null ? presetOverride : group != null ? group.preset : null;

        public static void GetActive(List<OutlineTarget> destination)
        {
            destination.Clear();
            SnapshotList.Clear();
            SnapshotList.AddRange(ActiveSet);
            for (int i = 0; i < SnapshotList.Count; i++)
            {
                OutlineTarget target = SnapshotList[i];
                if (target != null && target.isActiveAndEnabled && target.ResolvedPreset != null)
                    destination.Add(target);
            }
        }

        void OnEnable()
        {
            CacheRenderers();
            ActiveSet.Add(this);
            if (!runtimeInitialized || runtimeWasPlaying != Application.isPlaying)
                ResetPlayback();
            previousWorldPosition = transform.position;
            motionDelta = Vector3.zero;
            previousEffectTime = Time.realtimeSinceStartupAsDouble;
#if UNITY_EDITOR
            previousPreviewTime = UnityEditor.EditorApplication.timeSinceStartup;
            UnityEditor.EditorApplication.update -= UpdateEditorPreview;
            UnityEditor.EditorApplication.update += UpdateEditorPreview;
#endif
        }

        [ContextMenu("Reset Outline Playback")]
        public void ResetPlayback()
        {
            runtimeInitialized = true;
            runtimeWasPlaying = Application.isPlaying;
            visibility = startOn ? 1f : 0f;
            transitionProgress = visibility;
            state = startOn ? PlaybackState.On : PlaybackState.Off;
            previousWorldPosition = transform.position;
            motionDelta = Vector3.zero;
            motionSpeed = 0f;
            motionDirection = Vector3.zero;
            motionVelocity = Vector3.zero;
            rhythmCycles = 0.0;
            flowOffsetWorld = Vector3.zero;
            flowDirectionWorld = Vector3.up;
            holdTimer = 0f;
        }

        void OnDisable()
        {
            ActiveSet.Remove(this);
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= UpdateEditorPreview;
#endif
            ReleaseOutlineMaterial();
        }

        public Material GetOutlineMaterial(Material fallback)
        {
            Material template = outlineMaterialTemplate != null ? outlineMaterialTemplate : fallback;
            if (template == null) return null;
            if (outlineMaterialInstance == null || outlineMaterialDirty || instanceTemplate != template || outlineMaterialInstance.shader != template.shader)
            {
                ReleaseOutlineMaterial();
                outlineMaterialInstance = new Material(template)
                {
                    name = name + " [Outline Instance]",
                    hideFlags = HideFlags.HideAndDontSave
                };
                instanceTemplate = template;
                outlineMaterialDirty = false;
            }
            return outlineMaterialInstance;
        }

        void ReleaseOutlineMaterial()
        {
            if (outlineMaterialInstance == null) return;
            if (Application.isPlaying) Destroy(outlineMaterialInstance);
            else DestroyImmediate(outlineMaterialInstance);
            outlineMaterialInstance = null;
            instanceTemplate = null;
        }

        void OnValidate()
        {
            outlineMaterialDirty = true;
            if (!Application.isPlaying)
                CacheRenderers();
        }

        void Update()
        {
            if (Application.isPlaying) AdvancePlayback(Time.deltaTime);
        }

#if UNITY_EDITOR
        void UpdateEditorPreview()
        {
            double now = UnityEditor.EditorApplication.timeSinceStartup;
            float dt = Mathf.Max(0f, (float)(now - previousPreviewTime));
            previousPreviewTime = now;
            if (Application.isPlaying || !isActiveAndEnabled) return;
            bool changing = state == PlaybackState.Appearing || state == PlaybackState.Disappearing || loopInDemo;
            AdvancePlayback(dt);
            if (changing)
            {
                UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
                UnityEditor.SceneView.RepaintAll();
            }
        }
#endif

        void AdvancePlayback(float dt)
        {
            OutlinePreset preset = ResolvedPreset;
            if (preset == null) return;

            if (state == PlaybackState.Appearing)
            {
                transitionProgress = Mathf.MoveTowards(transitionProgress, 1f, dt / Mathf.Max(0.01f, preset.appearDuration));
                visibility = preset.EvaluateBezier(transitionProgress);
                if (transitionProgress >= 0.999f) { transitionProgress = visibility = 1f; state = PlaybackState.On; holdTimer = 0f; }
            }
            else if (state == PlaybackState.Disappearing)
            {
                transitionProgress = Mathf.MoveTowards(transitionProgress, 0f, dt / Mathf.Max(0.01f, preset.disappearDuration));
                visibility = preset.EvaluateBezier(transitionProgress);
                if (transitionProgress <= 0.001f) { transitionProgress = visibility = 0f; state = PlaybackState.Off; holdTimer = 0f; }
            }

            if (loopInDemo && (state == PlaybackState.On || state == PlaybackState.Off))
            {
                holdTimer += dt;
                if (holdTimer >= loopHold)
                {
                    holdTimer = 0f;
                    if (state == PlaybackState.On) TurnOff(); else TurnOn();
                }
            }
        }

        public void SubmitWorldVelocity(Vector3 worldVelocity)
        {
            if (float.IsNaN(worldVelocity.x) || float.IsInfinity(worldVelocity.x)
                || float.IsNaN(worldVelocity.y) || float.IsInfinity(worldVelocity.y)
                || float.IsNaN(worldVelocity.z) || float.IsInfinity(worldVelocity.z))
            { ResetMotion(); return; }
            externalVelocity = worldVelocity;
            externalVelocityTime = Time.realtimeSinceStartupAsDouble;
            motionInput = MotionInput.ExternalWorldVelocity;
        }

        public void ResetMotion()
        {
            previousWorldPosition = transform.position;
            motionDelta = motionDirection = motionVelocity = externalVelocity = Vector3.zero;
            motionSpeed = 0f;
            lastDirectionSampleTime = double.NegativeInfinity;
            externalVelocityTime = double.NegativeInfinity;
        }

        public void ClearExternalVelocity()
        {
            externalVelocity = Vector3.zero;
            externalVelocityTime = double.NegativeInfinity;
            motionInput = MotionInput.TransformDelta;
        }

        void LateUpdate()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            float effectDt = Application.isPlaying ? Time.deltaTime : (float)System.Math.Max(0.0, now - previousEffectTime);
            previousEffectTime = now;
            Vector3 current = transform.position;
            motionDelta = current - previousWorldPosition;

            previousWorldPosition = current;
            if (!Application.isPlaying && motionInput == MotionInput.TransformDelta)
            {
                motionSpeed = 0f;
                motionVelocity = motionDirection = Vector3.zero;
                AdvanceFlow(effectDt);
                return;
            }
            if (effectDt <= 0f || (motionInput == MotionInput.TransformDelta && teleportDistance > 0f && motionDelta.magnitude > teleportDistance))
            {
                motionVelocity = Vector3.zero;
                motionSpeed = 0f;
                motionDirection = Vector3.zero;
                motionDelta = Vector3.zero;
                return;
            }
            float dt = Mathf.Max(effectDt, 0.00001f);
            bool externalFresh = now - externalVelocityTime <= Mathf.Max(0.01f, externalVelocityTimeout);
            Vector3 instantVelocity = motionInput == MotionInput.ExternalWorldVelocity
                ? (externalFresh ? externalVelocity : Vector3.zero) : motionDelta / dt;
            // 对完整矢量低通：高刷新率的零物理步帧自然释放，不再每帧将尾向量清零。
            float response = Mathf.Max(0.001f, motionSmoothingSeconds);
            float blend = 1f - Mathf.Exp(-dt / response);
            motionVelocity = Vector3.Lerp(motionVelocity, instantVelocity, blend);
            // Smooth magnitude only for intensity/rate. Direction always uses the latest
            // nonzero raw sample, so reversing never waits for the low-pass vector.
            motionSpeed = Mathf.Lerp(motionSpeed, instantVelocity.magnitude, blend);
            if (instantVelocity.sqrMagnitude > 1e-8f)
            {
                motionDirection = instantVelocity.normalized;
                lastDirectionSampleTime = now;
            }
            else if (motionInput == MotionInput.ExternalWorldVelocity
                || now - lastDirectionSampleTime > Mathf.Max(0.02f, Time.fixedDeltaTime * 1.5f))
                motionDirection = Vector3.zero;
            // Between physics ticks, preserve direction for at most 1.5 fixed steps.
            // Explicit external zero is immediate; no historical silhouette is retained.
            AdvanceFlow(effectDt);
        }

        void AdvanceFlow(float dt)
        {
            OutlinePreset preset = ResolvedPreset;
            if (preset == null || dt <= 0f) return;
            float frequency = overrideRhythmFrequency ? rhythmFrequencyHz : preset.rhythmFrequencyHz;
            rhythmCycles = (rhythmCycles + System.Math.Max(0f, frequency) * dt) % 25.0;
            float motion = preset.EvaluateMotionFlame(motionSpeed);
            Vector3 rear = motionDirection.sqrMagnitude > 1e-8f ? -motionDirection : Vector3.up;
            Vector3 growth = Vector3.Lerp(Vector3.up, rear, motion);
            if (growth.sqrMagnitude < 1e-6f) growth = Vector3.up;
            growth.Normalize();
            // Rotate continuously rather than blending opposite vectors through zero.
            flowDirectionWorld = preset.flowDirectionSmoothingSeconds <= 0f ? growth
                : Vector3.RotateTowards(flowDirectionWorld, growth, dt * 2f / preset.flowDirectionSmoothingSeconds, 0f).normalized;
            float scale = FlowReferenceHeight;
            flowOffsetWorld += (flowDirectionWorld * preset.flowSpeed.magnitude)
                * (dt * Mathf.Lerp(1f, 2f, motion) * scale);
        }

        public void TurnOn()
        {
            if (state != PlaybackState.On)
            {
                state = PlaybackState.Appearing;
            }
        }

        public void TurnOff()
        {
            if (state != PlaybackState.Off)
            {
                state = PlaybackState.Disappearing;
            }
        }

        public void Toggle()
        {
            if (state == PlaybackState.On || state == PlaybackState.Appearing) TurnOff(); else TurnOn();
        }

        public Color GetColor(int index)
        {
            if (overrideOutlineAppearance) return index == 0 ? innerOutlineColor : outerOutlineColor;
            if (!overrideColors) return ResolvedPreset.GetColor(index);
            switch (index)
            {
                case 0: return color0;
                case 1: return color1;
                case 2: return color2;
                default: return color3;
            }
        }

        public Vector4 GetWidths()
        {
            OutlinePreset preset = ResolvedPreset;
            Vector4 widths = preset.Widths;
            if (!overrideOutlineAppearance) return widths;
            return Vector4.one * Mathf.Clamp(outlineWidthPixels, 1f, 20f);
        }

        public float GetFlameStrength() => overrideOutlineAppearance ? Mathf.Clamp(flameStrength, 0f, 4f) : ResolvedPreset.flameIntensity;

        public float GetDistortion() => overrideOutlineAppearance ? Mathf.Clamp(outlineJitterPixels, -3f, 3f) : ResolvedPreset.distortion;

        public Color GetFlameColor() => overrideOutlineAppearance ? outerOutlineColor : ResolvedPreset.flameColor;

        public Bounds GetWorldBounds()
        {
            bool initialized = false;
            Bounds result = new Bounds(transform.position, Vector3.one);
            for (int i = 0; i < renderers.Count; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled) continue;
                if (!initialized) { result = renderer.bounds; initialized = true; }
                else result.Encapsulate(renderer.bounds);
            }
            return result;
        }

        public void CacheRenderers()
        {
            renderers.Clear();
            if (explicitRenderers != null && explicitRenderers.Length > 0)
            {
                for (int i = 0; i < explicitRenderers.Length; i++)
                    if (explicitRenderers[i] != null) renderers.Add(explicitRenderers[i]);
            }
            else
            {
                Renderer[] found = includeChildren ? GetComponentsInChildren<Renderer>(true) : GetComponents<Renderer>();
                renderers.AddRange(found);
            }
            bool changed = !flowReferenceValid || flowReferenceRenderers.Count != renderers.Count;
            for (int i = 0; !changed && i < renderers.Count; i++)
                changed = flowReferenceRenderers[i] != renderers[i];
            if (!changed) return;
            Bounds reference = new Bounds(transform.position, Vector3.one);
            bool first = true;
            foreach (Renderer item in renderers)
            {
                if (item == null) continue;
                Bounds candidate = item.bounds;
                if (item is SkinnedMeshRenderer skinned)
                {
                    Bounds local = skinned.localBounds;
                    candidate = new Bounds(skinned.transform.TransformPoint(local.center), Vector3.zero);
                    for (int x = -1; x <= 1; x += 2)
                    for (int y = -1; y <= 1; y += 2)
                    for (int z = -1; z <= 1; z += 2)
                        candidate.Encapsulate(skinned.transform.TransformPoint(local.center
                            + Vector3.Scale(local.extents, new Vector3(x, y, z))));
                }
                if (first) { reference = candidate; first = false; }
                else reference.Encapsulate(candidate);
            }
            flowReferenceCenterLocal = transform.InverseTransformPoint(reference.center);
            flowReferenceHeightLocal = Mathf.Max(0.01f, reference.size.y / Mathf.Max(0.001f, Mathf.Abs(transform.lossyScale.y)));
            flowReferenceRenderers.Clear();
            flowReferenceRenderers.AddRange(renderers);
            flowReferenceValid = true;
        }
    }
}
