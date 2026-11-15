#if UNITY_EDITOR
// 删除地面检修盖板 FORGE_FloorHatch(2026-10-11 用户令"删掉吧")。
// 盖板是 Blender 原生构件(0.8×0.8m 深色金属 + 4 螺栓 + 把手), 无灯直照显突兀。
// 删完走与二期相同的重烤链(删除件可能挡过光, 地板光图要重算):
//   存盘 → StripExtStatic → 门区灯复位(防二次 ×0.33) → 纯 Baked 重烤(跳内置探针)
//   → Polish → 手工反射盒 → 出图 → 线性读数。
// 不跑 AddWalkPlayer/FixMissingLightmapUVs: 无新网格无新碰撞, 场景里玩家与碰撞体已是存盘态。
// 可还原性: 删除前把变换/网格名打进日志, Blender 源件仍在工程里, 随时可按日志重建。
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace HST
{
    public static class HstHatchRemove
    {
        const string SceneDir = "Assets/Scenes";
        const string SceneName = "SC_Corridor_Pragmata";
        const string HatchRoot = "FORGE_FloorHatch";

        [MenuItem("HST/删除地面检修盖板+重烤出图")]
        public static void Run()
        {
            var scene = EditorSceneManager.OpenScene(SceneDir + "/" + SceneName + ".unity", OpenSceneMode.Single);

            // ---- 删除(板本体 + 4 螺栓 + 把手是兄弟件, 统一按前缀扫) ----
            var mrs = Object.FindObjectsOfType<MeshRenderer>(true)
                        .Where(mr => mr.name.StartsWith("FORGE_FloorHatch")).ToArray();
            if (mrs.Length == 0) { Debug.LogError("[HST-HATCH] 场景里没有 FORGE_FloorHatch*(可能已删过), 全流程中止"); return; }
            var sb = new System.Text.StringBuilder("[HST-HATCH] 删除 " + HatchRoot + " 件数=" + mrs.Length);
            foreach (var mr in mrs)   // 台账: 万一要还原, 凭这份日志 + Blender 源件可 1:1 重建
            {
                var b = mr.bounds;
                sb.Append("\n  " + mr.name + " mat=" + (mr.sharedMaterial != null ? mr.sharedMaterial.name : "null")
                          + " center=(" + b.center.x.ToString("0.000") + "," + b.center.y.ToString("0.000") + "," + b.center.z.ToString("0.000") + ")"
                          + " size=(" + b.size.x.ToString("0.000") + "," + b.size.y.ToString("0.000") + "," + b.size.z.ToString("0.000") + ")");
            }
            Debug.Log(sb.ToString());
            foreach (var mr in mrs) Object.DestroyImmediate(mr.gameObject);   // 含兄弟件螺栓/把手一并带走
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            HstCorridorExtend.Log("盖板: " + HatchRoot + " 已删并存盘(" + mrs.Length + " 个网格)");

            // ---- 重烤链(与二期 RunAll 同序, 去掉几何专属步骤) ----
            HstCorridorExtend.StripExtStatic();
            HstCorridorStage2.ResetDoorLights();
            HstCorridorExtend.Log("盖板: 重烤(纯 Baked + GPU; 跳过内置探针, 反射由手工盒接管)");
            SceneBuilder._indirectOverride = 4096;
            SceneBuilder._skipProbeBake = true;
            SceneBuilder.BakeCorridorOnly();
            SceneBuilder._skipProbeBake = false;
            SceneBuilder._indirectOverride = 0;
            SceneBuilder.Polish();
            HstCorridorExtend.Log("盖板: 手工反射盒 + 相机裁决");
            SceneBuilder.BakeProbeManual();
            HstCorridorExtend.Log("盖板: 出图 + 线性读数");
            SceneBuilder.Shoot();
            SceneBuilder.DiagLinear();
            HstCorridorExtend.Log("盖板: 全流程完成");
        }

        // 删除后的地面核验图: 从玩家视线方向(走向大门)看盖板原位 (-8.5, 0, -0.4), 不存盘不重烤
        [MenuItem("HST/盖板删除地面核验图(不存盘)")]
        public static void FloorCheck()
        {
            var scene = EditorSceneManager.OpenScene(SceneDir + "/" + SceneName + ".unity", OpenSceneMode.Single);
            var tpl = GameObject.Find("CAM_02_Door");
            if (tpl == null) { Debug.LogError("[HST-HATCH] 找不到 CAM_02_Door"); return; }
            HstDoorWallShot.RenderTo(tpl.GetComponent<Camera>(),
                new Vector3(-4.8f, 2.2f, 0.8f), 55f,
                "F:/VSCode/工业级渲染/renders/unity_repro/_check_hatch_gone.png",
                new Vector3(-8.7f, 0f, -0.5f));
            Debug.Log("[HST-HATCH] 地面核验图完成, 场景未改动");
        }

        // 挑高交界取证: 侧墙高带顶(5.192)/门楣/腰线三个视点, 只渲染不存盘不重烤
        [MenuItem("HST/挑高交界取证图(不存盘)")]
        public static void UpperCheck()
        {
            EditorSceneManager.OpenScene(SceneDir + "/" + SceneName + ".unity", OpenSceneMode.Single);
            var tpl = GameObject.Find("CAM_02_Door");
            if (tpl == null) { Debug.LogError("[HST-HATCH] 找不到 CAM_02_Door"); return; }
            var cam = tpl.GetComponent<Camera>();
            string dir = "F:/VSCode/工业级渲染/renders/unity_repro";
            // ① 侧墙与天花交界(抬头看)
            HstDoorWallShot.RenderTo(cam, new Vector3(-6.0f, 2.6f, 0.0f), 60f,
                dir + "/_check_junction_sidewall.png", new Vector3(-11.0f, 5.3f, 2.6f));
            // ② 门墙与天花交界(抬头看)
            HstDoorWallShot.RenderTo(cam, new Vector3(-13.0f, 2.2f, 0.0f), 70f,
                dir + "/_check_junction_doorwall.png", new Vector3(-17.98f, 5.0f, 0.0f));
            // ③ 侧墙正立面(垂直正视, 一眼看清全部横棱; 距离拉远到 fov 全高 5.2m)
            HstDoorWallShot.RenderTo(cam, new Vector3(-8.0f, 2.6f, -3.5f), 42f,
                dir + "/_check_elevation_sidewall.png", new Vector3(-8.0f, 2.6f, 3.975f));
            Debug.Log("[HST-HATCH] 交界取证图完成");
        }
    }
}
#endif
