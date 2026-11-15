using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using GameJam.Flow;

/// <summary>
/// Plays one looping Wwise music event for the lifetime of a scene object.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(-200)]
public sealed class WwiseMusicSource : MonoBehaviour
{
    [SerializeField] private string _eventId = "music_run";
    [SerializeField, Min(0f)] private float _fadeOutMs = 250f;
    [SerializeField] private bool _followChaos;
    [SerializeField] private string _lowEventId = "music_stand";
    [SerializeField] private string _middleEventId = "music_walk";
    [SerializeField] private string _highEventId = "music_run";
    [SerializeField, Min(0)] private int _middleChaos = 31;
    [SerializeField, Min(1)] private int _highChaos = 80;

    private uint _playingId = AkUnitySoundEngine.AK_INVALID_PLAYING_ID;
    private Scene _ownerScene;
    private ChaosService _chaos;
    private string _activeEventId;
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

        if (!_followChaos)
        {
            PlayMusic(_eventId);
            yield break;
        }

        while (!_ownerSceneUnloaded && _ownerScene.isLoaded &&
               (RunFlowController.Current == null || RunFlowController.Current.Chaos == null))
            yield return null;
        if (_ownerSceneUnloaded || !_ownerScene.isLoaded) yield break;

        _chaos = RunFlowController.Current.Chaos;
        _chaos.Changed += OnChaosChanged;
        PlayForChaos(_chaos.Value);
    }

    private void OnDisable()
    {
        if (_chaos != null)
        {
            _chaos.Changed -= OnChaosChanged;
            _chaos = null;
        }
        Stop();
    }

    private void OnDestroy()
    {
        SceneManager.sceneUnloaded -= OnSceneUnloaded;
        if (_chaos != null) _chaos.Changed -= OnChaosChanged;
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
        _activeEventId = null;
    }

    private void OnChaosChanged(int value, string reason)
    {
        PlayForChaos(value);
    }

    private void PlayForChaos(int chaos)
    {
        string eventId = chaos >= _highChaos
            ? _highEventId
            : chaos >= _middleChaos ? _middleEventId : _lowEventId;
        if (_activeEventId == eventId && _playingId != AkUnitySoundEngine.AK_INVALID_PLAYING_ID)
            return;

        Stop();
        PlayMusic(eventId);
    }

    private void PlayMusic(string eventId)
    {
        if (string.IsNullOrEmpty(eventId)) return;
        _activeEventId = eventId;
        _playingId = WwiseEventBridge.Play(eventId, gameObject);
    }
}
