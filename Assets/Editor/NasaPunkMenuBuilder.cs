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
    private const string DeathRequest = "Temp/BuildCRTDeath.request";
    private const string PauseRequest = "Temp/BuildCRTPause.request";
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
        if ((!File.Exists(Request) && !File.Exists(CRTRequest) && !File.Exists(PauseRequest) && !File.Exists(DeathRequest)) || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        // 请求保留到编辑模式稳定后再执行，避免播放状态下保存场景。
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorApplication.isPlaying = false;
            return;
        }
        if (File.Exists(DeathRequest)) { File.Delete(DeathRequest); BuildCRTDeath(); }
        else if (File.Exists(PauseRequest)) { File.Delete(PauseRequest); BuildCRTPause(); }
        else if (File.Exists(CRTRequest)) { File.Delete(CRTRequest); BuildCRT(); }
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
        CRTImage(rect, name, new Vector2(440, 74), Vector2.zero, text);
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
        Material titleMaterial = CRTMaterial("TitleCRT", "UI/CRT/Text", 38);
        Material textMaterial = CRTMaterial("ButtonCRT", "UI/CRT/Text", 14);
        textMaterial.SetFloat("_GlowRadius", 24);
        textMaterial.SetFloat("_GlowBreathPeriod", 3);
        textMaterial.SetFloat("_GlowBreathDepth", .75f);
        textMaterial.SetFloat("_GlowStrength", .85f);
        textMaterial.SetColor("_GlowColor", new Color(.78f, .22f, .26f, 1f));
        EditorUtility.SetDirty(textMaterial);
        Material shutterMaterial = CRTMaterial("ShutterCRT", "UI/CRT/Text", 16);
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
            MenuVisualSettingsExport.Export(root);
            Snapshot(root, "MainMenu"); Snapshot(root, "MainMenu-Hover"); Snapshot(root, "MainMenu-Entry"); Snapshot(root, "MainMenu-Glitch"); Snapshot(root, "MainMenu-TravelStart"); Snapshot(root, "MainMenu-TravelMiddle");
        }
        finally
        {
            if (!existing) EditorSceneManager.CloseScene(scene, true);
            if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
        }
    }

    [MenuItem("超高速行者/UI/装配 CRT 暂停菜单")]
    public static void BuildCRTPause()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        try
        {
            BuildPause();
            AssetDatabase.SaveAssets();
            Debug.Log("[CRTPause] SUCCESS / Temp/MenuPreview/PauseMenu.png");
        }
        catch (Exception e) { Debug.LogException(e); }
    }

    private static void BuildPause()
    {
        const string path = "Assets/Prefabs/UI/PauseMenu.prefab";
        foreach (string name in new[] { "PauseTitle", "Retry", "PauseExit" })
        {
            string texturePath = CRTArt + name + ".png";
            AssetDatabase.ImportAsset(texturePath);
            var importer = (TextureImporter)AssetImporter.GetAtPath(texturePath);
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
        }
        Material titleMaterial = AssetDatabase.LoadAssetAtPath<Material>(CRTArt + "TitleCRT.mat");
        Material textMaterial = AssetDatabase.LoadAssetAtPath<Material>(CRTArt + "ButtonCRT.mat");
        Material shutterMaterial = AssetDatabase.LoadAssetAtPath<Material>(CRTArt + "ShutterCRT.mat");
        GameObject root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            for (int i = root.transform.childCount - 1; i >= 0; i--)
                UnityEngine.Object.DestroyImmediate(root.transform.GetChild(i).gameObject);
            ConfigureCanvas(root, 50);
            Transform panel = Stretch(root.transform, "PauseMenuPanel");
            panel.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, .3f);
            RectTransform content = Center(panel, "Pause content", new Vector2(540, 550), Vector2.zero);
            RawImage title = CRTImage(content, "PauseTitle", new Vector2(540, 110), Vector2.zero, titleMaterial);
            title.rectTransform.anchorMin = title.rectTransform.anchorMax = new Vector2(0, 1);
            title.rectTransform.pivot = new Vector2(0, 1);
            CanvasGroup titleOpacity = title.gameObject.AddComponent<CanvasGroup>();
            RectTransform divider = (RectTransform)Box(content, "Divider", 20, 126, 500, 2, new Color(1, 1, 1, .65f)).transform;
            Transform buttons = Rect(content, "Buttons", 20, 150, 500, 400).transform;
            CanvasGroup buttonOpacity = buttons.gameObject.AddComponent<CanvasGroup>();
            Button resume = CRTButton(buttons, "Continue", 150, textMaterial, shutterMaterial, 0);
            Button retry = CRTButton(buttons, "Retry", 50, textMaterial, shutterMaterial, 0);
            Button settings = CRTButton(buttons, "Settings", -50, textMaterial, shutterMaterial, 0);
            Button back = CRTButton(buttons, "PauseExit", -150, textMaterial, shutterMaterial, 0);
            Transform settingsPanel = Stretch(panel, "Settings panel");
            Button fullscreen = CRTButton(settingsPanel, "Fullscreen", 60, textMaterial, shutterMaterial, 0);
            Button close = CRTButton(settingsPanel, "Close", -60, textMaterial, shutterMaterial, 0);
            Navigate(fullscreen, close);
            settingsPanel.gameObject.SetActive(false);
            foreach (Button button in new[] { resume, retry, settings, back, fullscreen, close })
            {
                var motion = new SerializedObject(button.GetComponent<CRTMenuButton>());
                motion.FindProperty("_tiltDegrees").floatValue = 0;
                motion.ApplyModifiedPropertiesWithoutUndo();
            }
            Navigate(resume, retry, settings, back);
            var presentation = panel.gameObject.AddComponent<CRTPausePresentation>();
            Wire(presentation, "_title", title, "_titleOpacity", titleOpacity, "_divider", divider, "_buttons", buttonOpacity);
            Wire(root.GetComponent<PauseUIManager>(), "_pauseMenuPanel", panel.gameObject,
                "_resumeButton", resume, "_retryButton", retry, "_returnButton", back,
                "_settingsButton", settings, "_settingsPanel", settingsPanel.gameObject,
                "_pauseContent", content.gameObject, "_closeSettingsButton", close, "_fullscreenButton", fullscreen);
            PrefabUtility.SaveAsPrefabAsset(root, path);
            MenuVisualSettingsExport.Export(root);
            Snapshot(root, "PauseMenu");
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }

    [MenuItem("超高速行者/UI/装配 CRT 死亡菜单")]
    public static void BuildCRTDeath()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        const string path = "Assets/Prefabs/DeathScreen.prefab";
        foreach (string name in new[] { "DeathTitle", "BattleAgain", "TitleScreen" })
        {
            string texturePath = CRTArt + name + ".png";
            AssetDatabase.ImportAsset(texturePath);
            var importer = (TextureImporter)AssetImporter.GetAtPath(texturePath);
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
        }
        GameObject root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            for (int i = root.transform.childCount - 1; i >= 0; i--)
                UnityEngine.Object.DestroyImmediate(root.transform.GetChild(i).gameObject);
            ConfigureCanvas(root, 51);
            var controller = root.GetComponent<DeathRespawnUI>();
            if (controller == null) controller = root.AddComponent<DeathRespawnUI>();
            Transform panel = Stretch(root.transform, "DeathScreenPanel");
            panel.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, .3f);
            RectTransform content = Center(panel, "Death content", new Vector2(540, 350), Vector2.zero);
            var titleMaterial = AssetDatabase.LoadAssetAtPath<Material>(CRTArt + "TitleCRT.mat");
            var textMaterial = AssetDatabase.LoadAssetAtPath<Material>(CRTArt + "ButtonCRT.mat");
            var shutterMaterial = AssetDatabase.LoadAssetAtPath<Material>(CRTArt + "ShutterCRT.mat");
            RawImage title = CRTImage(content, "DeathTitle", new Vector2(540, 110), Vector2.zero, titleMaterial);
            title.rectTransform.anchorMin = title.rectTransform.anchorMax = title.rectTransform.pivot = new Vector2(0, 1);
            CanvasGroup opacity = title.gameObject.AddComponent<CanvasGroup>();
            RectTransform divider = (RectTransform)Box(content, "Divider", 20, 126, 500, 2, new Color(1, 1, 1, .65f)).transform;
            Transform buttons = Rect(content, "Buttons", 20, 150, 500, 200).transform;
            CanvasGroup buttonOpacity = buttons.gameObject.AddComponent<CanvasGroup>();
            Button retry = CRTButton(buttons, "BattleAgain", 50, textMaterial, shutterMaterial, 0);
            Button back = CRTButton(buttons, "TitleScreen", -50, textMaterial, shutterMaterial, 0);
            foreach (Button button in new[] { retry, back })
            {
                var serialized = new SerializedObject(button.GetComponent<CRTMenuButton>());
                serialized.FindProperty("_tiltDegrees").floatValue = 0;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            Navigate(retry, back);
            var presentation = panel.gameObject.AddComponent<CRTPausePresentation>();
            Wire(presentation, "_title", title, "_titleOpacity", opacity, "_divider", divider, "_buttons", buttonOpacity);
            Wire(controller, "respawnButton", retry, "panelRoot", panel, "_returnButton", back);
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            var settings = Resources.Load<SceneUISettings>("SceneUISettings");
            settings.DeathScreen = prefab;
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            Snapshot(root, "DeathScreen");
            Debug.Log("[CRTDeath] SUCCESS / Temp/MenuPreview/DeathScreen.png");
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
        label.font = AssetDatabase.LoadAssetAtPath<Font>("Assets/Art/UI/Fonts/NanoDotSong.ttf");
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
                if (behaviour is MainMenuUI || behaviour is PauseUIManager || behaviour is DeathRespawnUI) UnityEngine.Object.DestroyImmediate(behaviour);
            if (name == "PauseMenu" || name == "DeathScreen") copy.transform.GetChild(0).gameObject.SetActive(true);
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
                float clock = name.EndsWith("TravelStart") ? .55f : name.EndsWith("TravelMiddle") ? 1.75f : name.EndsWith("Glitch") ? .2f : name.EndsWith("Entry") ? .9f : 5f;
                presentation.Preview(clock);
                var buttons = copy.GetComponentsInChildren<CRTMenuButton>();
                if (name.EndsWith("Hover")) buttons[0].OnPointerEnter(null);
                foreach (var button in buttons) button.Preview(clock, 1);
            }
            var pause = copy.GetComponentInChildren<CRTPausePresentation>(true);
            if (pause != null) pause.Preview(3);
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
