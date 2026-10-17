using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// 仅在显式菜单操作或本地请求文件存在时重建，避免导入资源覆盖人工调整。
[InitializeOnLoad]
public static class NasaPunkMenuBuilder
{
    private const string CRTRequest = "Temp/BuildCRTMenu.request";
    private const string CRTArt = "Assets/Art/UI/CRT/";
    private const string Request = "Temp/BuildNasaMenus.request";
    private const string Art = "Assets/Art/UI/NasaPunk/";
    private static readonly Color Paint = Hex("F4F3EF");
    private static readonly Color Ink = Hex("20232A");
    private static readonly Color Dark = Hex("0A1420");
    private static readonly Color Cream = Hex("D8D2C0");
    private static readonly Color Cyan = Hex("4AA8C0");

    static NasaPunkMenuBuilder() { EditorApplication.update += Poll; }

    private static void Poll()
    {
        if ((!File.Exists(Request) && !File.Exists(CRTRequest)) || EditorApplication.isCompiling || EditorApplication.isUpdating ||
            EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (File.Exists(CRTRequest)) { File.Delete(CRTRequest); BuildCRT(); }
        else { File.Delete(Request); Build(); }
    }

    [MenuItem("超高速行者/UI/装配 NASA-Punk 菜单")]
    public static void Build()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogWarning("请退出播放后装配菜单。");
            return;
        }
        try
        {
            // 覆盖目标资产前保留编辑器里尚未落盘的场景，便于找回人工修改。
            Scene openMenu = SceneManager.GetSceneByPath("Assets/Scenes/MainMenu.unity");
            bool reopenMenu = openMenu.IsValid() && openMenu.isLoaded;
            if (reopenMenu)
            {
                Directory.CreateDirectory("Temp/MenuBackup");
                if (!EditorSceneManager.SaveScene(openMenu, "Temp/MenuBackup/MainMenu-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".unity", true))
                    throw new IOException("主菜单备份失败，已停止装配。");
            }
            foreach (string path in Directory.GetFiles(Art, "*.png"))
            {
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = false;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.maxTextureSize = 2048;
                importer.SaveAndReimport();
            }
            BuildPause();
            BuildMain();
            AssetDatabase.SaveAssets();
            Debug.Log("[NasaPunkMenuBuilder] SUCCESS: MainMenu / PauseMenu 已装配；已配置四态和显式导航。预览：Temp/MenuPreview");
        }
        catch (Exception e) { Debug.LogException(e); }
    }

    [MenuItem("超高速行者/UI/装配 CRT 主菜单")]
    public static void BuildCRT()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        try
        {
            AssetDatabase.ImportAsset(CRTArt + "PerspectiveTide.shader", ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(CRTArt + "CRTText.shader", ImportAssetOptions.ForceUpdate);
            BuildMain();
            AssetDatabase.SaveAssets();
            Debug.Log("[CRTMenu] SUCCESS / Temp/MenuPreview");
        }
        catch (Exception e) { Debug.LogException(e); }
    }

    private static Material CRTMaterial(string name, string shader, float scans = 90)
    {
        string path = CRTArt + name + ".mat";
        Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null) { material = new Material(Shader.Find(shader)); AssetDatabase.CreateAsset(material, path); }
        if (material.HasProperty("_ScanCount")) material.SetFloat("_ScanCount", scans);
        EditorUtility.SetDirty(material);
        return material;
    }

    private static RectTransform Center(Transform parent, string name, Vector2 size, Vector2 position)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rect = (RectTransform)go.transform;
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
        rect.sizeDelta = size; rect.anchoredPosition = position;
        return rect;
    }

    private static RawImage CRTImage(Transform parent, string name, Vector2 size, Vector2 position, Material material)
    {
        RawImage image = Center(parent, name, size, position).gameObject.AddComponent<RawImage>();
        image.texture = AssetDatabase.LoadAssetAtPath<Texture2D>(CRTArt + name + ".png");
        image.material = material; image.raycastTarget = false;
        return image;
    }

    private static Button CRTButton(Transform parent, string name, float y, Material text, Material shutter, float phase)
    {
        RectTransform rect = Center(parent, name + " Button", new Vector2(460, 80), new Vector2(0, y));
        Image hit = rect.gameObject.AddComponent<Image>(); hit.color = Color.clear;
        var button = rect.gameObject.AddComponent<Button>(); button.targetGraphic = hit;
        button.transition = Selectable.Transition.None;
        RawImage fill = CRTImage(rect, "Shutter", new Vector2(460, 80), Vector2.zero, shutter);
        fill.texture = Texture2D.whiteTexture; fill.color = Hex("64161E");
        fill.rectTransform.localScale = new Vector3(0, 1, 1);
        CRTImage(rect, name, new Vector2(360, 60), Vector2.zero, text);
        var motion = rect.gameObject.AddComponent<CRTMenuButton>(); Wire(motion, "_shutter", fill.rectTransform);
        var so = new SerializedObject(motion); so.FindProperty("_phase").floatValue = phase; so.ApplyModifiedPropertiesWithoutUndo();
        return button;
    }

    private static void BuildMain()
    {
        foreach (string path in Directory.GetFiles(CRTArt, "*.png"))
        {
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Default; importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false; importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.maxTextureSize = 2048; importer.SaveAndReimport();
        }
        Material titleMaterial = CRTMaterial("TitleCRT", "UI/CRT/Text");
        Material textMaterial = CRTMaterial("ButtonCRT", "UI/CRT/Text", 30);
        Material shutterMaterial = CRTMaterial("ShutterCRT", "UI/CRT/Text", 32);
        Material tideMaterial = CRTMaterial("PerspectiveTide", "UI/CRT/Perspective Tide");
        Scene previous = SceneManager.GetActiveScene();
        Scene scene = SceneManager.GetSceneByPath("Assets/Scenes/MainMenu.unity");
        bool existing = scene.IsValid() && scene.isLoaded;
        if (existing)
        {
            Directory.CreateDirectory("Temp/MenuBackup");
            if (!EditorSceneManager.SaveScene(scene, "Temp/MenuBackup/MainMenu-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".unity", true))
                throw new IOException("主菜单备份失败。");
        }
        if (!existing) scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        SceneManager.SetActiveScene(scene);
        try
        {
            foreach (GameObject oldRoot in scene.GetRootGameObjects()) UnityEngine.Object.DestroyImmediate(oldRoot);
            Camera camera = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)).GetComponent<Camera>();
            camera.tag = "MainCamera"; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
            camera.transform.position = new Vector3(0, 0, -10);
            GameObject root = CanvasRoot("MainMenu", 0);
            RawImage background = Stretch(root.transform, "Perspective Tide").gameObject.AddComponent<RawImage>();
            background.material = tideMaterial; background.raycastTarget = false;
            RectTransform title = Center(root.transform, "Title assembly", new Vector2(900, 260), new Vector2(0, 325));
            CanvasGroup opacity = title.gameObject.AddComponent<CanvasGroup>();
            RawImage heading = CRTImage(title, "Title", new Vector2(900, 190), new Vector2(0, 30), titleMaterial);
            RawImage subtitle = CRTImage(title, "Subtitle", new Vector2(620, 72), new Vector2(0, -86), titleMaterial);
            Button start = CRTButton(root.transform, "Start", -35, textMaterial, shutterMaterial, 0);
            Button resume = CRTButton(root.transform, "Continue", -140, textMaterial, shutterMaterial, .4f);
            Button settings = CRTButton(root.transform, "Settings", -140, textMaterial, shutterMaterial, .8f);
            Button exit = CRTButton(root.transform, "Exit", -245, textMaterial, shutterMaterial, 1.6f);
            resume.gameObject.SetActive(false); Navigate(start, settings, exit);
            Transform panel = Stretch(root.transform, "Settings panel");
            Image dim = panel.gameObject.AddComponent<Image>(); dim.color = new Color(0, 0, 0, .95f);
            Button fullscreen = CRTButton(panel, "Fullscreen", 60, textMaterial, shutterMaterial, 0);
            Button close = CRTButton(panel, "Close", -60, textMaterial, shutterMaterial, .8f);
            Navigate(fullscreen, close); panel.gameObject.SetActive(false);
            var menu = root.AddComponent<MainMenuUI>();
            Wire(menu, "_startButton", start, "_continueButton", resume, "_settingsButton", settings,
                "_exitButton", exit, "_settingsPanel", panel.gameObject, "_closeSettingsButton", close, "_fullscreenButton", fullscreen);
            var presentation = root.AddComponent<CRTMenuPresentation>();
            Wire(presentation, "_title", title, "_titleOpacity", opacity, "_background", background);
            var serialized = new SerializedObject(presentation);
            var images = serialized.FindProperty("_titleImages"); images.arraySize = 2;
            images.GetArrayElementAtIndex(0).objectReferenceValue = heading;
            images.GetArrayElementAtIndex(1).objectReferenceValue = subtitle;
            serialized.ApplyModifiedPropertiesWithoutUndo(); EditorUtility.SetDirty(presentation);
            var events = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            events.GetComponent<InputSystemUIInputModule>().AssignDefaultActions();
            if (!EditorSceneManager.SaveScene(scene, "Assets/Scenes/MainMenu.unity")) throw new IOException("主菜单保存失败。");
            Snapshot(root, "MainMenu"); Snapshot(root, "MainMenu-Hover"); Snapshot(root, "MainMenu-Entry");
        }
        finally
        {
            if (!existing) EditorSceneManager.CloseScene(scene, true);
            if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
        }
    }

    private static void BuildPause()
    {
        const string path = "Assets/Prefabs/UI/PauseMenu.prefab";
        GameObject root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            for (int i = root.transform.childCount - 1; i >= 0; i--)
                UnityEngine.Object.DestroyImmediate(root.transform.GetChild(i).gameObject);
            ConfigureCanvas(root, 50);
            Transform panel = Stretch(root.transform, "PauseMenuPanel");
            var dim = panel.gameObject.AddComponent<Image>();
            dim.color = new Color(0.015f, 0.025f, 0.04f, 0.82f);
            Transform page = DesignPage(panel);
            Transform board = Box(page, "Pause hardware", 280, 110, 1360, 860, Paint).transform;
            Stripes(board, 40, 38);
            Label(board, "OPERATIONS / HOLD", 40, 75, 780, 28, 20, Hex("0B3D91"));
            Label(board, "MISSION\nPAUSED", 40, 129, 750, 170, 77, Ink, true);
            Label(board, "AWAITING OPERATOR COMMAND", 44, 324, 760, 30, 20, Ink);
            Rule(board, 40, 382, 1280, Ink);
            Button resume = MenuButton(board, "Resume", 30, 416, 650, 216);
            Button retry = MenuButton(board, "Retry", 680, 416, 650, 216);
            Button back = MenuButton(board, "Return", 30, 635, 650, 216);
            Navigate(resume, retry, back);
            Transform instrument = Box(board, "Hold instrument", 780, 110, 530, 240, Dark).transform;
            Label(instrument, "CONTROL STATE", 24, 18, 480, 28, 17, Cyan);
            Label(instrument, "HOLD", 24, 60, 480, 85, 68, Cream, true);
            Label(instrument, "GAME TIME / SUSPENDED", 24, 175, 480, 30, 19, Cream);
            Label(board, "[ ESC ]  RESUME\n\nSW-026 / CHECKLIST 02\nOPERATOR CONTROL PANEL", 775, 666, 550, 135, 18, Ink);
            Wire(root.GetComponent<PauseUIManager>(), "_pauseMenuPanel", panel.gameObject,
                "_resumeButton", resume, "_retryButton", retry, "_returnButton", back);
            PrefabUtility.SaveAsPrefabAsset(root, path);
            Snapshot(root, "PauseMenu");
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }

    private static GameObject CanvasRoot(string name, int order)
    {
        var root = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        ConfigureCanvas(root, order);
        return root;
    }

    private static void ConfigureCanvas(GameObject root, int order)
    {
        Canvas canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = order;
        CanvasScaler scaler = root.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
    }

    // 固定比例的设计区居中；超宽屏扩展背景，不拉伸按钮或裁掉操作区域。
    private static Transform DesignPage(Transform parent)
    {
        Transform background = Stretch(parent, "Backdrop");
        if (parent.GetComponent<Canvas>() != null)
            background.gameObject.AddComponent<Image>().color = Paint;
        var page = new GameObject("Design / 1920x1080", typeof(RectTransform));
        page.transform.SetParent(background, false);
        RectTransform rect = (RectTransform)page.transform;
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
        rect.sizeDelta = new Vector2(1920, 1080);
        if (parent.GetComponent<Canvas>() != null) Stripes(rect, 88, 35);
        return rect;
    }

    private static Transform Stretch(Transform parent, string name)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rect = (RectTransform)go.transform;
        rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        return rect;
    }

    private static GameObject Rect(Transform parent, string name, float x, float y, float w, float h)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rect = (RectTransform)go.transform;
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
        rect.anchoredPosition = new Vector2(x, -y);
        rect.sizeDelta = new Vector2(w, h);
        return go;
    }

    private static GameObject Box(Transform p, string name, float x, float y, float w, float h, Color color)
    {
        GameObject go = Rect(p, name, x, y, w, h);
        go.AddComponent<Image>().color = color;
        go.GetComponent<Image>().raycastTarget = false;
        return go;
    }

    private static void Label(Transform p, string text, float x, float y, float w, float h, int size, Color color, bool bold = false)
    {
        Text label = Rect(p, text.Replace('\n', ' '), x, y, w, h).AddComponent<Text>();
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.text = text; label.fontSize = size; label.color = color;
        label.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
        label.raycastTarget = false;
        label.horizontalOverflow = HorizontalWrapMode.Overflow;
        label.verticalOverflow = VerticalWrapMode.Overflow;
    }

    private static void Picture(Transform p, string name, string sprite, float x, float y, float w, float h)
    {
        Image image = Box(p, name, x, y, w, h, Color.white).GetComponent<Image>();
        image.sprite = Sprite(sprite); image.preserveAspect = true;
    }

    private static Sprite Sprite(string name)
    {
        Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(Art + name + ".png");
        if (sprite == null) throw new InvalidOperationException("缺少按钮素材：" + name);
        return sprite;
    }

    private static Button MenuButton(Transform p, string name, float x, float y, float w, float h)
    {
        GameObject go = Box(p, name + " Button", x, y, w, h, Color.white);
        Image image = go.GetComponent<Image>();
        image.raycastTarget = true; image.preserveAspect = true; image.sprite = Sprite(name + "_Normal");
        Button button = go.AddComponent<Button>();
        button.targetGraphic = image; button.transition = Selectable.Transition.SpriteSwap;
        button.spriteState = new SpriteState {
            highlightedSprite = Sprite(name + "_Highlighted"), selectedSprite = Sprite(name + "_Highlighted"),
            pressedSprite = Sprite(name + "_Pressed"), disabledSprite = Sprite(name + "_Disabled") };
        return button;
    }

    private static void Navigate(params Button[] buttons)
    {
        for (int i = 0; i < buttons.Length; i++)
            buttons[i].navigation = new Navigation { mode = Navigation.Mode.Explicit,
                selectOnUp = buttons[(i + buttons.Length - 1) % buttons.Length],
                selectOnLeft = buttons[(i + buttons.Length - 1) % buttons.Length],
                selectOnDown = buttons[(i + 1) % buttons.Length], selectOnRight = buttons[(i + 1) % buttons.Length] };
    }

    private static void Wire(UnityEngine.Object target, params object[] pairs)
    {
        var serialized = new SerializedObject(target);
        for (int i = 0; i < pairs.Length; i += 2)
            serialized.FindProperty((string)pairs[i]).objectReferenceValue = (UnityEngine.Object)pairs[i + 1];
        serialized.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(target);
    }

    private static void Stripes(Transform p, float x, float y)
    {
        string[] colors = { "0B3D91", "E4002B", "E29726", "A8ACAF" };
        for (int i = 0; i < colors.Length; i++) Box(p, "Division stripe", x + i * 52, y, 48, 8, Hex(colors[i]));
    }

    private static void Rule(Transform p, float x, float y, float w, Color color) => Box(p, "Panel seam", x, y, w, 2, color);

    private static void Stamp(Transform p, float x, float y, string text, Color color)
    {
        Transform stamp = Rect(p, "Inspection stamp", x, y, 175, 106).transform;
        stamp.localRotation = Quaternion.Euler(0, 0, 8);
        Rule(stamp, 0, 0, 175, color); Rule(stamp, 0, 104, 175, color);
        Box(stamp, "Left rule", 0, 0, 2, 106, color); Box(stamp, "Right rule", 173, 0, 2, 106, color);
        Label(stamp, text, 12, 12, 160, 90, 28, color, true);
    }

    private static void Footer(Transform p, string left, string right)
    {
        Rule(p, 88, 985, 1744, Ink);
        Label(p, left, 88, 1010, 850, 30, 17, Ink);
        Label(p, right, 1030, 1010, 850, 30, 17, Ink);
    }

    private static Color Hex(string hex) { ColorUtility.TryParseHtmlString("#" + hex, out Color color); return color; }

    private static void Snapshot(GameObject source, string name)
    {
        Scene scene = EditorSceneManager.NewPreviewScene();
        RenderTexture rt = RenderTexture.GetTemporary(1920, 1080, 24);
        RenderTexture previous = RenderTexture.active;
        try
        {
            GameObject copy = UnityEngine.Object.Instantiate(source);
            SceneManager.MoveGameObjectToScene(copy, scene);
            foreach (MonoBehaviour behaviour in copy.GetComponentsInChildren<MonoBehaviour>(true))
                if (behaviour is MainMenuUI || behaviour is PauseUIManager) UnityEngine.Object.DestroyImmediate(behaviour);
            if (name == "PauseMenu") copy.transform.GetChild(0).gameObject.SetActive(true);
            var cameraObject = new GameObject("Preview camera", typeof(Camera));
            SceneManager.MoveGameObjectToScene(cameraObject, scene);
            Camera camera = cameraObject.GetComponent<Camera>();
            camera.scene = scene;
            camera.overrideSceneCullingMask = EditorSceneManager.GetSceneCullingMask(scene);
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Dark;
            camera.orthographic = true; camera.orthographicSize = 540; camera.targetTexture = rt;
            camera.transform.position = new Vector3(0, 0, -100);
            camera.farClipPlane = 1000;
            Canvas canvas = copy.GetComponent<Canvas>();
            copy.GetComponent<CanvasScaler>().enabled = false;
            canvas.renderMode = RenderMode.WorldSpace; canvas.worldCamera = camera;
            var canvasRect = (RectTransform)copy.transform;
            canvasRect.sizeDelta = new Vector2(1920, 1080);
            canvasRect.position = Vector3.zero; canvasRect.rotation = Quaternion.identity; canvasRect.localScale = Vector3.one;
            Canvas.ForceUpdateCanvases();
            CRTMenuPresentation presentation = copy.GetComponent<CRTMenuPresentation>();
            if (presentation != null)
            {
                float clock = name.EndsWith("Entry") ? .9f : 5f;
                presentation.Preview(clock);
                var buttons = copy.GetComponentsInChildren<CRTMenuButton>();
                if (name.EndsWith("Hover")) buttons[0].OnPointerEnter(null);
                foreach (var button in buttons) button.Preview(clock, 1);
            }
            Canvas.ForceUpdateCanvases();
            camera.Render(); RenderTexture.active = rt;
            var texture = new Texture2D(1920, 1080, TextureFormat.RGB24, false);
            texture.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0); texture.Apply();
            Directory.CreateDirectory("Temp/MenuPreview");
            File.WriteAllBytes("Temp/MenuPreview/" + name + ".png", texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);
        }
        finally
        {
            RenderTexture.active = previous; RenderTexture.ReleaseTemporary(rt);
            EditorSceneManager.ClosePreviewScene(scene);
        }
    }
}
