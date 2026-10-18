using System;
using System.Collections.Generic;

namespace GameJam.Flow
{
    public static class FlowConfigValidator
    {
        public static List<string> Validate(RunFlowConfig config)
        {
            var errors = new List<string>();
            if (config == null) { errors.Add("未指定全局流程配置"); return errors; }
            if (config.MaximumChaos <= config.MinimumChaos) errors.Add("普通混乱度上下限无效");
            if (!(config.VisitWeightMultiplier > 0 && config.VisitWeightMultiplier <= 1)) errors.Add("权重倍率必须在 (0,1] 内");
            if (config.InitialChaos < config.MinimumChaos || config.InitialChaos > config.MaximumChaos) errors.Add("起始混乱度超出普通范围");
            if (config.ConnectionLength <= 0 || config.ConnectionGrowth <= 1 || config.PlacementAttempts < 1) errors.Add("连接段长度、延长倍率或尝试次数无效");
            if (config.InitialRoom == null) errors.Add("缺少教程差分");
            if (config.FinalRoom == null) errors.Add("缺少最终房差分");
            var variants = new HashSet<RoomVariantDefinition>();
            var pools = new HashSet<RoomPoolDefinition>();
            Add(variants, config.InitialRoom); Add(variants, config.FinalRoom);
            foreach (RoomPoolDefinition pool in config.NormalPools) if (pool != null) pools.Add(pool);
            if (config.EnableSpecialRoutes)
            {
                if (config.NegativePool != null) pools.Add(config.NegativePool);
                if (config.ExceptionalPool != null) pools.Add(config.ExceptionalPool);
            }
            foreach (LoopRuleDefinition loop in config.Loops)
            {
                if (loop == null) { errors.Add("循环列表含空引用"); continue; }
                if (loop.MinimumChaos > loop.MaximumChaos) errors.Add(loop.name + "：循环区间无效");
                if (loop.NeutralPool == null || loop.ChaosPool == null) errors.Add(loop.name + "：循环的两个来源池必须配置");
                if (loop.NeutralPool != null) { pools.Add(loop.NeutralPool); if (loop.NeutralPool.ChangesChaos) errors.Add(loop.name + "：普通房来源不能使用增加混乱度的地图池"); }
                if (loop.ChaosPool != null) { pools.Add(loop.ChaosPool); if (!loop.ChaosPool.ChangesChaos) errors.Add(loop.name + "：变化房来源必须使用增加混乱度的地图池"); }
                if (loop.NeutralCounts == null || loop.NeutralCounts.Length == 0) errors.Add(loop.name + "：没有普通房数量候选");
                else foreach (int count in loop.NeutralCounts) if (count != 3 && count != 6) errors.Add(loop.name + "：普通房数量应为3或6");
                foreach (LoopRuleDefinition other in config.Loops)
                    if (other != null && Array.IndexOf(config.Loops, other) > Array.IndexOf(config.Loops, loop) &&
                        loop.MinimumChaos <= other.MaximumChaos && loop.MaximumChaos >= other.MinimumChaos)
                        errors.Add(loop.name + " 与 " + other.name + "：循环区间重叠");
            }
            var poolIds = new HashSet<string>();
            foreach (RoomPoolDefinition pool in pools)
            {
                if (string.IsNullOrWhiteSpace(pool.Id) || !poolIds.Add(pool.Id)) errors.Add(pool.name + "：地图池编号为空或重复");
                if (pool.Entries.Length == 0) errors.Add(pool.name + "：地图池为空");
                var members = new HashSet<RoomVariantDefinition>();
                foreach (RoomPoolEntry entry in pool.Entries)
                {
                    if (entry == null || !entry.Enabled) continue;
                    if (entry.Variant == null) { errors.Add(pool.name + "：条目缺少差分"); continue; }
                    Add(variants, entry.Variant);
                    if (!members.Add(entry.Variant)) errors.Add(pool.name + "：差分重复注册 " + entry.Variant.Id);
                    if (!(entry.Weight > 0) || float.IsInfinity(entry.Weight)) errors.Add(pool.name + "：启用条目的权重必须为有限正数");
                    if (pool.ChangesChaos != (entry.Variant.StabilizerGain > 0)) errors.Add(pool.name + "：差分的稳定装置增量与池类别不符 " + entry.Variant.Id);
                }
            }
            var ids = new HashSet<string>();
            var rooms = new HashSet<RoomDefinition>();
            var pendingVariants = new Queue<RoomVariantDefinition>(variants);
            while (pendingVariants.Count > 0)
            {
                RoomVariantDefinition item = pendingVariants.Dequeue();
                if (item.Room == null) continue;
                foreach (RoomVariantDefinition sibling in item.Room.Variants)
                    if (sibling != null && variants.Add(sibling)) pendingVariants.Enqueue(sibling);
            }
            foreach (RoomVariantDefinition variant in variants)
            {
                if (string.IsNullOrWhiteSpace(variant.Id) || !ids.Add(variant.Id)) errors.Add(variant.name + "：差分编号为空或重复");
                if (variant.MinimumChaos > variant.MaximumChaos) errors.Add(variant.name + "：混乱度区间无效");
                if (variant.Room == null) { errors.Add(variant.name + "：没有所属箱庭"); continue; }
                rooms.Add(variant.Room);
                if (Array.IndexOf(variant.Room.Variants, variant) < 0) errors.Add(variant.name + "：未登记在所属箱庭的差分列表");
                FlowRoomTemplate template = variant.Room.Template;
                if (template == null) continue;
                errors.AddRange(FlowEnemySpawnValidation.Variant(variant));
                errors.AddRange(FlowTextValidation.Variant(variant));
                foreach (string groupId in variant.ContentGroups)
                {
                    bool found = false;
                    foreach (FlowContentGroup group in template.GetComponentsInChildren<FlowContentGroup>(true)) if (group.Id == groupId) found = true;
                    if (!found) errors.Add(variant.Id + "：模板缺少内容组 " + groupId);
                }
                if (variant.StabilizerGain > 0 && template.GetComponentsInChildren<StabilityDevice>(true).Length == 0) errors.Add(variant.Id + "：增加混乱度的差分没有稳定装置");
            }
            ids.Clear();
            foreach (RoomDefinition room in rooms)
            {
                if (string.IsNullOrWhiteSpace(room.Id) || !ids.Add(room.Id)) errors.Add(room.name + "：箱庭编号为空或重复");
                if (room.Variants.Length < 1 || room.Variants.Length > 6) errors.Add(room.name + "：差分数量必须为1～6");
                if (room.Template == null) { errors.Add(room.name + "：缺少模板"); continue; }
                FlowRoomTemplate template = room.Template;
                errors.AddRange(FlowEnemySpawnValidation.Template(template));
                errors.AddRange(FlowTextValidation.Template(template));
                if (template.Entry == null || template.Exit == null || template.EntryDoor == null || template.ExitDoor == null) errors.Add(room.name + "：门接口或门板缺失");
                if (template.EntrySize.x <= 0 || template.EntrySize.y <= 0 || template.ExitSize.x <= 0 || template.ExitSize.y <= 0) errors.Add(room.name + "：门洞尺寸必须为正数");
                if (template.transform.localScale != UnityEngine.Vector3.one) errors.Add(room.name + "：房间模板根节点不能缩放");
                if (template.GeometryBounds.size.x <= 0 || template.GeometryBounds.size.z <= 0) errors.Add(room.name + "：缺少有效的几何包围盒");
                if ((template.Entry != null && template.Entry.parent != template.transform) || (template.Exit != null && template.Exit.parent != template.transform)) errors.Add(room.name + "：门接口必须是模板根节点的直接子节点");
                if (template.GetComponentsInChildren<PlayerMotor>(true).Length > 0) errors.Add(room.name + "：房间模板不能包含玩家");
                foreach (Enemy enemy in template.GetComponentsInChildren<Enemy>(true))
                {
                    FlowContentGroup group = enemy.GetComponentInParent<FlowContentGroup>(true);
                    if (group == null || !group.GameplayOnly) errors.Add(room.name + "：敌人必须放在仅当前房启用的内容组");
                }
                foreach (EnemySpawner spawner in template.GetComponentsInChildren<EnemySpawner>(true))
                {
                    FlowContentGroup group = spawner.GetComponentInParent<FlowContentGroup>(true);
                    if (group == null || !group.GameplayOnly) errors.Add(room.name + "：生成器必须放在仅当前房启用的内容组");
                }
                if (template.GetComponentsInChildren<FlowCheckpoint>(true).Length == 0) errors.Add(room.name + "：缺少记录点");
                var checkpointIds = new HashSet<string>();
                foreach (FlowCheckpoint checkpoint in template.GetComponentsInChildren<FlowCheckpoint>(true))
                    if (string.IsNullOrWhiteSpace(checkpoint.Id) || !checkpointIds.Add(checkpoint.Id)) errors.Add(room.name + "：记录点编号为空或重复");
                var deviceIds = new HashSet<string>();
                foreach (StabilityDevice device in template.GetComponentsInChildren<StabilityDevice>(true))
                    if (string.IsNullOrWhiteSpace(device.Id) || !deviceIds.Add(device.Id)) errors.Add(room.name + "：稳定装置编号为空或重复");
                for (int i = 0; i < room.Variants.Length; i++)
                {
                    RoomVariantDefinition a = room.Variants[i];
                    if (a == null || a.Room != room) { errors.Add(room.name + "：差分列表存在空值或错误归属"); continue; }
                    for (int j = i + 1; j < room.Variants.Length; j++)
                    {
                        RoomVariantDefinition b = room.Variants[j];
                        if (b != null && a.MinimumChaos <= b.MaximumChaos && a.MaximumChaos >= b.MinimumChaos) errors.Add(room.name + "：差分区间重叠 " + a.Id + " / " + b.Id);
                    }
                }
                bool ordinary = false;
                foreach (RoomVariantDefinition variant in room.Variants)
                    if (variant != null && variant.MinimumChaos <= config.MaximumChaos && variant.MaximumChaos >= config.MinimumChaos) ordinary = true;
                for (int chaos = config.MinimumChaos; ordinary && chaos <= config.MaximumChaos;)
                {
                    RoomVariantDefinition covering = room.ResolveVariant(chaos, null);
                    if (covering == null) { errors.Add(room.name + "：差分缺少混乱度 " + chaos + " 的覆盖"); break; }
                    if (covering.MaximumChaos >= config.MaximumChaos) break;
                    chaos = covering.MaximumChaos + 1;
                }
            }
            foreach (LoopRuleDefinition loop in config.Loops)
            {
                if (loop == null || loop.NeutralPool == null || loop.ChaosPool == null || loop.NeutralCounts == null || loop.NeutralCounts.Length == 0) continue;
                var samples = new SortedSet<int> { loop.MinimumChaos, loop.MaximumChaos };
                foreach (RoomPoolDefinition pool in new[] { loop.NeutralPool, loop.ChaosPool })
                    foreach (RoomPoolEntry entry in pool.Entries)
                    {
                        if (entry == null || entry.Variant == null) continue;
                        if (entry.Variant.MinimumChaos >= loop.MinimumChaos && entry.Variant.MinimumChaos <= loop.MaximumChaos) samples.Add(entry.Variant.MinimumChaos);
                        if (entry.Variant.MaximumChaos >= loop.MinimumChaos && entry.Variant.MaximumChaos < loop.MaximumChaos) samples.Add(entry.Variant.MaximumChaos + 1);
                    }
                foreach (int sample in samples)
                    if (!HasEligible(loop.NeutralPool, sample) || !HasEligible(loop.ChaosPool, sample))
                    { errors.Add(loop.name + "：在混乱度 " + sample + " 无法组成含变化房的循环"); break; }
            }
            if (config.InitialRoom != null && !config.InitialRoom.Supports(config.InitialChaos)) errors.Add("教程差分不支持起始混乱度");
            if (config.FinalRoom != null && !config.FinalRoom.Supports(config.MaximumChaos)) errors.Add("最终房差分不支持终局混乱度");
            return errors;
        }
        private static void Add(HashSet<RoomVariantDefinition> set, RoomVariantDefinition value) { if (value != null) set.Add(value); }
        private static bool HasEligible(RoomPoolDefinition pool, int chaos)
        {
            foreach (RoomPoolEntry entry in pool.Entries)
                if (entry != null && entry.Enabled && entry.Weight > 0 && entry.Variant != null && entry.Variant.Supports(chaos)) return true;
            return false;
        }
    }
}
