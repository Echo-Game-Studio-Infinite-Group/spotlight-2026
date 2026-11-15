using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// v6: 材质冲洗——四个场景各自的材质逐个渲成 256px 材质球, 供 Python 拼材质表。
// 棚拍环境: 固定主光+补光+中性环境光, 不开后期, 排除场景自带灯光干扰。
public static class SceneStillRender
{
    static readonly string OutDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "..", "_render_stills");
    static readonly string[] Scenes = {
        "Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_Pragmata.unity",
        "Assets/Art/Environment/HST_Kit/Scenes/SC_CityFacade_LookDev.unity",
        "Assets/Art/Environment/HST_Kit/Scenes/SC_Atrium_Concept.unity",
        "Assets/Art/Environment/HST_Kit/Scenes/SC_Atrium_Military.unity",
    };

    public static void RenderSwatches()
    {
        Directory.CreateDirectory(OutDir);
        var studio = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.45f, 0.45f, 0.48f);
        RenderSettings.skybox = null;

        var rig = new GameObject("Rig"); rig.transform.position = new Vector3(0f, 5000f, 0f); // 高空棚拍, 避开场景几何
        var sunGO = new GameObject("Sun");
        var sun = sunGO.AddComponent<Light>();
        sun.type = LightType.Directional; sun.intensity = 1.15f; sun.shadows = LightShadows.None;
        sunGO.transform.rotation = Quaternion.Euler(45f, -35f, 0f);
        var fillGO = new GameObject("Fill");
        var fill = fillGO.AddComponent<Light>();
        fill.type = LightType.Directional; fill.intensity = 0.35f; fill.shadows = LightShadows.None;
        fillGO.transform.rotation = Quaternion.Euler(30f, 145f, 0f);

        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad); // 平面贴图式, 不用球
        var quadRenderer = quad.GetComponent<MeshRenderer>();
        var camGO = new GameObject("CamGO");
        var cam = camGO.AddComponent<Camera>();
        quad.transform.position = new Vector3(0f, 5000f, 0f);
        cam.transform.position = new Vector3(0f, 5000f, -1.18f);
        cam.transform.LookAt(new Vector3(0f, 5000f, 0f));
        cam.fieldOfView = 44f; cam.nearClipPlane = 0.05f; cam.farClipPlane = 50f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.16f, 0.17f, 0.19f, 1f);
        const int S = 256;

        foreach (var scenePath in Scenes)
        {
            var target = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            string tag = Path.GetFileNameWithoutExtension(scenePath);
            foreach (var l in Object.FindObjectsOfType<Light>())
                if (l.gameObject.scene == target) l.enabled = false; // 棚拍只留棚灯

            var mats = new HashSet<Material>();
            foreach (var r in Object.FindObjectsOfType<Renderer>(false))
                if (r.gameObject.scene == target && r.enabled)
                    foreach (var m in r.sharedMaterials) if (m != null) mats.Add(m);
            var sorted = mats.OrderBy(m => m.name).ToArray();
            Debug.Log($"[MATLIST] BEGIN {tag} count={sorted.Length}");
            foreach (var m in sorted)
            {
                Debug.Log($"[MATLIST] {tag}|{m.name}|{m.shader.name}");
                quadRenderer.sharedMaterial = m;
                File.WriteAllBytes(Path.Combine(OutDir, $"SW_{tag}_{m.name}.png"), Snap(cam, S));
            }
            EditorSceneManager.CloseScene(target, true);
            Debug.Log($"[MATLIST] END {tag}");
        }
        Debug.Log("[MATLIST] ALL DONE");
    }

    // v8: 材质合并后验证——重渲走廊与Concept的固定机位, 与合并前逐像素比对。
    // v9: 量墙面/地面的世界尺寸(AABB), 供贴图真实世界标定
    public static void MeasureFaces()
    {
        var scene = EditorSceneManager.OpenScene("Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
        foreach (var r in Object.FindObjectsOfType<Renderer>())
        {
            var mats = r.sharedMaterials;
            bool interesting = mats.Any(m => m != null && (m.name.StartsWith("M_ARCH") || m.name.StartsWith("M_HS")));
            if (!interesting) continue;
            var b = r.bounds;
            Debug.Log($"[FACE] {r.gameObject.name}|{string.Join("+", mats.Select(m => m.name))}|size={b.size.x:F3}x{b.size.y:F3}x{b.size.z:F3}|center={b.center.x:F2},{b.center.y:F2},{b.center.z:F2}");
        }
        Debug.Log("[FACE] DONE");
    }

    // v11: 回滚 CalibrateMaterials 的一切场景改动, 恢复标准材质口径
    public static void RestoreStandardMaterials()
    {
        var scene = EditorSceneManager.OpenScene("Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
        var floorM = FindMat("M_ARCH_FloorM");
        var wallM = FindMat("M_ARCH_WallM");
        var bandM = FindMat("M_ARCH_WallM_Band");
        floorM.SetTextureScale("_BaseMap", new Vector2(1f, 1f));
        floorM.SetTextureOffset("_BaseMap", Vector2.zero);
        bandM.SetTextureScale("_BaseMap", new Vector2(1f, 1f));
        bandM.SetTextureOffset("_BaseMap", Vector2.zero);
        EditorUtility.SetDirty(floorM); EditorUtility.SetDirty(bandM);
        int nF = 0, nC = 0, nP = 0;
        foreach (var r in Object.FindObjectsOfType<Renderer>())
        {
            var nm = r.gameObject.name;
            if (nm.StartsWith("ARCH_Floor_")) { r.sharedMaterials = new[] { floorM }; nF++; }
            else if (nm.StartsWith("ARCH_CeilBand")) { r.sharedMaterials = new[] { wallM }; nC++; }
            else if (nm.StartsWith("ARCH_Panel_"))
                r.sharedMaterials = new[] { nm.Contains("band") ? bandM : wallM };
            if (nm.StartsWith("ARCH_Panel_")) nP++;
        }
        Debug.Log($"[RESTORE] 地板{nF} 顶带{nC} 墙板{nP} 已还原标准材质");
        AssetDatabase.SaveAssets();
        EditorSceneManager.SaveScene(scene);
        // 删除标定期创建的克隆材质
        foreach (var g in AssetDatabase.FindAssets("", new[] { "Assets/Art/Environment/HST_Kit/Materials/HST_Prags" }))
        {
            var p = AssetDatabase.GUIDToAssetPath(g);
            var mn = System.IO.Path.GetFileNameWithoutExtension(p);
            if (mn.StartsWith("M_ARCH_FloorM_O") || mn == "M_ARCH_WallM_CeilBand"
                || mn == "M_ARCH_WallM_B" || mn == "M_ARCH_WallM_Band_B")
                AssetDatabase.DeleteAsset(p);
        }
        Debug.Log("[RESTORE] DONE 克隆材质已删除, 场景已保存");
    }

    // v10: 材质标定 —— 地板砖缝对齐墙线/中线(自校准 UV 拟合), 上带/顶带裁切, 墙板 A/B 交替
    static Material FindMat(string name)
    {
        var guids = AssetDatabase.FindAssets($"{name} t:Material");
        foreach (var g in guids)
        {
            var p = AssetDatabase.GUIDToAssetPath(g);
            var m = AssetDatabase.LoadAssetAtPath<Material>(p);
            if (m != null && m.name == name) return m;
        }
        return null;
    }

    static Material CloneMat(Material src, string newName)
    {
        var existing = FindMat(newName);
        if (existing != null) return existing;
        var m = Object.Instantiate(src);
        m.name = newName;
        AssetDatabase.CreateAsset(m, $"Assets/Art/Environment/HST_Kit/Materials/HST_Prags/{newName}.mat");
        return m;
    }

    // 把材质里所有引用 T_HS_PanelWall_White 的贴图换成 _B 变体(shader 无关, 按资产名匹配)
    static void SwapToB(Material m)
    {
        var names = m.GetTexturePropertyNames();
        foreach (var prop in names)
        {
            var t = m.GetTexture(prop) as Texture2D;
            if (t == null || !t.name.StartsWith("T_HS_PanelWall_White")) continue;
            var guid = AssetDatabase.FindAssets($"{t.name}_B t:Texture2D");
            foreach (var g in guid)
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                var nt = AssetDatabase.LoadAssetAtPath<Texture2D>(p);
                if (nt != null && nt.name == t.name + "_B") { m.SetTexture(prop, nt); break; }
            }
        }
    }

    public static void CalibrateMaterials()
    {
        var scene = EditorSceneManager.OpenScene("Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
        AssetDatabase.Refresh();

        // ---- ① 地板: tiling.x=4(4.6m/图), 每条地板条按 UV 拟合求 offset 让墙线落在砖缝上 ----
        // 砖缝在纹理 V 的 {0, 0.4146, 0.8292}(=物理 {0,1.99,3.98}m); 墙线 z=±3.98 → uv 0.8292 间距
        var floorM = FindMat("M_ARCH_FloorM");
        floorM.SetTextureScale("_BaseMap", new Vector2(4f, 1f));
        floorM.SetTextureOffset("_BaseMap", new Vector2(0f, 0f));
        EditorUtility.SetDirty(floorM);
        var offGroups = new Dictionary<string, List<Renderer>>();
        foreach (var r in Object.FindObjectsOfType<Renderer>())
        {
            if (!r.gameObject.name.StartsWith("ARCH_Floor_")) continue;
            var mf = r.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) continue;
            var uv = mf.sharedMesh.uv;
            var verts = mf.sharedMesh.vertices;
            if (uv == null || uv.Length == 0) continue;
            // 最小二乘拟合 uv.v = a*worldZ + b
            double sz = 0, szz = 0, sv = 0, szv = 0; int n = verts.Length;
            for (int i = 0; i < n; i++)
            {
                double z = r.transform.TransformPoint(verts[i]).z;
                sz += z; szz += z * z; sv += uv[i].y; szv += z * uv[i].y;
            }
            double a = (n * szv - sz * sv) / (n * szz - sz * sz);
            double b = (sv - a * sz) / n;
            // 已修坑: 本工程地板 UV 是中心居中制(v=0.5 在走道中线, 不是 0..1)。
            // 让可见窗 [中线, 墙线] 恰好铺满纹理的砖区 [0, 0.8292](避开 0.8292..1 的墙下备用带):
            //   XP 条(z>0, t(0)=0.5+off=0.8292) → off=0.3292; XN 条(t(0)=off-0.5=0) → off=0.5。
            double off = r.gameObject.name.Contains("_XN") ? 0.5 : 0.3292;
            double uvWall = a * (r.bounds.center.z >= 0 ? 3.98 : -3.98) + b;
            double centerCheck = (((b) + off) % 1 + 1) % 1;
            string key = off.ToString("F4");
            if (!offGroups.ContainsKey(key)) offGroups[key] = new List<Renderer>();
            offGroups[key].Add(r);
            Debug.Log($"[CAL] {r.gameObject.name} uv(z)={a:F5}z+{b:F4} uvWall={uvWall:F4} → offset.y={off:F4} 中线tex={centerCheck:F3}(砖缝)");
        }
        int gi = 0;
        foreach (var kv in offGroups)
        {
            Material m;
            if (gi == 0 && Mathf.Approximately((float)double.Parse(kv.Key), 0f))
            {
                m = floorM; // 主材质自身承担 offset 0
            }
            else
            {
                m = CloneMat(floorM, $"M_ARCH_FloorM_O{kv.Key.Replace(".", "").Replace("-", "n")}");
                m.SetTextureOffset("_BaseMap", new Vector2(0f, (float)double.Parse(kv.Key)));
                EditorUtility.SetDirty(m);
            }
            foreach (var r in kv.Value) r.sharedMaterials = new[] { m };
            gi++;
        }

        // ---- ② 上带板: 裁本图顶部 1.792m(tiling.y=1.792/3.4) ----
        var bandM = FindMat("M_ARCH_WallM_Band");
        bandM.SetTextureScale("_BaseMap", new Vector2(1f, 0.5271f));
        bandM.SetTextureOffset("_BaseMap", new Vector2(0f, 0.4729f));
        EditorUtility.SetDirty(bandM);

        // ---- ③ 顶带: 克隆 WallM, 顶部 0.98m 条 × 10.27 板距 ----
        var ceilM = CloneMat(FindMat("M_ARCH_WallM"), "M_ARCH_WallM_CeilBand");
        ceilM.SetTextureScale("_BaseMap", new Vector2(10.268f, 0.288f));
        ceilM.SetTextureOffset("_BaseMap", new Vector2(0f, 0.712f));
        EditorUtility.SetDirty(ceilM);
        foreach (var r in Object.FindObjectsOfType<Renderer>())
            if (r.gameObject.name.StartsWith("ARCH_CeilBand"))
                r.sharedMaterials = new[] { ceilM };

        // ---- ④ 墙板 A/B 交替(主板与上带板按同一 x 奇偶, 保证上下列对齐) ----
        var wallM = FindMat("M_ARCH_WallM");
        var wallB = CloneMat(wallM, "M_ARCH_WallM_B"); SwapToB(wallB); EditorUtility.SetDirty(wallB);
        var bandB = CloneMat(bandM, "M_ARCH_WallM_Band_B"); SwapToB(bandB); EditorUtility.SetDirty(bandB);
        int nA = 0, nB = 0;
        foreach (var r in Object.FindObjectsOfType<Renderer>())
        {
            var nm = r.gameObject.name;
            bool isPanel = nm.StartsWith("ARCH_Panel_") && !r.sharedMaterials.Any(m => m != null && m.name.StartsWith("M_ARCH_Floor"));
            if (!isPanel) continue;
            bool isBand = nm.Contains("band");
            int parity = Mathf.RoundToInt(r.bounds.center.x / 1.8f) & 1;
            if (parity == 1)
            {
                r.sharedMaterials = new[] { isBand ? bandB : wallB };
                nB++;
            }
            else nA++;
        }
        Debug.Log($"[CAL] 墙板 A={nA} B={nB}");

        AssetDatabase.SaveAssets();
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[CAL] DONE 已保存场景");
    }

    public static void RunMergeVerify()
    {
        Directory.CreateDirectory(OutDir);
        const int w = 1920, h = 1080;
        foreach (var path in new[] { "Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_Pragmata.unity", "Assets/Art/Environment/HST_Kit/Scenes/SC_Atrium_Concept.unity" })
        {
            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            try { var ls = UnityEditor.Lightmapping.lightingSettings; if (ls != null) ls.autoGenerate = false; }
            catch (System.Exception) { }
            string tag = Path.GetFileNameWithoutExtension(path).Replace("SC_Corridor_Pragmata", "CORR2").Replace("SC_Atrium_Concept", "CONC2");
            foreach (var src in Object.FindObjectsOfType<Camera>(true).Where(c => c.name.StartsWith("CAM_")).OrderBy(c => c.name))
            {
                var go = new GameObject("V_" + src.name);
                var cam = go.AddComponent<Camera>();
                cam.CopyFrom(src);
                cam.nearClipPlane = 0.05f; cam.farClipPlane = 3000f;
                var cd = go.AddComponent<UniversalAdditionalCameraData>(); cd.renderPostProcessing = true;
                go.transform.position = src.transform.position;
                go.transform.rotation = src.transform.rotation;
                Snap(cam, w, h, Path.Combine(OutDir, $"{tag}_{src.name}.png"));
            }
        }
        Debug.Log("[VERIFY] ALL DONE");
    }

    static void Snap(Camera cam, int w, int h, string file)
    {
        var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
        cam.targetTexture = rt; cam.aspect = (float)w / h; cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, w, h), 0, 0); tex.Apply();
        RenderTexture.active = null;
        var bytes = tex.EncodeToPNG();
        File.WriteAllBytes(file, bytes);
        Object.DestroyImmediate(tex);
        cam.targetTexture = null; rt.Release(); Object.DestroyImmediate(rt);
        Debug.Log($"[VERIFY] ok {Path.GetFileName(file)} bytes={bytes.Length}");
    }

    static byte[] Snap(Camera cam, int size)
    {
        var rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32);
        cam.targetTexture = rt; cam.aspect = 1f; cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(size, size, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, size, size), 0, 0); tex.Apply();
        RenderTexture.active = null;
        cam.targetTexture = null; rt.Release(); Object.DestroyImmediate(rt);
        var bytes = tex.EncodeToPNG();
        Object.DestroyImmediate(tex);
        return bytes;
    }
}
