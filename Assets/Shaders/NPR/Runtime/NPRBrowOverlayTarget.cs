using System.Collections.Generic;
using UnityEngine;

namespace Spotlight.NPR
{
    [ExecuteAlways, DisallowMultipleComponent]
    public sealed class NPRBrowOverlayTarget : MonoBehaviour
    {
        [Tooltip("只绑定当前角色的脸部 Renderer；Face 材质负责眉毛遮罩与强度。")]
        public Renderer FaceRenderer;
        [Tooltip("只绑定同一个角色的头发 Renderer，不需要将刘海拆成另一个网格。")]
        public Renderer HairRenderer;

        internal static readonly List<NPRBrowOverlayTarget> ActiveTargets = new List<NPRBrowOverlayTarget>();
        void OnEnable()
        {
            // 显式注册角色配对，不能全局搜索脸部或让其他角色借用头发模板。
            if (!ActiveTargets.Contains(this)) ActiveTargets.Add(this);
        }
        void OnDisable() => ActiveTargets.Remove(this);
        void OnDestroy() => ActiveTargets.Remove(this);
    }
}
