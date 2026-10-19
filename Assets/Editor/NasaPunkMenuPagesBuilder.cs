using System;
using System.IO;
using System.Linq;
using GameJam.Flow;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public static partial class NasaPunkMenuBuilder
{
    private const string PagesRequest = "Temp/BuildCRTPages.request";
    private const string Credits = "程序组\n主程/3C/UI    緋雲\n系统/运营    Y.Sir\nUI/吉祥物    1382263\n道具    Dr.鸣\n道具    S.Y.A.\n\n策划组\n程序/3C/主策划    哈士奇人\n系统策划    KinoChain\n系统策划    Kr.林晓风\n关卡策划    此地无银\n文案    The Blank\n\n美术组\n2d美术/角色原案    炸虾翻滚一圈半\n3d美术    派派派\n地图设计    qwq\n音乐/音效    炡凧\n资产/程序    空气";
    [InitializeOnLoadMethod]
    private static void WatchPages() => EditorApplication.update += PollPages;
    private static void PollPages()
    {
        if (!File.Exists(PagesRequest) || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        if (EditorApplication.isPlayingOrWillChangePlaymode) { EditorApplication.isPlaying = false; return; }
        File.Delete(PagesRequest);
        try { BuildPages(); } catch (Exception e) { Debug.LogException(e); }
    }
    [MenuItem("超高速行者/UI/装配关卡选择和致谢")]
    public static void BuildPages()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        foreach (string name in new[] { "LevelSelect", "Thanks", "LevelSelectTitle", "ThanksTitle" })
        {
            string path = CRTArt + name + ".png";
            AssetDatabase.ImportAsset(path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.alphaIsTransparency = true; importer.mipmapEnabled = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
        }
        var previous = SceneManager.GetActiveScene();
        const string flowPath = "Assets/Scenes/FlowGame.unity";
        var flow = SceneManager.GetSceneByPath(flowPath);
        bool opened = !flow.IsValid() || !flow.isLoaded;
        if (opened) flow = EditorSceneManager.OpenScene(flowPath, OpenSceneMode.Additive);
        RunFlowConfig config;
        try { config = flow.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<RunFlowController>(true)).Single().Config; }
        finally { if (opened) EditorSceneManager.CloseScene(flow, true); }
        if (config == null) throw new InvalidOperationException("FlowGame 没有流程配置");
        var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/Art/UI/Fonts/NanoDotSong SDF.asset");
        if (font == null)
        {
            font = TMP_FontAsset.CreateFontAsset(AssetDatabase.LoadAssetAtPath<Font>("Assets/Art/UI/Fonts/NanoDotSong.ttf"), 64, 12,
                UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA, 2048, 2048, AtlasPopulationMode.Dynamic, true);
            font.name = "NanoDotSong SDF";
            AssetDatabase.CreateAsset(font, "Assets/Art/UI/Fonts/NanoDotSong SDF.asset");
            AssetDatabase.AddObjectToAsset(font.material, font);
            foreach (var atlas in font.atlasTextures) AssetDatabase.AddObjectToAsset(atlas, font);
        }
        font.TryAddCharacters(Credits + "关卡选择致谢返回特别鸣谢大连理工大学Echo Game StudioTA/社长大人KiiyeAnd……You回到标题画面" + string.Concat(LevelSelection.GetLevels(config).Select(v => v.Room.DisplayName + v.Id)));
        font.material.shader = Shader.Find("TextMeshPro/Distance Field");
        font.material.EnableKeyword("GLOW_ON");
        font.material.SetColor("_GlowColor", new Color(.78f, .22f, .26f, .65f));
        font.material.SetFloat("_GlowOffset", 0);
        font.material.SetFloat("_GlowInner", .05f);
        font.material.SetFloat("_GlowOuter", .8f);
        font.material.SetFloat("_GlowPower", .6f);
        EditorUtility.SetDirty(font); EditorUtility.SetDirty(font.material);
        PrepareButtonMaterial(font);
        BuildSettingsPage();
        BuildPage(false, config, font);
        BuildPage(true, config, font);
        MigrateExistingPrefab("Assets/Prefabs/UI/PauseMenu.prefab");
        MigrateExistingPrefab("Assets/Prefabs/DeathScreen.prefab");
        AttachPages();
        AssetDatabase.SaveAssets();
        if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
        Debug.Log("[CRTPages] SUCCESS: LevelSelect / Thanks / MainMenu");
    }
    private static TMP_Text PageText(Transform parent, string name, string value, Vector2 size, TMP_FontAsset font, float fontSize)
    {
        var text = Center(parent, name, size, Vector2.zero).gameObject.AddComponent<TextMeshProUGUI>();
        text.font = font; text.fontSharedMaterial = font.material; text.text = value;
        text.fontSize = fontSize; text.alignment = TextAlignmentOptions.Center;
        text.color = Color.white; text.raycastTarget = false;
        return text;
    }
    private static void BuildPage(bool thanks, RunFlowConfig config, TMP_FontAsset font)
    {
        string name = thanks ? "Thanks" : "LevelSelect";
        var titleMat = AssetDatabase.LoadAssetAtPath<Material>(CRTArt + "TitleCRT.mat");
        var textMat = AssetDatabase.LoadAssetAtPath<Material>(CRTArt + "ButtonCRT.mat");
        var shutterMat = AssetDatabase.LoadAssetAtPath<Material>(CRTArt + "ShutterCRT.mat");
        var root = CanvasRoot(name, 60);
        try
        {
            Stretch(root.transform, "Fullscreen black backdrop").gameObject.AddComponent<Image>().color = Color.black;
            var panel = Center(root.transform, "Content", new Vector2(1000, 860), Vector2.zero);
            var title = CRTImage(panel, name + "Title", new Vector2(800, 110), new Vector2(0, 350), titleMat);
            var titleAlpha = title.gameObject.AddComponent<CanvasGroup>();
            var divider = Center(panel, "Divider", new Vector2(900, 2), new Vector2(0, 275));
            divider.gameObject.AddComponent<Image>().color = new Color(1,1,1,.65f);
            var body = Center(panel, "Body", new Vector2(900, 600), new Vector2(0,-40));
            var alpha = body.gameObject.AddComponent<CanvasGroup>();
            var viewport = Center(body, "Viewport", new Vector2(900, 520), new Vector2(0,40));
            viewport.gameObject.AddComponent<RectMask2D>();
            var close = CRTButton(body, "Close", -280, textMat, shutterMat, 0);
            var motion = new SerializedObject(close.GetComponent<CRTMenuButton>());
            motion.FindProperty("_tiltDegrees").floatValue = 0; motion.ApplyModifiedPropertiesWithoutUndo();
            if (thanks)
            {
                var credits = PageText(viewport, "Developer credits", Credits, new Vector2(880,1450), font, 42);
                credits.rectTransform.anchorMin = credits.rectTransform.anchorMax = new Vector2(.5f,1);
                credits.rectTransform.pivot = new Vector2(.5f,1);
                credits.alignment = TextAlignmentOptions.Top;
                credits.lineSpacing = 42;
                // 致谢阅读区随画布伸展，避免固定大小的窗口截掉可用屏幕。
                panel.anchorMin = Vector2.zero; panel.anchorMax = Vector2.one;
                panel.offsetMin = new Vector2(96, 40); panel.offsetMax = new Vector2(-96, -40);
                title.rectTransform.anchorMin = title.rectTransform.anchorMax = new Vector2(.5f, 1);
                title.rectTransform.anchoredPosition = new Vector2(0, -65);
                divider.anchorMin = new Vector2(0, 1); divider.anchorMax = Vector2.one;
                divider.sizeDelta = new Vector2(-96, 2); divider.anchoredPosition = new Vector2(0, -140);
                body.anchorMin = Vector2.zero; body.anchorMax = Vector2.one;
                body.offsetMin = new Vector2(0, 70); body.offsetMax = new Vector2(0, -175);
                viewport.anchorMin = Vector2.zero; viewport.anchorMax = Vector2.one;
                viewport.offsetMin = viewport.offsetMax = Vector2.zero;
                credits.rectTransform.anchorMin = new Vector2(0, 1); credits.rectTransform.anchorMax = Vector2.one;
                credits.rectTransform.sizeDelta = new Vector2(-48, 1450);
                close.transform.SetParent(panel, false);
                var closeRect = (RectTransform)close.transform;
                closeRect.anchorMin = closeRect.anchorMax = new Vector2(.5f, 0);
                closeRect.anchoredPosition = new Vector2(0, 40);
                var ui = root.AddComponent<ThanksUI>();
                var special = PageText(root.transform, "Special thanks", "特别鸣谢", new Vector2(1600,160), font, 52);
                var specialOpacity = special.gameObject.AddComponent<CanvasGroup>();
                specialOpacity.alpha = 0;
                special.gameObject.SetActive(false);
                UnityEngine.Object.DestroyImmediate(close.transform.Find("Close").gameObject);
                PageText(close.transform, "Return to title", "回到标题画面", new Vector2(480,80), font, 42);
                close.gameObject.SetActive(false);
                Wire(ui, "_credits", credits.rectTransform, "_viewport", viewport, "_closeButton", close,
                    "_specialThanks", special, "_specialOpacity", specialOpacity, "_title", title.gameObject, "_divider", divider.gameObject);
            }
            else
            {
                var content = Center(viewport, "Levels", new Vector2(860,520), Vector2.zero);
                content.anchorMin = new Vector2(0,1); content.anchorMax = Vector2.one; content.pivot = new Vector2(.5f,1);
                content.sizeDelta = Vector2.zero;
                var layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
                layout.spacing = 12; layout.childControlHeight = true; layout.childForceExpandHeight = false;
                var fitter = content.gameObject.AddComponent<ContentSizeFitter>(); fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                var scroll = viewport.gameObject.AddComponent<ScrollRect>(); scroll.viewport = viewport; scroll.content = content; scroll.horizontal = false;
                viewport.gameObject.AddComponent<Image>().color = Color.clear;
                var template = CRTButton(content, "Start", 0, textMat, shutterMat, 0);
                UnityEngine.Object.DestroyImmediate(template.transform.Find("Start").gameObject);
                PageText(template.transform, "Level name", "关卡", new Vector2(820,74), font, 38);
                template.GetComponent<RectTransform>().sizeDelta = new Vector2(860,80);
                var fill = template.transform.Find("Shutter").GetComponent<RectTransform>(); fill.sizeDelta = new Vector2(860,80);
                var element = template.gameObject.AddComponent<LayoutElement>(); element.preferredHeight = 80;
                var so = new SerializedObject(template.GetComponent<CRTMenuButton>()); so.FindProperty("_tiltDegrees").floatValue = 0; so.ApplyModifiedPropertiesWithoutUndo();
                template.gameObject.SetActive(false);
                var ui = root.AddComponent<LevelSelectUI>();
                Wire(ui, "_config", config, "_content", content, "_template", template, "_closeButton", close);
            }
            var presentation = root.AddComponent<CRTPausePresentation>();
            Wire(presentation, "_title", title, "_titleOpacity", titleAlpha, "_divider", divider, "_buttons", alpha);
            if (thanks) Wire(root.GetComponent<ThanksUI>(), "_presentation", presentation);
            MigrateButtons(root);
            presentation.Preview(3);
            // 预览实例不会序列化为材质资源，保存前恢复 TitleCRT 引用。
            title.material = titleMat;
            Snapshot(root, name);
            MenuVisualSettingsExport.Export(root);
            PrefabUtility.SaveAsPrefabAsset(root, "Assets/Prefabs/UI/" + name + ".prefab");
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }
    private static void AttachPages()
    {
        const string path = "Assets/Scenes/MainMenu.unity";
        var scene = SceneManager.GetSceneByPath(path);
        bool opened = !scene.IsValid() || !scene.isLoaded;
        if (opened) scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
        try
        {
            Directory.CreateDirectory("Temp/MenuBackup");
            if (!EditorSceneManager.SaveScene(scene, "Temp/MenuBackup/MainMenu-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".unity", true))
                throw new IOException("主菜单备份失败");
            var menu = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<MainMenuUI>(true)).Single();
            var data = new SerializedObject(menu);
            var main = data.FindProperty("_mainPanel").objectReferenceValue as GameObject;
            if (main == null) main = menu.gameObject;
            if (menu.gameObject == main)
            {
                var controller = new GameObject("MenuController");
                SceneManager.MoveGameObjectToScene(controller, scene);
                var replacement = controller.AddComponent<MainMenuUI>();
                EditorUtility.CopySerialized(menu, replacement);
                UnityEngine.Object.DestroyImmediate(menu);
                menu = replacement;
            }
            data = new SerializedObject(menu);
            var oldSettings = data.FindProperty("_settingsPanel").objectReferenceValue as GameObject;
            if (oldSettings != null && oldSettings.transform.IsChildOf(main.transform)) UnityEngine.Object.DestroyImmediate(oldSettings);
            var layers = scene.GetRootGameObjects().FirstOrDefault(r => r.name == "Layers");
            if (layers == null)
            {
                layers = new GameObject("Layers");
                SceneManager.MoveGameObjectToScene(layers, scene);
            }
            var text = AssetDatabase.LoadAssetAtPath<Material>(CRTArt + "ButtonCRT.mat");
            var shutter = AssetDatabase.LoadAssetAtPath<Material>(CRTArt + "ShutterCRT.mat");
            Button ButtonFor(string field, string name, float phase)
            {
                var existing = new SerializedObject(menu).FindProperty(field).objectReferenceValue as Button;
                return existing != null ? existing : CRTButton(main.transform, name, 0, text, shutter, phase);
            }
            var level = ButtonFor("_levelSelectButton", "LevelSelect", .6f);
            var thanks = ButtonFor("_thanksButton", "Thanks", 1.2f);
            GameObject Page(string name)
            {
                foreach (var parent in new[] { main.transform, layers.transform })
                {
                    var existing = parent.Find(name + " page") ?? parent.Find(name);
                    if (existing != null) UnityEngine.Object.DestroyImmediate(existing.gameObject);
                }
                var page = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/UI/" + name + ".prefab"), layers.transform);
                page.name = name; page.SetActive(false); return page;
            }
            var settings = Page("Settings");
            var settingsData = new SerializedObject(settings.GetComponent<SettingsUI>());
            Wire(menu, "_mainPanel", main, "_levelSelectButton", level, "_thanksButton", thanks,
                "_levelSelectPanel", Page("LevelSelect").GetComponent<LevelSelectUI>(), "_thanksPanel", Page("Thanks").GetComponent<ThanksUI>(),
                "_settingsPanel", settings, "_fullscreenButton", settingsData.FindProperty("_fullscreenButton").objectReferenceValue,
                "_closeSettingsButton", settingsData.FindProperty("_closeButton").objectReferenceValue);
            MigrateButtons(main);
            main.SetActive(true);
            menu.RefreshButtons();
            foreach (var rect in main.GetComponentsInChildren<RectTransform>(true)) EditorUtility.SetDirty(rect);
            EditorUtility.SetDirty(menu);
            Snapshot(main, "MainMenu");
            EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
        }
        finally { if (opened) EditorSceneManager.CloseScene(scene, true); }
    }
}
