using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// 淡入工作室Logo的同时异步加载游戏主场景
public class Bootloader : MonoBehaviour
{
    [Header("Logo演出")]
    [Tooltip("要展示的工作室Logo")]
    [SerializeField] private Image _logo;
    [Tooltip("淡入时长（秒）")]
    [SerializeField] private float _fadeInDuration = 1f;
    [Tooltip("淡入完成后停留时长（秒）")]
    [SerializeField] private float _holdDuration = 1f;

    [Header("场景加载")]
    [Tooltip("异步加载的主目标场景名，必须加入 Build Settings")]
    [SerializeField] private string _mainScene = "MainMenu";

    private void Awake()
    {
        if (_logo != null) SetLogoAlpha(0f);
    }

    private void Start()
    {
        StartCoroutine(BootSeq());
    }

    private IEnumerator BootSeq()
    {
        float startTime = Time.realtimeSinceStartup;

        AsyncOperation load = SceneManager.LoadSceneAsync(_mainScene);
        if (load == null)
        {
            Debug.LogError($"[Bootloader] 无法加载场景「{_mainScene}」，请检查 Build Settings", this);
            yield break;
        }

        load.allowSceneActivation = false;

        yield return PlayLogoIntro();
        yield return WaitLoadReady(load, startTime);

        load.allowSceneActivation = true;
    }

    private IEnumerator PlayLogoIntro()
    {
        if (_logo == null)
        {
            Debug.LogWarning("[Bootloader] 未指定 Logo Image，跳过淡入表演", this);
            yield break;
        }

        if (_fadeInDuration > 0f)
        {
            float elapsed = 0f;
            while (elapsed < _fadeInDuration)
            {
                elapsed += Time.unscaledDeltaTime;
                SetLogoAlpha(Mathf.Clamp01(elapsed / _fadeInDuration));
                yield return null;
            }
        }

        SetLogoAlpha(1f);

        float hold = 0f;
        while (hold < _holdDuration)
        {
            hold += Time.unscaledDeltaTime;
            yield return null;
        }
    }

    private IEnumerator WaitLoadReady(AsyncOperation load, float startTime)
    {
        while (load.progress < 0.9f ||
               Time.realtimeSinceStartup - startTime < (_fadeInDuration + _holdDuration))
        {
            yield return null;
        }
    }

    private void SetLogoAlpha(float alpha)
    {
        Color color = _logo.color;
        color.a = alpha;
        _logo.color = color;
    }
}
