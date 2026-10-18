using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Plays one looping Wwise music event for the lifetime of a scene object.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(-200)]
public sealed class WwiseMusicSource : MonoBehaviour
{
    [SerializeField] private string _eventId = "music_run";
    [SerializeField, Min(0f)] private float _fadeOutMs = 250f;

    private uint _playingId = AkUnitySoundEngine.AK_INVALID_PLAYING_ID;
    private Scene _ownerScene;
    private bool _ownerSceneUnloaded;

    private void Awake()
    {
        _ownerScene = gameObject.scene;
        SceneManager.sceneUnloaded += OnSceneUnloaded;
    }

    private IEnumerator Start()
    {
        while (!AkUnitySoundEngine.IsInitialized())
            yield return null;
        if (_ownerSceneUnloaded || !_ownerScene.isLoaded)
            yield break;

        if (GetComponent<AkGameObj>() == null)
            gameObject.AddComponent<AkGameObj>();
        _playingId = WwiseEventBridge.Play(_eventId, gameObject);
    }

    private void OnDisable()
    {
        Stop();
    }

    private void OnDestroy()
    {
        SceneManager.sceneUnloaded -= OnSceneUnloaded;
    }

    private void OnSceneUnloaded(Scene scene)
    {
        if (scene != _ownerScene) return;
        _ownerSceneUnloaded = true;
        Stop();
    }

    public void Stop()
    {
        if (_playingId == AkUnitySoundEngine.AK_INVALID_PLAYING_ID)
            return;
        AkUnitySoundEngine.StopPlayingID(
            _playingId,
            Mathf.RoundToInt(_fadeOutMs));
        _playingId = AkUnitySoundEngine.AK_INVALID_PLAYING_ID;
    }
}
