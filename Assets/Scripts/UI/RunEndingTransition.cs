using System.Collections;
using GameJam.Flow;
using UnityEngine;
using UnityEngine.SceneManagement;

[DisallowMultipleComponent]
[RequireComponent(typeof(RunFlowController))]
public sealed class RunEndingTransition : MonoBehaviour
{
    [SerializeField] private string _endingScenePath = "Assets/Scenes/Ending.unity";
    [SerializeField, Min(0)] private float _delay = 1f;
    private RunFlowController _flow;
    private bool _transitioning;

    private void Awake() => _flow = GetComponent<RunFlowController>();

    private void Update()
    {
        // 单独订阅通关结果，不修改公共流程或测试关卡的通关条件。
        if (!_flow.IsComplete || _transitioning) return;
        _transitioning = true;
        if (!Application.CanStreamedLevelBeLoaded(_endingScenePath))
        {
            Debug.LogError("[Ending] 结局场景未加入 Build Settings：" + _endingScenePath, this);
            return;
        }
        StartCoroutine(ShowEnding());
    }

    private IEnumerator ShowEnding()
    {
        yield return new WaitForSecondsRealtime(_delay);
        // 先收回流式房间，避免后台预加载在切换后留下旧房间对象。
        yield return _flow.ReleaseRooms();
        SceneManager.LoadSceneAsync(_endingScenePath, LoadSceneMode.Single);
    }
}
