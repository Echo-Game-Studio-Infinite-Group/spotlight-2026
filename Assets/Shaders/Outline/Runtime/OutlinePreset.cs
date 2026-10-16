using UnityEngine;

namespace DeferredOutline
{
    public enum OutlineTransitionMode
    {
        RandomFragmentLoss = 0,
        WorldBottomToTop = 1
    }

    [CreateAssetMenu(menuName = "Rendering/Deferred Outline/Preset", fileName = "OutlinePreset")]
    public sealed class OutlinePreset : ScriptableObject
    {
        [Header("Reference mask edge / HDR core")]
        [HideInInspector] public float tbnEqualSpacingPixels = 0f;
        [Range(1, 2), Tooltip("1使用边界色；2使用黑边/白核心同一形态同一半径。3/4是旧资产兼容，不叠加额外人形环带。")] public int colorLayerCount = 2;
        [ColorUsage(true,true)] public Color color0 = Color.black;
        [ColorUsage(true,true)] public Color color1 = new Color(4.916183f,6.9198833f,7.8718348f,1f);
        [HideInInspector] public Color color2 = Color.white;
        [HideInInspector] public Color color3 = Color.white;
        [Min(0.1f),Tooltip("参考工程唯一采样半径，黑白共用；不是累计宽度。 ")] public float width0 = 5.59f;
        [HideInInspector] public float width1 = 5.59f;
        [HideInInspector] public float width2 = 5.59f;
        [HideInInspector] public float width3 = 5.59f;
        [Header("Continuous UV distortion (reference units)")]
        [Range(-3f,3f),Tooltip("扰动强度取绝对值，1.85为1倍外伸预算；正负兼容旧资产，方向始终静止向上、运动向后。 ")] public float distortion=-1.85f;
        [Tooltip("长度控制常驻平流速度；静止向世界上方，运动时朝世界速度反向。参考速度0.2。 ")] public Vector2 flowSpeed=new Vector2(0f,.2f);
        [Range(.1f,30f),Tooltip("水平噪声tiling；参考3.03，垂直为1。 ")] public float flowScale=3.03f;
        [Min(0f), Tooltip("Breathing rhythm in cycles per second. Zero freezes the pulse; flow speed remains independent.")]
        [HideInInspector]
        public float rhythmFrequencyHz = 0.39788736f;

        // Legacy glow fields retained only for serialized compatibility; HDR core color drives Bloom.
        [HideInInspector] public float glowIntensity = 1.25f;
        [HideInInspector] public float glowRadius = 5f;

        // Legacy tail shape fields: no historical/translated silhouette in reference mode.
        [HideInInspector] public float motionTailMaxPixels = 12f;
        [HideInInspector] public float motionTailSeconds = 0.085f;
        [HideInInspector] public float motionTailReturnSeconds = 0.2f;
        [Min(0f), Tooltip("0立即响应运动方向（默认）。大于0才启用可选方向平滑，不保留历史mask。 ")] public float flowDirectionSmoothingSeconds = 0f;

        [Header("Motion-to-flow direction / rate (world units per second)")]
        [Min(0f)] public float motionFlameStartSpeed = 0.15f;
        [Min(0.01f)] public float motionFlameFullSpeed = 3.2f;
        [Tooltip("Cubic Bezier speed-response control point 1. X is normalized speed; Y is flame response.")]
        public Vector2 motionFlameBezier1 = new Vector2(0.25f, 0.02f);
        [Tooltip("Cubic Bezier speed-response control point 2. X is normalized speed; Y is flame response.")]
        public Vector2 motionFlameBezier2 = new Vector2(0.75f, 0.9f);

        [Header("Anchored extension / fraction of projected character height")]
        [Range(0f, .25f), Tooltip("静止外伸上限占角色屏幕高度的比例；连续噪声在此预算内变化，根部保持当前mask。")]
        public float idleExtensionHeight = .03f;
        [Range(0f, .4f), Tooltip("到达Motion Flame Full Speed时的外伸预算。与角色投影高度成比例，不随屏幕分辨率脱体。")]
        public float movingExtensionHeight = .10f;

        [Header("Current-frame UV distortion strength")]
        [HideInInspector]
        public bool enableSpecialMask;
        [HideInInspector]
        public Texture2D specialMask;
        [HideInInspector]
        public Vector2 maskTiling = Vector2.one;
        [HideInInspector]
        public Vector2 maskOffset;
        [Range(0f, 5f)] public float flameIntensity = 1f;
        [HideInInspector]
        public Color flameColor = new Color(1f, 0.15f, 0.01f, 1f);

        [Header("On / Off state machine")]
        public OutlineTransitionMode transitionMode = OutlineTransitionMode.RandomFragmentLoss;
        [Min(0.01f)] public float appearDuration = 0.8f;
        [Min(0.01f)] public float disappearDuration = 0.55f;
        [Tooltip("Cubic Bezier control points. X is reserved for editor visualization; Y controls easing.")]
        public Vector2 bezierControl1 = new Vector2(0.22f, 0.05f);
        public Vector2 bezierControl2 = new Vector2(0.18f, 1f);
        [Range(0.001f, 0.25f)] public float transitionEdgeSoftness = 0.055f;
        [Range(0f, 1f)] public float transitionDistortion = 0.22f;
        [Range(0.1f, 30f)] public float transitionNoiseScale = 7f;

        public Color GetColor(int index)
        {
            switch (index)
            {
                case 0: return color0;
                case 1: return color1;
                case 2: return color2;
                default: return color3;
            }
        }

        public Vector4 Widths => new Vector4(width0, width1, width2, width3);

        public float EvaluateMotionFlame(float worldSpeed)
        {
            float speed01 = Mathf.InverseLerp(motionFlameStartSpeed,
                Mathf.Max(motionFlameStartSpeed + 0.01f, motionFlameFullSpeed), worldSpeed);
            if (speed01 <= 0f) return 0f;
            if (speed01 >= 1f) return 1f;

            // Invert the Bezier X coordinate rather than treating speed as its
            // parameter. This makes both control points meaningful in the Inspector.
            Vector2 p1 = new Vector2(Mathf.Clamp01(motionFlameBezier1.x), Mathf.Clamp01(motionFlameBezier1.y));
            Vector2 p2 = new Vector2(Mathf.Clamp(motionFlameBezier2.x, p1.x, 1f),
                Mathf.Clamp(motionFlameBezier2.y, p1.y, 1f));
            float low = 0f, high = 1f;
            for (int i = 0; i < 10; i++)
            {
                float mid = (low + high) * 0.5f;
                if (EvaluateCubic(mid, p1.x, p2.x) < speed01) low = mid;
                else high = mid;
            }
            return Mathf.Clamp01(EvaluateCubic((low + high) * 0.5f, p1.y, p2.y));
        }

        static float EvaluateCubic(float t, float p1, float p2)
        {
            float inverse = 1f - t;
            return 3f * inverse * inverse * t * p1
                + 3f * inverse * t * t * p2 + t * t * t;
        }

        public float EvaluateBezier(float t)
        {
            t = Mathf.Clamp01(t);
            float inverse = 1f - t;
            return Mathf.Clamp01(3f * inverse * inverse * t * bezierControl1.y
                + 3f * inverse * t * t * bezierControl2.y + t * t * t);
        }
    }
}
