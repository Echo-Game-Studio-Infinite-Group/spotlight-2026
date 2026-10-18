using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public sealed class CameraWallFadeTests
{
    private readonly List<Object> _created = new List<Object>();
    private readonly Vector3 _origin = new Vector3(16000f, 100f, 16000f);
    private CameraWallFade _fade;
    private Transform _target;
    private Camera _camera;

    [SetUp]
    public void SetUp()
    {
        _target = Create("Target").transform;
        _camera = Create("Camera").AddComponent<Camera>();
        _camera.transform.position = _origin + Vector3.forward * 10f;
        _fade = Create("Fade").AddComponent<CameraWallFade>();
        _fade.Configure(_target, null, _camera);
    }

    [TearDown]
    public void TearDown()
    {
        if (_fade != null) _fade.enabled = false;
        for (int i = _created.Count - 1; i >= 0; i--)
            if (_created[i] != null) Object.DestroyImmediate(_created[i]);
        _created.Clear();
    }

    private GameObject Create(string name)
    {
        var go = new GameObject(name);
        go.transform.position = _origin;
        _created.Add(go);
        return go;
    }

    private Material Material(string shader = "Universal Render Pipeline/Lit")
    {
        var material = new Material(Shader.Find(shader));
        _created.Add(material);
        return material;
    }

    private MeshRenderer Wall(float z, Material material)
    {
        GameObject go = Create("Wall");
        go.transform.position += Vector3.forward * z;
        go.AddComponent<BoxCollider>();
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        return renderer;
    }

    private void Tick()
    {
        Physics.SyncTransforms();
        typeof(CameraWallFade).GetMethod("UpdateFade", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(_fade, new object[] { 1f });
    }

    private static Color FadeColor(Renderer renderer, int slot = 0)
    {
        var block = new MaterialPropertyBlock();
        renderer.GetPropertyBlock(block, slot);
        return block.GetColor("_BaseColor");
    }

    [Test]
    public void FadesAllMaterialsAndRestoresOriginalsAfterViewClears()
    {
        Material red = Material();
        Material blue = Material();
        red.SetColor("_BaseColor", new Color(1f, 0f, 0f, .8f));
        blue.SetColor("_BaseColor", Color.blue);
        MeshRenderer first = Wall(3f, red);
        first.sharedMaterials = new[] { red, blue };
        MeshRenderer second = Wall(7f, blue);
        MeshRenderer outside = Wall(-3f, red);
        Tick();
        Assert.AreNotSame(red, first.sharedMaterials[0]);
        Assert.AreNotSame(blue, second.sharedMaterial);
        Assert.AreSame(red, outside.sharedMaterial);
        Assert.AreEqual(.16f, FadeColor(first).a, .001f);
        Assert.AreEqual(Color.blue.b, FadeColor(first, 1).b);
        Assert.AreEqual(.2f, FadeColor(first, 1).a, .001f);
        Assert.AreEqual(.8f, red.GetColor("_BaseColor").a);
        _camera.transform.position = _origin + Vector3.right * 10f;
        Tick();
        Assert.AreSame(red, first.sharedMaterials[0]);
        Assert.AreSame(blue, first.sharedMaterials[1]);
        Assert.AreSame(blue, second.sharedMaterial);
    }

    [Test]
    public void FindsChildColliderAndChildRendererAndCameraInsideWall()
    {
        Material material = Material();
        MeshRenderer parentRenderer = Wall(3f, material);
        Object.DestroyImmediate(parentRenderer.GetComponent<BoxCollider>());
        Create("ChildCollider").transform.SetParent(parentRenderer.transform, false);
        parentRenderer.transform.GetChild(0).localPosition = Vector3.zero;
        parentRenderer.transform.GetChild(0).gameObject.AddComponent<BoxCollider>();
        GameObject parentCollider = Create("ParentCollider");
        parentCollider.transform.position += Vector3.forward * 6f;
        parentCollider.AddComponent<BoxCollider>();
        GameObject child = Create("ChildRenderer");
        child.transform.SetParent(parentCollider.transform, false);
        child.transform.localPosition = Vector3.zero;
        MeshRenderer childRenderer = child.AddComponent<MeshRenderer>();
        childRenderer.sharedMaterial = material;
        MeshRenderer containingCamera = Wall(10f, material);
        Tick();
        Assert.AreNotSame(material, parentRenderer.sharedMaterial);
        Assert.AreNotSame(material, childRenderer.sharedMaterial);
        Assert.AreNotSame(material, containingCamera.sharedMaterial);
    }

    [Test]
    public void ExpandsQueryBufferWithoutMissingObstacles()
    {
        Material material = Material();
        var walls = new List<MeshRenderer>();
        for (int i = 0; i < 80; i++) walls.Add(Wall(1f + i * .1f, material));
        Tick();
        foreach (MeshRenderer wall in walls) Assert.AreNotSame(material, wall.sharedMaterial);
    }

    [Test]
    public void IgnoresPlayerTriggersAndExcludedLayers()
    {
        Material material = Material();
        MeshRenderer player = Wall(1f, material);
        player.transform.SetParent(_target, true);
        MeshRenderer trigger = Wall(3f, material);
        trigger.GetComponent<BoxCollider>().isTrigger = true;
        MeshRenderer excluded = Wall(5f, material);
        excluded.gameObject.layer = 2;
        typeof(CameraWallFade).GetField("_wallLayers", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(_fade, (LayerMask)(~(1 << 2)));
        Tick();
        Assert.AreSame(material, player.sharedMaterial);
        Assert.AreSame(material, trigger.sharedMaterial);
        Assert.AreSame(material, excluded.sharedMaterial);
    }

    [Test]
    public void PreservesPropertyBlocksAndRestoresOnDisable()
    {
        Material material = Material();
        MeshRenderer renderer = Wall(4f, material);
        renderer.sharedMaterials = new[] { material, material };
        var global = new MaterialPropertyBlock();
        global.SetColor("_BaseColor", Color.green);
        renderer.SetPropertyBlock(global);
        var indexed = new MaterialPropertyBlock();
        indexed.SetColor("_BaseColor", new Color(1f, 0f, 0f, .5f));
        renderer.SetPropertyBlock(indexed, 1);
        Tick();
        Assert.AreEqual(.2f, FadeColor(renderer).a, .001f);
        Assert.AreEqual(.1f, FadeColor(renderer, 1).a, .001f);
        _fade.enabled = false;
        Assert.AreSame(material, renderer.sharedMaterial);
        var restored = new MaterialPropertyBlock();
        renderer.GetPropertyBlock(restored, 0);
        Assert.IsTrue(restored.isEmpty);
        renderer.GetPropertyBlock(restored, 1);
        Assert.AreEqual(.5f, restored.GetColor("_BaseColor").a);
        renderer.GetPropertyBlock(restored);
        Assert.AreEqual(Color.green, restored.GetColor("_BaseColor"));
    }

    [Test]
    public void UsesTransparentCounterpartForProjectToonWalls()
    {
        Material material = Material("OurFunction/NPR/Deferred Toon");
        MeshRenderer renderer = Wall(4f, material);
        Tick();
        Assert.AreEqual("OurFunction/NPR/Transparent Toon", renderer.sharedMaterial.shader.name);
        Assert.AreEqual("OurFunction/NPR/Deferred Toon", material.shader.name);
        Assert.AreEqual(.2f, FadeColor(renderer).a, .001f);
    }
}
