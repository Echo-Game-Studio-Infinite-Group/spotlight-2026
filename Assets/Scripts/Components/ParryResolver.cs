using System.Collections.Generic;
using UnityEngine;

// 两条入口共用几何判定：主动扫刀先于伤害；身体触发器只补查同一套框，不能凭开窗免伤。
public static class ParryResolver
{
    private static readonly List<CombatComponent> Sources = new List<CombatComponent>();
    private static readonly List<PlayerCombat> Players = new List<PlayerCombat>();
    public static void Register(CombatComponent source) { if (!Sources.Contains(source)) Sources.Add(source); }
    public static void Unregister(CombatComponent source) => Sources.Remove(source);
    public static void Register(PlayerCombat player) { if (!Players.Contains(player)) Players.Add(player); }
    public static void Unregister(PlayerCombat player) => Players.Remove(player);

    public static void Collect(PlayerCombat player, AttackVolumePose from, AttackVolumePose to, float spacing,
        int mask, int environmentMask, Vector3 offset, Dictionary<CombatComponent, ParryContact> contacts, float start = 0f, float end = 1f)
    {
        foreach (CombatComponent source in Sources)
        {
            if (source == null || source.Camp == CampType.Player || (mask & (1 << source.gameObject.layer)) == 0
                || contacts.ContainsKey(source) || !source.TryGetParryMotion(out AttackVolumePose targetFrom, out AttackVolumePose target)) continue;
            if (ParryGeometry.Swept(from, to, ParryGeometry.Lerp(targetFrom, target, start),
                ParryGeometry.Lerp(targetFrom, target, end), spacing, out Vector3 point)
                && !Blocked(player.transform, source.transform, to.Root + offset, point, environmentMask))
                contacts.Add(source, new ParryContact(source, point, target.Center - to.Center));
        }
    }

    public static bool ResolveIncoming(CombatComponent source, AttackVolumePose? from = null, AttackVolumePose? to = null)
    {
        // 回调会取消动作、回收子弹，遍历快照以免组件失活修改注册表。
        foreach (PlayerCombat player in Players.ToArray())
            if (player != null && player.ResolveIncomingParry(source, from, to)) return true;
        return false;
    }

    public static bool Blocked(Transform player, Transform source, Vector3 origin, Vector3 point, int mask)
    {
        Vector3 ray = point - origin;
        if (ray.sqrMagnitude < .000001f) return false;
        foreach (RaycastHit hit in Physics.RaycastAll(origin, ray.normalized, ray.magnitude, mask, QueryTriggerInteraction.Ignore))
            if (!hit.transform.IsChildOf(player) && !hit.transform.IsChildOf(source)
                && hit.collider.GetComponentInParent<IAttackDamageReceiver>() == null) return true;
        return false;
    }

    public static void Commit(IParryReceiver player, Dictionary<CombatComponent, ParryContact> contacts)
    {
        if (contacts.Count == 0) return;
        var consumed = new List<ParryContact>(contacts.Count);
        // 所有来源先被消费，再通知表现和取消玩家，避免第一颗子弹吞掉同批其他拼刀。
        foreach (ParryContact contact in contacts.Values)
            if (contact.Source != null && contact.Source.ConsumeParry(contact)) consumed.Add(contact);
        if (consumed.Count > 0) player.ReceiveParry(consumed);
    }
}
