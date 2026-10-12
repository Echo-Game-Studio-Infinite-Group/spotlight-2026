using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Spotlight.VFX
{
    // RuinsFireShoot —— 废墟氛围火出图（静帧 + 高速掠过序列）
    //
    // 纪律来自调研 §6.11：**验收必须"在速度下"做**——静帧看细节是错的验收方式。
    // 所以这里两件事都做：① 若干静帧（看形态/光照/缺口）② 一条"以 20 m/s 掠过 100m"的序列帧，
    // 模拟玩家真实看到的那 1 秒。
    //
    // 可复现性：逐帧显式指定 `_Time` 全局、风向量、闪烁相位、粒子模拟时刻 → 同一输入必然同一输出。
    // 渲染口径与 project-0 既有出图脚本一致（HDR RT → 手动 Linear→sRGB → PNG），便于横向对照。
    public static class RuinsFireShoot
    {
        const string ScenePath = "Assets/Scenes/SC_VFX_RuinsFire.unity";
        const string OutDir = "F:/VSCode/taptap-game-jem/_render_fire";
        const int W = 1600, H = 900;

        [MenuItem("超高速行者/VFX/废墟氛围火出图（静帧+掠过序列）")]
        public static void Run()
        {
            if (!File.Exists(Path.GetFullPath(Path.Combine(Application.dataPath, "..", ScenePath))))
            {
                Debug.LogError("[RuinsFireShoot] 场景不存在，请先重建演示场景");
                return;
            }
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Directory.CreateDirectory(OutDir);
            Directory.CreateDirectory(OutDir + "/flyby");

            var cam = Object.FindObjectOfType<Camera>();
            if (cam == null) { Debug.LogError("[RuinsFireShoot] 场景里没有相机"); return; }
            var cd = cam.GetUniversalAdditionalCameraData();
            bool ppSaved = cd.renderPostProcessing;
            cd.renderPostProcessing = true;

            // 场景静态元素：火点
            var fires = new List<Transform>();
            var root = GameObject.Find("RUINS_FIRES");
            if (root != null)
                foreach (Transform c in root.transform)
                    if (c.name.StartsWith("FX_RuinsFire_Roadside")) fires.Add(c);
            fires.Sort((a, b) => a.position.z.CompareTo(b.position.z));
            if (fires.Count == 0) { Debug.LogError("[RuinsFireShoot] 场景里没有路边火"); return; }

            var pick = fires[Mathf.Clamp(fires.Count / 3, 0, fires.Count - 1)];
            Vector3 fp = pick.position;
            Debug.Log($"[RuinsFireShoot] 取样火堆 #{fires.Count / 3} @ {fp}");

            float t = 3.6f;   // 取一个"有内容"的动画时刻（不是起手帧）

            // ---------- 静帧 ----------
            // 机位一律在跑道内侧（|x| < 3.2）——火在 x≈±2.6，两侧是 x=±4.35 的断墙，
            // 站到墙外就只会拍到墙背（第一版就是这么翻车的）
            Shoot(cam, t, new Vector3(fp.x - 3.0f, 1.15f, fp.z - 2.4f), fp + new Vector3(0f, 0.6f, 0f), 42f, "01_roadside_close");
            Shoot(cam, t + 0.4f, new Vector3(fp.x * 0.15f, 1.45f, fp.z - 6.2f), fp + new Vector3(0f, 0.5f, 0f), 38f, "02_roadside_8m");
            Shoot(cam, t + 0.8f, new Vector3(-fp.x * 0.6f, 2.1f, fp.z - 17f), fp + new Vector3(0f, 0.5f, 0f), 34f, "03_roadside_25m");
            Shoot(cam, t + 1.2f, new Vector3(0f, 6.4f, 10f), new Vector3(0f, 0.6f, 62f), 46f, "04_row_wide");
            Shoot(cam, t + 1.6f, new Vector3(1.35f, 1.20f, 30.6f), new Vector3(2.85f, 1.15f, 34f), 44f, "05_linger_close");
            Shoot(cam, t + 2.0f, new Vector3(-0.4f, 1.6f, 28.4f), new Vector3(2.6f, 1.05f, 34f), 40f, "06_linger_three_quarter");

            // ---------- "全黑只剩火"：关环境光与月光，证明火是唯一暖光源 ----------
            var amb = RenderSettings.ambientLight;
            var moonGo = GameObject.Find("L_Moon");
            Light moon = moonGo != null ? moonGo.GetComponent<Light>() : null;
            bool moonOn = moon != null && moon.enabled;
            RenderSettings.ambientLight = new Color(0.006f, 0.006f, 0.008f, 1f);
            if (moon != null) moon.enabled = false;
            Shoot(cam, t + 2.4f, new Vector3(fp.x - 2.8f, 1.10f, fp.z - 2.2f), fp + new Vector3(0f, 0.6f, 0f), 42f, "07_fire_only_no_ambient");
            RenderSettings.ambientLight = amb;
            if (moon != null) moon.enabled = moonOn;

            // ---------- 高速掠过序列：20 m/s × 5s = 100m ----------
            var flyby = cam.GetComponent<RuinsFireFlyby>();
            if (flyby == null) flyby = cam.gameObject.AddComponent<RuinsFireFlyby>();
            int frames = 20;
            float dt = 0.25f;          // 每帧 5m —— 相当于"每 5 米存一帧"
            for (int i = 0; i < frames; i++)
            {
                float ft = 1.2f + i * dt;
                flyby.Sample(ft, out Vector3 p, out Quaternion r);
                Shoot(cam, ft, p, r, 62f, $"f{i:D2}_z{p.z:F0}", OutDir + "/flyby", flybyLook: true);
            }

            cd.renderPostProcessing = ppSaved;
            Debug.Log($"[RuinsFireShoot] 完成 → {OutDir}（静帧 7 张 + 掠过 {frames} 帧）");
        }

        // 隔离诊断：同一机位分别关掉 灯 / 透明件 / 粒子，一图定因（"椭圆到底是什么"这类问题别靠猜）
        [MenuItem("超高速行者/VFX/废墟氛围火：隔离诊断出图")]
        public static void Diag()
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Directory.CreateDirectory(OutDir + "/diag");
            var cam = Object.FindObjectOfType<Camera>();
            var cd = cam.GetUniversalAdditionalCameraData(); cd.renderPostProcessing = true;

            Vector3 pos = new Vector3(1.35f, 1.20f, 30.6f);
            Vector3 tgt = new Vector3(2.85f, 1.15f, 34f);

            Shoot(cam, 5.6f, pos, tgt, 44f, "diag/00_all", OutDir);

            var lights = Object.FindObjectsOfType<Light>(true);
            var ls = new bool[lights.Length];
            for (int i = 0; i < lights.Length; i++) { ls[i] = lights[i].enabled; if (lights[i].type == LightType.Point) lights[i].enabled = false; }
            Shoot(cam, 5.6f, pos, tgt, 44f, "diag/01_no_pointlight", OutDir);
            for (int i = 0; i < lights.Length; i++) lights[i].enabled = ls[i];

            var pss = Object.FindObjectsOfType<ParticleSystem>(true);
            var psState = new bool[pss.Length];
            for (int i = 0; i < pss.Length; i++) { psState[i] = pss[i].gameObject.activeSelf; pss[i].gameObject.SetActive(false); }
            Shoot(cam, 5.6f, pos, tgt, 44f, "diag/02_no_particles", OutDir);
            for (int i = 0; i < pss.Length; i++) pss[i].gameObject.SetActive(psState[i]);

            var mrs = Object.FindObjectsOfType<MeshRenderer>(true);
            var glows = new System.Collections.Generic.List<MeshRenderer>();
            foreach (var r in mrs)
                if (r.sharedMaterial != null && r.sharedMaterial.shader != null &&
                    r.sharedMaterial.shader.name.StartsWith("OurFunction/VFX/RuinsFireFlipbook") &&
                    r.sharedMaterial.GetFloat("_ColorMode") > 0.5f)   // flat 模式 = 光斑/烟
                    glows.Add(r);
            var gState = new bool[glows.Count];
            for (int i = 0; i < glows.Count; i++) { gState[i] = glows[i].enabled; glows[i].enabled = false; }
            Shoot(cam, 5.6f, pos, tgt, 44f, "diag/03_no_flatcards", OutDir);
            for (int i = 0; i < glows.Count; i++) glows[i].enabled = gState[i];

            Debug.Log($"[RuinsFireShoot] 诊断完成（灯 {lights.Length} / 粒子 {pss.Length} / flat卡片 {glows.Count}）");
        }

        // 逐子物体隔离：把驻足档火的每个子物体**单独打开**渲一张 → 一图定名。
        // 用途：像"火焰身上那条降饱和竖带到底是什么"这类问题，靠关掉整类（灯/粒子/flat卡）
        // 已经分不出来（三张诊断图里它都在），必须下沉到"单个卡片"这一层。
        [MenuItem("超高速行者/VFX/废墟氛围火：逐子物体隔离出图")]
        public static void DiagCards()
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Directory.CreateDirectory(OutDir + "/iso");
            var cam = Object.FindObjectOfType<Camera>();
            cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;

            Transform fire = null;
            var root = GameObject.Find("RUINS_FIRES");
            if (root != null)
                foreach (Transform c in root.transform)
                    if (c.name.StartsWith("FX_RuinsFire_Linger")) { fire = c; break; }
            if (fire == null) { Debug.LogError("[RuinsFireShoot] 场景里没有驻足档火"); return; }

            // 与 Diag() 同机位（正对驻足档那堆火）
            Vector3 pos = new Vector3(1.35f, 1.20f, 30.6f);
            Vector3 tgt = new Vector3(2.85f, 1.15f, 34f);

            var kids = new List<Transform>();
            foreach (Transform c in fire) kids.Add(c);
            var state = new bool[kids.Count];
            for (int i = 0; i < kids.Count; i++) state[i] = kids[i].gameObject.activeSelf;

            for (int i = 0; i < kids.Count; i++)
            {
                for (int j = 0; j < kids.Count; j++) kids[j].gameObject.SetActive(j == i);
                Shoot(cam, 5.6f, pos, tgt, 44f, $"iso/{i:D2}_{kids[i].name}", OutDir);
            }
            for (int i = 0; i < kids.Count; i++) kids[i].gameObject.SetActive(state[i]);
            Debug.Log($"[RuinsFireShoot] 逐子物体隔离完成（{kids.Count} 个）：{string.Join(", ", kids.ConvertAll(k => k.name))}");
        }

        [MenuItem("超高速行者/VFX/废墟氛围火：重建场景并出图（一键）")]
        public static void BuildAndRun()
        {
            RuinsFireDemo.Build();
            AssetDatabase.SaveAssets();
            Run();
        }

        // 一把梭（批处理入口）：重生成图集 → 重建材质/预制体（含材质自检）→ 重建演示场景
        // → 逐子物体隔离出图 → 静帧 + 掠过序列。改完生成器/构建器只跑这一个方法就够。
        [MenuItem("超高速行者/VFX/废墟氛围火：全链重建+全部出图")]
        public static void RebuildAll()
        {
            RuinsFireBuilder.Build();
            RuinsFireDemo.Build();
            AssetDatabase.SaveAssets();
            DiagCards();
            Diag();
            Run();
            Debug.Log("[RuinsFireShoot] RebuildAll 完成（资产 + 场景 + 隔离/诊断/静帧/掠过）");
        }

        // ------------------------------------------------------------------
        static void Shoot(Camera cam, float time, Vector3 pos, Vector3 target, float fov, string name, string dir = null, bool flybyLook = false)
        {
            var rot = Quaternion.LookRotation((target - pos).normalized, Vector3.up);
            Shoot(cam, time, pos, rot, fov, name, dir, flybyLook);
        }

        static void Shoot(Camera cam, float time, Vector3 pos, Quaternion rot, float fov, string name, string dir = null, bool flybyLook = false)
        {
            // 可复现：把动画时刻、风、火光闪烁、粒子全部拨到指定时刻
            Shader.SetGlobalVector("_Time", new Vector4(time / 20f, time, time * 2f, time * 3f));
            var wind = RuinsFireWind.Evaluate(new Vector2(0.35f, 1f), 0.5f, 0.4f, 0.11f, 0.45f, time);
            RuinsFireWind.SetGlobal(wind);
            foreach (var f in Object.FindObjectsOfType<RuinsFireFlicker>(true)) f.Apply(time);
            foreach (var ps in Object.FindObjectsOfType<ParticleSystem>(true))
            {
                if (!ps.gameObject.activeInHierarchy) continue;
                ps.Simulate(time, true, true);   // 重启并推进到 time → 确定性
                ps.Pause();
            }

            var tr = cam.transform;
            var posSaved = tr.position; var rotSaved = tr.rotation;
            float fovSaved = cam.fieldOfView;
            tr.SetPositionAndRotation(pos, rot);
            cam.fieldOfView = fov;

            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Linear);
            var prev = cam.targetTexture;
            cam.targetTexture = rt;
            cam.Render();

            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGBAFloat, false, true);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            var px = tex.GetPixels();
            var t8 = new Texture2D(W, H, TextureFormat.RGBA32, false, false);
            var c8 = new Color[px.Length];
            for (int k = 0; k < px.Length; k++)
                c8[k] = new Color(L2S(Mathf.Clamp01(px[k].r)), L2S(Mathf.Clamp01(px[k].g)), L2S(Mathf.Clamp01(px[k].b)), 1f);
            t8.SetPixels(c8); t8.Apply();

            string d = dir ?? OutDir;
            Directory.CreateDirectory(d);
            File.WriteAllBytes($"{d}/{name}.png", t8.EncodeToPNG());

            cam.targetTexture = prev;
            tr.SetPositionAndRotation(posSaved, rotSaved);
            cam.fieldOfView = fovSaved;
            Object.DestroyImmediate(rt); Object.DestroyImmediate(tex); Object.DestroyImmediate(t8);
        }

        static float L2S(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(Mathf.Max(c, 0f), 1f / 2.4f) - 0.055f;
    }
}
