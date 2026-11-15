using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace GameJam.Flow
{
    public static class FlowEnemySpawnValidation
    {
        public static List<string> Template(FlowRoomTemplate template)
        {
            var errors = new List<string>();
            var ids = new HashSet<string>();
            foreach (FlowEnemySpawnPoint point in template.GetComponentsInChildren<FlowEnemySpawnPoint>(true))
            {
                if (string.IsNullOrWhiteSpace(point.Id) || !ids.Add(point.Id)) errors.Add(template.name + "：出生点编号为空或重复 " + point.Id);
                if (!Finite(point.GroundSearchDistance) || point.GroundSearchDistance <= 0f || !Finite(point.MaximumHorizontalSnap)
                    || point.MaximumHorizontalSnap < 0f || !Finite(point.HeightOffset) || point.HeightOffset < 0f)
                    errors.Add(template.name + "：出生点落地参数无效 " + point.Id);
            }
            return errors;
        }

        public static List<string> Variant(RoomVariantDefinition variant, FlowRoomTemplate template = null)
        {
            var errors = new List<string>();
            if (template == null) template = variant.Room != null ? variant.Room.Template : null;
            if (template == null) return errors;
            var points = new HashSet<string>();
            foreach (FlowEnemySpawnPoint point in template.GetComponentsInChildren<FlowEnemySpawnPoint>(true))
                if (!string.IsNullOrWhiteSpace(point.Id)) points.Add(point.Id);
            var ids = new HashSet<string>();
            foreach (FlowEnemySpawnBinding binding in variant.EnemySpawns ?? new FlowEnemySpawnBinding[0])
            {
                if (binding == null || string.IsNullOrWhiteSpace(binding.PointId)) { errors.Add(variant.Id + "：出生点配置为空或缺少编号"); continue; }
                if (!ids.Add(binding.PointId)) errors.Add(variant.Id + "：出生点配置重复 " + binding.PointId);
                if (!points.Contains(binding.PointId)) errors.Add(variant.Id + "：失效出生点配置 " + binding.PointId);
                if (!binding.Enabled) continue;
                if (binding.EnemyPrefab == null || binding.EnemyPrefab.GetComponent<EnemyBase>() == null || binding.EnemyPrefab.scene.IsValid())
                { errors.Add(variant.Id + "：出生点 " + binding.PointId + " 必须指定敌人预制体资产"); continue; }
                NavMeshAgent agent = binding.EnemyPrefab.GetComponent<NavMeshAgent>();
                if (agent != null && agent.enabled && template.Navigation == null)
                    errors.Add(variant.Id + "：出生点 " + binding.PointId + " 的敌人需要地图模板导航数据");
            }
            return errors;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
