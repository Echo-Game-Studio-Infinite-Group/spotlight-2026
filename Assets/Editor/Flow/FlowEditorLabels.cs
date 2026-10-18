using System;
using System.Collections.Generic;
using System.Reflection;
using GameJam.Flow;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GameJam.FlowEditor
{
    public static class FlowEditorLabels
    {
        private static readonly Dictionary<Type, Dictionary<string, GUIContent>> Fields = new Dictionary<Type, Dictionary<string, GUIContent>>();

        public static GUIContent Field(Type type, string name)
        {
            if (!Fields.TryGetValue(type, out Dictionary<string, GUIContent> labels))
            {
                labels = new Dictionary<string, GUIContent>();
                foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    InspectorNameAttribute title = field.GetCustomAttribute<InspectorNameAttribute>();
                    TooltipAttribute tooltip = field.GetCustomAttribute<TooltipAttribute>();
                    labels[field.Name] = new GUIContent(title != null ? title.displayName : field.Name, tooltip?.tooltip ?? string.Empty);
                }
                Fields.Add(type, labels);
            }
            return labels.TryGetValue(name, out GUIContent label) ? label : new GUIContent(name);
        }

        public static string Asset(Object asset)
        {
            if (asset is RunFlowConfig) return "全局流程";
            if (asset is RoomDefinition room) return "箱庭：" + RoomName(room);
            if (asset is RoomVariantDefinition variant)
                return RoomName(variant.Room) + " · 混乱度 " + variant.MinimumChaos + "～" + variant.MaximumChaos + "（" + variant.Id + "）";
            if (asset is RoomPoolDefinition pool) return (pool.ChangesChaos ? "变化房池" : "普通房池") + "（" + pool.Id + "）";
            if (asset is LoopRuleDefinition loop) return "循环：混乱度 " + loop.MinimumChaos + "～" + loop.MaximumChaos + "（" + loop.Id + "）";
            return asset != null ? asset.name : "未指定";
        }

        public static string Description(Object asset)
        {
            if (asset is RunFlowConfig) return "配置整局的起点、终点、选房规则、混乱度与复活方式，以及房间之间的连接段。";
            if (asset is RoomDefinition) return "箱庭提供共用地图模板，各个差分按混乱度选择。显示名称可使用中文，编号用于配表引用。";
            if (asset is RoomVariantDefinition) return "差分决定箱庭在指定混乱度下的内容组、叙事、灯光、稳定装置奖励及各出生点对应的敌人。上下限均包含边界值。";
            if (asset is RoomPoolDefinition) return "地图池登记可抽取的差分与基础权重。下方概率预览会考虑混乱度资格和进入次数造成的权重衰减。";
            if (asset is LoopRuleDefinition) return "在指定混乱度区间内，抽取若干普通房和一个变化房组成固定循环序列；离开区间后退出循环。";
            return "";
        }

        private static string RoomName(RoomDefinition room)
            => room == null ? "未指定箱庭" : string.IsNullOrWhiteSpace(room.DisplayName) ? room.Id : room.DisplayName;
    }
}
