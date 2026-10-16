using System;
using UnityEngine;

namespace GameJam.Actions
{
    [Serializable]
    public sealed class ActionHitVolume
    {
        public string DisplayName = "刀刃判定";
        [Tooltip("相对玩家根节点的路径；空值使用玩家根节点")]
        public string AnchorPath;
        public Vector3 Center = new Vector3(0f, 0.6f, 1f);
        public Vector3 Rotation;
        public Vector3 Size = new Vector3(1.3f, 1.2f, 0.8f);
        public ActionFrameAnchor Start = new ActionFrameAnchor();
        public ActionFrameAnchor End = new ActionFrameAnchor { RelativeTo = ActionFrameAnchor.Boundary.ActionEnd };
        [Min(0)] public int HitGroup = 1;
        [Tooltip("平移及旋转扫掠采样的最大间距；小于判定框最薄边")]
        [Min(0.01f)] public float SweepSpacing = 0.1f;

        public bool IsActive(ActionDefinition action, double frame)
            => action.TryResolve(Start, out int start) && action.TryResolve(End, out int end) && frame >= start && frame < end;

        public bool TryGetPose(Transform root, out Vector3 center, out Vector3 extents, out Quaternion rotation)
        {
            Transform anchor = string.IsNullOrEmpty(AnchorPath) ? root : root.Find(AnchorPath);
            center = extents = default; rotation = Quaternion.identity;
            if (anchor == null) return false;
            center = anchor.TransformPoint(Center);
            Vector3 scale = anchor.lossyScale;
            extents = Vector3.Scale(Size * 0.5f, new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            rotation = anchor.rotation * Quaternion.Euler(Rotation);
            return true;
        }
    }
}
