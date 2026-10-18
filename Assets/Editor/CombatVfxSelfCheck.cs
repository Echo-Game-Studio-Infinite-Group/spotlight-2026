using UnityEditor;
using UnityEngine;

// 战斗火花特效批量自检：-executeMethod CombatVfxSelfCheck.Run（不进菜单，批处理专用）。
//
// 无帧循环也能验的层：共享材质生成、三类爆发特效全链路装配（模板→克隆→播放）、
// 滑铲发射器装配。火花「看得到看不到」是渲染层的事，交给 Play 模式实机目检
// （按 E 真的 parry 一次 + 跑起来滑铲），本自检不越权替它下结论。
public static class CombatVfxSelfCheck
{
    public static void Run()
    {
        if (EditorApplication.isPlaying)
        {
            Debug.LogError("[CombatVfxSelfCheck] 请先退出 Play 模式再执行");
            return;
        }

        int failures = 0;

        // 1) 共享材质：贴图程序化生成 + Sprites/Default 就位
        Material spark = CombatSparks.SparkMaterial;
        bool materialOk = spark != null && spark.mainTexture != null && spark.shader != null;
        Debug.Log($"[CombatVfxSelfCheck] 共享材质={(materialOk ? "就位" : "生成失败")} "
            + $"shader={(spark != null && spark.shader != null ? spark.shader.name : "无")} "
            + $"贴图={(spark != null && spark.mainTexture != null ? spark.mainTexture.name : "无")}");
        if (!materialOk) failures++;

        // 2) 三类爆发特效：走真实入口播放一次（覆盖模板装配→池克隆→Play 的整条链）
        //    编辑模式下粒子不会真模拟，但装配期的任何配置错误（模块参数/材质/组件缺失）都会在这里抛。
        CombatSparks.PlayHit(Vector3.zero, Vector3.forward, 25f);
        CombatSparks.PlayParry(Vector3.up);
        CombatSparks.PlayParryProjectile(Vector3.up * 2f, Vector3.forward);
        Debug.Log("[CombatVfxSelfCheck] 爆发特效装配：三类 Play 入口无异常（模板/克隆/播放链路走通）");

        // 3) 滑铲发射器：临时玩家直呼 EnsureEmitter（编辑模式 AddComponent 不跑 OnEnable）
        GameObject player = new GameObject("CombatVfxSelfCheckPlayer");
        try
        {
            player.AddComponent<SlideSparks>(); // RequireComponent 会自动补 PlayerMotor/CharacterController
            player.GetComponent<SlideSparks>().EnsureEmitter();
            ParticleSystem emitter = player.GetComponentInChildren<ParticleSystem>();
            bool emitterOk = emitter != null;
            Debug.Log($"[CombatVfxSelfCheck] 滑铲发射器装配={(emitterOk ? "就位" : "失败")}"
                + (emitterOk ? $"（粒子系统={emitter.name}）" : "（EnsureEmitter 没产出 ParticleSystem）"));
            if (!emitterOk) failures++;
        }
        finally
        {
            Object.DestroyImmediate(player);
        }

        // 收尾：自检产物（池根/闪光灯）当场清掉，不污染批处理场景；静态引用下次访问会自愈重建
        foreach (GameObject candidate in Object.FindObjectsOfType<GameObject>(true))
        {
            if (candidate.name.StartsWith("CombatSparks(", System.StringComparison.Ordinal))
                Object.DestroyImmediate(candidate);
        }

        Debug.Log(failures == 0
            ? "[CombatVfxSelfCheck] 结论：通过 —— 材质/三类爆发特效/滑铲发射器装配全部就位"
            : $"[CombatVfxSelfCheck] 结论：失败 —— {failures} 项未过，见上面各行");
    }
}
