using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace GameJam.Flow
{
    public static class FlowTextValidation
    {
        public static List<string> Template(FlowRoomTemplate template)
        {
            var errors = new List<string>(); var ids = new HashSet<string>();
            foreach (FlowTextPoint point in template.GetComponentsInChildren<FlowTextPoint>(true))
            {
                if (string.IsNullOrWhiteSpace(point.Id) || !ids.Add(point.Id)) errors.Add(template.name + "：文字点编号为空或重复 " + point.Id);
                if (point.Visual == null || !point.Visual.transform.IsChildOf(point.transform)) errors.Add(template.name + "：文字点缺少内部表现节点 " + point.Id);
                else if (point.Visual.GetComponent<TextMeshPro>().font == null) errors.Add(template.name + "：文字点缺少字体 " + point.Id);
            }
            return errors;
        }

        public static List<string> Variant(RoomVariantDefinition variant, FlowRoomTemplate template = null)
        {
            var errors = new List<string>(); var ids = new HashSet<string>(); var points = new Dictionary<string, FlowTextPoint>();
            if (template == null && variant.Room != null) template = variant.Room.Template;
            if (template == null) return errors;
            foreach (FlowTextPoint point in template.GetComponentsInChildren<FlowTextPoint>(true))
                if (!string.IsNullOrWhiteSpace(point.Id) && !points.ContainsKey(point.Id)) points.Add(point.Id, point);
            foreach (FlowTextBinding binding in variant.Texts ?? new FlowTextBinding[0])
            {
                string prefix = variant.Id + "：文字点 ";
                if (binding == null || string.IsNullOrWhiteSpace(binding.PointId)) { errors.Add(prefix + "配置为空或缺少编号"); continue; }
                if (!ids.Add(binding.PointId)) errors.Add(prefix + "配置重复 " + binding.PointId);
                if (!points.TryGetValue(binding.PointId, out FlowTextPoint point)) { errors.Add(prefix + "失效配置 " + binding.PointId); continue; }
                if (!binding.Enabled) continue;
                if (string.IsNullOrWhiteSpace(binding.Content)) errors.Add(prefix + binding.PointId + "：启用后必须填写文字内容");
                if (!Positive(binding.FontSize) || !Positive(binding.AppearanceSpeed) || !Positive(binding.DisappearanceSpeed) ||
                    !Nonnegative(binding.HoldSeconds) || !Nonnegative(binding.MaximumDistance)) errors.Add(prefix + binding.PointId + "：字号、时间、距离或速度无效");
                CheckEffect(binding.Appearance ?? point.DefaultAppearance, prefix + binding.PointId + "：出现效果", errors);
                if (binding.Display == FlowTextDisplay.Timed) CheckEffect(binding.Disappearance ?? point.DefaultDisappearance, prefix + binding.PointId + "：消失效果", errors);
            }
            return errors;
        }

        private static bool Positive(float value) => value > 0f && !float.IsInfinity(value);
        private static bool Nonnegative(float value) => value >= 0f && !float.IsInfinity(value);
        private static void CheckEffect(FlowTextAnimation effect, string prefix, List<string> errors)
        {
            if (effect == null) { errors.Add(prefix + "未指定"); return; }
            if (!Positive(effect.Duration) || effect.Materials == null || effect.Tracks == null || (effect.Materials.Length == 0 && effect.Tracks.Length == 0))
                errors.Add(prefix + "：尚未正确读取动画曲线");
            else foreach (FlowTextAnimation.MaterialKey key in effect.Materials)
                if (key == null || key.Material == null || key.Material.shader == null) errors.Add(prefix + "：缺少材质或 Shader");
        }
    }
}
