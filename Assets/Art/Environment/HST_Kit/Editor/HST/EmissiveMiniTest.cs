#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace HST
{
    // 最小实验(2026-10-08): 1x1m 发光面(辐射=1) 在 4x4m 接收面上方 1m —— Unity 的 Progressive CPU
    // 光照贴图器到底会不会把**自发光网格**当光源? 走廊实测"开/关发光 GI 两版烘焙三位小数完全一致"
    // (贡献=0), 需要判定是全局机制问题还是走廊某处设置问题。
    // 判据: 接收面中心渲染值应 ≈ E/π(物理口径, 约 0.1~0.2) —— 若为 0 则自发光 GI 根本没工作。
    public static class EmissiveMiniTest
    {
        [MenuItem("HST/微型自发光GI测试")]
        public static void Run()
        {
            // 三组对照: A 面→面(现状) / B 盒→面(发光体加厚) / C 面→盒(接收体加厚)
            Test("A_面->面", false, false);
            Test("B_盒->面", true, false);
            Test("C_面->盒", false, true);
            // D 点灯对照: 同几何同距离放 1cd 点灯(发光体退出) —— 若 D>0 而 A~C=0,
            // 说明读回/场景链路没坏, 是"自发光进 GI"这一环的问题; 若 D 也=0, 是读回链路问题。
            TestPoint();
        }

        static void TestPoint()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var recv = GameObject.CreatePrimitive(PrimitiveType.Quad);
            recv.name = "RECV";
            recv.transform.position = Vector3.zero;
            recv.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);
            recv.transform.localScale = new Vector3(4f, 4f, 1f);
            var mRecv = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "M_MINI_Recv" };
            mRecv.SetColor("_BaseColor", Color.white);
            recv.GetComponent<MeshRenderer>().sharedMaterial = mRecv;
            Object.DestroyImmediate(recv.GetComponent<Collider>());
            var mf = recv.GetComponent<MeshFilter>();
            var m2 = Object.Instantiate(mf.sharedMesh);
            Unwrapping.GenerateSecondaryUVSet(m2);
            mf.sharedMesh = m2;
            GameObjectUtility.SetStaticEditorFlags(recv, StaticEditorFlags.ContributeGI);
            recv.GetComponent<MeshRenderer>().scaleInLightmap = 8f;
            var lgo = new GameObject("PT"); var li = lgo.AddComponent<Light>();
            li.type = LightType.Point; li.intensity = 1f; li.range = 10f;
            li.lightmapBakeType = LightmapBakeType.Baked; li.shadows = LightShadows.Soft;
            lgo.transform.position = new Vector3(0f, 1f, 0f);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = Color.black; RenderSettings.skybox = null;
            EditorSceneManager.SaveScene(scene, "Assets/Scenes/HST_MiniTest.unity");   // 场景必须存盘
            var ls = new LightingSettings();
            ls.lightmapper = LightingSettings.Lightmapper.ProgressiveCPU;
            ls.bakedGI = true; ls.realtimeGI = false;
            ls.lightmapResolution = 20f; ls.maxBounces = 2;
            ls.directSampleCount = 64; ls.indirectSampleCount = 512; ls.lightmapPadding = 2;
            Lightmapping.lightingSettings = ls;
            Lightmapping.Clear();
            Debug.Log($"[MINI] D_点灯 Bake ok={Lightmapping.Bake()} lmIdx={recv.GetComponent<MeshRenderer>().lightmapIndex}");
            ReadBack("D_点灯");
        }

        static void ReadBack(string tag)
        {
            var camGo = new GameObject("MINI_CAM");
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true; cam.orthographicSize = 0.5f;
            cam.nearClipPlane = 0.01f; cam.farClipPlane = 10f;
            cam.transform.position = new Vector3(0f, 0.05f, 0f);
            cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black;
            var cd = cam.GetUniversalAdditionalCameraData();
            cd.renderPostProcessing = false;
            var rt = new RenderTexture(64, 64, 24, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Linear);
            cam.targetTexture = rt; cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(64, 64, TextureFormat.RGBAFloat, false, true);
            tex.ReadPixels(new Rect(0, 0, 64, 64), 0, 0); tex.Apply();
            var c = tex.GetPixel(32, 32);
            var c2 = tex.GetPixel(5, 5);
            Debug.Log($"[MINI-RESULT] {tag}: 中心=({c.r:F4},{c.g:F4},{c.b:F4}) 边缘(5,5)=({c2.r:F4},{c2.g:F4},{c2.b:F4})  (0=自发光没进 GI)");
            cam.targetTexture = null;
            Object.DestroyImmediate(rt); Object.DestroyImmediate(tex);
        }

        static void Test(string tag, bool emitBox, bool recvBox)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var recv = GameObject.CreatePrimitive(recvBox ? PrimitiveType.Cube : PrimitiveType.Quad);
            recv.name = "RECV";
            recv.transform.position = Vector3.zero;
            recv.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);   // 面朝上
            recv.transform.localScale = recvBox ? new Vector3(4f, 0.2f, 4f) : new Vector3(4f, 4f, 1f);

            var emit = GameObject.CreatePrimitive(emitBox ? PrimitiveType.Cube : PrimitiveType.Quad);
            emit.name = "EMIT";
            emit.transform.position = new Vector3(0f, 1f, 0f);
            emit.transform.rotation = Quaternion.Euler(90f, 0f, 0f);    // 面朝下
            emit.transform.localScale = emitBox ? new Vector3(1f, 0.02f, 1f) : new Vector3(1f, 1f, 1f);

            var shEmit = Shader.Find("Universal Render Pipeline/Lit");   // 必须 URP/Lit: 光照贴图器靠渲染材质提取 emissive
            Debug.Log("[MINI] " + tag + " 发光材质 shader = " + (shEmit != null ? shEmit.name : "null!"));
            if (shEmit == null) return;
            var mEmit = new Material(shEmit) { name = "M_MINI_Emit_" + tag };
            mEmit.SetColor("_BaseColor", Color.black);
            mEmit.SetColor("_EmissionColor", new Color(1f, 1f, 1f, 1f));   // 辐射 1.0
            mEmit.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive;
            mEmit.doubleSidedGI = true;
            mEmit.SetInt("_Cull", 0);
            string mp = "Assets/HST_MINI_Emit.mat";
            if (File.Exists(mp)) AssetDatabase.DeleteAsset(mp);
            AssetDatabase.CreateAsset(mEmit, mp);
            SceneBuilder.ForceEmissionKeywordPublic(mEmit);      // 内存级
            AssetDatabase.SaveAssets();
            SceneBuilder.PatchEmissionKeywordOnDisk(mp);         // 磁盘级(真正的开关)

            var mRecv = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "M_MINI_Recv" };
            mRecv.SetColor("_BaseColor", Color.white);

            emit.GetComponent<MeshRenderer>().sharedMaterial = mEmit;
            recv.GetComponent<MeshRenderer>().sharedMaterial = mRecv;
            Object.DestroyImmediate(emit.GetComponent<Collider>());
            Object.DestroyImmediate(recv.GetComponent<Collider>());

            // 2026-10-08 补: 原生 Quad/Cube 没有 UV2, 会被 Progressive 烘焙器整个排除(当年读 0 的根因)。
            // 逐网格拷贝 + GenerateSecondaryUVSet, 与走廊管线同法。
            foreach (var go in new[] { recv, emit })
            {
                var mf = go.GetComponent<MeshFilter>();
                var m2 = Object.Instantiate(mf.sharedMesh);
                m2.name = mf.sharedMesh.name + "_uv2";
                Unwrapping.GenerateSecondaryUVSet(m2);
                mf.sharedMesh = m2;
            }

            foreach (var go in new[] { recv, emit })
            {
                GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.ContributeGI);
                go.GetComponent<MeshRenderer>().scaleInLightmap = 8f;
            }

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = Color.black;
            RenderSettings.skybox = null;
            // 场景必须存盘: 未保存场景的光照贴图不落盘也不挂接(2026-10-08, mini 恒 0 的根因之一)
            EditorSceneManager.SaveScene(scene, "Assets/Scenes/HST_MiniTest.unity");

            var ls = new LightingSettings();
            ls.lightmapper = LightingSettings.Lightmapper.ProgressiveCPU;
            ls.bakedGI = true; ls.realtimeGI = false;
            ls.lightmapResolution = 20f;
            ls.maxBounces = 2;
            ls.directSampleCount = 64; ls.indirectSampleCount = 512;
            ls.lightmapPadding = 2;
            Lightmapping.lightingSettings = ls;
            Lightmapping.Clear();
            bool ok = Lightmapping.Bake();
            Debug.Log($"[MINI] {tag} Bake ok={ok} lmIdx={recv.GetComponent<MeshRenderer>().lightmapIndex}");
            ReadBack(tag);
        }
    }
}
#endif
