using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

// 通过真实场景和射线检查页面，避免只验证 onClick 而漏掉遮挡。
[InitializeOnLoad]
public static class NasaPunkMenuValidation
{
    private const string Request = "Temp/ValidateCRTMenu.request";
    private static int _step
    {
        get => SessionState.GetInt("CRTValidation.Step", 0);
        set => SessionState.SetInt("CRTValidation.Step", value);
    }
    private static double _next;
    private static MainMenuUI _menu;
    private static readonly List<string> Results = new List<string>();
    private static readonly string[] Pages = { "Settings", "LevelSelect", "Thanks" };

    static NasaPunkMenuValidation() => EditorApplication.update += Update;

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target);

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        Results.Add(message);
    }

    private static void Click(Button button)
    {
        Canvas.ForceUpdateCanvases();
        var rect = (RectTransform)button.transform;
        var pointer = new PointerEventData(EventSystem.current)
        { position = RectTransformUtility.WorldToScreenPoint(null, rect.TransformPoint(rect.rect.center)) };
        var hits = new List<RaycastResult>();
        EventSystem.current.RaycastAll(pointer, hits);
        Require(hits.Count > 0 && hits[0].gameObject.GetComponentInParent<Button>() == button, button.name + " raycast reaches button" +
            " [" + string.Join(", ", hits.Select(hit => hit.gameObject.name)) + "; " +
            string.Join(", ", button.GetComponentsInParent<CanvasGroup>().Select(group => group.name + ":" + group.alpha + "/" + group.blocksRaycasts)) + "]");
        Require(button.IsInteractable(), button.name + " interactable");
        ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerClickHandler);
    }

    private static void Update()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        try
        {
            if (_step == 0)
            {
                if (!File.Exists(Request) || EditorApplication.isPlayingOrWillChangePlaymode) return;
                bool thanksOnly = File.ReadAllText(Request).Trim() == "Thanks";
                SessionState.SetBool("CRTValidation.ThanksOnly", thanksOnly);
                File.Delete(Request);
                Results.Clear();
                SessionState.SetBool("CRTValidation.ThanksRevealed", false);
                SessionState.SetBool("CRTValidation.ThanksAutoReturn", false);
                var menu = UnityEngine.Object.FindObjectOfType<MainMenuUI>();
                Require(menu != null, "MainMenu controller exists");
                if (thanksOnly)
                {
                    Require(Field<ThanksUI>(menu, "_thanksPanel") != null, "Thanks panel is connected");
                    File.WriteAllLines("Temp/MenuPreview/Validation.txt", Results);
                    _step = 5;
                    EditorApplication.isPlaying = true;
                    _next = EditorApplication.timeSinceStartup + 2;
                    return;
                }
                var main = Field<GameObject>(menu, "_mainPanel");
                var layers = GameObject.Find("Layers");
                Require(layers != null && layers.transform.parent == main.transform.parent, "Layers is sibling of MainMenu");
                Require(menu.gameObject != main && !menu.transform.IsChildOf(main.transform), "Controller stays active outside MainMenu");
                foreach (string name in Pages)
                {
                    var page = layers.transform.Find(name);
                    Require(page != null && PrefabUtility.IsPartOfPrefabInstance(page), name + " prefab instance saved under Layers");
                }
                foreach (var button in main.GetComponentsInChildren<Button>(true))
                    Require(button.GetComponentInChildren<TMP_Text>(true) != null, button.name + " uses TMP");
                Require(!ShaderUtil.ShaderHasError(Shader.Find("UI/CRT/TMP Text")), "TMP CRT shader compiles");
                File.WriteAllLines("Temp/MenuPreview/Validation.txt", Results);
                _step = 1;
                EditorApplication.isPlaying = true;
                _next = EditorApplication.timeSinceStartup + 2;
                return;
            }
            if (!EditorApplication.isPlaying || EditorApplication.timeSinceStartup < _next) return;
            if (Results.Count == 0) Results.AddRange(File.ReadAllLines("Temp/MenuPreview/Validation.txt"));
            _menu = UnityEngine.Object.FindObjectOfType<MainMenuUI>();
            int pageIndex = (_step - 1) / 2;
            string pageName = Pages[pageIndex];
            var panel = pageName == "Settings" ? Field<GameObject>(_menu, "_settingsPanel")
                : pageName == "LevelSelect" ? Field<LevelSelectUI>(_menu, "_levelSelectPanel").gameObject
                : Field<ThanksUI>(_menu, "_thanksPanel").gameObject;
            if (pageName == "Thanks" && SessionState.GetBool("CRTValidation.ThanksAutoReturn", false))
            {
                Require(!panel.activeSelf && Field<GameObject>(_menu, "_mainPanel").activeInHierarchy, "Credits automatically return to MainMenu after one second");
                File.WriteAllLines("Temp/MenuPreview/Validation.txt", Results);
                Debug.Log("[CRTValidation] PASS: " + Results.Count + " checks");
                _step = 0;
                EditorApplication.isPlaying = false;
                return;
            }
            if (_step % 2 == 1)
            {
                string field = pageName == "Settings" ? "_settingsButton" : pageName == "LevelSelect" ? "_levelSelectButton" : "_thanksButton";
                if (SessionState.GetBool("CRTValidation.ThanksOnly", false)) Field<Button>(_menu, field).onClick.Invoke();
                else Click(Field<Button>(_menu, field));
                Require(panel.activeInHierarchy && !Field<GameObject>(_menu, "_mainPanel").activeSelf, pageName + " opens exclusively");
                Require(panel.transform.parent.Cast<Transform>().Count(p => p.gameObject.activeSelf) == 1, "Only one layer active");
                _next = EditorApplication.timeSinceStartup + 1;
                _step++;
            }
            else
            {
                var backdrop = panel.transform.Cast<Transform>().Select(child => child.GetComponent<Image>()).First(image => image != null);
                Require(backdrop.color == Color.black, pageName + " has opaque black backdrop");
                var corners = new Vector3[4];
                backdrop.rectTransform.GetWorldCorners(corners);
                var screen = panel.GetComponent<Canvas>().pixelRect;
                Require(corners[0].x <= screen.xMin + 1 && corners[0].y <= screen.yMin + 1 && corners[2].x >= screen.xMax - 1 && corners[2].y >= screen.yMax - 1,
                    pageName + " backdrop fills canvas " + screen + " corners " + corners[0] + " / " + corners[2]);
                var capture = ScreenCapture.CaptureScreenshotAsTexture();
                File.WriteAllBytes("Temp/MenuPreview/" + pageName + "-Game.png", capture.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(capture);
                {
                    var title = Field<RawImage>(panel.GetComponent<CRTPausePresentation>(), "_title");
                    Require(title != null && title.texture != null && title.texture.name == pageName + "Title", pageName + " uses baked RawImage title");
                    Require(title.material.shader.name == "UI/CRT/Text" && title.material.HasProperty("_Glitch"), pageName + " title retains TitleCRT glitch");
                }
                if (pageName == "LevelSelect")
                    Require(panel.GetComponentsInChildren<Button>().Length > 1, "FlowGame levels populated");
                if (pageName == "Thanks" && !SessionState.GetBool("CRTValidation.ThanksRevealed", false))
                {
                    var thanks = panel.GetComponent<ThanksUI>();
                    var credits = Field<RectTransform>(thanks, "_credits");
                    Require(credits.GetComponent<TMP_Text>().lineSpacing >= 40, "Credits have expanded line spacing");
                    Require(Field<RectTransform>(thanks, "_viewport").rect.height > 600, "Credits viewport uses available height");
                    var update = typeof(ThanksUI).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance);
                    var elapsed = typeof(ThanksUI).GetField("_elapsed", BindingFlags.NonPublic | BindingFlags.Instance);
                    var keyboard = InputSystem.AddDevice<Keyboard>();
                    try
                    {
                        elapsed.SetValue(thanks, 1f);
                        credits.anchoredPosition = new Vector2(0, -500);
                        float start = credits.anchoredPosition.y;
                        update.Invoke(thanks, null);
                        float normal = credits.anchoredPosition.y - start;
                        foreach (var key in new[] { Key.LeftShift, Key.RightShift })
                        {
                            InputSystem.QueueStateEvent(keyboard, new KeyboardState(key));
                            InputSystem.Update();
                            start = credits.anchoredPosition.y;
                            update.Invoke(thanks, null);
                            Require(Mathf.Abs((credits.anchoredPosition.y - start) - normal * 2) < .001f, key + " doubles credits speed");
                        }
                    }
                    finally { InputSystem.RemoveDevice(keyboard); }
                    typeof(ThanksUI).GetMethod("BeginSpecialThanks", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(thanks, null);
                    var specialUpdate = typeof(ThanksUI).GetMethod("UpdateSpecialThanks", BindingFlags.NonPublic | BindingFlags.Instance);
                    string[] pages = { "特别鸣谢", "大连理工大学\nEcho Game Studio", "TA/社长大人\nKiiye", "And……", "You" };
                    float duration = thanks.FadeDuration * 2 + thanks.LineHoldDuration;
                    for (int i = 0; i < pages.Length; i++)
                    {
                        elapsed.SetValue(thanks, i * duration + thanks.FadeDuration);
                        specialUpdate.Invoke(thanks, null);
                        Require(Field<TMP_Text>(thanks, "_specialThanks").text == pages[i], "Special thanks page " + (i + 1) + " preserves grouped lines");
                        Require(Field<CanvasGroup>(thanks, "_specialOpacity").alpha > .99f, "Special thanks page " + (i + 1) + " fades fully in");
                    }
                    elapsed.SetValue(thanks, 1000f);
                    specialUpdate.Invoke(thanks, null);
                    Require(Field<TMP_Text>(thanks, "_specialThanks").text == "You", "Special thanks reaches You");
                    Require(Field<float>(thanks, "_returnDelay") == 1f && panel.activeSelf, "Credits wait one second before returning");
                    var returnClock = typeof(ThanksUI).GetField("_returnElapsed", BindingFlags.NonPublic | BindingFlags.Instance);
                    var shiftKeyboard = InputSystem.AddDevice<Keyboard>();
                    try
                    {
                        InputSystem.QueueStateEvent(shiftKeyboard, new KeyboardState(Key.LeftShift));
                        InputSystem.Update();
                        update.Invoke(thanks, null);
                        Require(Mathf.Abs(Field<float>(thanks, "_returnElapsed") - Time.unscaledDeltaTime) < .001f, "Shift does not accelerate final return delay");
                    }
                    finally { InputSystem.RemoveDevice(shiftKeyboard); }
                    returnClock.SetValue(thanks, 0f);
                    SessionState.SetBool("CRTValidation.ThanksRevealed", true);
                    // 新激活的 Graphic 需经过一帧注册，随后才能验证真实射线。
                    _next = EditorApplication.timeSinceStartup + .2;
                    return;
                }
                object ui = pageName == "Settings" ? (object)panel.GetComponent<SettingsUI>()
                    : pageName == "LevelSelect" ? panel.GetComponent<LevelSelectUI>() : (object)panel.GetComponent<ThanksUI>();
                Click(Field<Button>(ui, "_closeButton"));
                Require(Field<GameObject>(_menu, "_mainPanel").activeInHierarchy && !panel.activeSelf, pageName + " returns to MainMenu");
                if (pageIndex == Pages.Length - 1)
                {
                    if (SessionState.GetBool("CRTValidation.ThanksOnly", false)) Field<Button>(_menu, "_thanksButton").onClick.Invoke();
                    else Click(Field<Button>(_menu, "_thanksButton"));
                    var thanks = panel.GetComponent<ThanksUI>();
                    Require(!Field<bool>(thanks, "_returnPending"), "Reopening credits resets completion state");
                    typeof(ThanksUI).GetMethod("BeginSpecialThanks", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(thanks, null);
                    typeof(ThanksUI).GetField("_elapsed", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(thanks, 1000f);
                    typeof(ThanksUI).GetMethod("UpdateSpecialThanks", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(thanks, null);
                    SessionState.SetBool("CRTValidation.ThanksAutoReturn", true);
                    _next = EditorApplication.timeSinceStartup + 1.2;
                }
                else { _step++; _next = EditorApplication.timeSinceStartup + 1; }
            }
        }
        catch (Exception exception)
        {
            _step = 0;
            File.WriteAllText("Temp/MenuPreview/Validation.txt", string.Join("\n", Results) + "\nFAIL: " + exception);
            Debug.LogException(exception);
            if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
        }
    }
}
