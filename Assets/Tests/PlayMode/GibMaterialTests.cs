#if UNITY_EDITOR
using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public sealed class GibMaterialTests
{
    private GameObject _enemyRoot;
    private Material _source;
    private GibComponent _gib;
    private GameObject _upper;
    private GameObject _lower;
    private Scene _scene;

    [SetUp]
    public void SetUp()
    {
        _scene = SceneManager.CreateScene("GibMaterialRegression");
        _enemyRoot = GameObject.CreatePrimitive(PrimitiveType.Cube);
        SceneManager.MoveGameObjectToScene(_enemyRoot, _scene);
        _enemyRoot.transform.position = new Vector3(300f, 10f, 300f);
        _source = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        _source.SetColor("_BaseColor", new Color(0.05f, 0.7f, 0.6f));
        _source.SetColor("_EmissionColor", new Color(0.03f, 2.9f, 2.6f));
        _source.SetFloat("_Metallic", 0.35f);
        _source.SetFloat("_Smoothness", 0.65f);
        _source.SetFloat("_Cull", 0f);
        _source.EnableKeyword("_EMISSION");
        _enemyRoot.GetComponent<MeshRenderer>().sharedMaterial = _source;
        _enemyRoot.AddComponent<Enemy>();
        _gib = _enemyRoot.AddComponent<GibComponent>();
        var settings = new SerializedObject(_gib);
        settings.FindProperty("_cutoutShader").objectReferenceValue = Shader.Find("GameJam/EnemyWaistCutout");
        settings.FindProperty("_waist").objectReferenceValue = _enemyRoot.transform;
        settings.FindProperty("_waistOffset").floatValue = 0f;
        settings.FindProperty("_cutColor").colorValue = new Color(0.8f, 0.01f, 0.01f);
        settings.FindProperty("_deathChance").floatValue = 1f;
        settings.FindProperty("_lifetime").floatValue = 0f;
        settings.ApplyModifiedPropertiesWithoutUndo();
    }

    private Material Slice()
    {
        var enemy = _enemyRoot.GetComponent<Enemy>();
        enemy.TakeDamage(enemy.MaxHealth, _enemyRoot.transform.position, Vector3.forward);
        Assert.IsTrue(_gib.IsSliced);
        var roots = _scene.GetRootGameObjects();
        _upper = System.Array.Find(roots, go => go.name == "WaistCut_Upper");
        _lower = System.Array.Find(roots, go => go.name == "WaistCut_Lower");
        Assert.NotNull(_upper);
        Assert.NotNull(_lower);
        return _upper.GetComponentInChildren<MeshRenderer>().sharedMaterial;
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        Object.DestroyImmediate(_enemyRoot);
        Object.DestroyImmediate(_source);
        if (_scene.IsValid()) yield return SceneManager.UnloadSceneAsync(_scene);
    }

    [UnityTest]
    public IEnumerator SurfaceProperties_AndTangentSpace_SurviveDeathAndAnimation()
    {
        var texture = new Texture2D(2, 2);
        try
        {
            string[] maps = { "_BaseMap", "_EmissionMap", "_BumpMap", "_MetallicGlossMap", "_OcclusionMap" };
            foreach (string map in maps) _source.SetTexture(map, texture);
            _source.SetTextureScale("_BaseMap", new Vector2(2f, 3f));
            _source.SetTextureOffset("_BaseMap", new Vector2(0.2f, 0.4f));
            _source.SetFloat("_BumpScale", 0.7f);
            _source.SetFloat("_OcclusionStrength", 0.6f);
            _source.EnableKeyword("_NORMALMAP");
            _source.EnableKeyword("_METALLICSPECGLOSSMAP");
            _source.EnableKeyword("_OCCLUSIONMAP");
            string original = EditorJsonUtility.ToJson(_source);
            _enemyRoot.transform.localScale = new Vector3(-1f, 1.2f, 0.9f);
            _enemyRoot.transform.rotation = Quaternion.Euler(10f, 35f, 5f);
            var sourceRenderer = _enemyRoot.GetComponent<MeshRenderer>();
            sourceRenderer.shadowCastingMode = ShadowCastingMode.TwoSided;
            sourceRenderer.reflectionProbeUsage = ReflectionProbeUsage.Simple;
            sourceRenderer.probeAnchor = _enemyRoot.transform;
            Material body = Slice();
            foreach (string map in maps) Assert.AreSame(texture, body.GetTexture(map), map);
            foreach (string color in new[] { "_BaseColor", "_EmissionColor", "_SpecColor" })
                Assert.AreEqual(_source.GetColor(color), body.GetColor(color), color);
            foreach (string value in new[] { "_Metallic", "_Smoothness", "_BumpScale", "_OcclusionStrength", "_Cull" })
                Assert.AreEqual(_source.GetFloat(value), body.GetFloat(value), value);
            foreach (string keyword in _source.shaderKeywords) Assert.IsTrue(body.IsKeywordEnabled(keyword), keyword);
            Assert.AreEqual(_source.GetTextureScale("_BaseMap"), body.GetTextureScale("_BaseMap"));
            Assert.AreEqual(_source.GetTextureOffset("_BaseMap"), body.GetTextureOffset("_BaseMap"));
            var upperRenderer = _upper.GetComponentInChildren<MeshRenderer>();
            Assert.AreEqual(sourceRenderer.shadowCastingMode, upperRenderer.shadowCastingMode);
            Assert.AreEqual(sourceRenderer.reflectionProbeUsage, upperRenderer.reflectionProbeUsage);
            Assert.AreSame(sourceRenderer.probeAnchor, upperRenderer.probeAnchor);
            Mesh originalMesh = _enemyRoot.GetComponent<MeshFilter>().sharedMesh;
            Vector4[] tangents = originalMesh.tangents;
            Assert.Greater(tangents.Length, 0);
            Mesh upperMesh = _upper.GetComponentInChildren<MeshFilter>().sharedMesh;
            Mesh lowerMesh = _lower.GetComponentInChildren<MeshFilter>().sharedMesh;
            Matrix4x4 initial = _enemyRoot.transform.localToWorldMatrix;
            AssertTangents(tangents, upperMesh.tangents, initial);
            _enemyRoot.transform.rotation *= Quaternion.Euler(25f, 15f, 0f);
            yield return null;
            AssertTangents(tangents, upperMesh.tangents, initial);
            AssertTangents(tangents, lowerMesh.tangents,
                _lower.transform.worldToLocalMatrix * _enemyRoot.transform.localToWorldMatrix);
            Assert.AreEqual(original, EditorJsonUtility.ToJson(_source), "不能改写原材质");
            CollectionAssert.AreEqual(tangents, originalMesh.tangents, "不能改写模型切线");
        }
        finally { Object.DestroyImmediate(texture); }
    }

    private static void AssertTangents(Vector4[] source, Vector4[] actual, Matrix4x4 matrix)
    {
        Assert.GreaterOrEqual(actual.Length, source.Length, "合并网格还包含断口顶点");
        for (int i = 0; i < source.Length; i++)
        {
            Vector3 expected = matrix.MultiplyVector(source[i]).normalized;
            Assert.Less((new Vector3(actual[i].x, actual[i].y, actual[i].z) - expected).sqrMagnitude, 0.000001f);
            Assert.AreEqual(source[i].w * (matrix.determinant < 0f ? -1f : 1f), actual[i].w);
        }
    }

    [UnityTest]
    public IEnumerator Merge_PreservesMaterialSlots_AndExcludesUpperOnlyPartsFromAnimation()
    {
        var glow = new Material(_source);
        try
        {
            glow.SetColor("_EmissionColor", Color.blue * 3f);
            var arm = GameObject.CreatePrimitive(PrimitiveType.Cube);
            arm.transform.SetParent(_enemyRoot.transform, false);
            arm.transform.localPosition = Vector3.up * 2f;
            arm.GetComponent<MeshRenderer>().sharedMaterial = _source;
            var leg = GameObject.CreatePrimitive(PrimitiveType.Cube);
            leg.transform.SetParent(_enemyRoot.transform, false);
            leg.transform.localPosition = Vector3.down * 2f;
            leg.GetComponent<MeshRenderer>().sharedMaterial = glow;
            Slice();
            Assert.AreEqual(1, _upper.GetComponentsInChildren<MeshRenderer>().Length);
            Assert.AreEqual(1, _lower.GetComponentsInChildren<MeshRenderer>().Length);
            var upper = _upper.GetComponentInChildren<MeshRenderer>();
            var lower = _lower.GetComponentInChildren<MeshRenderer>();
            Assert.AreEqual(2, upper.sharedMaterials.Length, "共用源材质的两个零件与封口只需两个材质槽");
            Assert.AreEqual(3, lower.sharedMaterials.Length, "保留两种身体材质与封口材质");
            Assert.AreSame(upper.sharedMaterials[0], lower.sharedMaterials[0], "两半共用同一份材质副本");
            Assert.AreEqual(glow.GetColor("_EmissionColor"), lower.sharedMaterials[1].GetColor("_EmissionColor"));
            Mesh lowerMesh = lower.GetComponent<MeshFilter>().sharedMesh;
            Vector3[] before = lowerMesh.vertices;
            Assert.Less(lowerMesh.bounds.max.y, 0.6f, "完全在腰上方的零件不能进入下半身网格");
            Vector2[] distances = lowerMesh.uv2;
            foreach (int index in lowerMesh.GetTriangles(1))
                Assert.Less(distances[index].x, 0f, "腿部三角形必须对应原来的自发光材质槽");
            foreach (int index in lowerMesh.GetTriangles(2)) Assert.AreEqual(0f, distances[index].x, "动画封口不再被裁掉");
            arm.transform.localPosition += Vector3.right;
            yield return null;
            CollectionAssert.AreEqual(before, lowerMesh.vertices, "上半身独占零件不能触发下半身更新");
            leg.transform.localPosition += Vector3.right;
            yield return null;
            Assert.IsFalse(System.Linq.Enumerable.SequenceEqual(before, lowerMesh.vertices), "下半身零件仍随动画 Transform 移动");
        }
        finally { Object.DestroyImmediate(glow); }
    }

    [Test]
    public void Render_BodyFrontAndBack_MatchLit_WithNormalMapAndAdditionalLight()
    {
        RequireGraphics();
        var normal = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
        try
        {
            normal.SetPixel(0, 0, new Color(0.6f, 0.45f, 1f, 0.6f));
            normal.Apply();
            _source.DisableKeyword("_EMISSION");
            _source.SetTexture("_BumpMap", normal);
            _source.EnableKeyword("_NORMALMAP");
            Material body = Slice();
            foreach (bool back in new[] { false, true })
            {
                string side = back ? "back" : "front";
                Color before = Render(_source, back, true, "lit_" + side);
                Color after = Render(body, back, true, "gib_" + side);
                Assert.Greater(before.maxColorComponent, 0.05f, "参考材质必须实际可见");
                AssertColor(before, after, "主体的 " + side + " 面必须保留原材质和光照");
                Assert.Less(after.r, after.g, "青色主体不能变成红色断口");
            }
        }
        finally { Object.DestroyImmediate(normal); }
    }

    [Test]
    public void Render_EmissionRemainsVisibleWithoutLight_AndOnlyCapAndEdgeUseCutColor()
    {
        RequireGraphics();
        Material body = Slice();
        Color before = Render(_source, false, false, "lit_emission");
        Color after = Render(body, true, false, "gib_emission_back");
        AssertColor(before, after, "无光照时也要保留自发光");
        Assert.Greater(after.g, 0.8f);
        Assert.Greater(after.b, 0.8f);
        Assert.Less(after.r, 0.1f);
        Material cap = System.Array.Find(_upper.GetComponentInChildren<MeshRenderer>().sharedMaterials,
            material => material.GetFloat("_CutSurface") > 0.5f);
        Assert.NotNull(cap, "必须验证实际生成的封口材质");
        Assert.AreEqual(1f, cap.GetFloat("_CutSurface"));
        cap.SetFloat("_CutEdge", 0f);
        Color cut = Render(cap, true, true, "gib_cap");
        Assert.Greater(cut.r, cut.g * 5f);
        Assert.Greater(cut.r, 0.05f, "Edge Width=0 时封口也必须显示断口色");
        Color unlitCap = Render(cap, false, false, "gib_cap_dark");
        Assert.Less(unlitCap.maxColorComponent, 0.02f, "断口不能继承身体自发光");
        body.SetFloat("_CutEdge", 3f);
        Color edge = Render(body, false, true, "gib_edge");
        Assert.Greater(edge.r, edge.g * 5f, "窄边也必须使用断口色");
    }

    private static void RequireGraphics()
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            Assert.Ignore("像素验证需要启用图形设备");
    }

    private static void AssertColor(Color expected, Color actual, string message)
    {
        Assert.That(actual.r, Is.EqualTo(expected.r).Within(0.035f), message);
        Assert.That(actual.g, Is.EqualTo(expected.g).Within(0.035f), message);
        Assert.That(actual.b, Is.EqualTo(expected.b).Within(0.035f), message);
    }

    private static Color Render(Material material, bool back, bool lit, string imageName)
    {
        var preview = new PreviewRenderUtility();
        var mesh = new Mesh();
        var pixels = new Texture2D(128, 128, TextureFormat.RGBA32, false, true);
        RenderTexture previous = RenderTexture.active;
        try
        {
            mesh.vertices = new[] { new Vector3(-1f,-1f,0f), new Vector3(-1f,1f,0f),
                new Vector3(1f,1f,0f), new Vector3(1f,-1f,0f) };
            mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
            mesh.uv = new[] { Vector2.zero, Vector2.up, Vector2.one, Vector2.right };
            mesh.triangles = back ? new[] { 0,2,1,0,3,2 } : new[] { 0,1,2,0,2,3 };
            mesh.RecalculateTangents();
            var quad = new GameObject("MaterialSample", typeof(MeshFilter), typeof(MeshRenderer));
            quad.GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = quad.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            var block = new MaterialPropertyBlock();
            block.SetVector("_CutPlane", new Vector4(0f,1f,0f,2f));
            block.SetFloat("_CutSide", 0f);
            renderer.SetPropertyBlock(block);
            preview.AddSingleGO(quad);
            preview.camera.orthographic = true;
            preview.camera.orthographicSize = 0.5f;
            preview.camera.transform.SetPositionAndRotation(new Vector3(0f,0f,-3f), Quaternion.identity);
            preview.camera.nearClipPlane = 0.01f;
            preview.camera.farClipPlane = 10f;
            preview.camera.clearFlags = CameraClearFlags.SolidColor;
            preview.camera.backgroundColor = Color.black;
            preview.camera.allowHDR = true;
            preview.ambientColor = lit ? new Color(0.2f, 0.2f, 0.2f) : Color.black;
            preview.lights[0].intensity = lit ? 1.2f : 0f;
            preview.lights[0].transform.rotation = Quaternion.Euler(20f, 25f, 0f);
            preview.lights[1].intensity = 0f;
            var point = new GameObject("AdditionalLight", typeof(Light));
            point.transform.position = new Vector3(0.5f, 0.25f, -1f);
            point.GetComponent<Light>().type = LightType.Point;
            point.GetComponent<Light>().range = 5f;
            point.GetComponent<Light>().intensity = lit ? 2f : 0f;
            preview.AddSingleGO(point);
            preview.BeginPreview(new Rect(0,0,128,128), GUIStyle.none);
            preview.Render(true);
            RenderTexture.active = (RenderTexture)preview.EndPreview();
            pixels.ReadPixels(new Rect(0,0,128,128), 0, 0);
            pixels.Apply();
            Directory.CreateDirectory("Logs/GibMaterialValidation");
            File.WriteAllBytes("Logs/GibMaterialValidation/" + imageName + ".png", pixels.EncodeToPNG());
            return pixels.GetPixel(64,64);
        }
        finally
        {
            RenderTexture.active = previous;
            preview.Cleanup();
            Object.DestroyImmediate(mesh);
            Object.DestroyImmediate(pixels);
        }
    }
}
#endif
