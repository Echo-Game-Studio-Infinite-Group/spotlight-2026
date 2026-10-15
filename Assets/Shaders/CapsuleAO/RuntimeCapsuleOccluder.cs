using System.Collections.Generic;
using UnityEngine;

namespace Spotlight.Rendering
{
    [ExecuteAlways, DisallowMultipleComponent]
    public sealed class RuntimeCapsuleOccluder : MonoBehaviour
    {
        [System.Serializable]
        public struct Capsule
        {
            public Vector3 Start;
            public Vector3 End;
            [Min(.001f)] public float Radius;
        }
        public Capsule[] Capsules = new Capsule[0];
        internal static readonly List<RuntimeCapsuleOccluder> Active = new List<RuntimeCapsuleOccluder>();
        void OnEnable() { if (!Active.Contains(this)) Active.Add(this); }
        void OnDisable() { Active.Remove(this); }
        internal int Append(Vector4[] start, Vector4[] end, int index)
        {
            if (Capsules == null) return index;
            Vector3 scale = transform.lossyScale;
            float radiusScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
            foreach (Capsule capsule in Capsules)
            {
                if (index >= start.Length) break;
                if (capsule.Radius <= 0) continue;
                Vector3 a = transform.TransformPoint(capsule.Start), b = transform.TransformPoint(capsule.End);
                start[index] = new Vector4(a.x, a.y, a.z, capsule.Radius * radiusScale);
                end[index] = new Vector4(b.x, b.y, b.z, 0);
                index++;
            }
            return index;
        }
    }
}
