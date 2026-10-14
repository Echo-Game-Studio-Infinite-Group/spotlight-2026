using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Spotlight.VFX
{
    // RuinsFireBuilder —— 按配置一键构建"废墟氛围火"全套资产（图集/材质/网格/预制体）
    //
    // 产出（Assets/Art/VFX/Fire/）：
    //   Textures/  T_FireFlame_A|B|C（掠过档三变体，剪影同族）/ T_FireLingerFlame / T_FireSmoke / T_FireGlow / T_FireEmber
    //   Meshes/    FireCard（底部中心为轴的竖直细分卡片：风弯曲要它能弯出弧）
    //   Materials/ M_RuinsFire_FlameA|B|C / M_RuinsFire_LingerFlame / M_RuinsFire_Glow / M_RuinsFire_GroundGlow / M_RuinsFire_Smoke / M_RuinsFire_Ember
    //   Prefabs/   FX_RuinsFire_Roadside（掠过档：无粒子、无灯、面片+光斑）/ FX_RuinsFire_Linger（驻足档：+烟+余烬+闪烁灯）
    //   RuinsFireConfig.asset（配方；本文件只负责"读配方做东西"，所有可调数值都在那里）
    //
    // **调参入口是 RuinsFireConfig.asset，不是这个文件**：播放速度、点光半径/强度、地面光斑尺寸、
    // 距离淡出、余烬寿命/尺寸/发射率、LOD 两个距离……全在配置里（Inspector 可改、可 diff）。
    // 这个文件里只留"怎么装配"的逻辑（网格拓扑、导入口径、材质混合口径、自检）。
    //
    // 与调研（docs/技术/游戏特效与火焰调研.md §6.5/§6.6）的对应：
    //   掠过档 = 自发光 flipbook 面片 + 烘焙等效光斑（这里用 GroundGlow 面片顶替"照亮地面"，
    //            正式工程应把火的静态照明烘进光照贴图，见 §6.4）；**不用真粒子**。
    //   驻足档 = 在其上补齐 烟 / 余烬 / 动态闪烁灯（动态灯只给驻足档，见 §6.4）。
    public static class RuinsFireBuilder
    {
        const string Root = "Assets/Art/VFX/Fire";
        const string TexDir = Root + "/Textures";
        const string MeshDir = Root + "/Meshes";
        const string MatDir = Root + "/Materials";
        const string PrefabDir = Root + "/Prefabs";
        const string ConfigPath = Root + "/RuinsFireConfig.asset";
        const string ShaderName = "OurFunction/VFX/RuinsFireFlipbook";

        [MenuItem("超高速行者/VFX/构建废墟氛围火资产")]
        public static void Build()
        {
            EnsureFolders();
            var cfg = LoadOrCreateConfig();
            if (cfg == null) return;

            // ---------- 1. 图集（按配置逐张生成）----------
            var sheetPaths = new Dictionary<string, string>();
            foreach (var s in cfg.sheets)
            {
                string path = TexDir + "/" + s.fileName;
                sheetPaths[s.fileName] = path;
                switch (s.kind)
                {
                    case RuinsFireConfig.SheetKind.Flame:
                        Gen(path, () => FireFlipbookGen.GenerateFlameSheet(path, s.style, s.seed));
                        break;
                    case RuinsFireConfig.SheetKind.Smoke:
                        Gen(path, () => FireFlipbookGen.GenerateSmokeSheet(path, s.seed));
                        break;
                    case RuinsFireConfig.SheetKind.Glow:
                        Gen(path, () => FireFlipbookGen.GenerateGlow(path, s.pixelSize));
                        break;
                    case RuinsFireConfig.SheetKind.Ember:
                        Gen(path, () => FireFlipbookGen.GenerateEmber(path, s.pixelSize));
                        break;
                }
                // 导入口径：颜色贴图（被当 RGB 读，如余烬）→ sRGB 开 + alpha 当透明；
                // 数据贴图（R=密度/G=温度/B=热点）→ sRGB 必须关，否则黑体温度被伽马二次污染。
                if (s.colorTexture) ImportColor(path, s.importMaxSize);
                else ImportData(path, s.importMaxSize);
            }

            var sh = Shader.Find(ShaderName);
            if (sh == null) { Debug.LogError($"[RuinsFire] 找不到着色器 {ShaderName}（是否编译失败？）"); return; }
            var urpParticlesUnlit = Shader.Find("Universal Render Pipeline/Particles/Unlit");

            // ---------- 2. 网格 ----------
            var card = BuildFireCard(MeshDir + "/FireCard.asset");

            // ---------- 3. 材质 ----------
            var mats = new Dictionary<string, Material>();
            foreach (var s in cfg.flameMaterials) mats[s.fileName] = FlameMat(MatDir, s, sh, Tex(sheetPaths, s.sheet));
            foreach (var s in cfg.glowMaterials) mats[s.fileName] = GlowMat(MatDir, s, sh, Tex(sheetPaths, s.sheet));
            foreach (var s in cfg.smokeMaterials) mats[s.fileName] = SmokeMat(MatDir, s, sh, Tex(sheetPaths, s.sheet));
            foreach (var s in cfg.emberMaterials) mats[s.fileName] = EmberMat(MatDir, s, urpParticlesUnlit, Tex(sheetPaths, s.sheet));

            AssetDatabase.SaveAssets();

            // ---------- 4. 预制体 ----------
            Material emberMat = cfg.emberMaterials.Length > 0 ? mats[cfg.emberMaterials[0].fileName] : null;
            BuildPrefab("FX_RuinsFire_Roadside", cfg.roadside, card, mats, emberMat);
            BuildPrefab("FX_RuinsFire_Linger", cfg.linger, card, mats, emberMat);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            VerifyMaterials(cfg);   // 必须放在导入之后：URP 的材质后处理器是在导入时跑并覆写属性的
            Debug.Log($"[RuinsFire] 构建完成 → {Root}（掠过档 + 驻足档；配方 = {ConfigPath}）");
        }

        // 配置资产不存在就按默认值建一个 —— 这样"第一次构建"也能直接跑，
        // 而且建完之后所有数值就都在 Inspector 里可改了。
        static RuinsFireConfig LoadOrCreateConfig()
        {
            var cfg = AssetDatabase.LoadAssetAtPath<RuinsFireConfig>(ConfigPath);
            if (cfg != null) return cfg;
            cfg = RuinsFireConfig.CreateDefault();
            AssetDatabase.CreateAsset(cfg, ConfigPath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[RuinsFire] 按默认值新建配方 {ConfigPath}（以后调参改它，不用改代码）");
            return AssetDatabase.LoadAssetAtPath<RuinsFireConfig>(ConfigPath);
        }

        static Texture2D Tex(Dictionary<string, string> paths, string fileName)
        {
            if (string.IsNullOrEmpty(fileName) || !paths.TryGetValue(fileName, out string p))
            {
                Debug.LogError($"[RuinsFire] 配置里引用了不存在的图集名『{fileName}』（sheets 里没有这张）");
                return null;
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(p);
        }

        // ------------------------------------------------------------------
        static void EnsureFolders()
        {
            string[] dirs = { "Assets/Art/VFX", Root, TexDir, MeshDir, MatDir, PrefabDir };
            foreach (var d in dirs)
                if (!AssetDatabase.IsValidFolder(d))
                    AssetDatabase.CreateFolder(Path.GetDirectoryName(d).Replace('\\', '/'), Path.GetFileName(d));
        }

        static string Gen(string path, System.Func<string> f)
        {
            // 每次都重生成：种子确定 → 输出确定（改生成器参数即生效，不会因为"文件已存在"而沉默地跳过）
            f();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            Debug.Log($"[RuinsFire] 生成图集 {path}");
            return path;
        }

        // 数据贴图导入：sRGB 关（否则黑体温度被伽马二次污染）、不压缩（避免数据通道块状伪影）、
        // mip 开（远距离不闪）、Clamp（我们按 tile 手动索引 UV）。
        static void ImportData(string path, int maxSize)
        {
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null) { Debug.LogWarning($"[RuinsFire] 无导入器 {path}"); return; }
            ti.textureType = TextureImporterType.Default;
            ti.sRGBTexture = false;
            ti.alphaSource = TextureImporterAlphaSource.None;
            ti.alphaIsTransparency = false;
            ti.textureCompression = TextureImporterCompression.Uncompressed;
            ti.mipmapEnabled = true;
            ti.filterMode = FilterMode.Bilinear;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.npotScale = TextureImporterNPOTScale.None;
            ti.maxTextureSize = maxSize;
            ti.SaveAndReimport();
        }

        // 颜色贴图（余烬/火星这类被当 RGB 读的）：sRGB 开、Alpha 当透alpha
        static void ImportColor(string path, int maxSize)
        {
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null) return;
            ti.textureType = TextureImporterType.Default;
            ti.sRGBTexture = true;
            ti.alphaSource = TextureImporterAlphaSource.FromInput;
            ti.alphaIsTransparency = true;
            ti.textureCompression = TextureImporterCompression.CompressedHQ;
            ti.mipmapEnabled = true;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.maxTextureSize = maxSize;
            ti.SaveAndReimport();
        }

        // 底部中心为轴的竖直卡片（细分 8 段 → 风弯曲能弯出弧线，而不是折成两段）
        static Mesh BuildFireCard(string path)
        {
            var exist = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (exist != null) return exist;

            const int cols = 1, rows = 8;
            int nv = (cols + 1) * (rows + 1);
            var verts = new Vector3[nv];
            var uvs = new Vector2[nv];
            var normals = new Vector3[nv];
            int i = 0;
            for (int j = 0; j <= rows; j++)
                for (int k = 0; k <= cols; k++, i++)
                {
                    float u = (float)k / cols, v = (float)j / rows;
                    verts[i] = new Vector3(u - 0.5f, v, 0f);   // 底部中心为轴、y ∈ [0,1]
                    uvs[i] = new Vector2(u, v);
                    normals[i] = new Vector3(0, 0, -1);
                }

            var tris = new List<int>();
            for (int j = 0; j < rows; j++)
                for (int k = 0; k < cols; k++)
                {
                    int a = j * (cols + 1) + k, b = a + 1, c = a + (cols + 1), d = c + 1;
                    tris.Add(a); tris.Add(c); tris.Add(b);
                    tris.Add(b); tris.Add(c); tris.Add(d);
                }

            var mesh = new Mesh { name = "FireCard" };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetNormals(normals);
            mesh.SetTriangles(tris, 0);
            // 顶点着色器会位移（billboard 旋转 + 风弯曲），包围盒必须给足，否则会被视锥剔除误裁
            mesh.bounds = new Bounds(new Vector3(0f, 1.2f, 0f), new Vector3(9f, 9f, 9f));
            AssetDatabase.CreateAsset(mesh, path);
            return AssetDatabase.LoadAssetAtPath<Mesh>(path);
        }

        static Material NewMat(string dir, string fileName, Shader sh)
        {
            string path = dir + "/" + fileName;
            var old = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (old != null) AssetDatabase.DeleteAsset(path);
            var m = new Material(sh);
            AssetDatabase.CreateAsset(m, path);
            return AssetDatabase.LoadAssetAtPath<Material>(path);
        }

        // 火焰材质：黑体配色 + 圆柱朝向 + 风弯曲 + 软粒子 + 距离淡出 + premultiply 混合
        static Material FlameMat(string dir, RuinsFireConfig.FlameMaterialSpec s, Shader sh, Texture2D sheet)
        {
            var m = NewMat(dir, s.fileName, sh);
            if (m == null) return null;
            m.SetTexture("_BaseMap", sheet);
            m.SetColor("_BaseColor", s.baseColor);
            m.SetColor("_EmissionColor", s.emissionColor);
            m.SetFloat("_Brightness", s.brightness);
            m.SetFloat("_Opacity", s.opacity);
            m.SetFloat("_DensityPower", s.densityPower);
            m.SetFloat("_FlipbookCols", s.cols);
            m.SetFloat("_FlipbookRows", s.rows);
            m.SetFloat("_Speed", s.speed);
            m.SetFloat("_RandomPhase", s.randomPhase);
            m.SetFloat("_ColorMode", s.colorMode);
            m.SetFloat("_KelvinLow", s.kelvinLow);
            m.SetFloat("_KelvinHigh", s.kelvinHigh);
            m.SetFloat("_Saturation", s.saturation);
            m.SetFloat("_HotspotBoost", s.hotspotBoost);
            m.SetFloat("_WindStrength", s.windStrength);
            m.SetFloat("_SwayAmp", s.swayAmp);
            m.SetFloat("_SwaySpeed", s.swaySpeed);
            m.SetFloat("_SoftParticlesNear", s.softNear);
            m.SetFloat("_SoftParticlesFar", s.softFar);
            m.SetFloat("_CameraNearFade", s.cameraNearFade);
            m.SetFloat("_CameraFarFade", s.cameraFarFade);
            m.SetFloat("_FadeStart", s.fadeStart);
            m.SetFloat("_FadeEnd", s.fadeEnd);
            // premultiply：Src=One, Dst=OneMinusSrcAlpha
            m.SetFloat("_SrcBlend", (float)BlendMode.One);
            m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_Cull", (float)CullMode.Off);   // 卡片要两面可见
            m.EnableKeyword("_PREMULTIPLY_ON");
            m.EnableKeyword("_BILLBOARD_ON");
            m.renderQueue = s.renderQueue;
            EditorUtility.SetDirty(m);
            return m;
        }

        // 光晕：flat 色 + 加色。billboard=true 是竖直光晕；false 平铺在地面当"照亮地面"的廉价顶替
        static Material GlowMat(string dir, RuinsFireConfig.GlowMaterialSpec s, Shader sh, Texture2D tex)
        {
            var m = NewMat(dir, s.fileName, sh);
            if (m == null) return null;
            m.SetTexture("_BaseMap", tex);
            m.SetColor("_BaseColor", Color.white);
            m.SetColor("_EmissionColor", s.emissionColor);
            m.SetFloat("_Opacity", s.opacity);
            m.SetFloat("_DensityPower", s.densityPower);
            m.SetFloat("_FlipbookCols", 1f);
            m.SetFloat("_FlipbookRows", 1f);
            m.SetFloat("_Speed", 0f);
            m.SetFloat("_RandomPhase", 0f);
            m.SetFloat("_ColorMode", 1f);              // flat tint（光晕不需要黑体）
            m.SetFloat("_WindStrength", 0f);
            m.SetFloat("_SwayAmp", 0f);
            m.SetFloat("_SoftParticlesNear", 0f);
            m.SetFloat("_SoftParticlesFar", 0f);       // 光晕不做软粒子：省一次深度采样
            m.SetFloat("_CameraNearFade", s.cameraNearFade);
            m.SetFloat("_CameraFarFade", s.cameraFarFade);
            m.SetFloat("_FadeStart", s.fadeStart);
            m.SetFloat("_FadeEnd", s.fadeEnd);
            m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)BlendMode.One);   // 加色
            m.SetFloat("_Cull", (float)CullMode.Off);
            if (s.billboard) m.EnableKeyword("_BILLBOARD_ON");
            m.renderQueue = s.renderQueue;
            EditorUtility.SetDirty(m);
            return m;
        }

        // 烟：premultiply 暗灰、薄（调研 #15461：木火几乎看不到黑烟 → _Opacity 压低）
        static Material SmokeMat(string dir, RuinsFireConfig.SmokeMaterialSpec s, Shader sh, Texture2D sheet)
        {
            var m = NewMat(dir, s.fileName, sh);
            if (m == null) return null;
            m.SetTexture("_BaseMap", sheet);
            m.SetColor("_BaseColor", Color.white);
            m.SetColor("_EmissionColor", s.emissionColor);
            m.SetFloat("_Opacity", s.opacity);
            m.SetFloat("_DensityPower", s.densityPower);
            m.SetFloat("_FlipbookCols", s.cols);
            m.SetFloat("_FlipbookRows", s.rows);
            m.SetFloat("_Speed", s.speed);
            m.SetFloat("_RandomPhase", 1f);
            m.SetFloat("_ColorMode", 1f);              // flat：烟不发光，是遮挡
            m.SetFloat("_WindStrength", s.windStrength);  // 烟比火更受风（飘得更斜）
            m.SetFloat("_SwayAmp", s.swayAmp);
            m.SetFloat("_SwaySpeed", s.swaySpeed);
            m.SetFloat("_SoftParticlesNear", s.softNear);
            m.SetFloat("_SoftParticlesFar", s.softFar);
            m.SetFloat("_CameraNearFade", s.cameraNearFade);
            m.SetFloat("_CameraFarFade", s.cameraFarFade);
            m.SetFloat("_FadeStart", s.fadeStart);
            m.SetFloat("_FadeEnd", s.fadeEnd);
            m.SetFloat("_SrcBlend", (float)BlendMode.One);
            m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_Cull", (float)CullMode.Off);
            m.EnableKeyword("_PREMULTIPLY_ON");
            m.EnableKeyword("_BILLBOARD_ON");
            m.renderQueue = s.renderQueue;
            EditorUtility.SetDirty(m);
            return m;
        }

        // 余烬：用 URP 官方 Particles/Unlit（stock，零风险；见调研"真粒子只用在驻足档/余烬"）
        //
        // ⚠️ 铁律：这个材质**只能写 URP 校验器"读"的属性，绝不能写它"写"的属性**。
        //    URP 的 Editor/AssetPostProcessors/MaterialPostprocessor.InitializeLatest 会对**新建**材质调
        //    ShaderUtils.UpdateMaterial(..., CreatedNewMaterial, ...) →
        //      · BaseShaderGUI.SetupMaterialBlendMode 按 _Surface/_Blend 反推并**覆写**
        //        _SrcBlend / _DstBlend / _ZWrite / RenderType 标签 / renderQueue；
        //      · ParticleGUI.SetMaterialKeywords 按 _SoftParticlesEnabled/_CameraFadingEnabled 反推并覆写
        //        _SOFTPARTICLES_ON / _SoftParticleFadeParams。
        //    导入发生在 AssetDatabase.SaveAssets() 之后 → 脚本里直接 SetFloat 这些值**必被冲掉**。
        //    实测事故：材质落到 _Surface 0 的不透明态（One/Zero/ZWrite 1），余烬渲染成
        //    "黑底方形 + 暖色圆点"（方块是贴图的黑色四角，圆心是径向渐变）。
        //    正确做法见下：只设"输入项"，让 URP 自己推导出正确状态。
        //    本项目自写的 OurFunction/* 着色器不受影响（GetShaderID 返回 Unknown → 后处理器直接跳过）。
        static Material EmberMat(string dir, RuinsFireConfig.EmberMaterialSpec s, Shader sh, Texture2D tex)
        {
            var m = NewMat(dir, s.fileName, sh);
            if (m == null) return null;
            m.SetTexture("_BaseMap", tex);
            m.SetColor("_BaseColor", s.baseColor);
            m.SetFloat("_SoftParticlesNearFadeDistance", s.softNearFadeDistance);
            m.SetFloat("_SoftParticlesFarFadeDistance", s.softFarFadeDistance);
            // ---- 输入项：URP 读这些，自己推导 blend / 关键字 / renderQueue ----
            m.SetFloat("_Surface", s.surface);                 // 1 = Transparent（0 会推导成 One/Zero 不透明）
            m.SetFloat("_Blend", s.blend);                     // 2 = Additive：SrcAlpha/One，余烬是"发光"
            m.SetFloat("_QueueOffset", s.queueOffset);         // → renderQueue = 3000 + 该值
            m.SetFloat("_ColorMode", s.colorMode);             // Multiply：贴图灰度 × _BaseColor × 顶点色
            m.SetFloat("_SoftParticlesEnabled", s.softParticlesEnabled);  // 3cm 的火星做软粒子没意义
            m.SetFloat("_CameraFadingEnabled", s.cameraFadingEnabled);
            m.SetFloat("_FlipbookBlending", s.flipbookBlending);
            m.SetFloat("_AlphaClip", s.alphaClip);
            EditorUtility.SetDirty(m);
            return m;
        }

        // 自检：把"材质被 URP 校验器冲成默认态"这类静默失败变成**响亮的报错**。
        // 必须在 Refresh() 之后调用（后处理器是在导入时跑的）。期望值全部从配置推出来。
        static void VerifyMaterials(RuinsFireConfig cfg)
        {
            foreach (var s in cfg.emberMaterials)
                CheckMat(MatDir + "/" + s.fileName, s.surface, s.blend,
                         3000 + Mathf.RoundToInt(s.queueOffset), null);
            foreach (var s in cfg.flameMaterials)
                CheckMat(MatDir + "/" + s.fileName, null, null, s.renderQueue, "_BILLBOARD_ON");
            foreach (var s in cfg.smokeMaterials)
                CheckMat(MatDir + "/" + s.fileName, null, null, s.renderQueue, "_BILLBOARD_ON");
            foreach (var s in cfg.glowMaterials)
                CheckMat(MatDir + "/" + s.fileName, null, null, s.renderQueue, s.billboard ? "_BILLBOARD_ON" : null);
        }

        static void CheckMat(string path, float? expectSurface, float? expectBlend, int expectQueue, string expectKeyword)
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { Debug.LogError($"[RuinsFire][自检] 材质缺失 {path}"); return; }
            string bad = null;
            if (expectSurface.HasValue && !Mathf.Approximately(m.GetFloat("_Surface"), expectSurface.Value))
                bad += $" _Surface={m.GetFloat("_Surface")}(期望{expectSurface.Value})";
            if (expectBlend.HasValue && !Mathf.Approximately(m.GetFloat("_Blend"), expectBlend.Value))
                bad += $" _Blend={m.GetFloat("_Blend")}(期望{expectBlend.Value})";
            if (m.renderQueue != expectQueue) bad += $" renderQueue={m.renderQueue}(期望{expectQueue})";
            if (expectKeyword != null && !m.IsKeywordEnabled(expectKeyword)) bad += $" 缺关键字{expectKeyword}";
            if (bad != null)
                Debug.LogError($"[RuinsFire][自检] {m.name} 状态不对：{bad} —— 大概率又被 URP 材质后处理器覆写了");
            else
                Debug.Log($"[RuinsFire][自检] {m.name} ok（queue={m.renderQueue} src={m.GetFloat("_SrcBlend")} dst={m.GetFloat("_DstBlend")} zwrite={m.GetFloat("_ZWrite")}）");
        }

        // ------------------------------------------------------------------
        static GameObject Card(Mesh mesh, Material mat, RuinsFireConfig.CardSpec spec)
        {
            var go = new GameObject(spec.name);
            go.transform.localPosition = spec.position;
            go.transform.localRotation = Quaternion.Euler(spec.euler);
            go.transform.localScale = spec.scale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;   // 火不投影（省，且投影会露馅）
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            return go;
        }

        // 两档共用一个装配流程：卡片 → 余烬 → 灯 → LOD。
        // （创建顺序会影响预制体序列化结果，保持固定顺序有助于"重建后产物应当无 diff"这条验证。）
        static void BuildPrefab(string prefabName, RuinsFireConfig.TierSpec tier, Mesh card, Dictionary<string, Material> mats, Material emberMat)
        {
            string path = PrefabDir + "/" + prefabName + ".prefab";
            var old = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (old != null) AssetDatabase.DeleteAsset(path);

            var root = new GameObject(prefabName);
            var byName = new Dictionary<string, GameObject>();

            foreach (var c in tier.cards)
            {
                if (!mats.TryGetValue(c.material, out Material mat) || mat == null)
                { Debug.LogError($"[RuinsFire] 卡片 {c.name} 引用了不存在的材质『{c.material}』"); continue; }
                var go = Card(card, mat, c);
                go.transform.SetParent(root.transform, false);
                byName[c.name] = go;
            }

            if (tier.embers) byName["Embers"] = BuildEmbers(root.transform, tier.emberSpec, emberMat);
            if (tier.light) byName["Light"] = BuildLight(root.transform, tier.lightSpec);

            var lod = root.AddComponent<RuinsFireLod>();
            lod.detailDistance = tier.lod.detailDistance;
            lod.cullDistance = tier.lod.cullDistance;
            lod.runtimeOnly = tier.lod.runtimeOnly;
            var details = new List<GameObject>();
            foreach (var n in tier.lod.detailObjects)
                if (byName.TryGetValue(n, out GameObject d)) details.Add(d);
                else Debug.LogWarning($"[RuinsFire] LOD detailObjects 里的『{n}』在这档里不存在，已跳过");
            lod.detailObjects = details.ToArray();

            PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            Debug.Log($"[RuinsFire] 预制体 {path}（卡片 {tier.cards.Length} 张，细货 {details.Count} 个）");
        }

        // 余烬（真粒子：只在驻足档出现）
        static GameObject BuildEmbers(Transform parent, RuinsFireConfig.EmberSpec s, Material emberMat)
        {
            var go = new GameObject("Embers");
            go.transform.SetParent(parent, false);
            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.duration = s.duration; main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(s.lifetimeMin, s.lifetimeMax);
            main.startSpeed = new ParticleSystem.MinMaxCurve(s.speedMin, s.speedMax);
            main.startSize = new ParticleSystem.MinMaxCurve(s.sizeMin, s.sizeMax);
            main.startColor = new ParticleSystem.MinMaxGradient(s.colorA, s.colorB);
            main.gravityModifier = s.gravityModifier;
            main.maxParticles = s.maxParticles;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.playOnAwake = true;
            main.prewarm = true;
            var em = ps.emission; em.rateOverTime = s.rateOverTime;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = s.shapeAngle; shape.radius = s.shapeRadius;
            shape.rotation = new Vector3(-90f, 0f, 0f);
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(s.gradColorA, 0f), new GradientColorKey(s.gradColorB, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, s.alphaHold), new GradientAlphaKey(0f, 1f) });
            col.color = new ParticleSystem.MinMaxGradient(grad);
            var size = ps.sizeOverLifetime; size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, s.sizeEnd)));

            var pr = go.GetComponent<ParticleSystemRenderer>();
            pr.sharedMaterial = emberMat;
            pr.renderMode = ParticleSystemRenderMode.Billboard;
            pr.alignment = ParticleSystemRenderSpace.View;
            pr.shadowCastingMode = ShadowCastingMode.Off;
            pr.receiveShadows = false;
            return go;
        }

        // 动态闪烁灯（只给驻足档）
        // 强度/半径的取值理由：本工程的不透明面走 NPR 卡通光照，光是**按阈值分带**的，
        // 点光一打到断墙上就是一个"边缘偏硬的大椭圆光池"（实测放大半径到 6m 时极其抢戏）。
        // 真实的废墟氛围火照度本来就低，且正式做法是把火的静态照明**烘进光照贴图**（调研 §6.4），
        // 动态灯只在"驻足档"留一点近距离闪烁感 —— 所以 range/intensity 都压得很小（见配置）。
        static GameObject BuildLight(Transform parent, RuinsFireConfig.LightSpec s)
        {
            var go = new GameObject("Light");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, s.height, 0f);
            var li = go.AddComponent<Light>();
            li.type = LightType.Point;
            li.color = s.color;
            li.intensity = s.intensity;
            li.range = s.range;
            li.shadows = LightShadows.None;          // 火光不投影（成本/收益不划算）
            li.renderMode = LightRenderMode.ForcePixel;
            var flick = go.AddComponent<RuinsFireFlicker>();
            flick.baseIntensity = s.intensity;
            flick.baseRange = s.range;
            flick.flickerSpeed = s.flickerSpeed;
            flick.intensityAmplitude = s.intensityAmplitude;
            flick.rangeAmplitude = s.rangeAmplitude;
            flick.phaseOffset = s.phaseOffset;
            return go;
        }
    }
}
