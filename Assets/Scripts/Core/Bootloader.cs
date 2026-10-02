using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// 启动流程：Logo 淡入表演 + 后台异步加载下一场景，两者并行
// 时间轴约束（AGENTS.md 第 5 条）：UI 表演一律走未缩放时间，时停/暂停不得影响启动流程
// 关键设计：加载期间 allowSceneActivation 恒为 false——场景加载再快也要等表演播完，否则 Logo 一闪而过
public class Bootloader : MonoBehaviour
{
    [Header("Logo 表演")]
    [Tooltip("Canvas 下的 Logo Image；其 alpha 由本脚本接管")]
    [SerializeField] private Image _logo;
    [Tooltip("淡入时长（秒）")]
    [SerializeField] private float _fadeInDuration = 1f;
    [Tooltip("淡入完成后停留时长（秒）")]
    [SerializeField] private float _holdDuration = 0.5f;

    [Header("场景加载")]
    [Tooltip("异步加载的目标场景名，须已加入 Build Settings")]
    [SerializeField] private string _nextSceneName = "TestScene";
    [Tooltip("启动表演的最短总时长（秒）：即使场景早就加载完也要等满，避免 Logo 一闪而过")]
    [SerializeField] private float _minSplashDuration = 2f;

    private void Awake()
    {
        // 先压到全透明，避免首帧闪出完整 Logo
        if (_logo != null) SetLogoAlpha(0f);
    }

    private void Start()
    {
        StartCoroutine(RunBootSequence());
    }

    private IEnumerator RunBootSequence()
    {
        float startTime = Time.realtimeSinceStartup;

        // 场景一启动就发起异步加载：加载耗时与 Logo 表演重叠，不额外增加等待
        AsyncOperation load = SceneManager.LoadSceneAsync(_nextSceneName);
        if (load == null)
        {
            // 场景不在 Build Settings 时 Unity 返回 null；此时不跳转，留在 BootScene 暴露问题
            Debug.LogError($"[Bootloader] 无法加载场景「{_nextSceneName}」，请确认已加入 Build Settings", this);
            yield break;
        }

        // 表演结束前不放行（此时 load.progress 封顶 0.9）
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

    // allowSceneActivation = false 时 progress 封顶 0.9，达到即代表资源已就绪；
    // 再叠加最短表演时长，两个条件都满足才放行
    private IEnumerator WaitLoadReady(AsyncOperation load, float startTime)
    {
        while (load.progress < 0.9f ||
               Time.realtimeSinceStartup - startTime < _minSplashDuration)
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
