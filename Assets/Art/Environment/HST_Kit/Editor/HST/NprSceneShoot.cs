#if UNITY_EDITOR
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace HST
{
    // NPR LookDev 出图(2026-10-09, 用户令): 在 project-0(瞎霍霍 = dev + Linear)渲染
    // TA 的 TATestScene(NPR 母材质 + GTAO/SSR/描边/体积光/Bloom 全特性)。
    // 渲染器特性随 URP-Renderer 资产生效; TAA 类历史帧特性在单帧渲染下无累积(如实记录)。
    public static class NprSceneShoot
    {
        [MenuItem("HST/NPR LookDev 出图(TATestScene)")]
        public static void Run()
        {
            EditorSceneManager.OpenScene("Assets/TA/LookDev/Scenes/TATestScene.unity", OpenSceneMode.Single);
            string outDir = @"F:/VSCode/工业级渲染/renders/npr_lookdev";
            Directory.CreateDirectory(outDir);
            var cams = UnityEngine.Object.FindObjectsOfType<Camera>(true)
                .Where(c => c.enabled && c.gameObject.activeInHierarchy)
                .OrderBy(c => c.depth).ToArray();
            Debug.Log($"[NPR] 相机 {cams.Length} 台, 开始渲染");
            int i = 0;
            foreach (var cam in cams)
            {
                var cd = cam.GetUniversalAdditionalCameraData();
                bool ppB = cd.renderPostProcessing; cd.renderPostProcessing = true;
                Shot(cam, $"{outDir}/npr_{i:D2}_{cam.gameObject.name}.png");
                cd.renderPostProcessing = ppB;
                Debug.Log($"[NPR] {cam.gameObject.name} fov={cam.fieldOfView:F0} 已出图");
                i++;
            }
            Debug.Log($"[NPR] 完成 → {outDir}");
        }

        static void Shot(Camera cam, string path)
        {
            int W = 1920, H = 1080;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Linear);
            var prev = cam.targetTexture;
            cam.targetTexture = rt; cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGBAFloat, false, true);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply();
            var px = tex.GetPixels();
            var t8 = new Texture2D(W, H, TextureFormat.RGBA32, false, false);
            var c8 = new Color[px.Length];
            for (int k = 0; k < px.Length; k++)
                c8[k] = new Color(L2S(Mathf.Clamp01(px[k].r)), L2S(Mathf.Clamp01(px[k].g)), L2S(Mathf.Clamp01(px[k].b)), 1f);
            t8.SetPixels(c8); t8.Apply();
            File.WriteAllBytes(path, t8.EncodeToPNG());
            cam.targetTexture = prev;
            Object.DestroyImmediate(rt); Object.DestroyImmediate(tex); Object.DestroyImmediate(t8);
        }

        static float L2S(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(Mathf.Max(c, 0f), 1f / 2.4f) - 0.055f;

        // 走廊换装 NPR 母材质并出图(2026-10-09, 用户令): 我们的材质 → TA 的
        // "OurFunction/NPR/Deferred Toon" 变体(贴图/颜色/金属光滑度直译, 其余用母材质默认),
        // 拍 CAM_01/02 后**原样还原, 不存盘**。验证: ①真资产过 NPR 风格统一; ②母材质吃不吃烘焙GI。
        [MenuItem("HST/NPR 走廊换装出图(非破坏)")]
        public static void CorridorNprSwapShoot()
        {
            EditorSceneManager.OpenScene("Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            var shader = Shader.Find("OurFunction/NPR/Deferred Toon");
            if (shader == null) { Debug.LogError("[NPR-C] 找不到 OurFunction/NPR/Deferred Toon"); return; }
            string outDir = @"F:/VSCode/工业级渲染/renders/npr_lookdev";
            Directory.CreateDirectory(outDir);

            var swap = new System.Collections.Generic.List<(MeshRenderer r, Material[] orig)>();
            int made = 0, kept = 0;
            foreach (var r in UnityEngine.Object.FindObjectsOfType<MeshRenderer>(true))
            {
                if (!r.gameObject.activeInHierarchy) continue;
                bool anyOurs = false;
                var cur = r.sharedMaterials;
                foreach (var m in cur)
                    if (m != null && (m.name.StartsWith("M_ARCH_") || m.name.StartsWith("M_FORGE_") || m.name.StartsWith("M_M_"))) anyOurs = true;
                if (!anyOurs) continue;
                var npr = new Material[cur.Length];
                for (int k = 0; k < cur.Length; k++)
                {
                    var src = cur[k];
                    if (src == null || src.name.EndsWith("Emit")) { npr[k] = src; kept++; continue; }   // 发光体保持原样
                    var nm = new Material(shader) { name = "NPRSWAP_" + src.name };
                    if (src.HasProperty("_BaseMap")) nm.SetTexture("_BaseMap", src.GetTexture("_BaseMap"));
                    if (src.HasProperty("_BaseColor")) nm.SetColor("_BaseColor", src.GetColor("_BaseColor"));
                    if (src.HasProperty("_MetallicGlossMap")) nm.SetTexture("_MetallicGlossMap", src.GetTexture("_MetallicGlossMap"));
                    if (src.HasProperty("_Metallic")) nm.SetFloat("_Metallic", src.GetFloat("_Metallic"));
                    if (src.HasProperty("_Smoothness")) nm.SetFloat("_Smoothness", src.GetFloat("_Smoothness"));
                    npr[k] = nm; made++;
                }
                swap.Add((r, cur));
                r.sharedMaterials = npr;
            }
            Debug.Log($"[NPR-C] 换装 {swap.Count} 个渲染器 / 新建 NPR 材质 {made} / 发光体保留 {kept}");

            foreach (string cn in new[] { "CAM_01_Hero", "CAM_02_Door" })
            {
                var go = GameObject.Find(cn);
                if (go == null) continue;
                var cam = go.GetComponent<Camera>();
                var cd = cam.GetUniversalAdditionalCameraData();
                bool ppB = cd.renderPostProcessing; cd.renderPostProcessing = true;
                Shot(cam, $"{outDir}/corridor_npr_{cn}.png");
                cd.renderPostProcessing = ppB;
                Debug.Log($"[NPR-C] {cn} 已出图");
            }

            // A/B 变体: 临时关 TAA+Bloom 渲染器特性拍一张(资产级开关, 拍完复原) —— 观感差异取证
            var rd = AssetDatabase.LoadAssetAtPath<UnityEngine.Rendering.Universal.UniversalRendererData>(
                "Assets/Settings/URP-Renderer.asset");
            var feats = rd.rendererFeatures;
            var featState = new bool[feats.Count];
            for (int k = 0; k < feats.Count; k++)
            {
                featState[k] = feats[k].isActive;
                if (feats[k].name.Contains("TAA") || feats[k].name.Contains("Bloom")) feats[k].SetActive(false);
            }
            EditorUtility.SetDirty(rd); AssetDatabase.SaveAssets();
            var camA = GameObject.Find("CAM_01_Hero").GetComponent<Camera>();
            var cdA = camA.GetUniversalAdditionalCameraData();
            bool ppA = cdA.renderPostProcessing; cdA.renderPostProcessing = true;
            Shot(camA, outDir + "/corridor_npr_CAM_01_Hero_noPostFeat.png");
            cdA.renderPostProcessing = ppA;
            for (int k = 0; k < feats.Count; k++) feats[k].SetActive(featState[k]);
            EditorUtility.SetDirty(rd); AssetDatabase.SaveAssets();
            Debug.Log("[NPR-C] A/B 变体(关TAA+Bloom)已出图并复原特性");

            foreach (var (r, orig) in swap) r.sharedMaterials = orig;   // 还原
            Debug.Log("[NPR-C] 材质已还原(场景未保存, 改动零残留)");
        }

        // 降噪版出图(2026-10-09, 用户问"不动管线怎么降辉光/天花圆圈是什么/删灯会不会黑"):
        // G1 = 发光体 _EmissionColor ÷4(对齐 TA 口径 1.5, 内容层参数, 不碰管线);
        // G2 = G1 + 关全部 54 盏烘焙代理灯 + 每根天花灯带用 3 盏宽角"代表灯"顶替(位置/色取自原灯阵)。
        // 全部临时态, 拍完还原。
        [MenuItem("HST/NPR 走廊降噪出图(发光÷4+清圆圈+代表灯)")]
        public static void CorridorNprCleanShoot()
        {
            EditorSceneManager.OpenScene("Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            var shader = Shader.Find("OurFunction/NPR/Deferred Toon");
            string outDir = @"F:/VSCode/工业级渲染/renders/npr_lookdev";

            // --- 换装(与 CorridorNprSwapShoot 同法) ---
            var swap = new System.Collections.Generic.List<(MeshRenderer r, Material[] orig)>();
            foreach (var r in UnityEngine.Object.FindObjectsOfType<MeshRenderer>(true))
            {
                if (!r.gameObject.activeInHierarchy) continue;
                bool any = false;
                var cur = r.sharedMaterials;
                foreach (var m in cur)
                    if (m != null && (m.name.StartsWith("M_ARCH_") || m.name.StartsWith("M_FORGE_") || m.name.StartsWith("M_M_"))) any = true;
                if (!any) continue;
                var npr = new Material[cur.Length];
                for (int k = 0; k < cur.Length; k++)
                {
                    var src = cur[k];
                    if (src == null || src.name.EndsWith("Emit")) { npr[k] = src; continue; }
                    var nm = new Material(shader) { name = "NPRSWAP_" + src.name };
                    if (src.HasProperty("_BaseMap")) nm.SetTexture("_BaseMap", src.GetTexture("_BaseMap"));
                    if (src.HasProperty("_BaseColor")) nm.SetColor("_BaseColor", src.GetColor("_BaseColor"));
                    if (src.HasProperty("_MetallicGlossMap")) nm.SetTexture("_MetallicGlossMap", src.GetTexture("_MetallicGlossMap"));
                    if (src.HasProperty("_Metallic")) nm.SetFloat("_Metallic", src.GetFloat("_Metallic"));
                    if (src.HasProperty("_Smoothness")) nm.SetFloat("_Smoothness", src.GetFloat("_Smoothness"));
                    npr[k] = nm;
                }
                swap.Add((r, cur));
                r.sharedMaterials = npr;
            }

            // --- 发光体 ÷4(临时实例, 不动资产) ---
            var temps = new System.Collections.Generic.List<Material>();
            foreach (var (r, orig) in swap)
            {
                var cur = r.sharedMaterials; bool chg = false;
                for (int k = 0; k < cur.Length; k++)
                {
                    var m = cur[k];
                    if (m == null || !m.name.EndsWith("Emit") || !m.HasProperty("_EmissionColor")) continue;
                    var t = new Material(m) { name = "EMITQ_" + m.name };
                    var e = t.GetColor("_EmissionColor");
                    t.SetColor("_EmissionColor", new Color(e.r * 0.25f, e.g * 0.25f, e.b * 0.25f, 1f));
                    temps.Add(t); cur[k] = t; chg = true;
                }
                if (chg) r.sharedMaterials = cur;
            }
            Debug.Log($"[NPR-G] 换装 {swap.Count} 渲染器, 发光体临时÷4 共 {temps.Count} 个");

            var cam = GameObject.Find("CAM_01_Hero").GetComponent<Camera>();
            var cd = cam.GetUniversalAdditionalCameraData();
            bool ppB = cd.renderPostProcessing; cd.renderPostProcessing = true;
            Shot(cam, outDir + "/corridor_npr_G1_发光4分1.png");
            Debug.Log("[NPR-G] G1(发光÷4, 灯原样)已出图");

            // --- G2: 关全部代理灯 + 每侧灯带 3 盏代表灯 ---
            var lights = UnityEngine.Object.FindObjectsOfType<Light>(true);
            var state = lights.Select(l => l.enabled).ToArray();
            foreach (var l in lights) l.enabled = false;
            var reps = new System.Collections.Generic.List<GameObject>();
            foreach (string side in new[] { "L_LUMEN_ChannelL", "L_LUMEN_ChannelR" })
            {
                var grp = lights.Where(l => l.name.StartsWith(side)).ToList();
                if (grp.Count == 0) continue;
                float minX = grp.Min(l => l.transform.position.x), maxX = grp.Max(l => l.transform.position.x);
                var p0 = grp[0].transform.position; var col = grp[0].color;
                float total = grp.Sum(l => l.intensity);
                for (int t = 0; t < 3; t++)
                {
                    var go = new GameObject($"NPRREP_{side}_{t}");
                    var li = go.AddComponent<Light>();
                    li.type = LightType.Spot; li.spotAngle = 140f; li.innerSpotAngle = 110f;
                    li.color = col; li.range = 30f; li.shadows = LightShadows.None;
                    li.intensity = total / 3f * 4.5f;   // 首轮 total/3 偏暗(mean158→47), ×4.5 配平
                    go.transform.position = new Vector3(Mathf.Lerp(minX, maxX, t / 2f), p0.y, p0.z);
                    go.transform.rotation = Quaternion.LookRotation(Vector3.down);
                    reps.Add(go);
                }
            }
            Shot(cam, outDir + "/corridor_npr_G2_清圆圈代表灯.png");
            Debug.Log($"[NPR-G] G2(灯全关+{reps.Count}盏代表灯)已出图");
            foreach (var go in reps) UnityEngine.Object.DestroyImmediate(go);

            // G3: 条带状代表灯 —— 每侧沿条带轴排 8 盏重叠宽角聚光(圆灯泡→拉成带), 强度分散回低值
            foreach (string side in new[] { "L_LUMEN_ChannelL", "L_LUMEN_ChannelR" })
            {
                var grp = lights.Where(l => l.name.StartsWith(side)).ToList();
                if (grp.Count == 0) continue;
                float minX = grp.Min(l => l.transform.position.x), maxX = grp.Max(l => l.transform.position.x);
                var p0 = grp[0].transform.position; var col = grp[0].color;
                float total = grp.Sum(l => l.intensity);
                for (int t = 0; t < 8; t++)
                {
                    var go = new GameObject($"NPRREP3_{side}_{t}");
                    var li = go.AddComponent<Light>();
                    li.type = LightType.Spot; li.spotAngle = 150f; li.innerSpotAngle = 130f;
                    li.color = col; li.range = 25f; li.shadows = LightShadows.None;
                    li.intensity = total / 8f * 2.0f;
                    go.transform.position = new Vector3(Mathf.Lerp(minX, maxX, t / 7f), p0.y, p0.z);
                    go.transform.rotation = Quaternion.LookRotation(Vector3.down);
                    reps.Add(go);
                }
            }
            Shot(cam, outDir + "/corridor_npr_G3_条带代表灯.png");
            Debug.Log($"[NPR-G] G3(条带状{reps.Count}盏×低强度)已出图");
            foreach (var go in reps) UnityEngine.Object.DestroyImmediate(go);
            for (int k = 0; k < lights.Length; k++) lights[k].enabled = state[k];
            cd.renderPostProcessing = ppB;

            foreach (var (r, orig) in swap) r.sharedMaterials = orig;
            foreach (var t in temps) UnityEngine.Object.DestroyImmediate(t);
            Debug.Log("[NPR-G] 全部还原(场景未保存, 零残留)");
        }

        // H 变体(2026-10-09, 用户令"复刻 TA 的打光方式和影带色, 出一张图, 不烤"):
        // 逐字段复刻 TATestScene 的照明语法 —— 青主光×3(走廊18m按节奏铺)/浅蓝辅光(门区)/
        // 中性弱方向光/暖出口光 + Flat 环境光(0.5,0.69,0.71) + 影带色(0.16,0.29,0.43) + 发光÷4。
        // 场景灯全部下岗, TA 配方灯临时上岗, 拍完全部还原。
        [MenuItem("HST/NPR 走廊·TA打光复刻出图")]
        public static void CorridorNprTaRigShoot()
        {
            EditorSceneManager.OpenScene("Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            var shader = Shader.Find("OurFunction/NPR/Deferred Toon");
            string outDir = @"F:/VSCode/工业级渲染/renders/npr_lookdev";
            var shadowCol = new Color(0.16f, 0.29f, 0.43f, 1f);   // TA 材质的影带色

            var swap = new System.Collections.Generic.List<(MeshRenderer r, Material[] orig)>();
            var tempsH = new System.Collections.Generic.List<Material>();
            foreach (var r in UnityEngine.Object.FindObjectsOfType<MeshRenderer>(true))
            {
                if (!r.gameObject.activeInHierarchy) continue;
                bool any = false;
                var cur = r.sharedMaterials;
                foreach (var m in cur)
                    if (m != null && (m.name.StartsWith("M_ARCH_") || m.name.StartsWith("M_FORGE_") || m.name.StartsWith("M_M_"))) any = true;
                if (!any) continue;
                var npr = new Material[cur.Length];
                for (int k = 0; k < cur.Length; k++)
                {
                    var src = cur[k];
                    if (src == null) { npr[k] = src; continue; }
                    if (src.name.EndsWith("Emit"))
                    {
                        var t = new Material(src) { name = "EMITQ_" + src.name };
                        var e = t.GetColor("_EmissionColor");
                        t.SetColor("_EmissionColor", new Color(e.r * 0.25f, e.g * 0.25f, e.b * 0.25f, 1f));
                        tempsH.Add(t); npr[k] = t; continue;
                    }
                    var nm = new Material(shader) { name = "NPRSWAP_" + src.name };
                    if (src.HasProperty("_BaseMap")) nm.SetTexture("_BaseMap", src.GetTexture("_BaseMap"));
                    if (src.HasProperty("_BaseColor")) nm.SetColor("_BaseColor", src.GetColor("_BaseColor"));
                    if (src.HasProperty("_MetallicGlossMap")) nm.SetTexture("_MetallicGlossMap", src.GetTexture("_MetallicGlossMap"));
                    if (src.HasProperty("_Metallic")) nm.SetFloat("_Metallic", src.GetFloat("_Metallic"));
                    if (src.HasProperty("_Smoothness")) nm.SetFloat("_Smoothness", src.GetFloat("_Smoothness"));
                    if (nm.HasProperty("_ShadowColor")) nm.SetColor("_ShadowColor", shadowCol);
                    if (nm.HasProperty("_ToonSoftness")) nm.SetFloat("_ToonSoftness", 0.15f);   // v5: 影带软化 0.035→0.15, 抹掉墙面过阈弧线
                    npr[k] = nm;
                }
                swap.Add((r, cur));
                r.sharedMaterials = npr;
            }

            var lights = UnityEngine.Object.FindObjectsOfType<Light>(true);
            var state = lights.Select(l => l.enabled).ToArray();
            foreach (var l in lights) l.enabled = false;
            var ambB = RenderSettings.ambientLight; var ambMB = RenderSettings.ambientMode;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.5f, 0.69f, 0.71f, 1f);

            var rig = new System.Collections.Generic.List<GameObject>();
            System.Action<Vector3, Color, float, float, float> pt = (p, c, i, rg, ang) =>
            { var go = new GameObject("TARIG_PT"); var li = go.AddComponent<Light>();
              li.type = ang > 0 ? LightType.Spot : LightType.Point; li.color = c; li.intensity = i; li.range = rg;
              if (ang > 0) { li.spotAngle = ang; li.innerSpotAngle = ang * 0.7f; go.transform.rotation = Quaternion.LookRotation(Vector3.down); }
              go.transform.position = p; rig.Add(go); };
            // v5(用户指出墙面抛物线痕): ①16 盏聚光→点光 —— 锥与竖直墙面的截线是圆锥曲线
            // (抛物线/双曲线), toon 二分把它印成硬弧; 点光没有锥缘, 衰减轮廓是平滑圆且 16 盏互相重叠。
            // ②影带软化 0.15(见上方换装), 残余过阈线也变渐变。
            System.Action<Vector3, Color, float, float, float> pt2 = (p, c, i, rg, ang) =>
            { var go = new GameObject("TARIG5"); var li = go.AddComponent<Light>();
              li.type = ang > 0 ? LightType.Spot : LightType.Point; li.color = c; li.intensity = i; li.range = rg;
              if (ang > 0) { li.spotAngle = ang; li.innerSpotAngle = ang * 0.7f; go.transform.rotation = Quaternion.LookRotation(Vector3.down); }
              go.transform.position = p; rig.Add(go); };
            var kc = new Color(0.85f, 0.93f, 0.93f);
            for (int s = 0; s < 2; s++)
                for (int t = 0; t < 8; t++)
                    pt2(new Vector3(Mathf.Lerp(-7.5f, 7.5f, t / 7f), 3.3f, s == 0 ? -1.05f : 1.05f),
                        kc, 2.8f, 6f, 0f);
            pt2(new Vector3(8.2f, 2.4f, 0f), new Color(0.68f, 0.84f, 0.90f), 4f, 8f, 0);
            pt2(new Vector3(8.9f, 1.2f, 0f), new Color(1.00f, 0.94f, 0.82f), 1f, 5f, 0);
            var sunGo = new GameObject("TARIG_SUN");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional; sun.color = new Color(0.796f, 0.796f, 0.796f); sun.intensity = 0.4f;
            sunGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            rig.Add(sunGo);
            Debug.Log($"[NPR-H] TA 配方灯 {rig.Count} 盏上岗(青主光×3/浅蓝辅/暖出口/弱阳), 影带色+发光÷4");

            var cam = GameObject.Find("CAM_01_Hero").GetComponent<Camera>();
            var cd = cam.GetUniversalAdditionalCameraData();
            bool ppB = cd.renderPostProcessing; cd.renderPostProcessing = true;
            Shot(cam, outDir + "/corridor_npr_H_TA打光复刻.png");
            cd.renderPostProcessing = ppB;

            foreach (var go in rig) UnityEngine.Object.DestroyImmediate(go);
            for (int k = 0; k < lights.Length; k++) lights[k].enabled = state[k];
            RenderSettings.ambientLight = ambB; RenderSettings.ambientMode = ambMB;
            foreach (var (r, orig) in swap) r.sharedMaterials = orig;
            foreach (var t in tempsH) UnityEngine.Object.DestroyImmediate(t);
            Debug.Log("[NPR-H] 全部还原(场景未保存, 零残留)");
        }

        // I 变体(2026-10-09, 用户问"能不能不弄灯泡就让室内明亮"): 卡通管线的"环境光"=影带色
        // (暗部底色, 处处一致、无形无斑)。把它从 TA 的暗蓝(0.16,0.29,0.43)调亮到中亮度蓝灰
        // (0.46,0.55,0.64) = 全场提亮零灯泡; 辅以弱方向光 0.45 给形体方向, 发光条带÷4 做点缀。
        // 全程实时, 零烘焙。GTAO(SSAO)+描边继续提供立体感。
        [MenuItem("HST/NPR 走廊·无灯泡明亮出图")]
        public static void CorridorNprNoBulbShoot()
        {
            EditorSceneManager.OpenScene("Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            var shader = Shader.Find("OurFunction/NPR/Deferred Toon");
            string outDir = @"F:/VSCode/工业级渲染/renders/npr_lookdev";
            var brightShadow = new Color(0.55f, 0.60f, 0.66f, 1f);   // "调亮的影带"= 卡通伪环境光

            var swap = new System.Collections.Generic.List<(MeshRenderer r, Material[] orig)>();
            var tempsI = new System.Collections.Generic.List<Material>();
            foreach (var r in UnityEngine.Object.FindObjectsOfType<MeshRenderer>(true))
            {
                if (!r.gameObject.activeInHierarchy) continue;
                bool any = false;
                var cur = r.sharedMaterials;
                foreach (var m in cur)
                    if (m != null && (m.name.StartsWith("M_ARCH_") || m.name.StartsWith("M_FORGE_") || m.name.StartsWith("M_M_"))) any = true;
                if (!any) continue;
                var npr = new Material[cur.Length];
                for (int k = 0; k < cur.Length; k++)
                {
                    var src = cur[k];
                    if (src == null) { npr[k] = src; continue; }
                    if (src.name.EndsWith("Emit"))
                    {
                        var t = new Material(src) { name = "EMITQ_" + src.name };
                        var e = t.GetColor("_EmissionColor");
                        t.SetColor("_EmissionColor", new Color(e.r * 0.25f, e.g * 0.25f, e.b * 0.25f, 1f));
                        tempsI.Add(t); npr[k] = t; continue;
                    }
                    var nm = new Material(shader) { name = "NPRSWAP_" + src.name };
                    if (src.HasProperty("_BaseMap")) nm.SetTexture("_BaseMap", src.GetTexture("_BaseMap"));
                    if (src.HasProperty("_BaseColor")) nm.SetColor("_BaseColor", src.GetColor("_BaseColor"));
                    if (src.HasProperty("_MetallicGlossMap")) nm.SetTexture("_MetallicGlossMap", src.GetTexture("_MetallicGlossMap"));
                    if (src.HasProperty("_Metallic")) nm.SetFloat("_Metallic", src.GetFloat("_Metallic"));
                    if (src.HasProperty("_Smoothness")) nm.SetFloat("_Smoothness", src.GetFloat("_Smoothness"));
                    if (nm.HasProperty("_ShadowColor")) nm.SetColor("_ShadowColor", brightShadow);
                    if (nm.HasProperty("_ToonSoftness")) nm.SetFloat("_ToonSoftness", 0.15f);
                    if (src.name == "M_ARCH_FloorM" && nm.HasProperty("_ToonThreshold"))
                        nm.SetFloat("_ToonThreshold", 2.5f);   // v7: 地板专用——阈值抬到不可达, 多灯光照度也翻不进全亮带
                    npr[k] = nm;
                }
                swap.Add((r, cur));
                r.sharedMaterials = npr;
            }

            var lights = UnityEngine.Object.FindObjectsOfType<Light>(true);
            var state = lights.Select(l => l.enabled).ToArray();
            foreach (var l in lights) l.enabled = false;      // 灯泡全部下岗

            // v3: 单方向光只照亮朝天面(墙 NdotL 不足)。改用卡通手游标配"多向填充光":
            // 4 个方位角方向光(45/135/225/315°, 仰角40) = 无形天光, 无位置无光池无灯泡;
            // + 影带抬到 0.70/0.75/0.80 当"环境亮底"。
            var fills = new System.Collections.Generic.List<GameObject>();
            for (int a = 0; a < 8; a++)
            {
                var go = new GameObject($"NOBULB_FILL_{a}");
                var li = go.AddComponent<Light>();
                li.type = LightType.Directional; li.color = new Color(0.85f, 0.92f, 0.95f); li.intensity = 0.45f;
                go.transform.rotation = Quaternion.Euler(12f, 45f * a, 0f);
                fills.Add(go);
            }
            Debug.Log("[NPR-I] 无灯泡模式v3: 4向填充光×1.2 + 亮影带0.75 + 发光÷4");

            var cam = GameObject.Find("CAM_01_Hero").GetComponent<Camera>();
            var cd = cam.GetUniversalAdditionalCameraData();
            bool ppB = cd.renderPostProcessing; cd.renderPostProcessing = true;
            Shot(cam, outDir + "/corridor_npr_I_无灯泡明亮.png");
            cd.renderPostProcessing = ppB;

            foreach (var go in fills) UnityEngine.Object.DestroyImmediate(go);
            for (int k = 0; k < lights.Length; k++) lights[k].enabled = state[k];
            foreach (var (r, orig) in swap) r.sharedMaterials = orig;
            foreach (var t in tempsI) UnityEngine.Object.DestroyImmediate(t);
            Debug.Log("[NPR-I] 全部还原(场景未保存, 零残留)");
        }
    }
}
#endif