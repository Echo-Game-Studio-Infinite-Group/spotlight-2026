#if UNITY_EDITOR
// 倒角实装轮 (2026-10-12): FBX 已换成"分级加宽倒角"版(SM_Corridor_Pragmata_v1.fbx 原地覆盖,
// meta/GUID 未动 → 场景引用与顺序全不变)。倒角改变 UV2 → 必须重烤(光照贴图按 UV2 索引)。
// 本工具: 重烤全链 + 倒角近景特写(自动取件取景, 只渲染不存盘)。
// 顺带打印目标件的 bounds —— 倒角切掉棱上极值点, AABB 会按倒角宽度内缩, 可与 manifest 对拍验证。
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace HST
{
    public static class HstBevelRebake
    {
        const string SceneDir = "Assets/Scenes";
        const string SceneName = "SC_Corridor_Pragmata";
        const string OutDir = "F:/VSCode/工业级渲染/renders/unity_repro";

        [MenuItem("HST/倒角轮-重烤+近景特写")]
        public static void RunAll()
        {
            Rebake();          // ① 重烤(UV2 变了, 必须)
            BevelShots();      // ② 近景特写
        }

        [MenuItem("HST/倒角轮-只重烤")]
        public static void Rebake()
        {
            EditorSceneManager.OpenScene(SceneDir + "/" + SceneName + ".unity", OpenSceneMode.Single);
            HstCorridorExtend.StripExtStatic();
            HstCorridorStage2.ResetDoorLights();
            SceneBuilder._indirectOverride = 4096;
            SceneBuilder._skipProbeBake = true;
            SceneBuilder.BakeCorridorOnly();
            SceneBuilder._skipProbeBake = false;
            SceneBuilder._indirectOverride = 0;
            SceneBuilder.Polish();
            SceneBuilder.BakeProbeManual();
            SceneBuilder.Shoot();
            SceneBuilder.DiagLinear();
            HstCorridorExtend.Log("倒角轮: 重烤全链完成");
        }

        [MenuItem("HST/倒角轮-只出近景特写(不重烤)")]
        public static void BevelShots()
        {
            EditorSceneManager.OpenScene(SceneDir + "/" + SceneName + ".unity", OpenSceneMode.Single);
            var tpl = GameObject.Find("CAM_02_Door");
            if (tpl == null) { Debug.LogError("[HST-BEV] 找不到 CAM_02_Door"); return; }
            var cam = tpl.GetComponent<Camera>();
            Directory.CreateDirectory(OutDir);

            // 目标件 → 3/4 近景(从件中心朝走廊内部斜 45°、略高, 看棱线兜光); 距离按件尺寸自适应
            ShotNear(cam, "FORGE_Crate_1", "_check_bevel_crate.png");
            ShotNear(cam, "FORGE_RackA_Body", "_check_bevel_rack.png");
            ShotNear(cam, "ARCH_DoorLeaf_L", "_check_bevel_doorleaf.png");
            ShotNear(cam, "ARCH_Panel_-10", "_check_bevel_wallpanel.png");
            Debug.Log("[HST-BEV] 倒角近景特写完成");
        }

        static void ShotNear(Camera cam, string name, string file)
        {
            var go = GameObject.Find(name);
            if (go == null) { Debug.LogWarning("[HST-BEV] 缺件 " + name); return; }
            var r = go.GetComponent<Renderer>();
            if (r == null) { Debug.LogWarning("[HST-BEV] 无渲染器 " + name); return; }
            var b = r.bounds;
            // 距离随件尺寸自适应: 对角线 ×0.85, 保证整件入画(固定距离会让大件"贴脸"、小件"看不清")
            float dist = Mathf.Max(b.size.magnitude * 0.85f, 0.9f);
            // 朝走廊内部(z→0)斜 45° 退开, 抬高看棱线
            float zsign = Mathf.Sign(b.center.z); if (zsign == 0f) zsign = 1f;
            var dir = new Vector3(-0.42f, 0.26f, -zsign * 0.87f).normalized;
            var pos = b.center + dir * dist;
            float fov = dist > 2.5f ? 45f : 40f;
            HstDoorWallShot.RenderTo(cam, pos, fov, OutDir + "/" + file, b.center);
            Debug.Log(string.Format("[HST-BEV] {0} dist={1:F2} bounds=({2:F3},{3:F3},{4:F3}) size=({5:F4},{6:F4},{7:F4})",
                name, dist, b.center.x, b.center.y, b.center.z, b.size.x, b.size.y, b.size.z));
        }
    }
}
#endif
