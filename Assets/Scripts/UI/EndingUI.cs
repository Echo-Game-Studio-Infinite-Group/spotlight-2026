using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

[DisallowMultipleComponent]
public sealed class EndingUI : MonoBehaviour
{
    [Header("结局正文（每个元素为一页）")]
    [SerializeField, TextArea(3, 12)] private string[] _pages = { "结局文案待补充" };
    [Tooltip("与 Pages 索引对应；留空时使用默认白字材质。")]
    [SerializeField] private Material[] _pageMaterials;
    private Material _defaultTextMaterial;
    [SerializeField] private TMP_Text _storyText;
    [SerializeField] private CanvasGroup _textOpacity;
    [SerializeField] private Button _continueButton;
    [Header("播放设置")]
    [SerializeField, Min(0)] private float _fadeDuration = .6f;
    [SerializeField, Min(0)] private float _minimumReadTime = .5f;
    [Tooltip("0 表示等玩家继续；大于 0 时按真实秒数自动翻页。")]
    [SerializeField, Min(0)] private float _autoAdvanceSeconds;
    [SerializeField] private string _mainMenuScenePath = "Assets/Scenes/MainMenu.unity";
    private int _pageIndex;
    private float _readyAt;
    private bool _changing;
    private bool _audioDebugWasVisible;

    private void Awake()
    {
        _defaultTextMaterial = _storyText.fontSharedMaterial;
        _continueButton.onClick.AddListener(Advance);
    }

    private void OnEnable()
    {
        GameManager.Instance.EndGame();
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        // 调试层跨场景常驻，结局期间暂时隐藏，离开后恢复原先开关。
        _audioDebugWasVisible = AudioDebugOverlay.Visible;
        AudioDebugOverlay.Visible = false;
        _pageIndex = 0;
        _changing = true;
        StartCoroutine(ShowPage());
    }

    private void OnDisable()
    {
        StopAllCoroutines();
        AudioDebugOverlay.Visible = _audioDebugWasVisible;
    }

    private void Update()
    {
        var keyboard = Keyboard.current;
        var gamepad = Gamepad.current;
        if ((keyboard != null && (keyboard.spaceKey.wasPressedThisFrame || keyboard.enterKey.wasPressedThisFrame)) ||
            (gamepad != null && gamepad.buttonSouth.wasPressedThisFrame)) Advance();
        if (_autoAdvanceSeconds > 0 && Time.unscaledTime - _readyAt >= _autoAdvanceSeconds) Advance();
    }

    public void Advance()
    {
        // 翻页期间锁住入口，避免按键与 UI 点击在同一帧跳过两页。
        if (_changing || Time.unscaledTime - _readyAt < _minimumReadTime) return;
        _changing = true;
        StartCoroutine(NextPage());
    }

    private IEnumerator ShowPage()
    {
        _storyText.fontSharedMaterial = _pageMaterials != null && _pageIndex < _pageMaterials.Length && _pageMaterials[_pageIndex] != null
            ? _pageMaterials[_pageIndex] : _defaultTextMaterial;
        _storyText.text = _pages != null && _pages.Length > 0 ? _pages[_pageIndex] : string.Empty;
        yield return Fade(0, 1);
        _readyAt = Time.unscaledTime;
        _changing = false;
    }

    private IEnumerator NextPage()
    {
        yield return Fade(1, 0);
        _pageIndex++;
        if (_pages != null && _pageIndex < _pages.Length)
        {
            yield return ShowPage();
            yield break;
        }
        if (!Application.CanStreamedLevelBeLoaded(_mainMenuScenePath))
        {
            Debug.LogError("[Ending] 主菜单场景未加入 Build Settings：" + _mainMenuScenePath, this);
            _pageIndex = Mathf.Max(0, (_pages?.Length ?? 0) - 1);
            yield return ShowPage();
            yield break;
        }
        MainMenuUI.RequestThanksOnLoad();
        SceneManager.LoadSceneAsync(_mainMenuScenePath, LoadSceneMode.Single);
    }

    private IEnumerator Fade(float from, float to)
    {
        _textOpacity.alpha = from;
        float elapsed = 0;
        // 纯展示走真实时间，通关前残留的时缓或暂停不会卡住文字。
        while (elapsed < _fadeDuration)
        {
            elapsed += Time.unscaledDeltaTime;
            _textOpacity.alpha = Mathf.Lerp(from, to, Mathf.Clamp01(elapsed / _fadeDuration));
            yield return null;
        }
        _textOpacity.alpha = to;
    }
}
