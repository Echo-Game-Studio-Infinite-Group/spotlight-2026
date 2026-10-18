#if UNITY_EDITOR
// 走廊二期 (2026-10-11, 用户反馈四条):
//   ① "周围的墙上饰品没有跟着动" → FORGE_* 贴墙件(机架/管线/桥架/阀门)随墙外移 ΔZ, 含机架专属灯
//   ② "挑高没有改"                 → 天花/灯槽/顶线脚整体抬 ΔY(=一块墙板短边 1.792m), 净高 3.4→5.192m
//   ③ "亮度还是太暗, 可能得换成3排灯" → 天花板重排为 3 条灯槽(z=0,±2.30) + 灯串三排 + 强度 ×LightK
//   ④ "门做成可以开关有门缝的门"    → 双扇内开自动门(中缝 6cm + 自发光门缝条), 见 Assets/Art/Environment/HST_Kit/Runtime/HST/HstSwingDoor.cs
//
// 关键前提(读代码得到, 不是猜):
//   · ARCH/FORGE 网格是"顶点烤死世界坐标", GO 变换全在原点 → 位置一律用 Renderer.bounds 校准;
//     但"整体平移"和"绕自身原点等比缩放"安全(y 缩放只改高度, 位置随后由 bounds 校准)。
//   · 走廊照明全靠 L_LUMEN_ChannelL/R 代理点灯串(69颗/排, Point, y=3.30, z=±1.05);
//     M_ARCH_PaneEmit 的发光亮条只负责"看得见", 不参与 GI。
//   · BakeScenes 每烤一次会把门区灯(DoorTop/Fill/EndWash)**再乘 0.33** → 本工具重烤前必须重置回基值。
//   · 幂等: 见到 HST_STAGE2_DONE 即中止(重跑请先 git 还原场景)。
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace HST
{
    public static class HstCorridorStage2
    {
        const string SceneDir = "Assets/Scenes";
        const string SceneName = "SC_Corridor_Pragmata";

        // ---------------- 参数区 ----------------
        const float DeltaZ = 1.8f;                 // 墙饰随墙外移(= 一期 ΔW)
        const float DeltaY = 1.792f;               // 挑高量(= 墙板短边, 高带正好一块板高)
        const float RowZ = 2.30f;                  // 灯带行位(±)
        const float LightK = 2.47f;                // 灯强倍率(1.9 起, 2026-10-11 用户令"再抬高约30%" → ×1.3)
        const float YOld = 3.4f;                   // 原净高(墙板顶)
        const float YNew = YOld + DeltaY;          // 新净高 5.192
        const float YBand = (YOld + YNew) * 0.5f;  // 高带中心 4.296
        // 天花分带: 板宽 0.98(原生), 灯槽宽 0.34 → 槽位 z=0,±2.30 时板带正好整数块, 无需缩放
        static readonly float[] PlankZ = { 0.66f, 1.64f, 2.96f, 3.94f, -0.66f, -1.64f, -2.96f, -3.94f };
        static readonly float[] SlotZ = { 0f, RowZ, -RowZ };
        static readonly float[] XTile = { -9f, 9.4f, 27.8f, 46.2f };   // 天花/墙基 x(节距 18.4, 4 档)
        // 门区灯基值(= 公式 ×0.33, 与一期 RunAll 同口径): DoorTop=100W·kArea/3×0.33 ...
        const float DoorTopI = 0.0396f, FillI = 0.01584f, EndWashI = 0.02376f;
        // 门几何
        const float DoorX = -18.30f, OpenHalfZ = 0.80f, GapCenter = 0.06f, GapSide = 0.02f;
        const float LeafH = 2.36f, LeafT = 0.06f;

        [MenuItem("HST/走廊二期-只做几何(挑高+墙饰+3排灯+门)")]
        public static void GeomOnly() { Core(); }

        [MenuItem("HST/走廊二期全流程(几何+重烤+探针+出图)")]
        public static void RunAll()
        {
            Core();
            HstWallMatFix.Fix();                         // 门楣/门扇/上段高带的贴图密度统一(材质变体)
            HstCorridorExtend.StripExtStatic();          // 外壳/城市不进烤
            HstCorridorExtend.Log("二期: 步骤2/7 补 UV2"); SceneBuilder.FixMissingLightmapUVs();
            HstCorridorExtend.Log("二期: 步骤3/7 碰撞体+主控"); SceneBuilder.AddWalkPlayer();
            ResetDoorLights();                            // BakeScenes 每烤 ×0.33, 重烤前必须复位
            HstCorridorExtend.Log("二期: 步骤4/7 重烤(纯 Baked + GPU; 跳过 BakeScenes 内探针烘烤, 反射由手工盒接管)");
            SceneBuilder._indirectOverride = 4096;
            SceneBuilder._skipProbeBake = true;
            SceneBuilder.BakeCorridorOnly();
            SceneBuilder._skipProbeBake = false;
            SceneBuilder._indirectOverride = 0;
            SceneBuilder.Polish();
            HstCorridorExtend.Log("二期: 步骤5/7 手工反射盒 + 相机裁决");
            SceneBuilder.BakeProbeManual();
            HstCorridorExtend.Log("二期: 步骤6/7 出图 + 线性读数");
            SceneBuilder.Shoot();
            SceneBuilder.DiagLinear();
            HstCorridorExtend.Log("二期: 步骤7/7 全流程完成");
        }

        // ---------------- 主流程 ----------------
        static void Core()
        {
            var scene = EditorSceneManager.OpenScene(SceneDir + "/" + SceneName + ".unity", OpenSceneMode.Single);
            if (GameObject.Find("HST_STAGE2_DONE") != null)
            { Debug.LogError("[HST-S2] 已做过二期(检测到标记)。重跑请先 git 还原场景。"); return; }

            // ---- 1. 墙饰随墙外移 ----
            var propPrefix = new[] { "FORGE_RackA", "FORGE_RackB", "FORGE_Pipe", "FORGE_Valve",
                                     "FORGE_Tray", "FORGE_CableDrop" };
            int moved = 0;
            foreach (var mr in HstCorridorExtend.AllMRs().ToArray())
            {
                if (!HstCorridorExtend.StartsWithAny(mr.name, propPrefix)) continue;
                float z = mr.bounds.center.z;
                if (Mathf.Abs(z) < 0.5f) continue;
                mr.transform.position += new Vector3(0f, 0f, Mathf.Sign(z) * DeltaZ);
                moved++;
            }
            foreach (var nm in new[] { "L_LUMEN_RackA", "L_LUMEN_RackB" })
            {
                var g = GameObject.Find(nm);
                if (g == null) continue;
                g.transform.position += new Vector3(0f, 0f, Mathf.Sign(g.transform.position.z) * DeltaZ);
                moved++;
            }
            HstCorridorExtend.Log("二期 1/6 墙饰随墙外移 " + moved + " 件(ΔZ=" + DeltaZ + ")");

            // ---- 2. 天花板重排: 8 条板带 × 4 档 x, 留出 3 条灯槽 ----
            var plankTpl = GameObject.Find("ARCH_Ceil_nY");
            if (plankTpl == null) { Debug.LogError("[HST-S2] 找不到天花模板 ARCH_Ceil_nY"); return; }
            var plankParent = plankTpl.transform.parent;
            float plankCy = plankTpl.GetComponent<MeshRenderer>().bounds.center.y + DeltaY;
            int np = 0;
            foreach (var zc in PlankZ)
                foreach (var xc in XTile)
                {
                    var c = HstCorridorExtend.CloneMesh(plankTpl, plankParent,
                        "ARCH_CeilBand_" + zc.ToString("0.00") + "_" + xc.ToString("0.0"));
                    HstCorridorExtend.AlignBoundsCenter(c, new Vector3(xc, plankCy, zc));
                    np++;
                }
            foreach (var mr in HstCorridorExtend.AllMRs().ToArray())
                if (mr.name.StartsWith("ARCH_Ceil_")) Object.DestroyImmediate(mr.gameObject);
            HstCorridorExtend.Log("二期 2/6 天花板重排: 新板 " + np + " 块, 旧板拆除, 留 3 条灯槽 z=0/±" + RowZ);

            // ---- 3. 灯槽: +z 组 → +2.30, -z 组 → -2.30, 克隆一组到 0 ----
            var covePrefix = new[] { "ARCH_CoveTop", "ARCH_CoveWall", "ARCH_LightPane", "ARCH_CoveCap" };
            var pSet = new List<GameObject>(); var nSet = new List<GameObject>();
            foreach (var mr in HstCorridorExtend.AllMRs().ToArray())
            {
                if (!HstCorridorExtend.StartsWithAny(mr.name, covePrefix)) continue;
                (mr.bounds.center.z > 0f ? pSet : nSet).Add(mr.gameObject);
            }
            var midSet = new List<GameObject>();
            foreach (var g in pSet)
                midSet.Add(HstCorridorExtend.CloneMesh(g, g.transform.parent, g.name + "_M"));
            foreach (var g in pSet) g.transform.position += new Vector3(0f, DeltaY, RowZ - 1.05f);
            foreach (var g in nSet) g.transform.position += new Vector3(0f, DeltaY, -(RowZ - 1.05f));
            foreach (var g in midSet) g.transform.position += new Vector3(0f, DeltaY, -1.05f);
            HstCorridorExtend.Log("二期 3/6 灯槽三排: 搬迁 " + (pSet.Count + nSet.Count) + " 件, 克隆中排 " + midSet.Count + " 件");

            // ---- 4. 灯串三排 + 强度 ----
            var chL = new List<Light>(); var chR = new List<Light>();
            foreach (var li in Object.FindObjectsOfType<Light>(true))
            {
                if (li.name.StartsWith("L_LUMEN_ChannelL")) chL.Add(li);
                else if (li.name.StartsWith("L_LUMEN_ChannelR")) chR.Add(li);
            }
            int nc = 0;
            foreach (var li in chL)
            {
                var srcPos = li.transform.position;
                var go = new GameObject("L_LUMEN_ChannelC_" + li.name.Substring(li.name.LastIndexOf('_') + 1));
                go.transform.SetParent(li.transform.parent, false);
                var nl = go.AddComponent<Light>();
                EditorUtility.CopySerialized(li, nl);
                nl.transform.position = srcPos + new Vector3(0f, DeltaY, -1.05f);   // 原 +1.05 → 0
                nc++;
            }
            foreach (var li in chL) li.transform.position += new Vector3(0f, DeltaY, RowZ - 1.05f);
            foreach (var li in chR) li.transform.position += new Vector3(0f, DeltaY, -(RowZ - 1.05f));
            var allCh = new List<Light>(); allCh.AddRange(chL); allCh.AddRange(chR);
            allCh.AddRange(Object.FindObjectsOfType<Light>(true).Where(l => l.name.StartsWith("L_LUMEN_ChannelC")));
            foreach (var li in allCh) li.intensity *= LightK;
            // 顶部补光灯(Fill)随天花抬
            int nf = 0;
            foreach (var li in Object.FindObjectsOfType<Light>(true))
                if (li.name.StartsWith("L_LUMEN_Fill")) { li.transform.position += new Vector3(0f, DeltaY, 0f); nf++; }
            HstCorridorExtend.Log("二期 4/6 灯串三排: 中排新增 " + nc + " 颗, 强度 ×" + LightK +
                                  " (共 " + allCh.Count + " 颗), Fill 抬升 " + nf + " 颗");

            // ---- 5. 素面高带 + 顶线脚 + 端墙/门头补板 + 外壳抬高 ----
            BuildUpperBand();
            HstCorridorExtend.Log("二期 5/6 素面高带 + 顶线脚 + 端墙/门头补板 + 外壳抬高");

            // ---- 6. 双扇内开自动门 ----
            BuildDoor();
            HstCorridorExtend.Log("二期 6/6 双扇内开自动门(中缝 " + (GapCenter * 100f) + "cm + 发光门缝)");

            var marker = new GameObject("HST_STAGE2_DONE");
            EditorSceneManager.SaveScene(scene);
            HstCorridorExtend.Log("二期几何完成并已存盘");
        }

        // 高带: 四壁在墙板顶(3.4)以上补 ΔY 高的上段(净高 3.4 → 5.192)
        // 高带 = 逐块 1:1 克隆"墙板"本身, 而不是克隆结构墙基 ARCH_WallBase ——
        //   · 材质: 墙板 M_ARCH_WallM 反照率 1.0(白); 墙基 M_ARCH_Base 反照率 0.045(近黑) ——
        //     2026-10-11 实测用错材质 → 墙顶一条近黑横带, 这是本轮的主修项。
        //   · 平面: 墙板在 z=±3.975, 与下半段同一平面(墙基在 ±4.035, 退后 6cm 会形成一道凹槽)。
        //   · 竖缝: 逐块克隆, 竖缝与下半段严丝合缝(旋转板/异宽板都会错缝)。
        // 代价: 板质在竖向上压到 1.792/3.4=0.527 —— 素面金属板, 只体现在板内细线疏密, 可接受。
        static void BuildUpperBand()
        {
            float sy = DeltaY / YOld;
            var panels = new List<GameObject>();
            foreach (var mr in HstCorridorExtend.AllMRs().ToArray())
                if (mr.name.StartsWith("ARCH_Panel_")) panels.Add(mr.gameObject);
            int nb = 0;
            foreach (var g in panels)
            {
                var b0 = g.GetComponent<MeshRenderer>().bounds;
                var s0 = g.transform.localScale;
                var c = HstCorridorExtend.CloneMesh(g, g.transform.parent, g.name + "_band");
                // 走廊物件 GO 缩放 100、本地旋转 -90°绕X → 本地 Z 映射到世界 Y(高度);
                // 压高必须压**本地 z**, 而且必须**乘**而不是赋值(赋值会把 100 倍缩放抹掉 → 缩成 1/100)
                c.transform.localScale = new Vector3(s0.x, s0.y, s0.z * sy);
                HstCorridorExtend.AlignBoundsCenter(c, new Vector3(b0.center.x, YBand, b0.center.z));
                nb++;
            }
            // 顶部线脚: 克隆现有"墙板顶线脚"上移 ΔY(原线脚留在 3.4 处当腰线)
            int nt = 0;
            foreach (var mr in HstCorridorExtend.AllMRs().ToArray())
            {
                if (!(mr.name.StartsWith("ARCH_Trim_") && mr.name.Contains("_hi"))) continue;
                var c = HstCorridorExtend.CloneMesh(mr.gameObject, mr.transform.parent, mr.name + "_top");
                c.transform.position += new Vector3(0f, DeltaY, 0f);
                nt++;
            }
            // 端墙 / 门墙: 重做(修一期缺陷) + 各自补上段
            var p = GameObject.Find("Section_End") != null ? GameObject.Find("Section_End").transform
                                                          : panels[0].transform.parent;
            var sFlags = StaticEditorFlags.ContributeGI | StaticEditorFlags.OccluderStatic |
                         StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccludeeStatic;
            // 门楣: 必须用**走廊墙板网格**克隆, 不能用 Unity 内置 Cube —— 内置 Cube 面是平的、
            // UV2 与墙板不同源, 出图后门楣是一片"没有起伏的浅色板", 和两侧墙板一眼是两种材质。
            // (贴图密度的裁切补偿由 HstWallMatFix 按名 HST_DoorHead 挂的材质变体负责。)
            RebuildEndWalls();
            MakeDoorHead(panels, p, sFlags);
            RaiseShell();
            HstCorridorExtend.Log("二期 5b 高带 " + nb + " 件(逐块克隆墙板) / 顶线脚 " + nt + " 件 / 门楣板 1 件 / 外壳抬高");
        }

        // 走廊两端墙(端墙 x=+40.775 / 门墙 x=-17.98)重做 + 各自补上段。
        // 一期缺陷: 这两处填充板写了 `c.transform.rotation = Quaternion.Euler(0,90,0)` —— **整体替换**了
        // 走廊网格固有的 -90°绕X 旋转(正确做法是"复合": extraY * baseRot)。后果: 板子被转成
        // 3.4×1.79m、5cm 厚的**水平片**平躺在 y=1.70, 两端墙实际是空的, 从走廊看直接穿透到黑外壳。
        // 实证(2026-10-11 射线探针): 沿 -x 打向门墙的射线一次都没命中 ARCH_DoorWallFill,
        // 全部命中外壳 FrontL/FrontR/FrontTop —— 于是"门所在的那面墙"看起来又黑又没有材质。
        static void RebuildEndWalls()
        {
            var tpl = GameObject.Find("ARCH_Panel_-10");     // +z 侧墙板模板(正面朝 -z)
            if (tpl == null) { Debug.LogError("[HST-S2] 缺 ARCH_Panel_-10 模板, 端墙未重做"); return; }
            var parent = tpl.transform.parent;
            float sy = DeltaY / YOld;
            var baseRot = tpl.transform.rotation;            // 走廊网格固有 -90°绕X
            int killed = 0;
            foreach (var mr in HstCorridorExtend.AllMRs().ToArray())
                if (mr.name.StartsWith("ARCH_DoorWallFill_") || mr.name.StartsWith("ARCH_EndWall_"))
                { Object.DestroyImmediate(mr.gameObject); killed++; }
            int made = 0;
            // 门墙 x=-17.98, 正面朝 +x; z 从 0.854 起逐块 1.792: 下段(全高) + 上段(压到 ΔY)
            foreach (float side in new[] { 1f, -1f })
                for (int k = 0; k < 2; k++)
                {
                    var zc = side * (0.854f + 0.896f + k * 1.792f);
                    made += WallRow(tpl, parent, baseRot, "ARCH_DoorWallFill_" + (side > 0f ? "P" : "N") + "_" + k,
                                    Quaternion.Euler(0f, -90f, 0f), -17.98f, zc, sy, 0);
                    made += WallRow(tpl, parent, baseRot, "ARCH_DoorWallFillTop_" + (side > 0f ? "P" : "N") + "_" + k,
                                    Quaternion.Euler(0f, -90f, 0f), -17.98f, zc, sy, 1);
                }
            // 端墙 x=+40.775, 正面朝 -x; z 从 0 起逐块 1.792: 3 块/侧
            foreach (float side in new[] { 1f, -1f })
                for (int k = 0; k < 3; k++)
                {
                    var zc = side * (0.896f + k * 1.792f);
                    made += WallRow(tpl, parent, baseRot, "ARCH_EndWall_" + (side > 0f ? "P" : "N") + "_" + k,
                                    Quaternion.Euler(0f, 90f, 0f), 40.775f, zc, sy, 0);
                    made += WallRow(tpl, parent, baseRot, "ARCH_EndWallTop_" + (side > 0f ? "P" : "N") + "_" + k,
                                    Quaternion.Euler(0f, 90f, 0f), 40.775f, zc, sy, 1);
                }
            HstCorridorExtend.Log("二期 5c 端墙/门墙重做: 拆 " + killed + " 件躺平板, 新建 " + made + " 件(下段+上段)");
        }

        // 单片墙板: rotation = extraY * baseRot(**复合**, 不是替换); row=0 全高 3.4, row=1 压到 ΔY(上段)
        static int WallRow(GameObject tpl, Transform parent, Quaternion baseRot, string name,
                           Quaternion extraY, float wx, float wz, float sy, int row)
        {
            var c = HstCorridorExtend.CloneMesh(tpl, parent, name);
            c.transform.rotation = extraY * baseRot;
            if (row == 1)
            {
                var s0 = c.transform.localScale;
                c.transform.localScale = new Vector3(s0.x, s0.y, s0.z * sy);   // 本地 z → 世界 Y(高度)
            }
            HstCorridorExtend.AlignBoundsCenter(c, new Vector3(wx, row == 0 ? YOld * 0.5f : YBand, wz));
            return 1;
        }

        // 外壳抬到新净高(走廊被包成方碑, 不抬会从门上看穿)
        static void RaiseShell()
        {
            var shell = GameObject.Find("HST_EXT_SHELL");
            if (shell == null) return;
            float top = YNew + 0.45f;                  // 外壳顶(原 = 3.85, 即旧净高 +0.45)
            float bodyH = top + 0.55f;                 // 底 -0.55
            float cy = (top - 0.55f) * 0.5f;
            void Set(string n, Vector3 pos, Vector3 sc)
            {
                var t = shell.transform.Find(n);
                if (t == null) return;
                t.position = pos; t.localScale = sc;
            }
            Set("SideL", new Vector3(18.3f, cy, 5.05f), new Vector3(74.4f, bodyH, 0.3f));
            Set("SideR", new Vector3(18.3f, cy, -5.05f), new Vector3(74.4f, bodyH, 0.3f));
            Set("Roof", new Vector3(18.3f, top + 0.15f, 0f), new Vector3(74.4f, 0.3f, 10.7f));
            Set("Back", new Vector3(55.65f, cy, 0f), new Vector3(0.3f, bodyH, 10.7f));
            Set("FrontL", new Vector3(-19.05f, cy, 3.0f), new Vector3(0.3f, bodyH, 4.1f));
            Set("FrontR", new Vector3(-19.05f, cy, -3.0f), new Vector3(0.3f, bodyH, 4.1f));
            // 门楣板: 从门头(2.595)一直到外壳顶
            float fh = top - 2.595f;
            Set("FrontTop", new Vector3(-19.05f, 2.595f + fh * 0.5f, 0f), new Vector3(0.3f, fh, 10.1f));
        }

        // 双扇内开自动门: 铰链挂根节点(localRotation==world, 不受 Section 变换影响)
        static void BuildDoor()
        {
            var panTpl = GameObject.Find("ARCH_Panel_-10");
            var seamMat = GameObject.Find("ARCH_FrameGlow_nY").GetComponent<MeshRenderer>().sharedMaterial;    // M_ARCH_FrameEmit
            float leafW = (OpenHalfZ * 2f - GapCenter - GapSide * 2f) * 0.5f;   // 0.75
            float hz = OpenHalfZ - GapSide;                                    // 铰链 z = ±0.78

            var hingeL = MkEmpty("HST_DoorHinge_L", new Vector3(DoorX, 0f, -hz));
            var hingeR = MkEmpty("HST_DoorHinge_R", new Vector3(DoorX, 0f, hz));
            MakeLeaf(panTpl, hingeL.transform, "ARCH_DoorLeaf_L", -1f, leafW, hz);
            MakeLeaf(panTpl, hingeR.transform, "ARCH_DoorLeaf_R", +1f, leafW, hz);
            // 门缝自发光条: 贴在各自自由边内侧, 随门扇开合(小尺寸发光件, Cube 即可)
            MkBox(hingeL.transform, "ARCH_DoorSeam_L", new Vector3(0f, LeafH * 0.5f, leafW + 0.012f),
                  new Vector3(0.05f, LeafH - 0.04f, 0.024f), seamMat);
            MkBox(hingeR.transform, "ARCH_DoorSeam_R", new Vector3(0f, LeafH * 0.5f, -(leafW + 0.012f)),
                  new Vector3(0.05f, LeafH - 0.04f, 0.024f), seamMat);

            var ctl = MkEmpty("HST_DoorController", new Vector3(DoorX, 0f, 0f));
            var dr = ctl.AddComponent<HstSwingDoor>();
            dr.hingeL = hingeL.transform; dr.hingeR = hingeR.transform;
            HstCorridorExtend.Log("二期 门: 扇宽 " + leafW + " 铰链 z=±" + hz + " 开角 " + dr.openAngle +
                                  "° 触发 " + dr.openRadius + "m");
        }

        // 门扇: 用**走廊墙板网格**克隆(与两侧墙板同一套起伏/UV2/材质), 而不是 Unity 内置 Cube。
        // 本地轴关系: x=板宽(→世界 z) / y=板厚(→世界 x) / z=板高(→世界 y)。
        // 动态件: 必须清掉从模板继承来的静态标记, 否则会被静态合批/烤进光照贴图, 开门时外观不跟着动。
        static void MakeLeaf(GameObject tpl, Transform hinge, string name, float side, float leafW, float hz)
        {
            if (tpl == null) { Debug.LogError("[HST-S2] 缺墙板模板, 门扇未建"); return; }
            const float panW = 1.792f, panH = 3.4f, panT = 0.05f;
            var s0 = tpl.transform.localScale;
            var c = HstCorridorExtend.CloneMesh(tpl, hinge, name);
            c.transform.localScale = new Vector3(s0.x * (leafW / panW), s0.y * (LeafT / panT), s0.z * (LeafH / panH));
            c.transform.rotation = Quaternion.Euler(0f, -90f, 0f) * tpl.transform.rotation;
            float zc = side > 0f ? (hz - leafW * 0.5f) : -(hz - leafW * 0.5f);
            HstCorridorExtend.AlignBoundsCenter(c, new Vector3(DoorX, LeafH * 0.5f, zc));
            GameObjectUtility.SetStaticEditorFlags(c, (StaticEditorFlags)0);
            var mf = c.GetComponent<MeshFilter>();
            var bc = c.AddComponent<BoxCollider>();
            if (mf != null && mf.sharedMesh != null) { bc.center = mf.sharedMesh.bounds.center; bc.size = mf.sharedMesh.bounds.size; }
        }

        // 门楣(门洞顶 2.4 → 新天花 5.192, z ±0.854): 同样用走廊墙板网格克隆并压到目标尺寸。
        // 内置 Cube 的门楣面是平的、UV2 与墙板不同源 → 出图后是一片"没有起伏的浅色板", 一眼两种材质。
        static void MakeDoorHead(List<GameObject> panels, Transform parent, StaticEditorFlags sFlags)
        {
            var tpl = GameObject.Find("ARCH_Panel_-10");
            if (tpl == null) { Debug.LogError("[HST-S2] 缺墙板模板, 门楣未建"); return; }
            var s0 = tpl.transform.localScale;
            var c = HstCorridorExtend.CloneMesh(tpl, parent, "HST_DoorHead");
            c.transform.localScale = new Vector3(s0.x * (1.708f / 1.792f), s0.y, s0.z * ((YNew - 2.4f) / YOld));
            c.transform.rotation = Quaternion.Euler(0f, -90f, 0f) * tpl.transform.rotation;
            HstCorridorExtend.AlignBoundsCenter(c, new Vector3(-17.98f, (2.4f + YNew) * 0.5f, 0f));
            GameObjectUtility.SetStaticEditorFlags(c, sFlags);
            HstCorridorExtend.Log("二期 门楣: 墙板网格克隆 " + (YNew - 2.4f).ToString("0.000") + "m 高(替掉内置 Cube)");
        }

        internal static void ResetDoorLights()   // internal: 盖板删除工具(HstHatchRemove)重烤前也要复位门区灯
        {
            // 必须自己切回走廊场景: 上一步 AddWalkPlayer 会在 3 个场景间轮转, 结束时活动场景停在中庭,
            // 直接 FindObjectsOfType 会一颗都找不到(2026-10-11 实测: 复位 0 颗 → 门区被二次 ×0.33)
            var sc = EditorSceneManager.OpenScene(SceneDir + "/" + SceneName + ".unity", OpenSceneMode.Single);
            int n = 0;
            foreach (var li in Object.FindObjectsOfType<Light>(true))
            {
                if (li.name.StartsWith("L_LUMEN_DoorTop")) { li.intensity = DoorTopI; n++; }
                else if (li.name.StartsWith("L_LUMEN_Fill")) { li.intensity = FillI; n++; }
                else if (li.name.StartsWith("L_LUMEN_EndWash")) { li.intensity = EndWashI; n++; }
            }
            EditorSceneManager.MarkSceneDirty(sc);
            EditorSceneManager.SaveScene(sc);   // 存盘后再进烤, 防 OpenScene 重载把内存改动丢掉
            HstCorridorExtend.Log("二期 门区灯复位到基值 " + n + " 颗(防 BakeScenes 二次 ×0.33)");
        }

        // ---------------- 小工具 ----------------
        static GameObject MkEmpty(string name, Vector3 pos)
        {
            var g = new GameObject(name);
            g.transform.position = pos;   // 无父 → localRotation == worldRotation
            return g;
        }

        static GameObject MkBox(Transform parent, string name, Vector3 localPos, Vector3 scale, Material m)
        {
            var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
            g.name = name;
            g.transform.SetParent(parent, false);
            g.transform.localPosition = localPos;
            g.transform.localScale = scale;
            var r = g.GetComponent<MeshRenderer>();
            r.sharedMaterial = m;
            return g;
        }
    }
}
#endif
