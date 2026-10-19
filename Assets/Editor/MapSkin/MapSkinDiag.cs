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
    /// 换装/打光后的三态隔离诊断（图形模式跑）：
    ///   A = 现状（NPR 母材质 Lit tag 版）
    ///   B = 同材质临时换成 URP/Lit（内存里换，**不写盘**）——若 B 亮 A 黑，问题在 NPR 材质链路；若都黑，问题在灯/环境光
    ///   C = A 的多帧累积（5 帧）——排除 TAA 收敛不足
    /// 同时打印材质/渲染器属性实况（shader 名、Lit tag 实际值、贴图、环境光、延迟模式、渲染器特性清单）。
    ///
    /// 菜单：MapSkin/诊断/三态隔离出图(A现状 / B换URP-Lit / C多帧)
    /// 命令行：-executeMethod MapSkin.MapSkinDiag.Run
    /// </summary>
    public static class MapSkinDiag
    {
        const string ScenePath = "Assets/Scenes/Level1.unity";
        static string ShotDir = "F:/VSCode/taptap-game-jem/_render_skin/_diag";
        const string UrpLit = "Universal Render Pipeline/Lit";

        [MenuItem("MapSkin/诊断/三态隔离出图(A现状 / B换URP-Lit / C多帧)")]
        public static void Run()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Directory.CreateDirectory(ShotDir);

            // ---------- 属性实况 ----------
            var rep = new StringBuilder();
            rep.AppendLine($"[诊断] 环境光 Mode={RenderSettings.ambientMode} Color={RenderSettings.ambientLight} " +
                           $"intensity={RenderSettings.ambientIntensity} 天空盒={(RenderSettings.skybox ? RenderSettings.skybox.name : "null")}");
            var rp = QualitySettings.renderPipeline as UniversalRenderPipelineAsset;
            rep.AppendLine($"[诊断] URP 资产={rp?.name ?? "null"}({rp?.GetType().Name})");
            var rdGuid = AssetDatabase.FindAssets("t:ScriptableRendererData").FirstOrDefault();
            var rd = rdGuid != null
                ? AssetDatabase.LoadAssetAtPath<UnityEngine.Rendering.Universal.ScriptableRendererData>(AssetDatabase.GUIDToAssetPath(rdGuid))
                : null;
            if (rd != null)
            {
                var so2 = new SerializedObject(rd);
                var mode = so2.FindProperty("m_RenderingMode");
                rep.AppendLine($"[诊断] Renderer={rd.name} m_RenderingMode={mode?.intValue}（0=Forward 1=Deferred 2=DeferredPlus）");
                var feats = so2.FindProperty("m_RendererFeatures");
                if (feats != null && feats.isArray)
                {
                    rep.Append("[诊断] 渲染器特性: ");
                    for (int i = 0; i < feats.arraySize; i++)
                        rep.Append((feats.GetArrayElementAtIndex(i).objectReferenceValue?.name ?? "?") + " ");
                    rep.AppendLine();
                }
            }

            var probes = new[] { "Material.014", "Material.005", "Material.015", "Material.038" };
            foreach (var n in probes)
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>($"Assets/Art/Models/MikuGandam/materials/{n}.mat");
                if (m == null) { rep.AppendLine($"[诊断] {n} 缺失"); continue; }
                rep.AppendLine($"[诊断] {n}: shader={m.shader.name} Lit tag={m.GetTag("UniversalMaterialType", true, "?")} " +
                               $"_BaseColor={m.GetColor("_BaseColor")} baseMap={(m.GetTexture("_BaseMap") ? m.GetTexture("_BaseMap").name : "null")} " +
                               $"scale={m.GetTextureScale("_BaseMap")} kw=[{string.Join(",", m.shaderKeywords)}]");
            }
            Debug.Log(rep.ToString());

            var camGo = new GameObject("__MapSkinDiagCam");
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.22f, 0.24f, 0.27f);
            var extra = cam.GetUniversalAdditionalCameraData();
            if (extra != null) { extra.renderPostProcessing = true; extra.renderShadows = true; }

            var root = scene.GetRootGameObjects().FirstOrDefault(g => g.name == "BOX_S_01");
            var b = Bounds(root);
            Frame(cam, b);

            Shoot(cam, "A_npr_2f", 2);
            Shoot(cam, "C_npr_5f", 5);

            // B: 内存里把语义表材质换成 URP/Lit（不写盘、不 SaveAssets）
            var lit = Shader.Find(UrpLit);
            int changed = 0;
            foreach (var m in FamilyMats())
            {
                if (m == null) continue;
                if (m.parent != null) continue;          // variant 的 shader 改不了，跳过
                m.shader = lit;
                changed++;
            }
            Debug.Log($"[诊断] B 态：{changed} 个基底材质在内存里换成 URP/Lit（未写盘）");
            Shoot(cam, "B_urplit_2f", 2);

            Object.DestroyImmediate(camGo);
            Debug.Log("[诊断] 出图 → " + ShotDir + "（场景未保存）");
        }

        static IEnumerable<Material> FamilyMats()
        {
            foreach (var n in MapSkinApply.SkinMatBaseNames)
                yield return AssetDatabase.LoadAssetAtPath<Material>($"Assets/Art/Models/MikuGandam/materials/{n}.mat");
        }

        /// <summary>亮度配方探针：逐档加环境光/主光、以及关 GTAO 对读数的影响（内存里改，不写盘）。
        /// -executeMethod MapSkin.MapSkinDiag.TuneLighting</summary>
        public static void TuneLighting()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Directory.CreateDirectory(ShotDir);

            var camGo = new GameObject("__MapSkinTuneCam");
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.22f, 0.24f, 0.27f);
            var extra = cam.GetUniversalAdditionalCameraData();
            if (extra != null) { extra.renderPostProcessing = true; extra.renderShadows = true; }
            Frame(cam, Bounds(scene.GetRootGameObjects().FirstOrDefault(g => g.name == "BOX_S_01")));

            var baseAmb = RenderSettings.ambientLight;
            var key = Object.FindObjectsOfType<Light>().FirstOrDefault(l => l.type == LightType.Directional && l.enabled);

            Shoot(cam, "L0_现状", 2);

            RenderSettings.ambientLight = new Color(1f, 1f, 1f);
            Shoot(cam, "L1_环境光满值", 2);

            if (key != null) { key.intensity = 2f; key.color = Color.white; }
            Shoot(cam, "L2_环境光满+主光2.0白", 2);

            // 关 GTAO：验证"环境光被 AO 乘掉"假设
            var rd = AssetDatabase.LoadAssetAtPath<UnityEngine.Rendering.Universal.ScriptableRendererData>(
                AssetDatabase.GUIDToAssetPath(AssetDatabase.FindAssets("t:ScriptableRendererData").First()));
            string gtName = "?";
            if (rd != null)
            {
                var so = new SerializedObject(rd);
                var feats = so.FindProperty("m_RendererFeatures");
                for (int i = 0; i < feats.arraySize; i++)
                {
                    var f = feats.GetArrayElementAtIndex(i).objectReferenceValue;
                    if (f == null || !f.name.ToLower().Contains("gtao")) continue;
                    gtName = f.name;
                    var fso = new SerializedObject(f);
                    var act = fso.FindProperty("m_Active");
                    if (act != null) { act.boolValue = false; fso.ApplyModifiedPropertiesWithoutUndo(); }
                }
            }
            Debug.Log($"[诊断] 已临时关闭 {gtName}（内存，不写盘）");
            Shoot(cam, "L3_环境光满+主光2.0+关GTAO", 2);

            // 还原 GTAO 开关：SerializedObject 改资产会把资产标脏，编辑器退出时可能落盘 ——
            // 不还原就会把 TA 的 9 个渲染器特性之一永久关掉（实测踩过：URP-Renderer.asset m_Active 1→0）
            if (rd != null)
            {
                var so = new SerializedObject(rd);
                var feats = so.FindProperty("m_RendererFeatures");
                for (int i = 0; i < feats.arraySize; i++)
                {
                    var f = feats.GetArrayElementAtIndex(i).objectReferenceValue;
                    if (f == null || !f.name.ToLower().Contains("gtao")) continue;
                    var fso = new SerializedObject(f);
                    var act = fso.FindProperty("m_Active");
                    if (act != null) { act.boolValue = true; fso.ApplyModifiedPropertiesWithoutUndo(); }
                }
                Debug.Log($"[诊断] {gtName} 开关已还原 = true（探针只做隔离读数，不留副作用）");
            }

            Object.DestroyImmediate(camGo);
            Debug.Log("[诊断] 配方探针出图 → " + ShotDir + "（场景未保存）");
        }

        static Bounds Bounds(GameObject root)
        {
            bool any = false; var b = new Bounds();
            if (root == null) return b;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || !r.enabled) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            return b;
        }

        static void Frame(Camera cam, Bounds b)
        {
            var dir = new Vector3(0.5f, 0.70710678f, 0.5f).normalized;
            float span = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
            float dist = span * 2.5f + 100f;
            cam.transform.position = b.center + dir * dist;
            cam.transform.LookAt(b.center, Vector3.up);
            float up = 0.7071f * b.size.y + 0.3536f * (b.size.x + b.size.z);
            cam.orthographicSize = Mathf.Max(up * 0.5f * 1.15f, 1f);
            cam.farClipPlane = dist * 3f;
        }

        static void Shoot(Camera cam, string name, int frames)
        {
            const int W = 1920, H = 1080;
            var rt = new RenderTexture(W, H, 24);
            cam.targetTexture = rt;
            for (int i = 0; i < frames; i++) cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            cam.targetTexture = null;
            // 亮度统计
            var px = tex.GetPixels32();
            double sum = 0; int dark = 0;
            for (int i = 0; i < px.Length; i++)
            {
                double l = (px[i].r + px[i].g + px[i].b) / (3.0 * 255.0);
                sum += l; if (l < 0.05) dark++;
            }
            Debug.Log($"[诊断] {name}: mean={sum / px.Length:F3} 死黑={dark * 100.0 / px.Length:F1}%");
            File.WriteAllBytes(Path.Combine(ShotDir, name + ".png"), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            rt.Release();
            Object.DestroyImmediate(rt);
        }
    }
}
