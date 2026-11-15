// HST 场景翻译器 —— 把 Blender 侧 manifest_* 逐字段复刻成 Unity 场景, 并数值回读校验。
// 菜单: HST/Build Both Scenes(建+存+校验) | HST/Verify Only(只校验)
// 坐标合同见 scripts/scene/export_unity.py: Unity = C·Blender, u=(x, z, -y)。
// 校验输出: F:/VSCode/工业级渲染/tmp/unity_export/verify_<tag>.csv (供 diff_manifests.py 对拍)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace HST
{
    public static class SceneBuilder
    {
        const string Root = "Assets/Art/Environment/HST_Kit/Manifests/HST_Prags";
        const string TexDir = "Assets/Art/Environment/HST_Kit/Textures/HST_Prags";
        const string MatDir = "Assets/Art/Environment/HST_Kit/Materials/HST_Prags";
        const string ModelDir = "Assets/Art/Environment/HST_Kit/Models/HST_Prags";
        const string SceneDir = "Assets/Scenes";
        const string VerifyOut = @"F:\VSCode\工业级渲染\tmp\unity_export";

        [Serializable] class MObj { public string name; public string mat; public float[] aabb_min_u; public float[] aabb_max_u; public float[] center_b; }
        [Serializable] class MMat { public string name; public string kind; public bool vcol; public float rough = 0.5f; public float metal; public List<float> albedo; public float alpha = 1f; public List<float> emit; public float emit_str; public string tex; public float ao_mix; }
        [Serializable] class MLight { public string name; public string kind; public float power; public List<float> color; public float[] pos_u; public float[] dir_u; public float size; public float size_y; public float[] long_u; }
        [Serializable] class MCam { public string name; public float[] pos_u; public float[] fwd_u; public float[] up_u; public float fov_v; public float far; }
        [Serializable] class MFog { public float density; public List<float> color; }
        [Serializable] class MPost { public float bloom_threshold; public float bloom_intensity; public string tonemap; }
        [Serializable] class MSect { public string name; public float x0; public float x1; }
        [Serializable] class Manifest
        {
            public string scene; public string fbx;
            public List<MObj> objects; public List<MMat> materials; public List<MLight> lights;
            public List<MCam> cameras; public MFog fog; public List<float> ambient; public MPost post;
            public List<MSect> sections;
        }

        // ---------------- 入口 ----------------
        // ---------------- 实管线补光表(翻译时的光照导演) ----------------
        // Cycles 里白墙/发光几何自带全局反弹; URP 发光不照明, 必须用环境光+实灯补。
        // 实测 2026-10-06: 通用换算(0.03)暗 4 倍+, 本表为逐场景校准值。
        static readonly Dictionary<string, Color> AmbientByTag = new Dictionary<string, Color>
        {
            // 2026-10-06 重标定(URP 改 HDR 调色 + 自发光不参与 GI 之后): 此前的值是在"LDR 夹断"的
            // 假象下调出来的, 全部作废。现在的判据是 DiagLinear 测到的线性 p50 = Blender 等效线性。
            ["corridor"]  = new Color(0.007158f, 0.008055f, 0.009850f),
            ["atrium"]    = new Color(0.05298f, 0.0595f, 0.07506f),
            ["atrium_v6"] = new Color(0.1465f, 0.1562f, 0.1758f),
        };
        // kArea: 全管线唯一经验系数(2026-10-09 大扫除后以地板 −1% 对表标定;
        // 0.0213 = 旧 0.025×0.85, 把删除的 dshare 折算进来, 灯阵净强度不变)
        static readonly Dictionary<string, float> AreaKByTag = new Dictionary<string, float>
        { ["corridor"] = 0.0036f, ["atrium"] = 0.05298f, ["atrium_v6"] = 0.3419f };
        // 中庭竖井方向补光(替代 Cycles 里巨型发光顶棚的远距反弹)。**必须进表**: 它是一盏
        // 硬编码的方向光, 不随 AreaK 缩放 —— 实测它占中庭总光量约 47%, 只缩 AreaK 会让
        // 中庭"怎么减都减不下去"(线性 p50 卡在 0.373, 而目标 0.234)。
        static readonly Dictionary<string, float> ShaftFillByTag = new Dictionary<string, float>
        { ["atrium"] = 0.155f };
        // 雾色缩放(大扫除 2026-10-09): 走廊 0.85 是 AgX 对表时代遗产, 删除(=1.0, 雾色用 manifest
        // 源值); 中庭 0.20 保留 —— 那是"Unity 自发光雾 vs Cycles 被照亮雾"的物理口径换算, 不是观感调参。
        static readonly Dictionary<string, float> FogScaleByTag = new Dictionary<string, float>
        { ["corridor"] = 1.0f, ["atrium"] = 0.20f, ["atrium_v6"] = 1.0f };
        // 实时直接光占比(1.0 = 全实时)。面积光用点灯串逼近时, 点光源**贴面**就会 1/r² 爆掉 ——
        // 走廊灯串摆在灯槽位(y=±1.05, z=3.378), 而天花面板就在它上方 2~7cm, 于是灯槽上方的天花
        // 被烤成白斑; Blender 侧是**面光源**, 不会出现这种峰值。实测量化: 02 机位(看门)整幅偏亮
        // 1.2~1.96 倍, 顶排(天花处)最亮 1.96。
        // 修法: 强度 ×share, bounceIntensity ÷share —— 烘焙 GI 总量**不变**(平滑部分由光照贴图承担),
        // 实时直接光降到 share, 峰值自然消失。改这个必须重烤。
        // 烘焙反射探针强度: 环境镜面项。1.0 时硬光斑超标(Unity 0.23~0.79% vs Blender 0.09~0.18%)
        // 且整体偏亮 —— 说明环境反射给多了。这是**唯一**能压它的旋钮(材质粗糙度要与 Blender 保持一致,
        // 不能靠改材质来"看起来不那么亮")。
        // 探针强度(大扫除 2026-10-09): 0.45 是 AgX/无GI时代压镜面的遗产, 删除(=1.0 真值)。
        // 镜面质量问题的正解是 Cycles 环境贴图探针(方法库方案C), 不是把真值打折。
        static readonly Dictionary<string, float> ProbeIntensityByTag = new Dictionary<string, float>
        { ["SC_Corridor_Pragmata"] = 1.0f, ["SC_Atrium_Concept"] = 1.0f, ["SC_Atrium_Military"] = 1.0f };
        // (历史表, 2026-10-09 大扫除后 dshare 不再参与走廊; 中庭两场景仍 Mixed, 暂保留)
        static readonly Dictionary<string, float> DirectShareByTag = new Dictionary<string, float>
        { ["corridor"] = 1.0f, ["atrium"] = 0.35f, ["atrium_v6"] = 0.35f };
        // 曝光(线性域, 在 tonemap 之前)作最终对表旋钮。**用户令 2026-10-08: 后处理永远默认,
        // 本表只作历史记录, 不再写入任何出图路径**; 亮度对表只在光源强度/材质/烘焙层做。
        static readonly Dictionary<string, float> PostExposureByTag = new Dictionary<string, float>
        { ["corridor"] = -0.45f, ["atrium"] = 0.8f, ["atrium_v6"] = 0.85f };
        static readonly Dictionary<string, float> AreaSpacingByTag = new Dictionary<string, float>
        { ["corridor"] = 0.85f, ["atrium"] = 1.8f, ["atrium_v6"] = 3.0f };
        // 发光强度系数: **同时决定灯带观感与烘焙照明**(自发光=真光源后)。
        // 2026-10-08 定标(EXP2/EXP3 对表 Cycles 线性值): Unity 烘焙器把 _EmissionColor 当辐射值
        // 换算辐照度时差一个 π(经验系数 0.31 ≈ 1/π=0.318; 扣除反照率 0.8→0.48 修正后吻合)。
        // 0.95→0.30 = π 补偿, 亮池/地板与 Blender 线性同量级; 观感侧灯带略暗由 bloom 收尾。
        // 发光强度系数(大扫除 2026-10-09): 0.95 是观感调参遗产, 删除(=1.0 = manifest 源值直译)。
        // 注: 1a 发光材质当光源的路线已判死刑(4×+膝点), 本系数现在只决定灯带可见亮度。
        static readonly Dictionary<string, float> EmissionKByTag = new Dictionary<string, float>
        { ["corridor"] = 0.17f, ["atrium"] = 0.50f, ["atrium_v6"] = 0.45f };

        [MenuItem("HST/Build Both Scenes + Verify")]
        public static void BuildBoth()
        {
            FixTextureImporters();
            foreach (var tag in new[] { "corridor", "atrium", "atrium_v6" })
            {
                try { Build(tag); }
                catch (Exception e) { Debug.LogError($"[HST] Build {tag} 失败: {e}"); }
            }
            VerifyAll();
        }

        [MenuItem("HST/Verify Only (dump csv)")]
        public static void VerifyMenu() { VerifyAll(); }

        [MenuItem("HST/Full Pass (rebuild+verify+camcheck+shoot)")]
        public static void FullPass() { BuildBoth(); CamCheck(); Shoot(); }

        [MenuItem("HST/Deliver All (relight + player + shoot)")]
        public static void DeliverAll()
        { EnableLightmapUVs(); BuildBoth(); AddWalkPlayer(); BakeLightingAll(); CamCheck(); Shoot(); }

        // ---------------- 烘焙 GI(自发光=真光源) + 光探针(主控受 GI) + 反射探针(玻璃/高光滑反射) ----
        [MenuItem("HST/生成光贴图 UV2 (全部网格)")]
        public static void EnableLightmapUVs()
        {
            // 导入器自带的二次 UV 生成**保持关闭**: 实测它会把 16 个微型件(LED/螺栓)导成**空网格**
            // (verts=0, 直接消失), 而走廊 162 个网格里 132 个缺 UV2 —— 光照贴图器会把"没有光贴图 UV"
            // 的网格**排除出烘焙几何**(后果: 灯带不当光源、灯槽不遮挡、墙面全平)。
            // 所以改为**逐网格在 Unity 里生成**(FixMissingLightmapUVs): 成功的用 Unity 展开,
            // 失败的用自写盒投影兜底, 保证非空且一定有 UV2。
            foreach (var guid in AssetDatabase.FindAssets("", new[] { ModelDir }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetImporter.GetAtPath(path) is ModelImporter imp && imp.generateSecondaryUV)
                {
                    imp.generateSecondaryUV = false;
                    imp.SaveAndReimport();
                    Debug.Log("[HST-UV2] 关导入器二次 UV(改逐网格生成): " + path);
                }
            }
        }

        // 逐网格补光贴图 UV: 缺 UV2 的网格做一份副本, 先试 Unity 展开, 失败则盒投影, 存成资产并替换引用
        [MenuItem("HST/逐网格补 UV2(缺的全补)")]
        public static void FixMissingLightmapUVs()
        {
            var scene = EditorSceneManager.OpenScene(SceneDir + "/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            EnsureFolder("Assets/Art/Environment/HST_Kit/Models/HST_Prags/UV2");
            int unwrapped = 0, boxed = 0, empt = 0;
            foreach (var mf in UnityEngine.Object.FindObjectsOfType<MeshFilter>(true))
            {
                var m = mf.sharedMesh;
                if (m == null) continue;
                if (m.uv2 != null && m.uv2.Length == m.vertexCount && m.vertexCount > 0) continue;
                if (m.vertexCount == 0) { empt++; Debug.LogWarning("[HST-UV2FIX] 网格为空(导入器已损坏): " + mf.name); continue; }
                var copy = UnityEngine.Object.Instantiate(m);
                copy.name = m.name + "_UV2";
                bool ok = false;
                try { Unwrapping.GenerateSecondaryUVSet(copy); ok = copy.uv2 != null && copy.uv2.Length == copy.vertexCount; } catch { ok = false; }
                if (ok) unwrapped++; else { BoxProjectUV2(copy); boxed++; }
                string path = "Assets/Art/Environment/HST_Kit/Models/HST_Prags/UV2/" + copy.name + ".asset";
                if (File.Exists(path)) AssetDatabase.DeleteAsset(path);
                AssetDatabase.CreateAsset(copy, path);
                mf.sharedMesh = copy;
            }
            AssetDatabase.SaveAssets();
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[HST-UV2FIX] Unity 展开 {unwrapped} + 盒投影 {boxed}, 空网格 {empt}");
        }

        // 兜底: 逐顶点投影到主轴平面, 整体拟合进 [0,1]² (只求非空 + 可采样, 小件足够)
        static void BoxProjectUV2(Mesh m)
        {
            var verts = m.vertices;
            var uv = new Vector2[verts.Length];
            var sz = m.bounds.size;
            int axis = (sz.x >= sz.y && sz.x >= sz.z) ? 0 : (sz.y >= sz.z ? 1 : 2);
            for (int i = 0; i < verts.Length; i++)
            {
                var v = verts[i];
                uv[i] = axis == 0 ? new Vector2(v.z, v.y) : axis == 1 ? new Vector2(v.x, v.z) : new Vector2(v.x, v.y);
            }
            var mn = new Vector2(float.MaxValue, float.MaxValue);
            var mx = new Vector2(float.MinValue, float.MinValue);
            foreach (var u in uv) { mn = Vector2.Min(mn, u); mx = Vector2.Max(mx, u); }
            var d = mx - mn;
            float s = 0.96f / Mathf.Max(Mathf.Max(d.x, d.y), 1e-4f);
            for (int i = 0; i < uv.Length; i++) uv[i] = (uv[i] - mn) * s + new Vector2(0.02f, 0.02f);
            m.uv2 = uv;
        }

        // 2026-10-10: 门外景观子树判定(城市背景/观景台/外壳挂在其下, 不参与烘焙与包围盒)
        static bool InExtSubtree(Transform t)
        {
            while (t != null) { if (t.name == "HST_EXT_ROOT") return true; t = t.parent; }
            return false;
        }

        [MenuItem("HST/Bake Lighting (GI + probes)")]
        public static void BakeLightingAll()
        { BakeScenes(new[] { "SC_Corridor_Pragmata", "SC_Atrium_Concept", "SC_Atrium_Military" }); }

        [MenuItem("HST/Bake Corridor Only (快迭代)")]
        public static void BakeCorridorOnly() { BakeScenes(new[] { "SC_Corridor_Pragmata" }); }

        // 两个大厅重烤(2026-10-13 用户令: "大厅你也直接重新烤吧, 对齐走廊就行, 走廊是我调好的了")。
        // 走**与走廊同一条 BakeScenes 管线**、读**同一张"实管线补光表"**(lmScale / 灯型 / 探针强度
        // 逐场景取自 AmbientByTag / AreaKByTag / ProbeIntensityByTag 那几张表), 不另立参数。
        // 与走廊的差别只有一处, 且是表里写明的: 走廊 lightsBaked=true(纯 Baked), 大厅 false(Mixed)
        // —— 原因是 NPR 母材质的明暗带由实时主光驱动(GetMainLight), 全 Baked 会让 band 归零;
        // 走廊 NPR 出图时也是靠临时补实时灯解决的。走廊场景本身不动。
        // _skipProbeBake=true: 探针烘烤在大尺度下要 35-40 min(4096 间接 × 6 面 256²), 走廊定稿时同样跳过
        // —— 反射 cubemap 沿用场景里已有的那张。
        [MenuItem("HST/Bake Halls Only (两个大厅, 走廊不动)")]
        public static void BakeHallsOnly()
        {
            _skipProbeBake = true;
            BakeScenes(new[] { "SC_Atrium_Concept", "SC_Atrium_Military" });
            _skipProbeBake = false;
        }

        // 单厅重烤(2026-10-13: 两厅一起烤在 4GB 卡 + 2.4GB 空闲内存下 OOM, 分开烤降显存/内存峰值)
        public static void BakeHallsOnlyOne(string sceneName)
        {
            _skipProbeBake = true;
            BakeScenes(new[] { sceneName });
            _skipProbeBake = false;
        }

        // 4096 采样版(EXP 探针共用): 发光直射走蒙特卡洛, 1024 不够
        public static void BakeCorridorOnly4096()
        {
            _indirectOverride = 4096;
            BakeScenes(new[] { "SC_Corridor_Pragmata" });
            _indirectOverride = 0;
        }

        // ---------------- 走廊门区修复(2026-10-07): 数值诊断定位的三个真凶 ----------------
        // ① LUMEN_DoorTop 点灯串贴着门头 0.12m, 1/r² 把整扇门烘平到线性~1.0(AgX 后=0.8157 恒亮平台,
        //    水平 std=0/垂直微梯度的指纹)—— 同天花板灯串同类热点, 当时漏了它。内缩到离门面 ~1.0m。
        // ② 门缝条 ARCH_DoorSeam 在 Cycles 是光源(发光体照明门叶, ±2% 带 +0.09 亮度); Unity 侧
        //    EmissiveIsBlack 让自发光不进 GI → 沿缝补 3 颗小 Baked 点灯, 强度按 AgX 逆映射算的
        //    Δlinear≈0.14 反推(E=I/d², d=0.17m, albedo 0.6/π) → I≈0.03/颗。
        // ③ 缝条单面平面被背面剔除整条吞掉(全图 max=0.8188<缝条应有的 0.91)—— Cycles 对单面
        //    平面双面着色, Unity 默认剔除; 发光 Unlit 材质一律 _Cull Off 对齐(在 Polish/CreateMat)。
        [MenuItem("HST/门区修复+走廊重烤+出图")]
        public static void DoorFixPass()
        {
            FixCorridorDoor();
            BakeCorridorOnly();
            Polish();
        }

        static void FixCorridorDoor()
        {
            var scene = EditorSceneManager.OpenScene(SceneDir + "/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            int moved = 0;
            foreach (var li in UnityEngine.Object.FindObjectsOfType<Light>(true))
                if (li.name.Contains("DoorTop"))
                { var p = li.transform.position; li.transform.position = new Vector3(-17.3f, p.y, p.z); moved++; }
            // 2026-10-08: 门缝 GI 不再用假点光。ARCH_DoorSeam 材质已是真发光体(参与烘焙 GI),
            // 射线直接从凹进的缝条算出 ±8cm 窄带 + 门叶 ×8 采样 —— 与 Blender 同一条物理路径。
            for (int k = 0; k < 8; k++)
            {
                var old = GameObject.Find("HST_SeamGlow_" + k);
                if (old != null) UnityEngine.Object.DestroyImmediate(old);
            }
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[HST-DOOR] DoorTop 内缩 {moved} 颗 + 门缝 GI 灯 3 颗(0.03cd, 1m)");
        }

        // ---------------- 手工 HDR 探针(2026-10-07): BakeReflectionProbe 的 HDR 钳位绕行 -------------
        // 实测 Lightmapping.BakeReflectionProbe 出的 cubemap 六面 max 只有 0.88~1.04, 而天花板灯带
        // 真实辐射是 4.2~4.6 —— 探针捕获路径把 HDR 钳在 ~1, 墙面镜面里的灯带亮度先天矮 4.6 倍,
        // 04 机位高通能量比卡在 0.67 的直接原因。绕行: 用已验证不钳的 RenderRaw(HDR RT)在探针中心
        // 渲六面, 手工拼 RGBAFloat cubemap(自带 mip=粗糙度盒滤波), 喂给 customReflection 全局通道。
        // 捕获时关雾: Cycles 的 world mist 只作用于相机光线, 反射光线不过雾, 探针面不该带雾。
        [MenuItem("HST/手工HDR探针(走廊)+出图")]
        public static void BakeProbeManual()
        {
            var scene = EditorSceneManager.OpenScene(SceneDir + "/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            var go = new GameObject("HST_ProbeCam");
            var cam = go.AddComponent<Camera>();
            cam.fieldOfView = 90f; cam.nearClipPlane = 0.05f; cam.farClipPlane = 60f;
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black;
            var cd = cam.GetUniversalAdditionalCameraData();
            cd.renderPostProcessing = false;   // 原始线性 HDR, 不过 AgX/泛光
            go.transform.position = new Vector3(-9.25f, 1.6f, 0f);
            bool prevFog = RenderSettings.fog;
            RenderSettings.fog = false;
            // 采集的是"环境辐射"(发光体+烘焙 GI), 不该包含"从中心看到的实时镜面热点"
            // (1024 下地板高光 1/d² 峰值实测 max=67 被采进 cube)。实时灯清零采集, 完了恢复。
            var allL = UnityEngine.Object.FindObjectsOfType<Light>(true);
            var savedI = new float[allL.Length];
            for (int i = 0; i < allL.Length; i++) { savedI[i] = allL[i].intensity; allL[i].intensity = 0f; }
            var dirs = new[] {
                new[]{ Vector3.right,   Vector3.up },       new[]{ Vector3.left,  Vector3.up },
                new[]{ Vector3.up,      Vector3.forward },  new[]{ Vector3.down, Vector3.forward },
                new[]{ Vector3.forward, Vector3.up },       new[]{ Vector3.back, Vector3.up } };
            var faces = new[] { CubemapFace.PositiveX, CubemapFace.NegativeX, CubemapFace.PositiveY,
                                CubemapFace.NegativeY, CubemapFace.PositiveZ, CubemapFace.NegativeZ };
            const int R = 1024;   // 256 的 mip5 会把 16.6m 灯带平均成 8×8, 条痕能量消失(质感轮实测)
            var cube = new Cubemap(R, TextureFormat.RGBAFloat, true) { name = "RP_Manual_Corridor" };
            float gmax = 0f;
            for (int i = 0; i < 6; i++)
            {
                go.transform.rotation = Quaternion.LookRotation(dirs[i][0], dirs[i][1]);
                float ml; int ov;
                var px = RenderRaw(cam, R, R, out ml, out ov);
                gmax = Mathf.Max(gmax, ml);
                cube.SetPixels(px, faces[i]);
            }
            cube.Apply(true, false);
            if (AssetDatabase.Contains(cube)) { EditorUtility.SetDirty(cube); AssetDatabase.SaveAssets(); }
            for (int i = 0; i < allL.Length; i++) allL[i].intensity = savedI[i];
            RenderSettings.fog = prevFog;
            UnityEngine.Object.DestroyImmediate(go);
            EnsureFolder("Assets/Art/Environment/HST_Kit/ReflectionProbes");
            string ap = "Assets/Art/Environment/HST_Kit/ReflectionProbes/RP_Manual_Corridor.asset";
            // 资产只建一次, 以后写进同一个资产 —— 删了重建会让 guid 变, 天空盒材质引用悬空
            // (审计实测: 重载后 sky._Tex=NULL, 反射全灭)。尺寸不符才重建并重绑。
            var exist = AssetDatabase.LoadAssetAtPath<Cubemap>(ap);
            if (exist != null && (exist.width != R || exist.format != TextureFormat.RGBAFloat))
            { AssetDatabase.DeleteAsset(ap); exist = null; }
            if (exist != null)
            {
                for (int i = 0; i < 6; i++) exist.SetPixels(cube.GetPixels(faces[i]), faces[i]);
                exist.Apply(true, false);
                EditorUtility.SetDirty(exist);
            }
            UnityEngine.Object.DestroyImmediate(cube);
            cube = exist != null ? exist : cube;
            if (exist == null) AssetDatabase.CreateAsset(cube, ap);
            // 生效通道 = 天空盒反射(defaultReflectionMode=Skybox)。**Skybox/6 Sided 没有 _Tex**,
            // 它吃六张 2D 面贴图(_FrontTex/_BackTex/_LeftTex/_RightTex/_UpTex/_DownTex) ——
            // 之前 SetTexture("_Tex") 写的是不存在的属性, SaveAssets 后 _Tex 恒 NULL(磁盘 .mat 实证)。
            var sky = AssetDatabase.LoadAssetAtPath<Material>("Assets/Art/Environment/HST_Kit/ReflectionProbes/M_SkyboxProbe_Corridor.mat");
            if (sky == null)
            {
                sky = new Material(Shader.Find("Skybox/6 Sided")) { name = "M_SkyboxProbe_Corridor" };
                AssetDatabase.CreateAsset(sky, "Assets/Art/Environment/HST_Kit/ReflectionProbes/M_SkyboxProbe_Corridor.mat");
            }
            // faces 数组顺序 = +X,−X,+Y,−Y,+Z,−Z → 槽位必须是 Right,Left,Up,Down,Front,Back
            // (填错 = 天花倒影跑地板上)
            var faceNames = new[] { "_RightTex", "_LeftTex", "_UpTex", "_DownTex", "_FrontTex", "_BackTex" };
            var faceDirs = new[] { "Right", "Left", "Up", "Down", "Front", "Back" };
            for (int i = 0; i < 6; i++)
            {
                string fp = "Assets/Art/Environment/HST_Kit/ReflectionProbes/Face_" + faceDirs[i] + ".asset";
                var faceTex = AssetDatabase.LoadAssetAtPath<Texture2D>(fp);
                if (faceTex == null || faceTex.width != R)
                {
                    if (faceTex != null) AssetDatabase.DeleteAsset(fp);
                    faceTex = new Texture2D(R, R, TextureFormat.RGBAHalf, false);
                    AssetDatabase.CreateAsset(faceTex, fp);
                }
                var fpx = cube.GetPixels((CubemapFace)i);
                faceTex.SetPixels(fpx);
                faceTex.Apply(false, false);
                EditorUtility.SetDirty(faceTex);
                sky.SetTexture(faceNames[i], faceTex);
            }
            sky.SetColor("_Tint", Color.white); sky.SetFloat("_Exposure", 1f);
            EditorUtility.SetDirty(sky); AssetDatabase.SaveAssets();
            RenderSettings.skybox = sky;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
            RenderSettings.reflectionIntensity = 1.1f;   // 消元法标定值(p95 口径 1.78× × 0.6)
            // 批处理机制(全部 A/B 实证): SpecCube0(天空盒直采)在手动 cam.Render() 下不生效;
            // 唯一活路 = 探针混合路径(unity_SpecCube1), 且它只在 defaultReflectionMode=Skybox 时激活。
            // 所以: BlendProbes + Custom探针挂cube + Skybox模式, 强度用 rp.intensity 标定。
            foreach (var mr in UnityEngine.Object.FindObjectsOfType<MeshRenderer>(true))
                mr.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
            // 探针绑定必须在 SaveScene 之前 —— 旧代码放在保存之后, 探针从未真正挂上手工cube
            var rp = UnityEngine.Object.FindObjectOfType<ReflectionProbe>();
            if (rp != null) { rp.mode = ReflectionProbeMode.Custom; rp.customBakedTexture = cube; EditorUtility.SetDirty(rp); }
            // 相机裁决(同样必须在 SaveScene 之前): CAM_01 深度=1(Game 视图恒为英雄机位), 其余 0 + 关多余 AudioListener
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Camera>(true))
                if (c.name.StartsWith("CAM_"))
                {
                    c.depth = c.name.StartsWith("CAM_01") ? 1 : 0;
                    var al = c.GetComponent<AudioListener>();
                    if (al != null && !c.name.StartsWith("CAM_01")) al.enabled = false;
                    EditorUtility.SetDirty(c);
                }
            // 不存盘 = 下个进程 OpenScene 全部丢弃(实测踩过: 绑定只活在当前会话, 出图全白干)
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[HST-PROBE2] 手工探针: 全局max={gmax:F2} → {ap}");
        }

        [MenuItem("HST/天空盒反射通道判决+修复")]
        public static void ProbeSkybox()
        {
            var scene = EditorSceneManager.OpenScene(SceneDir + "/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            var cam = System.Array.Find(UnityEngine.Object.FindObjectsOfType<Camera>(true), c => c.name.Contains("04"));
            var cube = AssetDatabase.LoadAssetAtPath<Cubemap>("Assets/Art/Environment/HST_Kit/ReflectionProbes/RP_Manual_Corridor.asset");
            string mp = "Assets/Art/Environment/HST_Kit/ReflectionProbes/M_SkyboxProbe_Corridor.mat";
            var sky = AssetDatabase.LoadAssetAtPath<Material>(mp);
            if (sky == null) { sky = new Material(Shader.Find("Skybox/6 Sided")) { name = "M_SkyboxProbe_Corridor" }; AssetDatabase.CreateAsset(sky, mp); }
            sky.SetTexture("_Tex", cube); sky.SetColor("_Tint", Color.white); sky.SetFloat("_Exposure", 1f);
            EditorUtility.SetDirty(sky);
            var prevSky = RenderSettings.skybox;
            var prevMode = RenderSettings.defaultReflectionMode;
            float m0, p0, x0; RawStats(RenderRawC(cam, 320, 180), out m0, out p0, out x0);
            RenderSettings.skybox = sky;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
            float m1, p1, x1; RawStats(RenderRawC(cam, 320, 180), out m1, out p1, out x1);
            Debug.Log($"[HST-SKY] 天空盒通道: mean {m0:F4}→{m1:F4}  Δ={m1-m0:F5}  (cube 仍是 x100 状态)");
            if (Mathf.Abs(m1 - m0) > 0.01f)
            {
                // 通道活了: 立刻把 cube 还原回 x1(真实辐射) 并保留天空盒绑定, 存盘
                foreach (CubemapFace f in Enum.GetValues(typeof(CubemapFace)))
                {
                    if (f == CubemapFace.Unknown) continue;
                    var px = cube.GetPixels(f);
                    for (int i = 0; i < px.Length; i++) px[i] = px[i] / 100f;
                    cube.SetPixels(px, f);
                }
                cube.Apply(true, false);
                EditorUtility.SetDirty(cube); AssetDatabase.SaveAssets();
                float m2, p2, x2; RawStats(RenderRawC(cam, 320, 180), out m2, out p2, out x2);
                Debug.Log($"[HST-SKY] cube 还原 x1: mean={m2:F4}  (Δ 对无反射 {m2-m0:F5} = 真实镜面贡献)");
                EditorSceneManager.SaveScene(scene);
            }
            else
            {
                RenderSettings.skybox = prevSky;
                RenderSettings.defaultReflectionMode = prevMode;
                Debug.LogWarning("[HST-SKY] 天空盒通道也没进画面 —— SpecCube0 在此渲染路径下未被采样");
            }
        }

        [MenuItem("HST/探针蛮力判决(x100)")]
        public static void ProbeBrute()
        {
            var scene = EditorSceneManager.OpenScene(SceneDir + "/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            var cam = System.Array.Find(UnityEngine.Object.FindObjectsOfType<Camera>(true), c => c.name.Contains("04"));
            var cube = AssetDatabase.LoadAssetAtPath<Cubemap>("Assets/Art/Environment/HST_Kit/ReflectionProbes/RP_Manual_Corridor.asset");
            float m0, p0, x0; RawStats(RenderRawC(cam, 320, 180), out m0, out p0, out x0);
            foreach (CubemapFace f in Enum.GetValues(typeof(CubemapFace)))
            {
                if (f == CubemapFace.Unknown) continue;
                var px = cube.GetPixels(f);
                for (int i = 0; i < px.Length; i++) px[i] = px[i] * 100f;
                cube.SetPixels(px, f);
            }
            cube.Apply(true, false);
            EditorUtility.SetDirty(cube); AssetDatabase.SaveAssets();
            float m1, p1, x1; RawStats(RenderRawC(cam, 320, 180), out m1, out p1, out x1);
            Debug.Log($"[HST-BRUTE] 探针x100: mean {m0:F4}→{m1:F4}  Δ={m1-m0:F5}  (≈0=采样从未发生)");
        }

        // 门区第二轮: 量化后校准 —— DoorTop ×0.45(门面过亮 +0.17 的主要嫌疑), 缝灯 ×7(带只有
        // Blender 的 1/9: +0.010 vs +0.090)
        // 终校准: 缝灯幅度(带 +0.12 vs 目标 +0.09 → ×0.75) + 天空盒反射强度(数值由
        // Blender 高光场消元实验定) —— 一次批处理收尾
        [MenuItem("HST/质感轮: 走廊回Mixed(直射0.45/间距0.75)+重烤+探针+出图")]
        public static void CorridorMixed() { CorridorMixed(false); }

        // directProbe: 只重建灯光 + 只直射烘焙 + 墙区读数(不起探针/不出图, 供分解诊断)
        public static void CorridorMixed(bool directProbe)
        {
            // 灯串加密: 既有灯按名字后缀删除重建会丢 DoorTop 内缩, 直接按新间距补插不行 ——
            // 最干净: 全删走廊点串, 按 manifest 重建(Build 的灯创建逻辑已带内缩与新间距)
            // 但 Build 会重摆一切; 这里只重建灯: 走 Build 太重, 手动补: 删 L_LUMEN_* 点串再按
            // manifest corridor 的 AREA 灯重生成。
            var scene = EditorSceneManager.OpenScene(SceneDir + "/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            string mp = Path.Combine(Application.dataPath, "HST_Prags/manifests/manifest_corridor.json");
            var man = JsonUtility.FromJson<Manifest>(File.ReadAllText(mp));
            float kArea = AreaKByTag["corridor"], sp = AreaSpacingByTag["corridor"], dshare = DirectShareByTag["corridor"];
            foreach (var li in UnityEngine.Object.FindObjectsOfType<Light>(true))
                if (li.name.StartsWith("L_LUMEN_")) UnityEngine.Object.DestroyImmediate(li.gameObject);
            foreach (var ml in man.lights)
            {
                var col = ml.color != null ? new Color(ml.color[0], ml.color[1], ml.color[2]) : Color.white;
                // 冷色偏移 (0.86,0.94,1.10) 是 AgX 之前"防黑白"时代的补丁, 现在让一切偏蓝 — 删
                if (ml.kind == "AREA")
                {
                    float big = Mathf.Max(ml.size, ml.size_y);
                    float wide = Mathf.Min(ml.size, ml.size_y);          // 光条宽度(0.28m 级)
                    int n = Mathf.Max(1, Mathf.CeilToInt(big / sp));
                    int nz = wide > 0.05f ? 3 : 1;                        // 宽度方向 3 点阵
                    Vector3 longU = ml.long_u != null ? V(ml.long_u) : Vector3.right;
                    Vector3 crossU = Vector3.Cross(Vector3.up, longU).normalized;
                    // 2026-10-08: **range 截断是错的**。旧值 min(big*0.6,12)≈10m, 而走廊尽头的墙离
                    // 灯串 9.2m —— 正好落在衰减窗里, 直射光被截到 ≈0(实测墙面直射 0.021 线性, 几乎全黑),
                    // 墙面只剩均匀间接光 → 平。Cycles 的面光源没有距离截断, 点串也不该有。
                    // 标定折算: 强度只保留原有近场标定, 不再乘 12/range(那个因子就是给截断做的补偿)。
                    float range = 40f;
                    var basePos = V(ml.pos_u);   // 大扫除: 删 DoorTop +0.85m 位移补丁(门区热点时代遗产)
                    for (int i = 0; i < n; i++)
                    for (int j = 0; j < nz; j++)
                    {
                        float t = (i - (n - 1) * 0.5f) * (big / n);
                        float w = (j - (nz - 1) * 0.5f) * (wide / nz);
                        var go = new GameObject($"L_{ml.name}_{i}_{j}");
                        var li = go.AddComponent<Light>();
                        // 面积光是单面发射(只朝法线半球), 点光是全向(会照亮背后/上方) ——
                        // 实测门头无光区被全向点照亮。改广角聚光(150°锥)朝 manifest 朝向。
                        li.type = LightType.Spot; li.spotAngle = 150f; li.innerSpotAngle = 120f;
                        go.transform.rotation = Quaternion.LookRotation(
                            ml.dir_u != null ? V(ml.dir_u) : Vector3.down);
                        li.color = col;
                        // 2026-10-09 参数大扫除(用户令: 误调参数全部复位): 删 dshare 0.85/1.18 拆分
                        // (那是 Mixed 实时高光尖刺时代的补丁, 现在纯 Baked 无意义);
                        // kArea 是全管线唯一经验系数(2026-10-09 以地板 −1% 对表标定), 其余全物理直译。
                        li.intensity = ml.power * kArea / (n * nz);
                        li.bounceIntensity = 1f;
                        // 2026-10-08: **必须开阴影**。旧版 None = 无遮蔽 → 60 颗点灯把整个走廊
                        // 连墙带一起冲成平灰(墙带实测 0.575 恒平, 而参考图 0.39~0.62 有结构)。
                        // Blender 侧是带阴影的真面光源; 点串只是逼近, 遮蔽不能省。
                        li.range = range; li.shadows = LightShadows.Soft;
                        li.lightmapBakeType = LightmapBakeType.Baked;
                        go.transform.position = basePos + longU * t + crossU * w;
                    }
                }
                else if (ml.kind == "POINT")
                {
                    // 机架专用近距灯(RackA/RackB): 上一版被连带删除且没重建 → 机架死黑
                    var go = new GameObject("L_" + ml.name);
                    var li = go.AddComponent<Light>();
                    li.type = LightType.Point; li.color = col;
                    li.intensity = ml.power * kArea;          // 大扫除: 删 0.10 手拍系数与 dshare, 统一走 kArea
                    li.bounceIntensity = 1f;
                    li.range = 8f; li.shadows = LightShadows.Soft;
                    li.lightmapBakeType = LightmapBakeType.Baked;
                    go.transform.position = V(ml.pos_u);
                }
            }
            EditorSceneManager.SaveScene(scene);
            ApplyStripIllumination();
            // 门框发光条(ARCH_FrameGlow ×3, str=5)的 GI: 同上, 改由真发光网格承担, 删假点光。
            for (int k = 0; k < 16; k++)
            {
                var oldf = GameObject.Find("L_FRGLOW_" + k);
                if (oldf != null) UnityEngine.Object.DestroyImmediate(oldf);
            }
            Debug.Log("[HST-MIXED] 走廊点串已按间距 " + sp + "m 重生成为 Mixed(含 POINT); "
                      + "L_PANE/L_FRGLOW 假光阵已删(发光网格=真光源)");
            if (directProbe)
            {
                _directOnlyProbe = true;
                // 第二档(命令行 -executeMethod 带参不便, 用标题区分): 关自发光 GI → 只剩实灯
                var mrAll = UnityEngine.Object.FindObjectsOfType<MeshRenderer>(true);
                var mats = new System.Collections.Generic.Dictionary<Material, MaterialGlobalIlluminationFlags>();
                foreach (var r in mrAll)
                    foreach (var mm in r.sharedMaterials)
                        if (mm != null && mm.HasProperty("_EmissionColor") && !mats.ContainsKey(mm))
                            mats[mm] = mm.globalIlluminationFlags;
                if (_emissiveGIOffProbe)
                {
                    foreach (var kv in mats) { kv.Key.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack; EditorUtility.SetDirty(kv.Key); }
                    Debug.Log($"[HST-DIRPROBE] 已关 {mats.Count} 个发光材质的 GI(只留实灯)");
                }
                if (_thickCoveProbe)
                {
                    int nt = 0;
                    foreach (var tr in UnityEngine.Object.FindObjectsOfType<Transform>(true))
                        if (tr.name.StartsWith("ARCH_Cove"))
                        { var s2 = tr.localScale; tr.localScale = new Vector3(s2.x, s2.y * 8f, s2.z); nt++; }
                    Debug.Log($"[HST-DIRPROBE] 灯槽罩墙加厚 ×8 (向上长, 全在天花之上不可见): {nt} 件");
                }
                BakeCorridorOnly();
                WallLightBreakdown.Run();
                foreach (var kv in mats) kv.Key.globalIlluminationFlags = kv.Value;
                _directOnlyProbe = false;
                return;
            }            BakeCorridorOnly();
            Polish();
            // 后处理锁默认(用户令 2026-10-08: "调色和后处理都是默认参数, 只动其他的")。
            // 旧 bloom 调参(2.5/0.40)废弃; None 曲线+默认 = Blender Standard 同构。
            WritePostDefaults();
            BakeProbeManual();
            Shoot();
        }

        // 统一入口: 后处理一律 URP 默认(无曲线/EV0/Bloom 关)。所有会出图的路径最后都走这里。
        static void WritePostDefaults()
        {
            foreach (var tag in new[] { "SC_Corridor_Pragmata", "SC_Atrium_Concept", "SC_Atrium_Military" })
            {
                var prof = AssetDatabase.LoadAssetAtPath<VolumeProfile>(MatDir + "/PP_" + tag + ".asset");
                if (prof == null) continue;
                if (prof.TryGet(out Tonemapping tm)) { tm.mode.value = TonemappingMode.None; EditorUtility.SetDirty(tm); }
                if (prof.TryGet(out ColorAdjustments ca2))
                { ca2.postExposure.value = 0f; ca2.contrast.value = 0f; ca2.saturation.value = 0f; ca2.hueShift.value = 0f; ca2.colorFilter.value = Color.white; EditorUtility.SetDirty(ca2); }
                if (prof.TryGet(out Bloom bl2))
                { bl2.threshold.value = 0.9f; bl2.intensity.value = 0f; bl2.scatter.value = 0.7f; bl2.clamp.value = 65479f; EditorUtility.SetDirty(bl2); }
                EditorUtility.SetDirty(prof);
            }
            AssetDatabase.SaveAssets();
            Debug.Log("[HST-POST] 后处理已锁 URP 默认(用户令 2026-10-08): None 曲线/EV0/Bloom 关");
        }

        // 灯带照明 = 代理灯串 + 面板 GI 关闭(防重复计数)。Build 与 CorridorMixed 两条路径共用。
        static void ApplyStripIllumination()
        {
            var paneMat = AssetDatabase.LoadAssetAtPath<Material>(MatDir + "/M_ARCH_PaneEmit.mat");
            if (paneMat != null)
            {
                paneMat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
                EditorUtility.SetDirty(paneMat);
                Debug.Log("[HST-MIXED] M_ARCH_PaneEmit GI 关闭(照明改由 L_STRIP 代理灯串承担)");
            }
            BuildStripCage();
        }

        // 灯带代理灯串(2026-10-08 重写): 从 **ARCH_LightPane 的真实几何** 生成, 而不是硬编码坐标。
        // 每条灯带沿长轴每 0.5m 一颗小面元灯, 摆在灯槽内(面板所在平面), 朝下 120° 锥 + **软阴影** +
        // range 40(无截断)。强度按物理配平: I = 面板辐射 L × 面元面积 a (这样照度 = L·a·cosθ/d²,
        // 与 Cycles 的发光面同量级)。关键差异: ①有阴影 → 灯槽挡板会挡掉射向墙中间的掠射光(亮池/暗带);
        // ②位置/朝向来自真实灯带几何, 不再是一条横穿走廊的"灯帘"。
        static void BuildStripCage()
        {
            foreach (var li in UnityEngine.Object.FindObjectsOfType<Light>(true))
                if (li.name.StartsWith("L_STRIP_")) UnityEngine.Object.DestroyImmediate(li.gameObject);
            var paneMat = AssetDatabase.LoadAssetAtPath<Material>(MatDir + "/M_ARCH_PaneEmit.mat");
            float L = 6.65f; var tint = new Color(0.92f, 0.96f, 1.0f);
            if (paneMat != null && paneMat.HasProperty("_EmissionColor"))
            {
                var c = paneMat.GetColor("_EmissionColor");
                L = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                tint = new Color(c.r / L, c.g / L, c.b / L);
            }
            int total = 0;
            foreach (var mr in UnityEngine.Object.FindObjectsOfType<MeshRenderer>(true))
            {
                if (!mr.name.StartsWith("ARCH_LightPane")) continue;
                var b = mr.bounds;
                bool alongX = b.size.x >= b.size.z;
                float len = alongX ? b.size.x : b.size.z;
                float wid = alongX ? b.size.z : b.size.x;
                int n = Mathf.Max(2, Mathf.CeilToInt(len / 0.5f));
                float areaSeg = len * wid / n;
                for (int i = 0; i < n; i++)
                {
                    float t = (i + 0.5f) / n - 0.5f;
                    var p = b.center + (alongX ? new Vector3(t * len, 0f, 0f) : new Vector3(0f, 0f, t * len));
                    var go = new GameObject($"L_STRIP_{mr.name}_{i}");
                    var li = go.AddComponent<Light>();
                    li.type = LightType.Spot; li.spotAngle = 120f; li.innerSpotAngle = 90f;
                    li.shadows = LightShadows.Soft;
                    li.color = tint;
                    li.intensity = L * areaSeg;          // 物理配平(radiance × 面元面积)
                    li.range = 40f;
                    li.lightmapBakeType = LightmapBakeType.Baked;
                    go.transform.position = p;
                    go.transform.rotation = Quaternion.LookRotation(Vector3.down);
                    total++;
                }
            }
            Debug.Log($"[HST-STRIP] 代理灯串 {total} 颗 (L={L:F2}, 每段面积 {0f:F2} 级), 软阴影 + range40");
        }

        // 一轮到位(2026-10-08): 走廊按清单重建(材质=真发光体, 参与烘焙 GI) → 灯光重建 → 重烤
        // → 反射探针 → 出图。判据: 灯条正下方 vs 两灯条中间 的墙面对比度(scripts/qa)
        [MenuItem("HST/GI真发光轮(重建+重烤+探针+出图)")]
        public static void GIEmitRebuild()
        {
            Debug.Log("[HST-GIEMIT] 关导入器UV2→重建走廊(发光网格改真光源)→逐网格补UV2→重烤");
            EnableLightmapUVs();
            Build("corridor");
            FixMissingLightmapUVs();     // 必须在 Build 之后: Build 会重挂 FBX 原始网格
            ApplyStripIllumination();    // 灯带代理灯串 + 面板 GI 关闭
            BakeCorridorLightingOnly();
        }

        // 只重烤走廊(不重建灯光/不出图), 供 UV2 修好后快速验证
        static void BakeCorridorLightingOnly() { BakeScenes(new[] { "SC_Corridor_Pragmata" }); }

        // 直射分解轮: 重建 + 只重建灯光 + **只直射烘焙**(bounces=0) + 墙区三态读数。
        // 用来定位"墙面平"到底发生在直射段还是间接段(与 Blender 的 la_direct_all 同口径对比)。
        [MenuItem("HST/直射分解轮(重建+只直射烘焙+墙区读数)")]
        public static void DirectProbeRebuild()
        {
            Debug.Log("[HST-DIRPROBE] 重建走廊 + 只直射烘焙");
            Build("corridor");
            CorridorMixed(true);
        }

        // 直射分解轮 B: 只直射 + **关掉全部自发光 GI** → 墙面读数 = 实灯(点串/门头灯/机架灯)的直接光。
        // 与 A 版的差 = 发光网格(灯带/门框发光条/门缝)的直接光。用来回答"灯光的直接光到底进没进 lightmap"。
        [MenuItem("HST/直射分解轮B(关发光GI: 只量实灯)")]
        public static void DirectProbeLightsOnly()
        {
            Debug.Log("[HST-DIRPROBE-B] 重建走廊 + 只直射 + 关自发光GI");
            Build("corridor");
            _emissiveGIOffProbe = true;
            CorridorMixed(true);
            _emissiveGIOffProbe = false;
        }

        // 直射分解轮 C: 只直射 + **灯槽罩墙加厚 ×8** → 若墙带深谷出现, 说明就是薄板漏光(射线偏移)。
        [MenuItem("HST/直射分解轮C(灯槽加厚×8)")]
        public static void DirectProbeThickCove()
        {
            Debug.Log("[HST-DIRPROBE-C] 重建走廊 + 只直射 + 灯槽罩墙加厚");
            Build("corridor");
            _thickCoveProbe = true;
            CorridorMixed(true);
            _thickCoveProbe = false;
        }

        // ---------------- 墙带实验轮(2026-10-08): 审计 + EXP1 门框GI隔离 + EXP2 真自发光源 ----------------
        // 回答两个问题: ①墙带"中间比两侧亮"里门框发光条的 GI 占多少; ②Unity 烘焙器能不能像
        // Cycles 一样**用发光几何本身当光源**(直射+遮挡全自动), 而不靠灯串代理。
        static void WallProbeAudit()
        {
            var sb = new System.Text.StringBuilder("[HST-AUDIT] 走廊烘焙配置审计\n");
            sb.AppendLine($"  lightmaps: {LightmapSettings.lightmaps.Length} 张, mode={LightmapSettings.lightmapsMode}");
            foreach (var rp in UnityEngine.Object.FindObjectsOfType<ReflectionProbe>(true))
                sb.AppendLine($"  probe {rp.name}: mode={rp.mode} res={rp.resolution} intensity={rp.intensity:F2} box={rp.size}");
            foreach (var guid in AssetDatabase.FindAssets("", new[] { MatDir }))
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
                if (m == null) continue;
                string sm = m.HasProperty("_Smoothness") ? m.GetFloat("_Smoothness").ToString("F2") : "-";
                string mt = m.HasProperty("_Metallic") ? m.GetFloat("_Metallic").ToString("F2") : "-";
                string bump = (m.HasProperty("_BumpMap") && m.GetTexture("_BumpMap")) ? m.GetTexture("_BumpMap").name : "-";
                sb.AppendLine($"  mat {m.name,-20} smooth={sm,-5} metal={mt,-5} bump={bump,-34} GI={m.globalIlluminationFlags}");
            }
            var lights = UnityEngine.Object.FindObjectsOfType<Light>(true);
            int strip = 0, other = 0;
            foreach (var l in lights) { if (l.name.StartsWith("L_STRIP_")) strip++; else other++; }
            sb.AppendLine($"  lights: L_STRIP_ {strip} 颗, 其它 {other} 颗({string.Join(",", lights.Where(l => !l.name.StartsWith("L_STRIP_")).Select(l => l.name).Distinct().Take(12))})");
            int noUv2 = 0, coveUv2Bad = 0, total = 0;
            foreach (var mf in UnityEngine.Object.FindObjectsOfType<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue; total++;
                // 注意通道号: Blender 第二层 UV → FBX UV1 → Unity 通道 1。查通道 2 会全部误报缺失
                var uv = new List<Vector2>(); mf.sharedMesh.GetUVs(1, uv);
                bool bad = uv.Count == 0;
                if (bad) noUv2++;
                if (bad && mf.name.StartsWith("ARCH_Cove")) coveUv2Bad++;
            }
            sb.AppendLine($"  UV2: {total} 网格, 缺 {noUv2}(其中 Cove {coveUv2Bad})");
            var head = GameObject.Find("ARCH_DoorWall_Head");
            if (head != null)
            {
                var r = head.GetComponent<MeshRenderer>();
                var so = new SerializedObject(r);
                sb.AppendLine($"  DoorWall_Head: lightmapIndex={r.lightmapIndex} scaleInLightmap={so.FindProperty("m_ScaleInLightmap").floatValue:F1} scaleOffset={r.lightmapScaleOffset}");
            }
            Debug.Log(sb.ToString());
        }

        // 只读转储: 关键网格的光贴图挂接/UV2/投影开关(排查"池消失"用, 不烘焙)
        [MenuItem("HST/转储关键网格光贴图信息")]        public static void DumpLmInfo()
        {
            EditorSceneManager.OpenScene(SceneDir + "/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            string[] want = { "ARCH_DoorWall_Head", "ARCH_LightPane", "ARCH_Cove", "ARCH_Floor", "ARCH_EndWall", "ARCH_DoorWall_Col" };
            foreach (var mf in UnityEngine.Object.FindObjectsOfType<MeshFilter>(true))
            {
                bool hit = false;
                foreach (var w in want) if (mf.name.StartsWith(w)) { hit = true; break; }
                if (!hit) continue;
                var r = mf.GetComponent<MeshRenderer>();
                var uv = new List<Vector2>(); mf.sharedMesh.GetUVs(1, uv);
                Debug.Log($"[LM] {mf.name,-30} lmIdx={r.lightmapIndex} tilingOff={r.lightmapScaleOffset} uv2={(uv.Count > 0 ? "有" : "无!")} " +
                          $"castShadows={r.shadowCastingMode} staticGI={(GameObjectUtility.GetStaticEditorFlags(mf.gameObject) & StaticEditorFlags.ContributeGI) != 0} mesh={mf.sharedMesh.name}");
            }
        }

        [MenuItem("HST/墙带实验轮(审计+门框GI隔离+真自发光)")]
        public static void WallProbeExperiments()
        {
            // ---- EXP1: 门框发光条 GI 隔离(只关 M_ARCH_FrameEmit, 其余不动) ----
            EditorSceneManager.OpenScene(SceneDir + "/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            WallProbeAudit();
            var frameMat = AssetDatabase.LoadAssetAtPath<Material>(MatDir + "/M_ARCH_FrameEmit.mat");
            var giB = frameMat.globalIlluminationFlags;
            frameMat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            EditorUtility.SetDirty(frameMat); AssetDatabase.SaveAssets();
            Debug.Log("[HST-EXP1] 门框发光条 GI 关 → 重烤 → 判据(与基线差 = 门框 GI 贡献)");
            BakeCorridorOnly();
            WallLightBreakdown.Run("_exp1");
            frameMat.globalIlluminationFlags = giB;
            EditorUtility.SetDirty(frameMat); AssetDatabase.SaveAssets();

            // ---- EXP2: 真自发光源(删灯串代理, 面板 GI 开, 间接采样 4096) ----
            // 与 Cycles 同构: 光源=发光几何本身, 灯槽端盖/内壁的遮挡由烘焙器自动算。
            EditorSceneManager.OpenScene(SceneDir + "/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            int killed = 0;
            foreach (var li in UnityEngine.Object.FindObjectsOfType<Light>(true))
                if (li.name.StartsWith("L_STRIP_")) { UnityEngine.Object.DestroyImmediate(li.gameObject); killed++; }
            var paneMat = AssetDatabase.LoadAssetAtPath<Material>(MatDir + "/M_ARCH_PaneEmit.mat");
            paneMat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive;
            EditorUtility.SetDirty(paneMat); AssetDatabase.SaveAssets();
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            Debug.Log($"[HST-EXP2] 真自发光: 删灯串 {killed} 颗, 面板 GI=BakedEmissive, 间接采样 4096(慢)");
            _indirectOverride = 4096;
            BakeCorridorOnly();
            _indirectOverride = 0;
            WallLightBreakdown.Run("_exp2");
            Debug.Log("[HST-EXP2] 场景停在此态(真自发光候选)。若败: HST/GI真发光轮 恢复灯串。");
        }

        // ---------------- 最终轮(2026-10-08): 贴图修复(_BaseMap) + 路线二选一 + 烘焙 + 出图 ----------------
        // 两条路共用骨架: 重建(材质带基色贴图) → 补 UV2 → 光源路线 → 烘焙(4096) → Polish → 探针 → 出图 → 线性读数。
        // emissive=true: 面板=真自发光源(Cycles 同构, EXP2 验证过); false: 灯串代理(需存盘!)。
        static void FinalRoundCore(bool emissive)
        {
            EnableLightmapUVs();
            Build("corridor");                 // 材质走修好的 CreateMat(_BaseMap + 基色白)
            FixMissingLightmapUVs();
            var scene = EditorSceneManager.GetActiveScene();
            if (emissive)
            {
                // 面板 GI 保持 CreateMat 的 BakedEmissive; 清掉可能残留的代理灯(灯串/面积光, 幂等)
                int k = 0;
                foreach (var li in UnityEngine.Object.FindObjectsOfType<Light>(true))
                    if (li.name.StartsWith("L_STRIP_") || li.name.StartsWith("L_AREA_"))
                    { UnityEngine.Object.DestroyImmediate(li.gameObject); k++; }
                if (k > 0) EditorSceneManager.SaveScene(scene);
                Debug.Log($"[HST-FINAL] 真自发光路线: 清残留代理灯 {k} 颗, 面板=光源");
            }
            else
            {
                ApplyStripIllumination();      // 灯串 + 面板 GI 关
                EditorSceneManager.SaveScene(scene);   // 灯串必须存盘, 否则 BakeScenes 重开场景即丢
            }
            _indirectOverride = 4096;
            // 环境光口径对齐 Blender: 世界背景 0.008 平光(场景里残留的 Trilight 赤道 0.114 会被
            // 烤进每个纹素当地板)。Build 会写 ambientLight, 这里再钉死模式=Flat。
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = AmbientByTag["corridor"];
            BakeCorridorOnly();
            _indirectOverride = 0;
            Polish();
            WritePostDefaults();          // 后处理锁默认(用户令 2026-10-08)
            BakeProbeManual();
            Shoot();
            DiagLinear();
        }

        [MenuItem("HST/最终轮-真自发光(贴图+发光源+烘焙+出图)")]
        public static void FinalCorridorEmissive() { FinalRoundCore(true); }

        [MenuItem("HST/最终轮-灯串代理(贴图+灯串+烘焙+出图)")]
        public static void FinalCorridorCage() { FinalRoundCore(false); }

        // EXP3: 真自发光 + **关 L_LUMEN 灯阵** → 量"地板"里 LUMEN 的份额。
        // 若池/中比值回到 Blender 的 2.0, 说明 LUMEN 的 AREA→灯阵换算系数(kArea, 无GI时代标定)
        // 在有 GI 后把地板抬过头 → 按 Blender 侧同口径分解重标 kArea。
        [MenuItem("HST/墙带实验轮EXP3(真自发光+关LUMEN灯阵)")]
        public static void WallProbeExp3NoLumen()
        {
            EditorSceneManager.OpenScene(SceneDir + "/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            int off = 0;
            foreach (var li in UnityEngine.Object.FindObjectsOfType<Light>(true))
                if (li.name.StartsWith("L_LUMEN_")) { li.enabled = false; off++; }
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            Debug.Log($"[HST-EXP3] 关 LUMEN 灯 {off} 颗 → 烤(4096) → 判据");
            _indirectOverride = 4096;
            BakeCorridorOnly();
            _indirectOverride = 0;
            WallLightBreakdown.Run("_exp3");
            Debug.Log("[HST-EXP3] 完成。灯保持关闭状态(下轮决定 kArea)。");
        }

        // EXP4: 当前态(K=0.95+贴图) + **关门区灯组**(DoorTop/EndWash/Fill/SeamGlow)→ 量门组在墙带的份额。
        // 背景: 最终轮2 地板/天花/大墙与 Blender ±5~9% 内, 唯门头墙带 +0.2 过亮 —— 门区三盏
        // AREA 灯的近距离 1/d² 换算嫌疑最大。
        [MenuItem("HST/墙带实验轮EXP4(关门区灯组)")]
        public static void WallProbeExp4NoDoorGroup()
        {
            EditorSceneManager.OpenScene(SceneDir + "/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            int off = 0;
            foreach (var li in UnityEngine.Object.FindObjectsOfType<Light>(true))
                if (li.name.Contains("DoorTop") || li.name.Contains("EndWash") || li.name.Contains("Fill")
                    || li.name.StartsWith("HST_SeamGlow"))
                { li.enabled = false; off++; }
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            Debug.Log($"[HST-EXP4] 关门区灯组 {off} 颗 → 烤(4096) → 判据");
            _indirectOverride = 4096;
            BakeCorridorOnly();
            _indirectOverride = 0;
            WallLightBreakdown.Run("_exp4");
            Debug.Log("[HST-EXP4] 完成(灯保持关闭)。");
        }

        // ---------------- EXP6(2026-10-09): 面板照明 = RECT AREA 烘焙面积光 ----------------
        // 方法库方案A(docs/Unity画面Blender质感_方法库与落地计划_2026-10-09.md)。
        // 依据: ①4× 定论(台账)证明材质发光缩放走不通; ②LUMEN 灯阵证明"灯"路径可校准 ±1%;
        // ③面积光只进 lightmap, 物理行为与 Cycles 面积发射器最接近, 无点光 1/r² 近距伪影。
        // 面板材质 EmissiveIsBlack(可见发光保留, 不双计)。
        static void BuildPaneAreaLights()
        {
            foreach (var li in UnityEngine.Object.FindObjectsOfType<Light>(true))
                if (li.name.StartsWith("L_AREA_")) UnityEngine.Object.DestroyImmediate(li.gameObject);
            // 场景中心(判朝向用: 面板法线朝走廊内)
            var cmin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var cmax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var r in UnityEngine.Object.FindObjectsOfType<MeshRenderer>(true))
            { cmin = Vector3.Min(cmin, r.bounds.min); cmax = Vector3.Max(cmax, r.bounds.max); }
            var sceneC = (cmin + cmax) / 2f;

            var paneMat = AssetDatabase.LoadAssetAtPath<Material>(MatDir + "/M_ARCH_PaneEmit.mat");
            float L = 6.65f; var tint = new Color(0.92f, 0.96f, 1f);
            if (paneMat != null && paneMat.HasProperty("_EmissionColor"))
            { var c = paneMat.GetColor("_EmissionColor"); L = Mathf.Max(c.r, Mathf.Max(c.g, c.b)); tint = new Color(c.r / L, c.g / L, c.b / L); }
            int n = 0;
            foreach (var mr in UnityEngine.Object.FindObjectsOfType<MeshRenderer>(true))
            {
                if (!mr.name.StartsWith("ARCH_LightPane")) continue;
                var b = mr.bounds;
                // 薄轴 = 法线轴(面板是薄板); 两长轴 = 面积光矩形尺寸
                int thin = 0; if (b.size.y < b.size[thin]) thin = 1; if (b.size.z < b.size[thin]) thin = 2;
                var dir = Vector3.zero; dir[thin] = 1f;
                // 朝向走廊内: b.center±0.1·dir 哪个离场景中心近, 法线就朝哪边
                if ((b.center + dir * 0.1f - sceneC).sqrMagnitude > (b.center - dir * 0.1f - sceneC).sqrMagnitude) dir = -dir;
                var rect = new Vector2(b.size[(thin + 1) % 3], b.size[(thin + 2) % 3]);

                var go = new GameObject($"L_AREA_{mr.name}");
                var li = go.AddComponent<Light>();
                li.type = LightType.Rectangle;            // 面积光: 只影响烘焙光照贴图
                // 本版(2022.3.33f1c1)无 Light.size/sizeY 属性 → 直接写序列化字段(跨版本最稳)
                var lso = new SerializedObject(li);
                var ap = lso.FindProperty("m_AreaSize");
                if (ap != null) { ap.vector2Value = rect; lso.ApplyModifiedPropertiesWithoutUndo(); }
                li.color = tint;
                li.intensity = L * rect.x * rect.y;        // 初始配平=辐射×面积, 标定轮再乘系数
                li.range = 40f;
                li.shadows = LightShadows.Soft;
                li.lightmapBakeType = LightmapBakeType.Baked;
                go.transform.position = b.center;
                go.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
                Debug.Log($"[HST-AREA] {mr.name}: 尺寸 {rect.x:F2}x{rect.y:F2}m 朝 {dir} 强度 {li.intensity:F1}");
                n++;
            }
            Debug.Log($"[HST-AREA] 面板面积光 {n} 盏(几何 1:1 来自 ARCH_LightPane)");
        }

        [MenuItem("HST/EXP6(面板面积光+烘焙+出图)")]
        public static void Exp6PaneAreaLights()
        {
            EditorSceneManager.OpenScene(SceneDir + "/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            BuildPaneAreaLights();
            var paneMat = AssetDatabase.LoadAssetAtPath<Material>(MatDir + "/M_ARCH_PaneEmit.mat");
            paneMat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;  // 只留可见发光
            EditorUtility.SetDirty(paneMat); AssetDatabase.SaveAssets();
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());   // 坑⑦: 灯必须存盘
            Debug.Log("[HST-EXP6] 面积光+面板GI关 → 烤 4096 → 出图");
            BakeCorridorOnly4096();
            var camGo = GameObject.Find("CAM_01_Hero");
            PostDefaults.ShotPublic(camGo.GetComponent<Camera>(),
                @"F:/VSCode/工业级渲染/renders/qa_2026-10-08/exp6_面积光.png");
            Debug.Log("[HST-EXP6] 完成。标定系数 = 目标池亮度/实测池亮度, 下一轮乘上重烤即终版。");
        }

        // EXP7(方案C, 方法库 docs/Unity画面Blender质感_方法库与落地计划_2026-10-09.md):
        // Cycles 在探针位渲的 6 面 EXR(tmp/cycles_cubemap.py 产出)→ 组 Cubemap → 探针 Custom。
        // 镜面反射从此是 Cycles 算的(结构性差距⑤的免费解), 不再是 256px 实时近似。
        [MenuItem("HST/EXP7(Cycles环境贴图→Custom探针)")]
        public static void Exp7CyclesProbeCubemap()
        {
            string src = @"F:/VSCode/工业级渲染/tmp/cubefaces";
            string dst = "Assets/Art/Environment/HST_Kit/ReflectionProbes/CyclesFaces";
            Directory.CreateDirectory(dst);
            var faceMap = new (string f, CubemapFace cf)[] {
                ("px", CubemapFace.PositiveX), ("nx", CubemapFace.NegativeX),
                ("py", CubemapFace.PositiveY), ("ny", CubemapFace.NegativeY),
                ("pz", CubemapFace.PositiveZ), ("nz", CubemapFace.NegativeZ) };
            var cb = new Cubemap(256, TextureFormat.RGBA32, true) { name = "RP_Cycles_Corridor" };
            foreach (var (f, cf) in faceMap)
            {
                string to = dst + "/" + f + ".exr";
                File.Copy(src + "/" + f + ".exr", to, true);
                AssetDatabase.ImportAsset(to, ImportAssetOptions.ForceUpdate);
                var imp = AssetImporter.GetAtPath(to) as TextureImporter;
                imp.textureType = TextureImporterType.Default; imp.sRGBTexture = false; imp.SaveAndReimport();
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(to);
                var cols = tex.GetPixels();
                cb.SetPixels(cols, cf);
                Debug.Log($"[HST-EXP7] 面 {f} → {cf} (avg {cols.Average(c => c.r):F3})");
            }
            cb.Apply();
            string cbPath = "Assets/Art/Environment/HST_Kit/ReflectionProbes/RP_Cycles_Corridor.asset";
            if (File.Exists(cbPath)) AssetDatabase.DeleteAsset(cbPath);
            AssetDatabase.CreateAsset(cb, cbPath);
            EditorSceneManager.OpenScene(SceneDir + "/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            foreach (var rp in UnityEngine.Object.FindObjectsOfType<ReflectionProbe>(true))
                if (rp.name.StartsWith("HST_ReflectionProbe"))
                {
                    rp.mode = ReflectionProbeMode.Custom;
                    rp.customBakedTexture = cb;
                    rp.intensity = 1f;               // Cycles 值即真值, 不再打折
                    EditorUtility.SetDirty(rp);
                    Debug.Log($"[HST-EXP7] {rp.name} → Custom(Cycles), intensity 1.0");
                }
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            Debug.Log("[HST-EXP7] 完成。批处理渲染不采样探针, 验收走 Play 出图。");
        }

        // ---------------- Play 模式取证 v2: 无协程, update 计数 + 看门狗 ----------------
        static int _playFrames;
        static bool _playShot;
        [MenuItem("HST/Play模式取证v2")]
        public static void PlayShoot2()
        {
            EditorSceneManager.OpenScene(SceneDir + "/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            // 进 Play 触发 domain reload → 静态与 update 订阅全清, 必须等 EnteredPlayMode 再挂泵
            EditorApplication.playModeStateChanged += st =>
            {
                if (st == PlayModeStateChange.EnteredPlayMode)
                { _playFrames = 0; _playShot = false; EditorApplication.update += PlayPump; }
            };
            EditorApplication.isPlaying = true;
        }
        static void PlayPump()
        {
            _playFrames++;
            if (_playFrames > 3000) { Debug.LogWarning("[HST-PLAY] 看门狗超时"); EditorApplication.Exit(3); }
            if (!EditorApplication.isPlaying || _playFrames < 120 || _playShot) return;
            _playShot = true;
            foreach (string cn in new[] { "01", "04" })
            {
                var cam = System.Array.Find(UnityEngine.Object.FindObjectsOfType<Camera>(), c => c.name.Contains(cn));
                if (cam == null) { Debug.LogWarning("[HST-PLAY] 相机缺 " + cn); continue; }
                float ml; int ov;
                var px = RenderRaw(cam, 960, 540, out ml, out ov);
                var tex = new Texture2D(960, 540, TextureFormat.RGBA32, false);
                var cols = new Color32[px.Length];
                for (int i = 0; i < px.Length; i++)
                    cols[i] = new Color32((byte)(Mathf.LinearToGammaSpace(px[i].r) * 255f + 0.5f),
                                          (byte)(Mathf.LinearToGammaSpace(px[i].g) * 255f + 0.5f),
                                          (byte)(Mathf.LinearToGammaSpace(px[i].b) * 255f + 0.5f), 255);
                tex.SetPixels32(cols); tex.Apply();
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(@"F:/VSCode/工业级渲染/tmp", "play_" + cn + ".png"), tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);
                Debug.Log($"[HST-PLAY] {cn} maxLin={ml:F3}");
            }
            EditorApplication.isPlaying = false;
            EditorApplication.Exit(0);
        }

        [MenuItem("HST/反射链路终审(重开场景状态)")]
        public static void ReflectionAudit()
        {
            var scene = EditorSceneManager.OpenScene(SceneDir + "/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            var sky = RenderSettings.skybox;
            Debug.Log($"[HST-AUDIT] skybox={(sky != null ? sky.name : "NULL")} mode={RenderSettings.defaultReflectionMode} " +
                      $"intensity={RenderSettings.reflectionIntensity:F2} shader={(sky != null ? sky.shader.name : "-")}");
            if (sky != null)
            {
                var t = sky.GetTexture("_FrontTex");
                Debug.Log($"[HST-AUDIT] sky._FrontTex={(t != null ? t.name + " " + t.GetType().Name : "NULL!")}");
                var cube = t as Cubemap;
                if (cube != null)
                {
                    var px = cube.GetPixels(CubemapFace.PositiveZ);
                    float mean = 0f, mx = 0f;
                    foreach (var c in px) { mean += 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b; mx = Mathf.Max(mx, c.maxColorComponent); }
                    Debug.Log($"[HST-AUDIT] cube+mip={cube.mipmapCount} format={cube.format} 面PZ mean={mean / px.Length:F4} max={mx:F2}");
                }
            }
            var mr = UnityEngine.Object.FindObjectsOfType<MeshRenderer>(true)[0];
            Debug.Log($"[HST-AUDIT] 首个Renderer probeUsage={mr.reflectionProbeUsage}");
            var cam = System.Array.Find(UnityEngine.Object.FindObjectsOfType<Camera>(true), c => c.name.Contains("04"));
            RawStats(RenderRawC(cam, 320, 180), out _, out _, out _);
            RawStats(RenderRawC(cam, 320, 180), out _, out _, out _);   // 暖机两帧(首帧偏差)
            var rp = UnityEngine.Object.FindObjectOfType<ReflectionProbe>();
            float prevRpi = rp != null ? rp.intensity : 1f;
            foreach (float k in new[] { 1f, 0f, 4f })
            {
                if (rp != null) rp.intensity = k;
                float a, p, x; RawStats(RenderRawC(cam, 320, 180), out a, out p, out x);
                Debug.Log($"[HST-AUDIT] rp.intensity={k:F1}: mean={a:F4} p50={p:F4} max={x:F4}");
            }
            if (rp != null) rp.intensity = prevRpi;
        }

        [MenuItem("HST/终校准(反射1.1+缝灯0.075)+重烤+探针+出图")]
        public static void FinalCalibDefault() { FinalCalib(1.1f, 0.075f); }

        public static void FinalCalib(float reflectionK, float seamI)
        {
            var scene = EditorSceneManager.OpenScene(SceneDir + "/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            foreach (var li in UnityEngine.Object.FindObjectsOfType<Light>(true))
                if (li.name.StartsWith("HST_SeamGlow")) li.intensity = seamI;
            EditorSceneManager.SaveScene(scene);
            BakeCorridorOnly();
            Polish();
            RenderSettings.reflectionIntensity = reflectionK;   // Polish 不动这个, 单独赋值
            EditorSceneManager.SaveScene(scene);
            BakeProbeManual();   // BakeProbeManual 会重设 reflectionIntensity, 之后覆盖
            RenderSettings.reflectionIntensity = reflectionK;
            EditorSceneManager.SaveScene(scene);
            Shoot();
        }

        [MenuItem("HST/五轮: 缝灯8颗窄带+门叶加密+DirectShare0.85+探针+出图")]
        public static void CorridorPass5()
        {
            FixCorridorDoor();      // 重建 8 颗窄缝灯(清旧3颗) + DoorTop 维持现位
            BakeCorridorOnly();     // 重烤(含门叶 lightmap ×8)
            Polish();               // 发光重定标 0.95(幂等) + 出图
            BakeProbeManual();      // 重建手工 HDR 探针(0.95 灯带)
            Shoot();
        }

        [MenuItem("HST/四轮: EmissionK0.80+天空盒反射0.6+探针+出图")]
        public static void CorridorPass4()
        {
            Polish();             // 发光重定标到 0.80(幂等)
            BakeProbeManual();    // 用 0.80 灯带重建探针 + reflectionIntensity=0.6
            Shoot();
        }

        [MenuItem("HST/门区三轮(灯带0.95+门校准)+重烤+探针+出图")]
        public static void CorridorPass3()
        {
            var scene = EditorSceneManager.OpenScene(SceneDir + "/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            foreach (var li in UnityEngine.Object.FindObjectsOfType<Light>(true))
            {
                if (li.name.Contains("DoorTop")) li.intensity *= 0.5f;
                if (li.name.StartsWith("HST_SeamGlow")) li.intensity = 0.6f;
            }
            EditorSceneManager.SaveScene(scene);
            BakeCorridorOnly();   // 重烤光照贴图(门灯变了); 会把 customReflection 指回钳位版(反正被无视)
            Polish();             // 发光重定标到 0.95(幂等) + 出图
            BakeProbeManual();    // 用 0.95 的灯带重建手工 HDR 探针(天空盒绑定保留在场景里)
            Shoot();              // 最终出图(带新探针)
        }

        [MenuItem("HST/门区二轮校准+重烤+手工探针+出图")]
        public static void CorridorPass2()
        {
            var scene = EditorSceneManager.OpenScene(SceneDir + "/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            foreach (var li in UnityEngine.Object.FindObjectsOfType<Light>(true))
            {
                if (li.name.Contains("DoorTop")) li.intensity *= 0.45f;
                if (li.name.StartsWith("HST_SeamGlow")) li.intensity = 0.2f;
            }
            EditorSceneManager.SaveScene(scene);
            BakeCorridorOnly();
            BakeProbeManual();
            Shoot();
        }

        // 直射取证模式: 烘焙只算直射(bounces=0), 用来回答"墙上的结构在直射还是在间接"
        static bool _directOnlyProbe;
        // 附加: 关掉所有自发光材质的 GI 贡献(EmissiveIsBlack), 把"实灯的直接光"单独量出来
        static bool _emissiveGIOffProbe;
        // 附加: 灯槽罩墙加厚(×8, 向上长) —— 验证"薄板 + 射线偏移导致灯槽不挡光"
        static bool _thickCoveProbe;
        // 实验轮: 临时抬高间接采样数(默认 0 = 用 1024)。真自发光源走蒙特卡洛采样, 1024 不够
        internal static int _indirectOverride;   // internal: 走廊扩建器(HstCorridorExtend)要走"无 Build 版"最终轮序列
        // internal: 外部流程(走廊二期 HstCorridorStage2)在"手工反射盒 BakeProbeManual 接管反射"的前提下
        // 跳过 BakeScenes 内的探针烘烤 —— 走廊尺度下它要 ~35-40 min(4096 间接采样 × 6 面 256²),
        // 而产物随后被 BakeProbeManual 的手工 1024 cube 整体覆盖(含 rp.customBakedTexture 与
        // RenderSettings 两处绑定), 属纯白烧。
        internal static bool _skipProbeBake;

        static void BakeScenes(string[] sceneNames)
        {            foreach (var sceneName in sceneNames)
            {
                var scene = EditorSceneManager.OpenScene(SceneDir + "/" + sceneName + ".unity", OpenSceneMode.Single);
                // 1) 全网格 LightmapStatic + 全灯改 Baked(贡献进光照贴图, 不与烘焙叠加双份)
                //    大尺度场景按比例降 texel 密度: 300m 竖井全分辨率烤不动也没必要
                float lmScale = sceneName == "SC_Atrium_Concept" ? 0.10f
                              : sceneName == "SC_Atrium_Military" ? 0.60f : 1f;
                foreach (var mf in UnityEngine.Object.FindObjectsOfType<MeshFilter>(true))
                {
                    var go = mf.gameObject;
                    // 2026-10-10 扩建守卫: 门外景观(HST_EXT_ROOT 子树: 城市背景/观景台/外壳)不进烘焙
                    // —— 否则城市(95m 外, ±40m 展开)会把包围盒/探针网格/光照贴图尺寸全部炸掉。
                    if (InExtSubtree(go.transform)) continue;
                    if (go.transform.root.name == "HST_Player") continue;   // 主控是动态件, 不当静态几何烤
                    var f = GameObjectUtility.GetStaticEditorFlags(go);
                    GameObjectUtility.SetStaticEditorFlags(go, f | StaticEditorFlags.ContributeGI);
                    var rso = new SerializedObject(mf.GetComponent<Renderer>());
                    var sProp = rso.FindProperty("m_ScaleInLightmap");
                    // 门缝发光带宽 ~±8cm, 全局 6 texel/m(16.7cm 纹素)采不出来 —— 门区局部 ×8=2cm 纹素
                    // 2026-10-08: 门头墙带 ARCH_DoorWall_Head 之前漏在名单外, 纹素 10cm → 灯带亮池/
                    // 门框阴影的横向结构被抹平(实测直射: Blender 0.082 谷 vs Unity 0.152 平)。
                    // 门区所有面(Frame/DoorWall/Leaf/Seam/Vest)一律加密。
                    float sc = (sceneName == "SC_Corridor_Pragmata" &&
                                (go.name.Contains("DoorLeaf") || go.name.Contains("Vest") || go.name.Contains("DoorSeam") ||
                                 go.name.Contains("DoorWall") || go.name.Contains("Frame"))) ? lmScale * 8f : lmScale;
                    if (sProp != null) { sProp.floatValue = sc; rso.ApplyModifiedPropertiesWithoutUndo(); }
                }
                // 灯型: 走廊用**纯 Baked** —— 直射+间接全部烤进光照贴图, 实时镜面项归零。
                // 为什么: 用户实测"墙壁像镜面反射不像漫反射" —— 根因是点灯串的**实时镜面高光**在
                // 光滑面(粗糙度 0.25)上打出一排小硬斑, 而 Blender 侧是 16.6m 面光源的柔和拉长亮带。
                // 纯 Baked 后墙面只剩烘焙的漫射+间接(平滑), 环境光泽由烘焙反射探针补。
                // (早先"纯 Baked 会塌成平灰"的结论是在 LDR 夹断的假象下得出的, 已作废。)
                bool lightsBaked = sceneName == "SC_Corridor_Pragmata";   // 走廊纯 Baked: 直接光+光追阴影(抛物线亮斑/箱子影子)+间接全进 lightmap(镜之边缘式)
                foreach (var li in UnityEngine.Object.FindObjectsOfType<Light>(true))
                {
                    // 门区两组灯只要 lightmap(无镜面需求) → 恒 Baked。
                    // 大扫除(2026-10-09): 删 ×0.45 手拍折减 —— 那是"AreaK 抬升+Mixed 穿墙"时代的
                    // 互补补丁; 现在 kArea 已按物理口径重标、走廊全 Baked, 灯强一律不二次打折。
                    // ⚠ 2026-10-13 修坑: 这条门区启发式**必须限定在走廊场景内**。它按灯名子串匹配,
                    // 而概念中庭的竖井补光叫 `L_ShaftFill` —— "Fill" 命中后被改成 Baked 且强度 ×0.33
                    // (0.155→0.05115), 竖井直接烤黑(实测俯视竖井机位 mean 0.563→0.003, 99.7% 死黑)。
                    bool doorGI = sceneName == "SC_Corridor_Pragmata" &&
                                  (li.name.Contains("DoorTop") || li.name.Contains("EndWash") ||
                                   li.name.Contains("Fill") || li.name.StartsWith("HST_SeamGlow"));
                    li.lightmapBakeType = (lightsBaked || doorGI) ? LightmapBakeType.Baked : LightmapBakeType.Mixed;
                    if (doorGI) li.intensity *= 0.33f;   // 校准第二档: 门区灯组压墙带中段(过热源)
                }
                //                                (纯 Baked 会杀掉高光渐变, 白板塌成平灰 — 2026-10-06 视觉对拍实测)
                // 2) 场景包围盒
                var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                foreach (var r in UnityEngine.Object.FindObjectsOfType<MeshRenderer>(true))
                {
                    if (InExtSubtree(r.transform)) continue;   // 包围盒只算走廊本体(理由见上守卫)
                    min = Vector3.Min(min, r.bounds.min); max = Vector3.Max(max, r.bounds.max);
                }
                // 3) 光探针网格(动态主控从探针采样烘焙 GI)
                var oldLP = GameObject.Find("HST_LightProbes");
                if (oldLP != null) UnityEngine.Object.DestroyImmediate(oldLP);
                var lpGo = new GameObject("HST_LightProbes");
                var lpg = lpGo.AddComponent<LightProbeGroup>();
                int nx = Mathf.Clamp((int)((max.x - min.x) / 2.5f), 4, 14);
                int nz = Mathf.Clamp((int)((max.z - min.z) / 2.5f), 3, 10);
                var pts = new List<Vector3>();
                for (int i = 0; i < nx; i++)
                    for (int j = 0; j < nz; j++)
                        foreach (float dy in new[] { 0.5f, 1.4f, 2.3f })
                            pts.Add(new Vector3(
                                Mathf.Lerp(min.x, max.x, i / (float)(nx - 1)),
                                min.y + dy,
                                Mathf.Lerp(min.z, max.z, j / (float)(nz - 1))));
                lpg.probePositions = pts.ToArray();
                // 4) 反射探针(实时分片刷新, 玻璃/高光滑面取环境反射)
                for (int k = 0; ; k++)
                {
                    var old = GameObject.Find("HST_ReflectionProbe_" + k);
                    if (old == null) break;
                    UnityEngine.Object.DestroyImmediate(old);
                }
                var rpGo = new GameObject("HST_ReflectionProbe_0");
                var rp = rpGo.AddComponent<ReflectionProbe>();
                // Realtime Every-Frame(2026-10-07 终版): 批处理 A/B 实证 Baked/Custom 探针在手动
                // cam.Render() 下采样恒 0, 而墙面的"粗糙镜面映灯带"(质感主诉)只能靠探针路径
                // (面积光镜面 vs 点光镜面有 (尺寸/距离)² 级失配, 点光补不出来)。Realtime 是
                // 运行时标准路径: Play/构建必然工作, 玩家移动时反射还随视点正确变化。
                rp.mode = ReflectionProbeMode.Baked;   // 2026-10-09: 实时探针编辑器非运行态不刷新=墙无反光; 改烘焙探针, 烤制时自动出 cubemap
                rp.refreshMode = ReflectionProbeRefreshMode.EveryFrame;
                rp.timeSlicingMode = ReflectionProbeTimeSlicingMode.IndividualFaces;
                rp.intensity = ProbeIntensityByTag[sceneName];
                rp.resolution = 256;
                rp.importance = 1;
                rp.transform.position = new Vector3((min.x + max.x) / 2f, min.y + 1.6f, (min.z + max.z) / 2f);
                var so = new SerializedObject(rp);   // 2022.3 的 boxSize/timeSlicing 走序列化属性, 版本间最稳
                var bs = so.FindProperty("m_BoxSize");
                if (bs != null) bs.vector3Value = new Vector3(max.x - min.x, Mathf.Min(max.y - min.y, 24f), max.z - min.z);
                so.ApplyModifiedPropertiesWithoutUndo();
                // 5) 烘焙(渐进 CPU)
                var ls = new LightingSettings();
                ls.lightmapper = LightingSettings.Lightmapper.ProgressiveGPU;   // 2026-10-09: GPU已恢复, CPU后端(历史习惯)→GPU(实测Blender 12.2x, 烘焙同理提速)
                ls.bakedGI = true;
                ls.realtimeGI = false;
                ls.lightmapResolution = sceneName == "SC_Corridor_Pragmata" ? 24 : 10;   // 走廊加密 2.4x: 墙面横向结构要采得出来
                // 路径追踪级配置(2026-10-07 调研: 官方文档+社区实测): 反弹 2→5(室内靠间接),
                // 间接采样 1024, 直接采样 64 —— 烘焙时间换质量, 与 Blender 同级
                ls.maxBounces = _directOnlyProbe ? 0 : 5;                 // 0 = 只算直射(取证用)
                ls.indirectSampleCount = _directOnlyProbe ? 1
                    : (_indirectOverride > 0 ? _indirectOverride : 1024);
                ls.directSampleCount = 64;    // int/float 双兼容(隐式转换两边都过)

                ls.lightmapPadding = 2;
                Lightmapping.lightingSettings = ls;
                Lightmapping.Clear();
                bool ok = Lightmapping.Bake();
                // 烘焙反射探针(必须显式烤: 探针的 cubemap 不是光照贴图的一部分)
                // Realtime 探针不走这条: 运行时自刷新, 这里烤了也会被下面的 Custom 覆盖顶掉
                bool rpOk = true;
                string rpPath = "Assets/Art/Environment/HST_Kit/ReflectionProbes/RP_" + sceneName + ".exr";
                if (!_skipProbeBake && rp.mode == ReflectionProbeMode.Baked)
                    rpOk = Lightmapping.BakeReflectionProbe(rp, rpPath);
                // **必须显式把烤好的 cubemap 挂上去**: 场景里只写 m_Mode/m_Resolution 是不够的 ——
                // 实测探针 YAML 里根本没有 cubemap 引用(m_CustomBakedTexture: 0), 于是四档 intensity
                // 数值完全相同(贡献恒为 0)。改用 Custom 模式 + 显式引用, 任何渲染路径都取得到。
                if (rp.mode == ReflectionProbeMode.Baked)
                AssetDatabase.ImportAsset(rpPath, ImportAssetOptions.ForceUpdate);
                var imp = AssetImporter.GetAtPath(rpPath) as TextureImporter;
                if (imp != null)
                {
                    bool ch = false;
                    if (imp.textureShape != TextureImporterShape.TextureCube) { imp.textureShape = TextureImporterShape.TextureCube; ch = true; }
                    if (imp.textureCompression != TextureImporterCompression.Uncompressed) { imp.textureCompression = TextureImporterCompression.Uncompressed; ch = true; }
                    if (imp.sRGBTexture) { imp.sRGBTexture = false; ch = true; }   // EXR 存线性辐照度
                    if (imp.mipmapEnabled == false) { imp.mipmapEnabled = true; ch = true; }
                    if (ch) imp.SaveAndReimport();
                }
                var cube = rp.mode == ReflectionProbeMode.Baked ? AssetDatabase.LoadAssetAtPath<Cubemap>(rpPath) : null;
                if (cube != null)
                {
                    rp.mode = ReflectionProbeMode.Custom; rp.customBakedTexture = cube;
                    // 全局环境反射也喂同一个 cubemap: 实测**逐渲染器探针通道在手动 cam.Render() 下
                    // 贡献恒为 0**(cubemap 有值、盒子覆盖、BlendProbes 都对, Δmean=0.00000),
                    // 而 SpecCube0 的回退源是天空盒(内置程序化 Default-Skybox)。把回退源换成我们的
                    // 烘焙 cubemap, 环境镜面项就有值了 —— 单探针场景两条通道等价, 走通的这条。
                    // 顺序必须"先贴图后模式": customReflection 为 null 时 getter 会抛异常。
                    RenderSettings.customReflection = cube;
                    RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
                }
                else Debug.LogWarning("[HST-BAKE] cubemap 载入失败: " + rpPath);
                EditorSceneManager.SaveScene(scene);
                Debug.Log($"[HST-BAKE] {sceneName}: bake={(ok ? "OK" : "FAIL")} probes={pts.Count} " +
                          $"lightmaps={(LightmapSettings.lightmaps != null ? LightmapSettings.lightmaps.Length : 0)} " +
                          $"反射探针={(rpOk ? "OK" : "FAIL")} mode={rp.mode} res={rp.resolution}");
            }
        }

        // ---------------- 主控入场 (复用团队 Player.prefab: PlayerMotor+输入+Cinemachine 全家桶) ----
        static readonly Dictionary<string, Vector3[]> SpawnByScene = new Dictionary<string, Vector3[]>
        {
            // u = (-x, z, -y); [0]=出生点 [1]=面向
            ["SC_Corridor_Pragmata"] = new[] { new Vector3(38.5f, 0.1f, 0f), new Vector3(-1, 0, 0) },   // 2026-10-10 扩建后: 远端出生, 面朝大门全速冲刺
            ["SC_Atrium_Concept"]    = new[] { new Vector3(0f, 0.1f, -6f), new Vector3(0, 0, 1) },
            ["SC_Atrium_Military"]   = new[] { new Vector3(26f, 0.1f, 10f), new Vector3(-1, 0, 0) },
        };

        [MenuItem("HST/Add Walk Player To Scenes")]
        public static void AddWalkPlayer()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab");
            if (prefab == null) { Debug.LogError("[HST] Assets/Prefabs/Player.prefab 不存在"); return; }
            foreach (var sceneName in new[] { "SC_Corridor_Pragmata", "SC_Atrium_Concept", "SC_Atrium_Military" })
            {
                var scene = EditorSceneManager.OpenScene(SceneDir + "/" + sceneName + ".unity", OpenSceneMode.Single);
                // 1) 全场景网格配碰撞体(静态布景, 凹网格即可; 幂等)
                int added = 0;
                foreach (var mf in UnityEngine.Object.FindObjectsOfType<MeshFilter>(true))
                {
                    if (mf.GetComponent<Collider>() != null) continue;
                    if (mf.transform.root.name == "HST_Player") continue;   // 主控视觉网格不配碰撞体(会和运动胶囊自撞)
                    if (mf.gameObject.isStatic || mf.transform.parent != null)
                    {
                        mf.gameObject.AddComponent<MeshCollider>();
                        added++;
                    }
                }
                // 2) 幂等放置主控
                var old = GameObject.Find("HST_Player");
                if (old != null) UnityEngine.Object.DestroyImmediate(old);
                var p = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                p.name = "HST_Player";
                var sp = SpawnByScene[sceneName];
                p.transform.position = sp[0];
                p.transform.rotation = Quaternion.LookRotation(sp[1], Vector3.up);

                // 3) 场景层跟随相机: Player.prefab 只带 Cinemachine 虚拟相机(与 TestScene 同构),
                //    实体 Camera+Brain+监听要放场景里; 否则 Play 会被静置截图机霸占画面
                var oldCam = GameObject.Find("HST_FollowCamera");
                if (oldCam != null) UnityEngine.Object.DestroyImmediate(oldCam);
                var camGo = new GameObject("HST_FollowCamera");
                camGo.tag = "MainCamera";
                var cam = camGo.AddComponent<Camera>();
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = 2000f;
                cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;  // 无 ACES 时高光直接削顶=Play 过曝
                camGo.AddComponent<AudioListener>();
                camGo.AddComponent<Cinemachine.CinemachineBrain>();
                camGo.transform.position = sp[0] + Vector3.up * 1.6f - sp[1] * 3f;
                camGo.transform.rotation = Quaternion.LookRotation(sp[1], Vector3.up);
                // 4) 静置截图机默认关掉(cam.Render 手动渲染不受 enabled 影响, Shoot 时会临时开)
                foreach (var c in UnityEngine.Object.FindObjectsOfType<Camera>(true))
                    if (c.name.StartsWith("CAM_")) c.enabled = false;

                EditorSceneManager.SaveScene(scene);
                Debug.Log($"[HST-PLAYER] {sceneName}: colliders+{added}, HST_Player @{sp[0]}, HST_FollowCamera(CinemachineBrain)");
            }
        }

        // 相机地标校验: 已知世界点应落在的视口坐标(期望值由 Blender 侧 cam_expected.py 算出),
        // 同时在地标视口位置采样渲染像素, 顺带诊断"场景到底画没画出来"。
        [MenuItem("HST/Cam Check + Diagnostics")]
        public static void CamCheck()
        {
            var LM = new Dictionary<string, Vector3>
            {
                ["corridor"]  = new Vector3(-18.27f, 1.19f, 0f),   // 门心
                ["atrium"]    = new Vector3(-64f, 10f, 116f),      // 竖井巨构核心
                ["atrium_v6"] = new Vector3(0f, 22.4f, -4f),       // 塔顶信标
            };
            foreach (var tag in new[] { "corridor", "atrium", "atrium_v6" })
            {
                string mp = Path.Combine(Application.dataPath, "HST_Prags/manifests/manifest_" + tag + ".json");
                if (!File.Exists(mp)) continue;
                var man = JsonUtility.FromJson<Manifest>(File.ReadAllText(mp));
                EditorSceneManager.OpenScene(SceneDir + "/" + man.scene + ".unity", OpenSceneMode.Single);
                var renderers = UnityEngine.Object.FindObjectsOfType<MeshRenderer>(true);
                var lights = UnityEngine.Object.FindObjectsOfType<Light>(true);
                Debug.Log($"[HST-DIAG] {tag}: renderers={renderers.Length} enabled={renderers.Count(r => r.enabled && r.gameObject.activeInHierarchy)} " +
                    $"lights={lights.Length} | " +
                    string.Join(" | ", lights.Take(3).Select(l => $"{l.name}:{l.type}:{l.intensity:F1}")));
                Vector3 lm = LM[tag];
                foreach (var cam in UnityEngine.Object.FindObjectsOfType<Camera>(true))
                {
                    var vp = cam.WorldToViewportPoint(lm);
                    const int w = 640, h = 360;
                    bool wasEnabled = cam.enabled;
                    cam.enabled = true;
                    var rt = new RenderTexture(w, h, 24);
                    cam.targetTexture = rt;
                    cam.Render();
                    RenderTexture.active = rt;
                    var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                    tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                    tex.Apply();
                    int px = Mathf.Clamp((int)(vp.x * w), 0, w - 1);
                    int py = Mathf.Clamp((int)(vp.y * h), 0, h - 1);
                    var c = tex.GetPixel(px, py);
                    cam.targetTexture = null;
                    cam.enabled = wasEnabled;
                    UnityEngine.Object.DestroyImmediate(rt);
                    UnityEngine.Object.DestroyImmediate(tex);
                    Debug.Log($"[HST-CAM] {man.scene}|{cam.name}|vp={vp.x:F3},{vp.y:F3}|depth={vp.z:F1}|" +
                        $"px={c.r:F2},{c.g:F2},{c.b:F2}|" +
                        $"pos={cam.transform.position.x:F2},{cam.transform.position.y:F2},{cam.transform.position.z:F2}|" +
                        $"fwd={cam.transform.forward.x:F2},{cam.transform.forward.y:F2},{cam.transform.forward.z:F2}|" +
                        $"mask={cam.cullingMask}");
                }
            }
        }

        // ---------------- 捕获路径(2026-10-06 修) ----------------
        // 坑: 相机渲进 8bit ARGB32 的 RenderTexture 时, URP 判定目标非 HDR → 全部 HDR 值在
        // tonemap 之前被夹到 1.0。后果: 所有出图的最大亮度恒等于 tonemap(1.0)(0.81~0.83),
        // 发光灯带(2.8)与白墙(1.1+)在画面里变成同一个值 —— 现场看起来就是"平、灰、死白",
        // 而 Blender 参考有 2% 过曝。修法: 渲进 DefaultHDR(半浮点, 线性)的 RT, 读回后再手动
        // 做 linear→sRGB(因为 HDR RT 里存的是 tonemap 之后的线性值, EncodeToPNG 不做转换)。
        static Color[] RenderRaw(Camera cam, int w, int h, out float maxLin, out int over1)
        {
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Linear);
            var prev = cam.targetTexture; bool was = cam.enabled;
            cam.enabled = true; cam.targetTexture = rt; cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGBAHalf, false, true);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0); tex.Apply();
            var px = tex.GetPixels();
            maxLin = 0f; over1 = 0;
            for (int i = 0; i < px.Length; i++)
            {
                float l = 0.2126f * px[i].r + 0.7152f * px[i].g + 0.0722f * px[i].b;
                if (l > maxLin) maxLin = l;
                if (l > 1f) over1++;
            }
            cam.targetTexture = prev; cam.enabled = was;
            UnityEngine.Object.DestroyImmediate(rt); UnityEngine.Object.DestroyImmediate(tex);
            return px;
        }

        // 线性 HDR 像素 → 8bit sRGB PNG 字节
        static Texture2D ToSrgbTex(Color[] px, int w, int h)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var o = new Color32[px.Length];
            for (int i = 0; i < px.Length; i++)
                o[i] = new Color32(
                    (byte)(Mathf.LinearToGammaSpace(Mathf.Clamp01(px[i].r)) * 255f),
                    (byte)(Mathf.LinearToGammaSpace(Mathf.Clamp01(px[i].g)) * 255f),
                    (byte)(Mathf.LinearToGammaSpace(Mathf.Clamp01(px[i].b)) * 255f), 255);
            t.SetPixels32(o); t.Apply();
            return t;
        }

        [MenuItem("HST/Shoot Only (不重建不烘焙)")]
        public static void ShootOnly() { Shoot(); }

        [MenuItem("HST/Shoot Screenshots")]
        public static void Shoot()
        {
            string outDir = @"F:\VSCode\工业级渲染\renders\unity_repro";
            Directory.CreateDirectory(outDir);
            foreach (var tag in new[] { "corridor", "atrium", "atrium_v6" })
            {
                string mp = Path.Combine(Application.dataPath, "HST_Prags/manifests/manifest_" + tag + ".json");
                if (!File.Exists(mp)) continue;
                var man = JsonUtility.FromJson<Manifest>(File.ReadAllText(mp));
                EditorSceneManager.OpenScene(SceneDir + "/" + man.scene + ".unity", OpenSceneMode.Single);
                foreach (var cam in UnityEngine.Object.FindObjectsOfType<Camera>(true))
                {
                    if (cam.name == "HST_FollowCamera") continue;   // 跟随相机归 Play 用, 不出静照
                    const int w = 1920, h = 1080;
                    float maxLin; int over1;
                    var px = RenderRaw(cam, w, h, out maxLin, out over1);
                    var outTex = ToSrgbTex(px, w, h);
                    File.WriteAllBytes(Path.Combine(outDir, man.scene + "_" + cam.name + ".png"),
                        outTex.EncodeToPNG());
                    UnityEngine.Object.DestroyImmediate(outTex);
                    Debug.Log($"[HST-shoot] {man.scene}|{cam.name} maxLin={maxLin:F3} >1.0占比={100f * over1 / px.Length:F2}%");
                }
                Debug.Log("[HST-shoot] " + man.scene + " done");
            }
        }

        // ---------------- HDR 读数诊断(2026-10-06) ----------------
        // 起因: 全部 14 张出图的最大亮度都卡在 0.81~0.83, 没有任何 >0.98 的像素, 而 Blender 参考有 2%
        // 过曝。出图是 8bit PNG, 判不出"管线里本来就没有 >1 的值"还是"有值但被截顶"。本诊断用
        // ARGBHalf 线性 RT 真读 HDR 值, 把这个问题变成数字。
        [MenuItem("HST/Diag HDR (真读数)")]
        public static void DiagHDR()
        {
            EditorSceneManager.OpenScene(SceneDir + "/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            Debug.Log($"[HST-HDR] 管线资产={(urp == null ? "null" : urp.name)} supportsHDR={(urp != null ? urp.supportsHDR.ToString() : "-")}");
            if (urp != null)
            {
                var so = new SerializedObject(urp);
                foreach (var k in new[] { "m_ColorGradingMode", "m_ColorGradingLutSize", "m_HDRColorBufferPrecision", "m_SupportsHDR" })
                {
                    var p = so.FindProperty(k);
                    Debug.Log($"[HST-HDR]   {k}={(p == null ? "缺" : (p.propertyType == SerializedPropertyType.Integer ? p.intValue.ToString() : p.boolValue.ToString()))}");
                }
            }
            var camGo = GameObject.Find("CAM_04_Low");
            var cam = camGo != null ? camGo.GetComponent<Camera>() : null;
            Debug.Log($"[HST-HDR] CAM_04_Low={(cam == null ? "缺" : "allowHDR=" + cam.allowHDR + " postFX=" + cam.GetUniversalAdditionalCameraData().renderPostProcessing)}");

            // 发光材质实际值 + 有多少个渲染器在用
            int hdr3 = 0, kw = 0, total = 0; float mx = 0f;
            var used = new HashSet<Material>();
            foreach (var mf in UnityEngine.Object.FindObjectsOfType<MeshFilter>(true))
            {
                var mr = mf.GetComponent<MeshRenderer>();
                if (mr == null || mr.sharedMaterial == null) continue;
                total++;
                if (!used.Add(mr.sharedMaterial)) continue;
                var m = mr.sharedMaterial;
                if (!m.HasProperty("_EmissionColor")) continue;
                var c = m.GetColor("_EmissionColor");
                float v = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                mx = Mathf.Max(mx, v);
                if (v > 1.001f) hdr3++;
                if (m.IsKeywordEnabled("_EMISSION")) kw++;
                Debug.Log($"[HST-HDR]   材质 {m.name}: _EmissionColor=({c.r:F2},{c.g:F2},{c.b:F2}) 关键字_EMISSION={(m.IsKeywordEnabled("_EMISSION") ? "开" : "关")} GI={m.globalIlluminationFlags}");
            }
            Debug.Log($"[HST-HDR] 网格={total} 去重材质={used.Count} 属HDR发光材质={hdr3} 开了_EMISSION关键字={kw} 最大发光分量={mx:F2}");

            if (cam == null) return;
            CaptureStats(cam, "A 8bit 默认路径(现役出图) ", RenderTextureFormat.ARGB32, false, 1f);
            CaptureStats(cam, "B half 线性 RT(真 HDR)   ", RenderTextureFormat.ARGBHalf, true, 1f);

            // 现场把发光 ×4, 看数字动不动 —— 数字不动 = 发光根本没进画面
            var bumped = new List<Material>();
            foreach (var m in used)
            {
                if (m.HasProperty("_EmissionColor"))
                {
                    var c = m.GetColor("_EmissionColor");
                    m.SetColor("_EmissionColor", new Color(c.r * 4f, c.g * 4f, c.b * 4f, 1f));
                    bumped.Add(m);
                }
            }
            CaptureStats(cam, "C 同上 + 发光x4          ", RenderTextureFormat.ARGBHalf, true, 1f);
            foreach (var m in bumped) { }
            AssetDatabase.Refresh();
        }

        // 渲染一次并按亮度统计(linear=true 时读到的是线性 HDR 真值)
        static void CaptureStats(Camera cam, string label, RenderTextureFormat fmt, bool linear, float mul)
        {
            const int w = 480, h = 270;
            var rw = linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.Default;
            var rt = new RenderTexture(w, h, 24, fmt, rw);
            var prev = cam.targetTexture;
            bool was = cam.enabled;
            cam.enabled = true;
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tf = fmt == RenderTextureFormat.ARGBHalf ? TextureFormat.RGBAHalf : TextureFormat.RGBA32;
            var tex = new Texture2D(w, h, tf, false, linear);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            var px = tex.GetPixels();
            var lum = new float[px.Length];
            float mx = 0f; int over1 = 0;
            for (int i = 0; i < px.Length; i++)
            {
                lum[i] = 0.2126f * px[i].r + 0.7152f * px[i].g + 0.0722f * px[i].b;
                if (lum[i] > mx) mx = lum[i];
                if (lum[i] > 1.0f) over1++;
            }
            var s = (float[])lum.Clone(); Array.Sort(s);
            Debug.Log($"[HST-HDR] {label} max={mx:F4} p50={s[s.Length / 2]:F4} p95={s[(int)(s.Length * 0.95f)]:F4} " +
                      $">1.0占比={100f * over1 / lum.Length:F2}%   (linear={linear} fmt={fmt})");
            cam.targetTexture = prev;
            cam.enabled = was;
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(tex);
        }

        // ---------------- 一次性调参诊断(2026-10-06) ----------------
        // 目的: 把"观感差"拆成三个可测量的问题, 一个批次全答完, 不靠猜:
        //   ① 发光到底进没进画面   → 关掉全部灯+环境光, 只剩自发光, 看图还有多亮
        //   ② 曝光该给多少         → 扫 postExposure, 直接对齐 Blender 同机位的 mean/p50
        //   ③ 背景色该给多少       → 幕墙外/竖井的"远处"在 Unity 是 clear color(现为近黑), Blender 是雾+世界
        // Blender 目标值(scripts/scene/img_pair_qa.py 量得, 04_low_channel):
        //   mean=0.6460 p50=0.6774 p95=0.8334 过曝=2.18%
        static readonly (string scene, string cam, float mean, float p50)[] TuneTargets =
        {
            ("SC_Corridor_Pragmata", "CAM_04_Low", 0.6460f, 0.6774f),
            ("SC_Atrium_Concept",    "CAM_InteriorThroughGlass", 0.4930f, 0.5205f),
            ("SC_Atrium_Military",   "CAM_Panorama", 0.7254f, 0.7754f),
        };

        [MenuItem("HST/Diag Tune (一个批次答全部)")]
        public static void DiagTune()
        {
            foreach (var t in TuneTargets)
            {
                EditorSceneManager.OpenScene(SceneDir + "/" + t.scene + ".unity", OpenSceneMode.Single);
                var camGo = GameObject.Find(t.cam);
                if (camGo == null) { Debug.LogWarning($"[HST-TUNE] {t.scene} 找不到相机 {t.cam}"); continue; }
                var cam = camGo.GetComponent<Camera>();
                Debug.Log($"[HST-TUNE] === {t.scene} / {t.cam} ===  Blender目标 mean={t.mean:F4} p50={t.p50:F4}");

                var vol = UnityEngine.Object.FindObjectOfType<Volume>();
                var prof = vol != null ? vol.sharedProfile : null;
                Tonemapping tmc = null; ColorAdjustments ca = null; Bloom bl = null;
                if (prof != null) { prof.TryGet(out tmc); prof.TryGet(out ca); prof.TryGet(out bl); }
                Debug.Log($"[HST-TUNE] volume={(vol == null ? "缺" : vol.name)} profile={(prof == null ? "缺" : prof.name)} " +
                          $"tonemap={(tmc == null ? "缺" : tmc.mode.value.ToString())} postExposure={(ca == null ? 0f : ca.postExposure.value):F2} " +
                          $"bloom={(bl == null ? "缺" : bl.intensity.value.ToString())}");

                // ① 发光单独可见性: 灯全灭 + 环境光黑
                var lights = UnityEngine.Object.FindObjectsOfType<Light>(true);
                var lightBackup = lights.Select(l => l.enabled).ToArray();
                var ambBackup = RenderSettings.ambientLight;
                foreach (var l in lights) l.enabled = false;
                RenderSettings.ambientLight = Color.black;
                ShootStats(cam, "① 只有自发光(灯灭+环境光黑)", t);
                for (int i = 0; i < lights.Length; i++) lights[i].enabled = lightBackup[i];
                RenderSettings.ambientLight = ambBackup;

                // ② 曝光扫描(捕获已修成 HDR 路径, 数字与出图一致; 上下都扫以便重新标定)
                if (ca != null)
                {
                    float ev0 = ca.postExposure.value;
                    foreach (float ev in new[] { 2.0f, 1.0f, 0f, -0.6f, -1.2f, -1.8f, -2.4f, -3.0f })
                    {
                        ca.postExposure.value = ev;
                        ShootStats(cam, $"② postExposure={ev:F1}EV", t);
                    }
                    ca.postExposure.value = ev0;
                }

                // ③ 背景色(把 clear color 换成偏亮的冷灰蓝, 看幕墙外/远处是否变正常)
                var bgBackup = cam.backgroundColor;
                cam.backgroundColor = new Color(0.16f, 0.19f, 0.24f);
                ShootStats(cam, "③ 背景色=0.16,0.19,0.24", t);
                cam.backgroundColor = bgBackup;
            }
        }

        // 统计(与出图同一条 HDR 捕获路径; 数值是显示空间 sRGB, 可直接和 Blender 图比)
        static void ShootStats(Camera cam, string label, (string scene, string cam, float mean, float p50) t)
        {
            const int w = 480, h = 270;
            float maxLin; int over1;
            var px = RenderRaw(cam, w, h, out maxLin, out over1);
            var lum = new float[px.Length];
            int hot = 0;
            float hotMinX = 1f, hotMaxX = 0f, hotMinY = 1f, hotMaxY = 0f;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var c = px[y * w + x];
                    float r = Mathf.LinearToGammaSpace(Mathf.Clamp01(c.r));
                    float g = Mathf.LinearToGammaSpace(Mathf.Clamp01(c.g));
                    float b = Mathf.LinearToGammaSpace(Mathf.Clamp01(c.b));
                    float v = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                    lum[y * w + x] = v;
                    if (v > 0.90f)
                    {
                        hot++;
                        float fx = x / (float)w, fy = y / (float)h;
                        if (fx < hotMinX) hotMinX = fx; if (fx > hotMaxX) hotMaxX = fx;
                        if (fy < hotMinY) hotMinY = fy; if (fy > hotMaxY) hotMaxY = fy;
                    }
                }
            var s = (float[])lum.Clone(); Array.Sort(s);
            float mean = 0f; foreach (var v in lum) mean += v; mean /= lum.Length;
            Debug.Log($"[HST-TUNE] {label,-34} mean={mean:F4} p50={s[s.Length / 2]:F4} p95={s[(int)(s.Length * .95f)]:F4} " +
                      $"max={s[s.Length - 1]:F4} HDRmax={maxLin:F2} >1.0%={100f * over1 / lum.Length:F2} " +
                      $"亮部(>0.90)={100f * hot / lum.Length:F1}%@x[{hotMinX:F2},{hotMaxX:F2}]y[{hotMinY:F2},{hotMaxY:F2}]");
            UnityEngine.Object.DestroyImmediate(ToSrgbTex(px, w, h));
        }

        // ---------------- 发光可见性判定(决定性一测) ----------------
        // 把反照率清零 + 环境光黑 + 雾关 + 灯灭, 画面里剩下的只能是自发光材质本身。
        // 全黑 = 发光没渲染; 只见灯带 = 发光正常(那"看不见灯带"就是遮挡/机位问题)。
        [MenuItem("HST/Diag Emit (发光是否可见)")]
        public static void DiagEmit()
        {
            EditorSceneManager.OpenScene(SceneDir + "/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            var amb = RenderSettings.ambientLight; bool fog = RenderSettings.fog;
            RenderSettings.ambientLight = Color.black; RenderSettings.fog = false;
            var lights = UnityEngine.Object.FindObjectsOfType<Light>(true);
            var lb = lights.Select(l => l.enabled).ToArray();
            foreach (var l in lights) l.enabled = false;

            var mats = new List<Material>(); var cols = new List<Color>();
            foreach (var mr in UnityEngine.Object.FindObjectsOfType<MeshRenderer>(true))
            {
                var m = mr.sharedMaterial;
                if (m == null || !m.HasProperty("_BaseColor") || mats.Contains(m)) continue;
                // 发光体已改用 Unlit, 辐射值就写在 _BaseColor 里 —— 清零等于把发光也关掉, 要跳过
                if (m.shader != null && m.shader.name.Contains("Unlit")) continue;
                mats.Add(m); cols.Add(m.GetColor("_BaseColor"));
                m.SetColor("_BaseColor", Color.black);
            }
            Debug.Log($"[HST-EMIT] 反照率已清零的材质数={mats.Count}");
            foreach (var cam in UnityEngine.Object.FindObjectsOfType<Camera>(true))
                if (cam.name.StartsWith("CAM_")) StatsDark(cam);

            for (int i = 0; i < mats.Count; i++) mats[i].SetColor("_BaseColor", cols[i]);
            for (int i = 0; i < lights.Length; i++) lights[i].enabled = lb[i];
            RenderSettings.ambientLight = amb; RenderSettings.fog = fog;
        }

        static void StatsDark(Camera cam)
        {
            const int w = 480, h = 270;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            var prev = cam.targetTexture; bool was = cam.enabled;
            cam.enabled = true; cam.targetTexture = rt; cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0); tex.Apply();
            var px = tex.GetPixels();
            float mx = 0f; int lit = 0;
            float x0 = 1f, x1 = 0f, y0 = 1f, y1 = 0f;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var c = px[y * w + x];
                    float v = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
                    if (v > mx) mx = v;
                    if (v > 0.05f)
                    {
                        lit++;
                        float fx = x / (float)w, fy = y / (float)h;
                        if (fx < x0) x0 = fx; if (fx > x1) x1 = fx;
                        if (fy < y0) y0 = fy; if (fy > y1) y1 = fy;
                    }
                }
            Debug.Log($"[HST-EMIT] {cam.name,-12} max={mx:F4} 可见(>0.05)占比={100f * lit / (w * h):F2}% " +
                      $"范围x[{x0:F2},{x1:F2}]y[{y0:F2},{y1:F2}]");
            cam.targetTexture = prev; cam.enabled = was;
            UnityEngine.Object.DestroyImmediate(rt); UnityEngine.Object.DestroyImmediate(tex);
        }

        // 走廊过曝倍数标定: 一路降曝光直到落在 Blender 目标 (mean 0.646 / p50 0.677),
        // 并分开看"全开"与"只留烘焙 GI"(灯灭+环境光黑)两条曲线, 判断过曝来自实时灯还是烘焙。
        [MenuItem("HST/Diag Scale (过曝倍数标定)")]
        public static void DiagScale()
        {
            EditorSceneManager.OpenScene(SceneDir + "/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            var cam = GameObject.Find("CAM_04_Low").GetComponent<Camera>();
            var vol = UnityEngine.Object.FindObjectOfType<Volume>();
            ColorAdjustments ca = null;
            if (vol != null && vol.sharedProfile != null) vol.sharedProfile.TryGet(out ca);
            var t = ("SC_Corridor_Pragmata", "CAM_04_Low", 0.6460f, 0.6774f);

            Debug.Log("[HST-SCALE] 目标 mean=0.6460 p50=0.6774");
            foreach (float ev in new[] { 0f, -2f, -3f, -4f, -5f, -6f, -7f, -8f })
            { ca.postExposure.value = ev; ShootStats(cam, $"全开 ev={ev:F0}", t); }

            var lights = UnityEngine.Object.FindObjectsOfType<Light>(true);
            var lb = lights.Select(l => l.enabled).ToArray();
            var amb = RenderSettings.ambientLight;
            foreach (var l in lights) l.enabled = false;
            RenderSettings.ambientLight = Color.black;
            foreach (float ev in new[] { -3f, -5f, -6f, -7f, -8f })
            { ca.postExposure.value = ev; ShootStats(cam, $"只留烘焙GI ev={ev:F0}", t); }

            for (int i = 0; i < lights.Length; i++) lights[i].enabled = lb[i];
            RenderSettings.ambientLight = amb;
            ca.postExposure.value = -0.2f;
        }

        // 真线性量级: 关 tonemap + 关 bloom + 曝光 0, 直接读线性值(p50/max)。
        // 曝光扫描只能告诉我"过曝很多", 这个能告诉我"过曝多少倍" —— 判据是 p50 线性值,
        // 目标是让 p50 落在 Blender 的等效线性 (~0.415, 对应 sRGB 0.677)。
        [MenuItem("HST/Diag Linear (真线性量级)")]
        public static void DiagLinear()
        {
            foreach (var t in TuneTargets)
            {
                EditorSceneManager.OpenScene(SceneDir + "/" + t.scene + ".unity", OpenSceneMode.Single);
                var camGo = GameObject.Find(t.cam);
                if (camGo == null) continue;
                var cam = camGo.GetComponent<Camera>();
                var vol = UnityEngine.Object.FindObjectOfType<Volume>();
                Tonemapping tm = null; ColorAdjustments ca = null; Bloom bl = null;
                if (vol != null && vol.sharedProfile != null)
                { vol.sharedProfile.TryGet(out tm); vol.sharedProfile.TryGet(out ca); vol.sharedProfile.TryGet(out bl); }
                var tmB = tm.mode.value; var caB = ca.postExposure.value; var blB = bl.intensity.value;
                tm.mode.value = TonemappingMode.None; ca.postExposure.value = 0f; bl.intensity.value = 0f;
                LinStats(cam, $"{t.scene}/{t.cam} 无tonemap", t.p50);
                // 关雾测量: Unity 的指数雾按 fogColor 无条件把远处拉向 fogColor(自发光),
                // 而 Cycles 的体积雾是被场景照亮的 —— 暗场景里 Unity 的雾会凭空发亮。
                // 这一行量出雾贡献了多少, 才能知道雾色该乘多少。
                bool fogB = RenderSettings.fog;
                RenderSettings.fog = false;
                LinStats(cam, $"{t.scene}/{t.cam} 关雾", t.p50);
                RenderSettings.fog = fogB;
                var lights = UnityEngine.Object.FindObjectsOfType<Light>(true);
                var lb = lights.Select(l => l.enabled).ToArray();
                var amb = RenderSettings.ambientLight; var ambB = amb;
                foreach (var l in lights) l.enabled = false;
                RenderSettings.ambientLight = Color.black;
                LinStats(cam, $"{t.scene}/{t.cam} 只烘焙GI", t.p50);
                for (int i = 0; i < lights.Length; i++) lights[i].enabled = lb[i];
                RenderSettings.ambientLight = ambB;
                tm.mode.value = tmB; ca.postExposure.value = caB; bl.intensity.value = blB;
            }
        }

        static void LinStats(Camera cam, string label, float targetSrgbP50)
        {
            float mx; int o1;
            var px = RenderRaw(cam, 480, 270, out mx, out o1);
            var lum = new float[px.Length];
            for (int i = 0; i < px.Length; i++) lum[i] = 0.2126f * px[i].r + 0.7152f * px[i].g + 0.0722f * px[i].b;
            var s = (float[])lum.Clone(); Array.Sort(s);
            float p50 = s[s.Length / 2];
            // Blender 等效线性目标(sRGB→线性)
            float tl = Mathf.Pow((targetSrgbP50 + 0.055f) / 1.055f, 2.4f);
            Debug.Log($"[HST-LIN] {label,-42} 线性 p05={s[s.Length / 20]:F3} p50={p50:F3} p95={s[(int)(s.Length * .95f)]:F3} " +
                      $"max={s[s.Length - 1]:F2} | Blender等效线性 p50={tl:F3} → 需要 {p50 / Mathf.Max(tl, 1e-6f):F1}x 减光");
        }

        // 走廊快迭代: 重建走廊(材质/灯光表已改) → 参数校验 → 重烤走廊 → 读线性量级。
        // 一个批次跑完, 拿到"这次的过曝倍数"这一个数字, 然后再改系数。
        [MenuItem("HST/Iterate Corridor (build+bake+linear)")]
        public static void IterateCorridor()
        { FixTextureImporters(); Build("corridor"); BakeCorridorOnly(); DiagLinear(); }

        // 三场全量: 重建 → 重烤 → 读线性量级(长, 约 12~15 分钟)
        [MenuItem("HST/Iterate All (build+bake+linear)")]
        public static void IterateAll()
        { FixTextureImporters(); BuildBoth(); BakeLightingAll(); DiagLinear(); }

        // 出图全量: 重建 → 主控入场 → 重烤 → 相机校验 → 14 机位出图 → 读线性量级(一次跑完拿数字+图)
        [MenuItem("HST/Finalize All (build+bake+player+cam+shoot+linear)")]
        public static void FinalizeAll()
        { EnableLightmapUVs(); BuildBoth(); AddWalkPlayer(); BakeLightingAll(); CamCheck(); Shoot(); DiagLinear(); }

        // 泛光量核实: 02 机位(看门)在 Unity 里门中央出现一个"大白球", Blender 侧只有紧致辉光。
        // 扫 bloom 强度, 看过曝占比掉到 Blender 水平(0.26%)需要多少。
        [MenuItem("HST/Diag Bloom (门区大白球核实)")]
        public static void DiagBloom()
        {
            EditorSceneManager.OpenScene(SceneDir + "/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            var cam = GameObject.Find("CAM_02_Door").GetComponent<Camera>();
            var vol = UnityEngine.Object.FindObjectOfType<Volume>();
            Bloom bl = null;
            if (vol != null && vol.sharedProfile != null) vol.sharedProfile.TryGet(out bl);
            if (bl == null) { Debug.LogWarning("[HST-BLOOM] 找不到 Bloom"); return; }
            var t = ("SC_Corridor_Pragmata", "CAM_02_Door", 0.4817f, 0.5096f);
            var iB = bl.intensity.value; var tB = bl.threshold.value;
            foreach (var cfg in new[] { (0.9f, 0.55f), (0.9f, 0.25f), (1.1f, 0.25f), (0.9f, 0.0f) })
            {
                bl.threshold.value = cfg.Item1; bl.intensity.value = cfg.Item2;
                ShootStats(cam, $"bloom 阈值={cfg.Item1:F1} 强度={cfg.Item2:F2}", t);
            }
            bl.intensity.value = iB; bl.threshold.value = tB;
        }

        // 反射探针量核实: 02 机位整幅偏亮 + 低频纱正好高 0.131(形态像"白环境反射糊了一层")。
        // 走廊的实时反射探针(128px, 盒覆盖全场)会把走廊的白墙当环境反射打回每个面, 掠射角视角
        // (04) 看不出, 正对视角(02 看门墙)最明显。扫 intensity 看能收回多少。
        [MenuItem("HST/Diag Probe (反射探针量核实)")]
        public static void DiagProbe()
        {
            EditorSceneManager.OpenScene(SceneDir + "/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            var cam = GameObject.Find("CAM_02_Door").GetComponent<Camera>();
            var rp = UnityEngine.Object.FindObjectOfType<ReflectionProbe>();
            if (rp == null) { Debug.LogWarning("[HST-PROBE] 找不到反射探针"); return; }
            var t = ("SC_Corridor_Pragmata", "CAM_02_Door", 0.4817f, 0.5096f);
            var b = rp.intensity;
            foreach (float k in new[] { 1f, 0.5f, 0.25f, 0f })
            { rp.intensity = k; ShootStats(cam, $"反射探针强度={k:F2}", t); }
            rp.intensity = b;
        }

        // ---------------- 深度诊断(2026-10-07): 门面恒亮平台 + 探针贡献=0 的定位实验 ---------
        // 三个可证伪命题一次批次全测完:
        //   ① Unity 02 门面是 AgX(1.0)=0.8157 的恒亮平台(std=0) → 门区必有一个线性≈1 的平发光面,
        //      按包围盒点名门区全部渲染器(名字/材质/shader/_BaseColor/lightmapIndex);
        //   ② 反射探针 EXR 是否本身有值(逐面 mean/max) —— 有值但画面不变 = 绑定断, 没值 = 烤坏;
        //   ③ 探针通道 vs 全局 defaultReflection 通道分别 A/B(强度 1→0 差分), 哪条通走哪条。
        [MenuItem("HST/Diag 深度诊断(门面平台+探针通道)")]
        public static void DiagDeep()
        {
            var scene = EditorSceneManager.OpenScene(SceneDir + "/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            // ① 门区渲染器点名: 门叶 x∈[-18.35,-18.25], z∈[-0.9,0.9] (Unity 坐标, manifest 实测)
            Debug.Log("[HST-DEEP] == 门区渲染器(x≈-18.3, |z|<0.95) ==");
            foreach (var mr in UnityEngine.Object.FindObjectsOfType<MeshRenderer>(true))
            {
                var b = mr.bounds;
                if (b.min.x > -18.45f && b.max.x < -18.15f && b.min.z > -0.95f && b.max.z < 0.95f)
                {
                    var m = mr.sharedMaterial;
                    string bc = m != null && m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor").ToString("F3") : "-";
                    Debug.Log($"[HST-DEEP]   {mr.name}  enabled={mr.enabled}  mat={(m != null ? m.name : "null")}  " +
                              $"shader={(m != null ? m.shader.name : "-")}  _BaseColor={bc}  " +
                              $"bounds=({b.min.x:F2},{b.min.y:F2},{b.min.z:F2})~({b.max.x:F2},{b.max.y:F2},{b.max.z:F2})");
                }
            }
            // ② 探针 cubemap 逐面数值(临时开 isReadable 才能读像素)
            var rpImp = AssetImporter.GetAtPath("Assets/Art/Environment/HST_Kit/ReflectionProbes/RP_SC_Corridor_Pragmata.exr") as TextureImporter;
            if (rpImp != null && !rpImp.isReadable) { rpImp.isReadable = true; rpImp.SaveAndReimport(); }
            var cube = AssetDatabase.LoadAssetAtPath<Cubemap>("Assets/Art/Environment/HST_Kit/ReflectionProbes/RP_SC_Corridor_Pragmata.exr");
            if (cube == null) Debug.LogWarning("[HST-DEEP] 探针 cubemap 载入失败");
            else foreach (CubemapFace f in Enum.GetValues(typeof(CubemapFace)))
                {
                    if (f == CubemapFace.Unknown) continue;
                    var px = cube.GetPixels(f);
                    float mean = 0f, mx = 0f;
                    foreach (var c in px) { mean += 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b; mx = Mathf.Max(mx, Mathf.Max(c.r, Mathf.Max(c.g, c.b))); }
                    Debug.Log($"[HST-DEEP] 探针面 {f}: mean={mean / px.Length:F4} max={mx:F2}  (0=烤黑)");
                }
            // ③ A/B 差分: 墙面机位(04)最能暴露环境镜面
            var cam = System.Array.Find(UnityEngine.Object.FindObjectsOfType<Camera>(true), c => c.name.Contains("04"));
            if (cam == null) { Debug.LogWarning("[HST-DEEP] 找不到 04 机位相机"); return; }
            float meanA, p50A, maxA; RawStats(RenderRawC(cam, 320, 180), out meanA, out p50A, out maxA);
            Debug.Log($"[HST-DEEP] 基线 04: mean={meanA:F4} p50={p50A:F4} max={maxA:F4}");
            var rp = UnityEngine.Object.FindObjectOfType<ReflectionProbe>();
            if (rp != null)
            {
                rp.intensity = 0f;
                float m0, p0, x0; RawStats(RenderRawC(cam, 320, 180), out m0, out p0, out x0);
                Debug.Log($"[HST-DEEP] 探针强度=0: mean={m0:F4}  Δmean={meanA - m0:F5}  (0=探针通道断)");
                rp.intensity = ProbeIntensityByTag["SC_Corridor_Pragmata"];
                // 全局通道 A/B: reflectionIntensity 只缩放 defaultReflection(BakeScenes 已喂 cubemap)。
                // 注意不能在这里读/写 customReflection —— 值为 null 时 getter/setter 链会抛异常。
                var prevRi = RenderSettings.reflectionIntensity;
                RenderSettings.reflectionIntensity = 0f;
                float m1, p1, x1; RawStats(RenderRawC(cam, 320, 180), out m1, out p1, out x1);
                Debug.Log($"[HST-DEEP] reflectionIntensity=0: mean={m1:F4}  Δmean={meanA - m1:F5}  (0=全局通道也断)");
                RenderSettings.reflectionIntensity = prevRi;
                float m2, p2, x2; RawStats(RenderRawC(cam, 320, 180), out m2, out p2, out x2);
                Debug.Log($"[HST-DEEP] 恢复复测: mean={m2:F4}  (与基线差 {Mathf.Abs(m2 - meanA):F5} = 幂等性)");
            }
            // ④ 02 机位线性真值: 平台是否恰在 1.0
            var cam2 = System.Array.Find(UnityEngine.Object.FindObjectsOfType<Camera>(true), c => c.name.Contains("02"));
            if (cam2 != null)
            {
                var px2 = RenderRawC(cam2, 480, 270);
                int ov = 0; foreach (var c2 in px2) if (0.2126f * c2.r + 0.7152f * c2.g + 0.0722f * c2.b > 1f) ov++;
                float m, p, x; RawStats(px2, out m, out p, out x);
                Debug.Log($"[HST-DEEP] 02 线性: mean={m:F4} p50={p:F4} max={x:F4} >1占比={100f * ov / px2.Length:F1}%  (max恰=1.0→链路上有1.0钳位)");
            }
            EditorSceneManager.MarkSceneDirty(scene);
        }


        static Color[] RenderRawC(Camera cam, int w, int h)
        {
            float ml; int ov;
            return RenderRaw(cam, w, h, out ml, out ov);
        }

        static void RawStats(Color[] px, out float mean, out float p50, out float max)
        {
            var lum = new float[px.Length];
            for (int i = 0; i < lum.Length; i++)
                lum[i] = 0.2126f * px[i].r + 0.7152f * px[i].g + 0.0722f * px[i].b;
            mean = 0f; max = 0f;
            foreach (var v in lum) { mean += v; max = Mathf.Max(max, v); }
            mean /= lum.Length;
            var s = (float[])lum.Clone(); Array.Sort(s);
            p50 = s[s.Length / 2];
        }

        // ---------------- 观感收尾(不重烤): ①修相机上向量 ②收紧泛光 ----------------
        // ① 清单导出的 up_u 之前写成 Blender +Y(应为 +Z)→ 映射后 = Unity (0,0,-1)=世界后方,
        //    LookRotation(fwd, 后方) 使整幅画面滚 90°(用户肉眼指出"歪脖向左 90 度")。
        //    这里对已存场景原地用 fwd + 世界向上重算旋转, 保住已烤的光照贴图。
        // ② 泛光: URP Bloom 的 scatter 默认 0.7 会把高光铺成一层很宽的纱(用户描述"高光到雾蒙蒙"
        //    "没有 Blender 端通透"), Blender 侧 post_glow 是 sigma 12px 的紧致辉光 → scatter 收到 0.30。
        [MenuItem("HST/Polish (修相机 + 收紧泛光 + 出图)")]
        public static void Polish()
        {
            foreach (var tag in new[] { "corridor", "atrium", "atrium_v6" })
            {
                string mp = Path.Combine(Application.dataPath, "HST_Prags/manifests/manifest_" + tag + ".json");
                if (!File.Exists(mp)) continue;
                var man = JsonUtility.FromJson<Manifest>(File.ReadAllText(mp));
                var scene = EditorSceneManager.OpenScene(SceneDir + "/" + man.scene + ".unity", OpenSceneMode.Single);
                int fixedN = 0;
                foreach (var cam in UnityEngine.Object.FindObjectsOfType<Camera>(true))
                {
                    var mc = man.cameras.FirstOrDefault(c => c.name == cam.name);
                    if (mc == null) continue;
                    cam.transform.rotation = Quaternion.LookRotation(V(mc.fwd_u), Vector3.up);
                    fixedN++;
                }
                // 后处理锁默认(用户令 2026-10-08: 调色/后处理永远默认, 只动光源/材质/烘焙)。
                // 旧的 manifest bloom 参数/EV 补偿全部废弃, 统一走 WritePostDefaults()。
                WritePostDefaults();
                // 发光重定标: 按 manifest 重算(幂等 —— 直接赋值而不是乘系数, 重复跑不会越压越小)。
                // 2026-10-08: 新材质是 HST/Emit(辐射写在 _EmissionColor, 参与烘焙 GI) —— 只重定标数值,
                // **不再把 shader 改回 Unlit**。旧 Unlit 路径(_BaseColor 装辐射, 不参与 GI)只作为
                // 老场景遗留材质的兼容分支保留; 改 shader 前必须重烤(发光 GI 跟着材质走)。
                foreach (var ms in man.materials)
                {
                    if (ms.emit == null || ms.emit.Count != 3 || ms.emit_str <= 0f) continue;
                    var m = AssetDatabase.LoadAssetAtPath<Material>(MatDir + "/M_" + ms.name + ".mat");
                    if (m == null || !m.HasProperty("_BaseColor")) continue;
                    float k = ms.emit_str * EmissionKByTag[tag];
                    var emit = new Color(ms.emit[0] * k, ms.emit[1] * k, ms.emit[2] * k, 1f);
                    if (m.HasProperty("_EmissionColor") && m.IsKeywordEnabled("_EMISSION"))
                    {
                        m.SetColor("_EmissionColor", emit);                    // 辐射(可见 + 进 GI)
                        if (ms.albedo != null && ms.albedo.Count == 3)
                            m.SetColor("_BaseColor", new Color(ms.albedo[0], ms.albedo[1], ms.albedo[2], 1f));
                        m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive;
                        m.doubleSidedGI = true;
                    }
                    else
                    {
                        var unlit = Shader.Find("Universal Render Pipeline/Unlit");
                        if (m.shader != unlit)
                        {
                            m.shader = unlit;
                            m.SetFloat("_Surface", 0f);
                            m.SetInt("_SrcBlend", (int)BlendMode.One);
                            m.SetInt("_DstBlend", (int)BlendMode.Zero);
                            m.SetFloat("_ZWrite", 1f);
                            m.SetOverrideTag("RenderType", "Opaque");
                            m.renderQueue = -1;
                        }
                        m.SetColor("_BaseColor", emit);
                    }
                    // Cycles 对单面平面双面着色, Unity 默认背面剔除 —— 门缝条(法线背向走廊)
                    // 被整条吞掉(实测全图 max 0.8188 < 缝条应有的 0.91)。发光面一律双面渲染。
                    if (m.HasProperty("_Cull")) m.SetInt("_Cull", 0);
                    EditorUtility.SetDirty(m);
                }
                // 渲染器必须真的去取探针: FBX 导入的 MeshRenderer 常常是 reflectionProbeUsage=Off,
                // 于是 unity_SpecCube0 永远是环境探针(本场景无天空盒 → 近似黑), 探针挂了等于没挂。
                int probeUse = 0;
                foreach (var mr2 in UnityEngine.Object.FindObjectsOfType<MeshRenderer>(true))
                {
                    if (mr2.reflectionProbeUsage != ReflectionProbeUsage.BlendProbes)
                    {
                        mr2.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
                        EditorUtility.SetDirty(mr2);
                    }
                    probeUse++;
                }
                // 反射探针强度(环境镜面项): 免重烤地迭代这个值
                int probeN = 0;
                foreach (var rp2 in UnityEngine.Object.FindObjectsOfType<ReflectionProbe>(true))
                {
                    rp2.intensity = ProbeIntensityByTag[man.scene];
                    EditorUtility.SetDirty(rp2);
                    probeN++;
                }
                EditorSceneManager.SaveScene(scene);
                AssetDatabase.SaveAssets();
                Debug.Log($"[HST-POLISH] {tag}: 相机重定向 {fixedN} 台, 泛光 scatter=0.30, " +
                          $"反射探针 {probeN} 个 intensity={ProbeIntensityByTag[man.scene]:F2}");
            }
            Shoot();
        }

        // 发光关键字: **`Material.EnableKeyword("_EMISSION")` 存不进资产** —— 实测 CreateAsset 后
        // .mat 里 `m_ValidKeywords: []`, 于是所有发光在后续会话里静默消失(DiagEmit 反照率清零后
        // 画面全黑 max=0.0000 才暴露)。改用检查器同款的 SerializedObject 写法, 直接写 m_ValidKeywords。
        // 2026-10-08: **磁盘级**写 _EMISSION 关键字。
        // 实测 `Material.EnableKeyword` 与 SerializedObject 写 m_ValidKeywords 都存不进 .mat
        // (磁盘上恒 `m_InvalidKeywords: [_EMISSION]`), 而 Progressive 光照贴图器据此把材质当**非发光**
        // —— 直接证据: 开/关发光材质 GI 两版烘焙, 墙面读数三位小数完全一致(贡献=0)。
        // .mat 就是 YAML 文本, 直接改文本再 ImportAsset 是唯一可靠路径。
        public static void PatchEmissionKeywordOnDisk(string path)
        {
            if (!File.Exists(path)) return;
            var txt = File.ReadAllText(path);
            // 两种写法都要覆盖: `m_ValidKeywords: []`(内联空数组) 与 `m_ValidKeywords:\n  - A\n  - B`
            var rx = new System.Text.RegularExpressions.Regex(@"m_ValidKeywords:(\s*\[\])?(?<v>(\r?\n  - [^\r\n]+)*)");
            var rxI = new System.Text.RegularExpressions.Regex(@"m_InvalidKeywords:(\s*\[\])?(?<v>(\r?\n  - [^\r\n]+)*)");
            var mv = rx.Match(txt); var mi = rxI.Match(txt);
            if (!mv.Success || !mi.Success) { Debug.LogWarning("[HST-KW] 关键字块缺失: " + path); return; }
            string v = mv.Groups["v"].Value, iv = mi.Groups["v"].Value;
            if (v.Contains("_EMISSION")) return;                       // 已有, 幂等
            v = "\n  - _EMISSION" + v;
            iv = iv.Replace("\n  - _EMISSION", "");
            txt = txt.Remove(mv.Index, mv.Length).Insert(mv.Index, "m_ValidKeywords:" + v);
            var mi2 = rxI.Match(txt);                                  // 索引已变, 重新定位
            txt = txt.Remove(mi2.Index, mi2.Length).Insert(mi2.Index, "m_InvalidKeywords:" + iv);
            File.WriteAllText(path, txt);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            Debug.Log("[HST-KW] " + Path.GetFileName(path) + " → m_ValidKeywords 加 _EMISSION(磁盘级)");
        }

        public static void ForceEmissionKeywordPublic(Material m) { ForceEmissionKeyword(m); }

        static void ForceEmissionKeyword(Material m)
        {
            var so = new SerializedObject(m);
            var vk = so.FindProperty("m_ValidKeywords");
            bool has = false;
            for (int i = 0; i < vk.arraySize; i++)
                if (vk.GetArrayElementAtIndex(i).stringValue == "_EMISSION") has = true;
            if (!has)
            {
                vk.InsertArrayElementAtIndex(vk.arraySize);
                vk.GetArrayElementAtIndex(vk.arraySize - 1).stringValue = "_EMISSION";
            }
            var ik = so.FindProperty("m_InvalidKeywords");
            if (ik != null)
                for (int i = ik.arraySize - 1; i >= 0; i--)
                    if (ik.GetArrayElementAtIndex(i).stringValue == "_EMISSION") ik.DeleteArrayElementAtIndex(i);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(m);
        }

        // 挂 AgX 后处理 + 关掉 URP 自带 tonemap。为什么要: 见 Assets/Art/Environment/HST_Kit/Runtime/HST/HstAgXFeature.cs 顶部注释
        // —— URP 2022.3 没有 AgX, 而 Blender 参考图是 AgX + Medium High Contrast; 曲线形状差异会让
        // "暗处对得上、亮处一律偏高", 单一曝光值无法兼顾多机位。
        [MenuItem("HST/Enable AgX (挂后处理 + 关 URP tonemap)")]
        public static void EnableAgX()
        {
            const string rp = "Assets/Settings/URP-Renderer.asset";
            var data = AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(rp);
            if (data == null) { Debug.LogError("[HST-AGX] 找不到 " + rp); return; }
            var shader = Shader.Find("HST/AgX");
            if (shader == null) { Debug.LogError("[HST-AGX] 找不到 HST/AgX 着色器(检查 Assets/Shaders/HST_AgX.shader 是否编译过)"); return; }
            HstAgXFeature feat = null;
            foreach (var f in data.rendererFeatures) if (f is HstAgXFeature x) feat = x;
            if (feat == null)
            {
                feat = ScriptableObject.CreateInstance<HstAgXFeature>();
                feat.name = "HstAgX";
                data.rendererFeatures.Insert(0, feat);   // 插最前: 同注入点的 feature 按列表顺序执行,
                AssetDatabase.AddObjectToAsset(feat, data);   // 让同事的 RadialRedshift 仍作用在 AgX 之后
                EditorUtility.SetDirty(data);
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(rp, ImportAssetOptions.ForceUpdate);
            }
            else if (data.rendererFeatures.IndexOf(feat) != 0)
            {
                data.rendererFeatures.Remove(feat);
                data.rendererFeatures.Insert(0, feat);
                EditorUtility.SetDirty(data);
                AssetDatabase.SaveAssets();
            }
            feat.settings.shader = shader;
            // LUT 贴图: 必须按 Non-Color(linear) 导入, 否则 Unity 会把显示值再解一次 sRGB, 曲线就歪了
            const string lutPath = "Assets/Art/Environment/HST_Kit/Textures/HST_Prags/T_AgX_LUT.png";
            var lutImp = AssetImporter.GetAtPath(lutPath) as TextureImporter;
            if (lutImp != null)
            {
                bool ch = false;
                if (lutImp.sRGBTexture) { lutImp.sRGBTexture = false; ch = true; }
                if (lutImp.mipmapEnabled) { lutImp.mipmapEnabled = false; ch = true; }
                if (lutImp.wrapMode != TextureWrapMode.Clamp) { lutImp.wrapMode = TextureWrapMode.Clamp; ch = true; }
                if (lutImp.filterMode != FilterMode.Bilinear) { lutImp.filterMode = FilterMode.Bilinear; ch = true; }
                if (lutImp.textureCompression != TextureImporterCompression.Uncompressed) { lutImp.textureCompression = TextureImporterCompression.Uncompressed; ch = true; }
                if (lutImp.maxTextureSize < 2048) { lutImp.maxTextureSize = 2048; ch = true; }
                if (ch) lutImp.SaveAndReimport();
            }
            feat.settings.lut = AssetDatabase.LoadAssetAtPath<Texture2D>(lutPath);
            if (feat.settings.lut == null) Debug.LogWarning("[HST-AGX] 找不到 LUT: " + lutPath + "(先跑 make_agx_lut.py)");
            EditorUtility.SetDirty(feat);
            data.SetDirty();
            AssetDatabase.SaveAssets();
            Debug.Log($"[HST-AGX] renderer 上 feature 数={data.rendererFeatures.Count}");

            // Volume 的 Tonemapping 必须关掉, 否则 AgX 之后再 tonemap 一次
            foreach (var tag in new[] { "corridor", "atrium", "atrium_v6" })
            {
                string mp = Path.Combine(Application.dataPath, "HST_Prags/manifests/manifest_" + tag + ".json");
                if (!File.Exists(mp)) continue;
                var man = JsonUtility.FromJson<Manifest>(File.ReadAllText(mp));
                var prof = AssetDatabase.LoadAssetAtPath<VolumeProfile>(MatDir + "/PP_" + man.scene + ".asset");
                if (prof == null) continue;
                Tonemapping tm; if (prof.TryGet(out tm))
                {
                    tm.mode.value = TonemappingMode.None;
                    EditorUtility.SetDirty(tm); EditorUtility.SetDirty(prof);
                    Debug.Log($"[HST-AGX] {tag} tonemapping → None");
                }
            }
            AssetDatabase.SaveAssets();
        }

        // 关键字能否落盘: 三种写法各测一遍, 并从文件里回读。
        // 起因: 墙面"像镜面不像漫反射" = URP Lit 的 _METALLICSPECGLOSSMAP 没生效 → smoothness 回落到标量。
        [MenuItem("HST/Diag Keywords (关键字能否落盘)")]
        public static void DiagKeywords()
        {
            var sh = Shader.Find("Universal Render Pipeline/Lit");
            var m = new Material(sh) { name = "KTEST" };
            m.EnableKeyword("_NORMALMAP");
            m.EnableKeyword("_METALLICSPECGLOSSMAP");
            string p = MatDir + "/KTEST.mat";
            if (File.Exists(p)) AssetDatabase.DeleteAsset(p);
            AssetDatabase.CreateAsset(m, p);
            AssetDatabase.SaveAssets();
            string txt = File.ReadAllText(p);
            Debug.Log($"[HST-KW] A 新建+EnableKeyword: 内存 NORMALMAP={m.IsKeywordEnabled("_NORMALMAP")} " +
                      $"文件含={txt.Contains("_NORMALMAP")} 文件含MS={txt.Contains("_METALLICSPECGLOSSMAP")}");

            var so = new SerializedObject(m);
            var vk = so.FindProperty("m_ValidKeywords");
            vk.InsertArrayElementAtIndex(vk.arraySize);
            vk.GetArrayElementAtIndex(vk.arraySize - 1).stringValue = "_SURFACE_TYPE_TRANSPARENT";
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(m);
            AssetDatabase.SaveAssets();
            txt = File.ReadAllText(p);
            Debug.Log($"[HST-KW] B SerializedObject: 文件含透明关键字={txt.Contains("_SURFACE_TYPE_TRANSPARENT")} " +
                      $"m_ValidKeywords 行={(txt.Contains("m_ValidKeywords: []") ? "空" : "非空")}");

            foreach (var n in new[] { "M_ARCH_WallM", "M_ARCH_FloorM", "M_FORGE_RackTex", "M_V6_Glass" })
            {
                var w = AssetDatabase.LoadAssetAtPath<Material>(MatDir + "/" + n + ".mat");
                if (w == null) continue;
                Debug.Log($"[HST-KW] C {n}: _Smoothness={w.GetFloat("_Smoothness"):F2} _Metallic={w.GetFloat("_Metallic"):F2} " +
                          $"MS图={(w.GetTexture("_MetallicGlossMap") != null)} 法线图={(w.GetTexture("_BumpMap") != null)} " +
                          $"NORMALMAP={w.IsKeywordEnabled("_NORMALMAP")} MSKEY={w.IsKeywordEnabled("_METALLICSPECGLOSSMAP")} " +
                          $"_Surface={w.GetFloat("_Surface")} 透明KEY={w.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT")} queue={w.renderQueue}");
            }
        }

        // ---------------- 建 ----------------
        public static void Build(string tag)
        {
            string json = File.ReadAllText(Path.Combine(Application.dataPath, "HST_Prags/manifests/manifest_" + tag + ".json"));
            var man = JsonUtility.FromJson<Manifest>(json);
            if (man == null || man.objects == null) { Debug.LogError("[HST] manifest 解析失败: " + tag); return; }

            EnsureFolder(MatDir);
            // 材质(按文件级幂等: 存在先删, 不动别人的)
            var matByName = new Dictionary<string, Material>();
            foreach (var ms in man.materials)
            {
                var mat = CreateMat(ms, EmissionKByTag[tag]);
                string mp = MatDir + "/M_" + ms.name + ".mat";
                if (File.Exists(mp)) AssetDatabase.DeleteAsset(mp);
                AssetDatabase.CreateAsset(mat, mp);
                if (ms.emit != null && ms.emit.Count == 3 && ms.emit_str > 0f)
                {
                    ForceEmissionKeyword(mat);
                    AssetDatabase.SaveAssets();
                    PatchEmissionKeywordOnDisk(mp);      // 关键: 磁盘级写关键字(光照贴图器据此认发光)
                }
                matByName[ms.name] = mat;
            }
            AssetDatabase.SaveAssets();

            // 场景
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var src = AssetDatabase.LoadAssetAtPath<GameObject>(ModelDir + "/" + man.fbx);
            if (src == null) { Debug.LogError("[HST] FBX 找不到: " + man.fbx); return; }
            var inst = UnityEngine.Object.Instantiate(src);
            inst.name = man.scene;

            // 分段父节点(走廊): 按 manifest 的 Blender 中心 x 分组
            Transform sectHolder = null;
            var sectMap = new Dictionary<string, Transform>();
            if (man.sections != null && man.sections.Count > 0)
            {
                sectHolder = new GameObject("Sections").transform;
                sectHolder.SetParent(inst.transform, false);
                foreach (var s in man.sections)
                {
                    var t = new GameObject(s.name).transform;
                    t.SetParent(sectHolder, false);
                    sectMap[s.name] = t;
                }
            }

            int matched = 0, unmatched = 0;
            foreach (var mf in inst.GetComponentsInChildren<MeshFilter>(true))
            {
                var go = mf.gameObject;
                var e = man.objects.FirstOrDefault(o => o.name == go.name);
                if (e == null) { unmatched++; Debug.LogWarning("[HST] 无清单条目: " + go.name); continue; }
                matched++;
                var mr = go.GetComponent<MeshRenderer>();
                if (mr != null && matByName.ContainsKey(e.mat)) mr.sharedMaterial = matByName[e.mat];
                GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic);
                if (sectMap.Count > 0 && e.center_b != null && e.center_b.Length > 0)
                {
                    float bx = e.center_b[0];
                    var sect = man.sections.FirstOrDefault(s => bx >= s.x0 && bx < s.x1);
                    if (sect != null) go.transform.SetParent(sectMap[sect.name], true);
                }
            }
            Debug.Log($"[HST] {tag}: 网格匹配 {matched}, 未匹配 {unmatched}");

            // 灯 (按场景校准: K=强度系数, spacing=点灯间距)
            float kArea = AreaKByTag[tag];
            float spacing = AreaSpacingByTag[tag];
            foreach (var ml in man.lights)
            {
                var col = ml.color != null ? new Color(ml.color[0], ml.color[1], ml.color[2]) : Color.white;
                if (tag == "corridor") col = new Color(col.r * 0.86f, col.g * 0.94f, col.b * 1.10f);  // 冷青偏色, 防"黑白"
                if (ml.kind == "SUN")
                {
                    var go = new GameObject("L_" + ml.name);
                    var li = go.AddComponent<Light>();
                    li.type = LightType.Directional; li.color = col;
                    li.intensity = ml.power * 0.8f * DirectShareByTag[tag];
                    li.bounceIntensity = 1f / DirectShareByTag[tag];
                    li.shadows = LightShadows.Soft;
                    go.transform.position = V(ml.pos_u);
                    go.transform.rotation = Quaternion.LookRotation(V(ml.dir_u), Vector3.up);
                }
                else if (ml.kind == "AREA")
                {
                    // 面积光→点灯串: 用"贴身密排低强度"逼近面光源的连续照明与掠射高光
                    // (稀疏强光只有均匀泡, 没有 Blender 那种贴面亮带与光泽流动 — 视觉对拍实测)
                    float big = Mathf.Max(ml.size, ml.size_y);
                    float sp = AreaSpacingByTag[tag];
                    int n = Mathf.Max(1, Mathf.CeilToInt(big / sp));
                    Vector3 longU = ml.long_u != null ? V(ml.long_u) : Vector3.right;
                    float range = 40f;   // 同 CorridorMixed: 不做距离截断(Cycles 面光源没有)
                    float dshare = DirectShareByTag[tag];
                    var basePos = V(ml.pos_u);
                    // 门头顶灯带离门面只有 0.12m —— 点光 1/r² 会把门烤成平亮平台(实测 02 机位
                    // 门面恒 0.8164=AgX(1.0), 水平 std=0)。内缩到 ~1.0m, 与天花板灯串同一修法。
                    if (ml.name.Contains("DoorTop")) basePos = basePos + Vector3.right * 0.85f;
                    for (int i = 0; i < n; i++)
                    {
                        float t = (i - (n - 1) * 0.5f) * (big / n);
                        var go = new GameObject($"L_{ml.name}_{i}");
                        var li = go.AddComponent<Light>();
                        li.type = LightType.Point; li.color = col;
                        li.intensity = ml.power * kArea / n * dshare;
                        li.bounceIntensity = 1f / dshare;   // 间接光倍率补偿: 烘焙 GI 总量不变
                        li.range = range; li.shadows = LightShadows.Soft;   // 同 CorridorMixed: 遮蔽必须在
                        go.transform.position = basePos + longU * t;
                    }
                }
                else
                {
                    var go = new GameObject("L_" + ml.name);
                    var li = go.AddComponent<Light>();
                    li.type = LightType.Point; li.color = col;
                    li.intensity = ml.power * 0.10f * DirectShareByTag[tag];
                    li.bounceIntensity = 1f / DirectShareByTag[tag];
                    li.range = 8f; li.shadows = LightShadows.None;
                    go.transform.position = V(ml.pos_u);
                }
            }
            if (tag == "atrium")
            {
                // 竖井方向补光: 替代 Cycles 里巨型发光顶棚的远距反弹
                var go = new GameObject("L_ShaftFill");
                var li = go.AddComponent<Light>();
                li.type = LightType.Directional; li.shadows = LightShadows.None;
                li.color = new Color(0.62f, 0.72f, 0.92f); li.intensity = ShaftFillByTag["atrium"];
                go.transform.rotation = Quaternion.LookRotation(new Vector3(-0.2f, 0.35f, -0.8f).normalized, Vector3.up);
            }

            // 相机
            foreach (var mc in man.cameras)
            {
                var go = new GameObject(mc.name);
                var cam = go.AddComponent<Camera>();
                cam.transform.position = V(mc.pos_u);
                cam.transform.rotation = Quaternion.LookRotation(V(mc.fwd_u), V(mc.up_u));
                cam.fieldOfView = mc.fov_v; cam.nearClipPlane = 0.05f; cam.farClipPlane = mc.far;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.01f, 0.012f, 0.018f);
                var cd = cam.GetUniversalAdditionalCameraData();
                cd.renderPostProcessing = true;
            }

            // 雾 / 环境
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogDensity = man.fog.density;
            float fs = FogScaleByTag[tag];
            RenderSettings.fogColor = new Color(man.fog.color[0] * fs, man.fog.color[1] * fs, man.fog.color[2] * fs);
            // 2026-10-09 审计修复: 只写 ambientLight 颜色不够 —— 场景若是 Trilight 模式(天空/赤道/地面
            // 三色, 赤道 0.114 一直在偷偷当地板), ambientLight 字段被整体无视。必须显式钉死 Flat。
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = AmbientByTag[tag];

            // 后处理 Volume —— 一律 URP 默认(用户令 2026-10-08): Bloom 关(None 曲线前强度=0),
            // Tonemapping=None(= Blender Standard 同构: 线性值纯 sRGB 编码, 无电影曲线)
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            var bloom = profile.Add<Bloom>(true);
            bloom.threshold.value = 0.9f;
            bloom.intensity.value = 0f;
            var tm = profile.Add<Tonemapping>(true);
            tm.mode.value = TonemappingMode.None;
            // 逐场景后期曝光(用户令 2026-10-08 起恒为 0: 后处理默认, 亮度只在光源/材质/烘焙层调)
            var ca = profile.Add<ColorAdjustments>(true);
            ca.postExposure.value = 0f;
            string ppPath = MatDir + "/PP_" + man.scene + ".asset";
            if (File.Exists(ppPath)) AssetDatabase.DeleteAsset(ppPath);
            AssetDatabase.CreateAsset(profile, ppPath);
            AssetDatabase.AddObjectToAsset(bloom, profile);
            AssetDatabase.AddObjectToAsset(tm, profile);
            AssetDatabase.AddObjectToAsset(ca, profile);
            AssetDatabase.SaveAssets();
            var vgo = new GameObject("PP_GlobalVolume");
            var vol = vgo.AddComponent<Volume>();
            vol.isGlobal = true; vol.sharedProfile = profile;

            string scenePath = SceneDir + "/" + man.scene + ".unity";
            EnsureFolder(SceneDir);
            EditorSceneManager.SaveScene(scene, scenePath);
            Debug.Log($"[HST] 场景已存: {scenePath} (objects={man.objects.Count} lights={man.lights.Count} cams={man.cameras.Count})");
        }

        // ---------------- 材质 ----------------
        static Material CreateMat(MMat ms, float emissionK)
        {
            bool emissive = ms.emit != null && ms.emit.Count == 3 && ms.emit_str > 0f;
            // 2026-10-08: 发光体 = URP/Lit + _EmissionColor + 磁盘级 _EMISSION 关键字 + 烘焙 GI。
            // 为什么必须 URP/Lit(而不是自建 Unlit/HST-Emit): Progressive 光照贴图器靠**渲染材质**提取
            // albedo/emissive(日志 "[PathTracer] Material rendered (update) albedo/emissive ..."), 只认标准
            // 着色器的提取路径; 而 _EMISSION 关键字存不进 .mat 的老问题已用磁盘级改写解决(见下)。
            // 旧旧做法(Unlit+HDR 基色 + EmissiveIsBlack = 自发光不照明)是用 L_PANE_/L_FRGLOW_/HST_SeamGlow
            // 假点光阵去模仿照明 —— 亮池与灯罩阴影结构整个丢失, 已废弃。
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            var mat = new Material(shader) { name = "M_" + ms.name };
            if (emissive)
            {
                float ek = ms.emit_str * emissionK;
                var albE = ms.albedo != null && ms.albedo.Count == 3
                    ? new Color(ms.albedo[0], ms.albedo[1], ms.albedo[2]) : Color.white;
                mat.SetColor("_BaseColor", albE);
                mat.SetFloat("_Metallic", 0f);
                mat.SetFloat("_Smoothness", Mathf.Clamp01(1f - ms.rough));
                mat.SetColor("_EmissionColor", new Color(ms.emit[0] * ek, ms.emit[1] * ek, ms.emit[2] * ek, 1f));
                ForceEmissionKeyword(mat);
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive;  // 发光进烘焙 GI
                mat.doubleSidedGI = true;                                                   // Cycles 双面发射
                Debug.Log($"[HST-EMIT] {ms.name}: shader={shader.name} _EmissionColor=({ms.emit[0] * ek:F2},{ms.emit[1] * ek:F2},{ms.emit[2] * ek:F2}) GI={mat.globalIlluminationFlags}");
                mat.SetFloat("_Surface", 0f);
                mat.SetInt("_SrcBlend", (int)BlendMode.One);
                mat.SetInt("_DstBlend", (int)BlendMode.Zero);
                mat.SetFloat("_ZWrite", 1f);
                mat.SetOverrideTag("RenderType", "Opaque");
                if (mat.HasProperty("_Cull")) mat.SetInt("_Cull", 0);
                return mat;
            }
            var alb = ms.albedo != null && ms.albedo.Count == 3
                ? new Color(ms.albedo[0], ms.albedo[1], ms.albedo[2]) : Color.white;
            mat.SetColor("_BaseColor", alb);
            mat.SetFloat("_Metallic", ms.metal);
            mat.SetFloat("_Smoothness", Mathf.Clamp01(1f - ms.rough));
            if (ms.tex != null)
            {
                // 带贴图时基色=白: Blender 的反照率就是贴图本身(BC×AO 预乘瓦片),
                // 再乘 manifest 标量会把墙双重染色(标量只该用于无贴图材质)。
                mat.SetColor("_BaseColor", Color.white);
                string d = TexDir + "/T_HS_" + ms.tex;
                // AO 口径与 Blender 对齐: Blender 端把 AO **乘进反照率**(build_lib.mat_tex),
                // 而 URP 的 _OcclusionMap 只压**间接光** —— 在"间接光占九成"的烘焙场景里两者
                // 数值接近但不相同。所以改用预乘好的 BCAO 贴图(见 scripts/scene/bake_ao_into_bc.py,
                // 线性域相乘后再编回 sRGB, 与 Blender 同式), 并把 _OcclusionStrength 置 0 防双重压暗。
                var bc = Load(d + "/T_HS_" + ms.tex + (ms.ao_mix > 0f
                    ? "_BCAO" + Mathf.RoundToInt(ms.ao_mix * 100f).ToString("00") + ".png"
                    : "_BC.png"), false);
                if (bc == null) bc = Load(d + "/T_HS_" + ms.tex + "_BC.png", false);
                var nm = Load(d + "/T_HS_" + ms.tex + "_N.png", false);
                var msMap = Load(d + "/T_HS_" + ms.tex + "_MS.png", false);
                // 2026-10-08 修: URP/Lit 的基色属性是 _BaseMap, 没有 _MainTex(内置管线的名字) ——
                // SetTexture("_MainTex") 静默无效, 表现为"bump/MS 挂上了唯独基色图从没挂上"。
                if (bc != null) mat.SetTexture("_BaseMap", bc);
                if (nm != null) { mat.SetTexture("_BumpMap", nm); mat.EnableKeyword("_NORMALMAP"); }
                if (msMap != null)
                {
                    mat.SetTexture("_MetallicGlossMap", msMap);
                    mat.EnableKeyword("_METALLICSPECGLOSSMAP");
                    mat.SetFloat("_SmoothnessTextureChannel", 1f); // smoothness = metallic alpha
                    mat.SetFloat("_Smoothness", 1f);
                }
                mat.SetFloat("_OcclusionStrength", 0f);   // AO 已在 BCAO 里, 不再压间接光
            }
            if (ms.alpha < 1f || ms.kind == "glass")
            {
                float a = ms.alpha < 1f ? ms.alpha : 0.22f;
                mat.SetFloat("_Surface", 1f); mat.SetFloat("_Blend", 0f);
                mat.SetOverrideTag("RenderType", "Transparent");
                mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.renderQueue = (int)RenderQueue.Transparent;
                var c = mat.GetColor("_BaseColor"); c.a = a; mat.SetColor("_BaseColor", c);
                if (ms.kind == "glass") mat.SetFloat("_Smoothness", 0.95f);
            }
            return mat;
        }

        static Texture2D Load(string path, bool unused)
        {
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static void FixTextureImporters()
        {
            foreach (var p in AssetDatabase.FindAssets("", new[] { TexDir }).Select(AssetDatabase.GUIDToAssetPath))
            {
                var imp = AssetImporter.GetAtPath(p) as TextureImporter;
                if (imp == null) continue;
                bool dirty = false;
                if (p.EndsWith("_N.png") && imp.textureType != TextureImporterType.NormalMap)
                { imp.textureType = TextureImporterType.NormalMap; dirty = true; }
                if ((p.EndsWith("_MS.png") || p.EndsWith("_AO.png") || p.EndsWith("_R.png") || p.EndsWith("_M.png")) && imp.sRGBTexture)
                { imp.sRGBTexture = false; dirty = true; }
                if (dirty) imp.SaveAndReimport();
            }
        }

        // ---------------- 校验 ----------------
        static void VerifyAll()
        {
            Directory.CreateDirectory(VerifyOut);
            foreach (var tag in new[] { "corridor", "atrium", "atrium_v6" })
            {
                string mp = Path.Combine(Application.dataPath, "HST_Prags/manifests/manifest_" + tag + ".json");
                if (!File.Exists(mp)) { Debug.LogError("[HST-verify] manifest 缺失 " + mp); continue; }
                var man = JsonUtility.FromJson<Manifest>(File.ReadAllText(mp));
                string scenePath = SceneDir + "/" + man.scene + ".unity";
                if (!File.Exists(scenePath)) { Debug.LogError("[HST-verify] 场景缺失 " + scenePath); continue; }
                var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                var sb = new StringBuilder("name,pos_err_mm,size_err_mm,amin_xyz,amax_xyz\n");
                float maxPos = 0f, maxSize = 0f; int n = 0, missing = 0;
                foreach (var mf in UnityEngine.Object.FindObjectsOfType<MeshFilter>(true))
                {
                    var e = man.objects.FirstOrDefault(o => o.name == mf.name);
                    if (e == null) { missing++; continue; }
                    var b = mf.GetComponent<Renderer>().bounds;
                    float dp = MaxAbs(b.min, e.aabb_min_u, true);
                    dp = Mathf.Max(dp, MaxAbs(b.max, e.aabb_max_u, true));
                    float ds = Mathf.Abs(b.size.x - (e.aabb_max_u[0] - e.aabb_min_u[0]));
                    ds = Mathf.Max(ds, Mathf.Abs(b.size.y - (e.aabb_max_u[1] - e.aabb_min_u[1])));
                    ds = Mathf.Max(ds, Mathf.Abs(b.size.z - (e.aabb_max_u[2] - e.aabb_min_u[2])));
                    sb.AppendLine($"{mf.name},{dp * 1000f:F3},{ds * 1000f:F3}," +
                        $"{b.min.x:F3};{b.min.y:F3};{b.min.z:F3},{b.max.x:F3};{b.max.y:F3};{b.max.z:F3}");
                    maxPos = Mathf.Max(maxPos, dp); maxSize = Mathf.Max(maxSize, ds); n++;
                }
                int cams = UnityEngine.Object.FindObjectsOfType<Camera>(true).Length;
                int lights = UnityEngine.Object.FindObjectsOfType<Light>(true).Length;
                sb.AppendLine($"SUMMARY,objects={n},missing={missing},max_pos_err_mm={maxPos * 1000f:F3},max_size_err_mm={maxSize * 1000f:F3},cams={cams},lights={lights}");
                File.WriteAllText(Path.Combine(VerifyOut, "verify_" + tag + ".csv"), sb.ToString());
                Debug.Log($"[HST-verify] {tag}: n={n} missing={missing} maxPos={maxPos * 1000f:F2}mm maxSize={maxSize * 1000f:F2}mm cams={cams} lights={lights}");
            }
            Debug.Log("[HST-verify] CSV 已写: " + VerifyOut);
        }

        static float MaxAbs(Vector3 a, float[] b, bool unused)
        {
            float m = Mathf.Abs(a.x - b[0]);
            m = Mathf.Max(m, Mathf.Abs(a.y - b[1]));
            m = Mathf.Max(m, Mathf.Abs(a.z - b[2]));
            return m;
        }

        // ---------------- 杂 ----------------
        static Vector3 V(float[] f) { return new Vector3(f[0], f[1], f[2]); }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            string leaf = Path.GetFileName(path);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
