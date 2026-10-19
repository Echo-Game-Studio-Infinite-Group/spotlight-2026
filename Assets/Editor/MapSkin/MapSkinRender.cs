using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace MapSkin
{
    /// <summary>
    /// Level1 40° 等距（俯仰 45° + 方位 45°）正交出图，按关卡区块逐张（S_01..S_07，见 MapSceneOrganize 区块口径）。
    ///
    /// 用途：换材质/打光后一眼看全图（QA 用），文件名带关卡号，输出到工程外 `_render_skin/`。
    /// 口径：
    ///   · 正交相机、俯仰 45° + 方位 45°（等距）；按区块渲染器包围盒自动取景（留 15% 余量）
    ///   · **GUI 模式渲染**（batchmode 下 URP 出图不稳）；每机位渲两帧取第二帧（TAA 需 ≥2 帧收敛，规范坑 #12）
    ///   · 后处理开关打开（让 TA 的 NPR/描边等渲染器特性按运行时口径出画），相机背景用中性深灰（利于读亮部）
    ///   · 只读场景：临时相机渲完即删，场景不写盘
    ///
    /// 菜单：MapSkin/⑤ 45° 等距出图
    /// 批处理/命令行：-executeMethod MapSkin.MapSkinRender.Run（建议 GUI：-projectPath &lt;工程&gt; -executeMethod ...）
    ///   可选 -shotdir &lt;目录&gt;、-shotw/-shoth &lt;像素&gt;
    /// </summary>
    public static class MapSkinRender
    {
        static string ScenePath = "Assets/Scenes/Level1.unity";
        static string ShotDir = "F:/VSCode/taptap-game-jem/_render_skin";
        static int W = 1920, H = 1080;
        static string Only = null;          // -shotonly BOX_S_01：只出一关（迭代用）
        static bool Closeups = false;       // -shotclose 1：每关加 3 张特写（贴近看接缝/贴图/转角）
        static string CamSpec = null;       // -cam x,y,z,tx,ty,tz,fov[,name]：任意机位
        static string CamFile = null;       // -camfile <txt>：每行一个机位（# 注释），批量自由镜头
        static readonly Vector3 IsoDir = new Vector3(0.5f, 0.70710678f, 0.5f);   // 方位 45° + 俯仰 45°

        [MenuItem("MapSkin/⑤ 45° 等距出图")]
        public static void RunMenu() => Run();

        public static void Run()
        {
            ParseArgs();
            if (Application.isBatchMode)
                Debug.LogWarning("[MapSkin·出图] 当前是 batchmode：URP 出图在此模式下不稳（规范坑 #8/#12 同源），" +
                                 "建议用 GUI 模式跑本入口。仍继续尝试。");

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Directory.CreateDirectory(ShotDir);

            var camGo = new GameObject("__MapSkinShotCam");
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.nearClipPlane = 0.3f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.22f, 0.24f, 0.27f, 0f);   // 中性深灰背景 + alpha=0（QA 的"仅几何"掩膜靠它）
            var extra = cam.GetUniversalAdditionalCameraData();
            if (extra != null)
            {
                extra.renderPostProcessing = true;                  // NPR/描边等按运行时口径出画
                extra.renderShadows = true;
            }

            var rep = new StringBuilder();
            if (CamSpec != null || CamFile != null)
            {
                if (CamFile != null)
                {
                    foreach (var line in File.ReadAllLines(CamFile))
                    {
                        var s = line.Trim();
                        if (s.Length == 0 || s.StartsWith("#")) continue;
                        ShootCustom(cam, s, rep);
                    }
                }
                else ShootCustom(cam, CamSpec, rep);
                Object.DestroyImmediate(camGo);
                Debug.Log($"[MapSkin·出图] 自定义机位 → {ShotDir}\n{rep}");
                return;
            }
            // 逐区块 + 一张全图总览
            rep.AppendLine("    机位        orthoSize   全画面 mean/死黑/过曝        仅几何 mean/死黑/过曝（§七 口径）");
            foreach (var root in scene.GetRootGameObjects().Where(g => g.name.StartsWith("BOX_S_")).OrderBy(g => g.name))
            {
                if (Only != null && root.name != Only) continue;
                // 两张：`BOX_*` 只框原区块（读建筑本体）；`BOX_*_full` 连分关装饰一起框（读装饰总貌）。
                Shoot(cam, root.name, CollectBounds(new[] { root }), rep);
                Shoot(cam, root.name + "_full", BlockBounds(scene, root.name), rep);
                if (Closeups)
                {
                    var bb = CollectBounds(new[] { root });
                    float span = Mathf.Max(bb.size.x, Mathf.Max(bb.size.y, bb.size.z));
                    float s = Mathf.Clamp(span * 0.12f, 10f, 40f);
                    bool alongX = bb.size.x >= bb.size.z;
                    for (int k = 1; k <= 3; k++)
                    {
                        var p = bb.center;
                        if (alongX) p.x = bb.min.x + bb.size.x * (0.25f * k);
                        else p.z = bb.min.z + bb.size.z * (0.25f * k);
                        p.y = bb.min.y + bb.size.y * 0.35f;
                        Shoot(cam, $"{root.name}_c{k}", new Bounds(p, Vector3.one * s), rep);
                    }
                }
            }
            if (Only == null)
                Shoot(cam, "Overview", CollectBounds(scene.GetRootGameObjects()
                            .Where(g => g.name.StartsWith("BOX_S_"))), rep);

            Object.DestroyImmediate(camGo);
            rep.AppendLine("    注：QA 门禁 §七 的 mean/死黑/过曝按「仅几何」列看（等距视图有大片背景，全画面列会被背景稀释）；");
            rep.AppendLine("        温度基准：大厅 mean 0.45–0.58、死黑 <2%、过曝 ≈0（过曝不计发光件）。");
            Debug.Log($"[MapSkin·出图] 完成 → {ShotDir}\n{rep}");
        }

        /// <summary>
        /// 任意机位：-cam "x,y,z,tx,ty,tz,fov[,name]"。透视相机，按当前疑问现定视角，
        /// 不再只依赖预设远景（迭代时看接缝/转角/贴图必须切特写）。
        /// </summary>
        static void ShootCustom(Camera cam, string spec, StringBuilder rep)
        {
            var p = spec.Split(',');
            if (p.Length < 7) { rep.AppendLine("[自定义机位] 参数不足：x,y,z,tx,ty,tz,fov[,name]"); return; }
            float F(int i) => float.Parse(p[i], System.Globalization.CultureInfo.InvariantCulture);
            var eye = new Vector3(F(0), F(1), F(2));
            var tgt = new Vector3(F(3), F(4), F(5));
            float fov = F(6);
            string name = p.Length >= 8 && !string.IsNullOrEmpty(p[7]) ? p[7] : "CUSTOM";

            cam.orthographic = false;
            cam.fieldOfView = fov;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 5000f;
            // 自由镜头按实际运行口径看天空盒（等距 QA 图才用纯色掩膜背景）
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.transform.position = eye;
            cam.transform.LookAt(tgt, Vector3.up);

            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            cam.Render(); cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            cam.targetTexture = null;
            var px = tex.GetPixels32();
            var full = Stats(px, false);
            var geo = Stats(px, true);
            File.WriteAllBytes(Path.Combine(ShotDir, name + ".png"), EncodeOpaque(tex));
            Object.DestroyImmediate(tex);
            rt.Release();
            Object.DestroyImmediate(rt);
            rep.AppendLine($"    {name,-12} eye=({eye.x:F1},{eye.y:F1},{eye.z:F1}) → ({tgt.x:F1},{tgt.y:F1},{tgt.z:F1}) fov={fov:F0}   {full}   {geo}");
        }

        static void Shoot(Camera cam, string tagName, Bounds b, StringBuilder rep)
        {
            if (b.size.sqrMagnitude < 1e-6f) { rep.AppendLine($"  {tagName}: 无渲染器，跳过"); return; }
            float span = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
            float dist = span * 2.5f + 100f;
            cam.transform.position = b.center + IsoDir.normalized * dist;
            cam.transform.LookAt(b.center, Vector3.up);

            // 正交尺寸：把世界包围盒投影到相机竖直轴，留 15% 余量
            float up = 0.7071f * b.size.y + 0.3536f * (b.size.x + b.size.z);
            cam.orthographicSize = Mathf.Max(up * 0.5f * 1.15f, 1f);
            cam.farClipPlane = dist * 3f;

            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            cam.Render();          // 第 1 帧：预热（着色器/特性/TAA）
            cam.Render();          // 第 2 帧：取这一帧
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            cam.targetTexture = null;

            var px = tex.GetPixels32();
            var full = Stats(px, useAlphaMask: false);
            var geo = Stats(px, useAlphaMask: true);

            var path = Path.Combine(ShotDir, tagName + ".png");
            File.WriteAllBytes(path, EncodeOpaque(tex));
            Object.DestroyImmediate(tex);
            rt.Release();
            Object.DestroyImmediate(rt);
            rep.AppendLine($"    {tagName,-10} {cam.orthographicSize,8:F1}   {full}   {geo}");
        }

        /// <summary>亮度统计：mean / 死黑(&lt;0.05) / 过曝(&gt;0.98) 占比。几何列按 alpha&gt;0.5 掩膜（没写到 alpha 就自动退回全画面）。</summary>
        static string Stats(Color32[] px, bool useAlphaMask)
        {
            int n = 0, dark = 0, blow = 0;
            double sum = 0;
            for (int i = 0; i < px.Length; i++)
            {
                if (useAlphaMask && px[i].a < 128) continue;
                double l = (px[i].r + px[i].g + px[i].b) / (3.0 * 255.0);
                sum += l; n++;
                if (l < 0.05) dark++;
                if (l > 0.98) blow++;
            }
            if (n == 0) return "（无几何像素）";
            if (useAlphaMask && (n > px.Length * 0.995))
                return $"mean={sum / n:F3} 死黑={dark * 100.0 / n:F1}% 过曝={blow * 100.0 / n:F1}%（alpha 未写，= 全画面）";
            return $"mean={sum / n:F3} 死黑={dark * 100.0 / n:F1}% 过曝={blow * 100.0 / n:F1}%";
        }

        /// <summary>出图时把 alpha 强制回不透明（QA 掩膜用 alpha，PNG 给人看不需要半透）。</summary>
        static byte[] EncodeOpaque(Texture2D tex)
        {
            var px = tex.GetPixels32();
            for (int i = 0; i < px.Length; i++) px[i].a = 255;
            var t2 = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
            t2.SetPixels32(px);
            t2.Apply();
            var bytes = t2.EncodeToPNG();
            Object.DestroyImmediate(t2);
            return bytes;
        }

        /// <summary>
        /// 取景要覆盖**分关装饰**：DEC_Details/S_0x 与 MAPSKIN_Details/S_0x 不是 BOX_S_0x 的子节点，
        /// 只按 BOX 根取景会把长管廊、月台、屋面设备全部裁到画外（S_04 的 650m 坡就是因此没进图）。
        /// </summary>
        static Bounds BlockBounds(UnityEngine.SceneManagement.Scene scene, string boxName)
        {
            string key = "S_" + boxName.Substring(boxName.Length - 2);
            var roots = new List<GameObject>();
            foreach (var g in scene.GetRootGameObjects())
            {
                if (g == null) continue;
                if (g.name == boxName) { roots.Add(g); continue; }
                if (g.name == "DEC_Details" || g.name == "MAPSKIN_Details")
                {
                    var c = g.transform.Find(key);
                    if (c != null) roots.Add(c.gameObject);
                }
            }
            return CollectBounds(roots);
        }

        static Bounds CollectBounds(IEnumerable<GameObject> roots)
        {
            bool any = false;
            var b = new Bounds();
            foreach (var root in roots)
            {
                if (root == null) continue;
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || !r.enabled) continue;
                    if (r.GetComponent<Camera>() != null) continue;
                    if (!any) { b = r.bounds; any = true; }
                    else b.Encapsulate(r.bounds);
                }
            }
            return b;
        }

        static void ParseArgs()
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-scene" && i + 1 < args.Length) ScenePath = args[i + 1];
                else if (args[i] == "-shotdir" && i + 1 < args.Length) ShotDir = args[i + 1];
                else if (args[i] == "-shotonly" && i + 1 < args.Length) Only = args[i + 1];
                else if (args[i] == "-shotclose" && i + 1 < args.Length) Closeups = args[i + 1] == "1";
                else if (args[i] == "-cam" && i + 1 < args.Length) CamSpec = args[i + 1];
                else if (args[i] == "-camfile" && i + 1 < args.Length) CamFile = args[i + 1];
                else if (args[i] == "-shotw" && i + 1 < args.Length) int.TryParse(args[i + 1], out W);
                else if (args[i] == "-shoth" && i + 1 < args.Length) int.TryParse(args[i + 1], out H);
            }
        }
    }
}
