using UnityEngine;

/// <summary>
/// Minimal runtime bootstrap for the Wwise audio path. It keeps the Wwise
/// action driver attached to the player and owns the F3 debug overlay.
/// </summary>
[DefaultExecutionOrder(-40)]
[DisallowMultipleComponent]
public sealed class WwiseAudioBootstrap : MonoBehaviour
{
    private static WwiseAudioBootstrap _instance;

    private AudioDebugOverlay _overlay;
    private PlayerInputReader _boundInput;
    private float _playerScanTimer;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Create()
    {
        if (_instance != null) return;
        GameObject root = new GameObject("[WwiseAudioBootstrap]");
        root.AddComponent<WwiseAudioBootstrap>();
    }

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);
        _overlay = gameObject.AddComponent<AudioDebugOverlay>();
    }

    private void Update()
    {
        _playerScanTimer -= TimeManager.UnscaledDeltaTime;
        if (_playerScanTimer > 0f) return;
        _playerScanTimer = 0.5f;

        PlayerMotor motor = FindObjectOfType<PlayerMotor>();
        if (motor == null) return;

        if (motor.gameObject.GetComponent<WwiseActionDriver>() == null)
        {
            motor.gameObject.AddComponent<WwiseActionDriver>();
        }

        PlayerInputReader input = motor.GetComponent<PlayerInputReader>();
        if (input != null && input != _boundInput)
        {
            if (_boundInput != null) _boundInput.ToggleHUD -= ToggleDebug;
            _boundInput = input;
            _boundInput.ToggleHUD += ToggleDebug;
        }
    }

    private void OnDestroy()
    {
        if (_instance == this) _instance = null;
        if (_boundInput != null) _boundInput.ToggleHUD -= ToggleDebug;
    }

    private void ToggleDebug()
    {
        if (_overlay != null) _overlay.Visible = !_overlay.Visible;
    }
}
