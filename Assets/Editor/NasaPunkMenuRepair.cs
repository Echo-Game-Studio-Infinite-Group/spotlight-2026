using System;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public static partial class NasaPunkMenuBuilder
{
    private static string ButtonCaption(string name)
    {
        switch (name)
        {
            case "Start": return "开始游戏";
            case "Continue": return "继续游戏";
            case "Settings": return "设置";
            case "Exit": return "退出游戏";
            case "Close": return "返回";
            case "Fullscreen": return "切换全屏";
            case "Retry": return "重新开始";
            case "PauseExit": return "退出";
            case "BattleAgain": return "重新战斗";
            case "TitleScreen": return "标题画面";
            case "LevelSelect": return "关卡选择";
            case "Thanks": return "致谢";
            default: return name;
        }
    }

    private static TMP_Text ButtonText(Transform parent, string name)
    {
        var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/Art/UI/Fonts/NanoDotSong SDF.asset");
        var label = PageText(parent, name, ButtonCaption(name), new Vector2(440, 80), font, 52);
        label.fontSharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(CRTArt + "ButtonTMP.mat") ?? font.material;
        label.extraPadding = true;
        return label;
    }

    private static void PrepareButtonMaterial(TMP_FontAsset font)
    {
        font.TryAddCharacters("开始游戏继续游戏设置退出游戏返回切换全屏重新开始退出重新战斗标题画面关卡选择致谢");
        const string path = CRTArt + "ButtonTMP.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(font.material);
            AssetDatabase.CreateAsset(material, path);
        }
        material.shader = Shader.Find("UI/CRT/TMP Text");
        material.SetTexture("_MainTex", font.atlasTexture);
        material.EnableKeyword("GLOW_ON");
        material.SetColor("_GlowColor", new Color(.78f, .22f, .26f, .65f));
        material.SetFloat("_GlowOuter", .8f);
        material.SetFloat("_GlowPower", .6f);
        material.SetFloat("_ScanlineWidth", 4);
        material.SetFloat("_ScanlineStrength", .22f);
        material.SetFloat("_GlowBreathPeriod", 3);
        material.SetFloat("_GlowBreathDepth", .75f);
        EditorUtility.SetDirty(material);
    }

    private static void MigrateButtons(GameObject root)
    {
        foreach (var button in root.GetComponentsInChildren<Button>(true))
        {
            foreach (var image in button.GetComponentsInChildren<RawImage>(true))
            {
                if (image.texture == null || image.name == "Shutter") continue;
                string key = image.texture.name;
                if (ButtonCaption(key) == key) continue;
                var parent = image.transform.parent;
                UnityEngine.Object.DestroyImmediate(image.gameObject);
                ButtonText(parent, key);
            }
            foreach (var label in button.GetComponentsInChildren<TMP_Text>(true))
            {
                label.fontSharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(CRTArt + "ButtonTMP.mat");
                label.extraPadding = true;
                EditorUtility.SetDirty(label);
            }
        }
    }

    private static void BuildSettingsPage()
    {
        var root = CanvasRoot("Settings", 60);
        try
        {
            Stretch(root.transform, "Fullscreen black backdrop").gameObject.AddComponent<Image>().color = Color.black;
            var content = Center(root.transform, "Content", new Vector2(600, 500), Vector2.zero);
            var titleMaterial = AssetDatabase.LoadAssetAtPath<Material>(CRTArt + "TitleCRT.mat");
            var title = CRTImage(content, "SettingsTitle", new Vector2(440, 100), new Vector2(0, 180), titleMaterial);
            var titleAlpha = title.gameObject.AddComponent<CanvasGroup>();
            var divider = Center(content, "Divider", new Vector2(540, 2), new Vector2(0, 110));
            divider.gameObject.AddComponent<Image>().color = new Color(1, 1, 1, .65f);
            var body = Center(content, "Buttons", new Vector2(540, 280), new Vector2(0, -40));
            var alpha = body.gameObject.AddComponent<CanvasGroup>();
            var text = AssetDatabase.LoadAssetAtPath<Material>(CRTArt + "ButtonCRT.mat");
            var shutter = AssetDatabase.LoadAssetAtPath<Material>(CRTArt + "ShutterCRT.mat");
            var fullscreen = CRTButton(body, "Fullscreen", 50, text, shutter, 0);
            var close = CRTButton(body, "Close", -60, text, shutter, 0);
            foreach (var button in new[] { fullscreen, close })
            {
                var so = new SerializedObject(button.GetComponent<CRTMenuButton>());
                so.FindProperty("_tiltDegrees").floatValue = 0;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            Navigate(fullscreen, close);
            Wire(root.AddComponent<SettingsUI>(), "_fullscreenButton", fullscreen, "_closeButton", close);
            var presentation = root.AddComponent<CRTPausePresentation>();
            Wire(presentation, "_title", title, "_titleOpacity", titleAlpha, "_divider", divider, "_buttons", alpha);
            presentation.Preview(3);
            // 预览使用临时材质，落盘必须引用可随预制体加载的资源。
            title.material = titleMaterial;
            MenuVisualSettingsExport.Export(root);
            PrefabUtility.SaveAsPrefabAsset(root, "Assets/Prefabs/UI/Settings.prefab");
            Snapshot(root, "Settings");
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }

    private static void MigrateExistingPrefab(string path)
    {
        var root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            MigrateButtons(root);
            PrefabUtility.SaveAsPrefabAsset(root, path);
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }
}
