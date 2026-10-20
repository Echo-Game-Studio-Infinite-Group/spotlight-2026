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
        gameObject.AddComponent<AudioDebugOverlay>();
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
        if (motor.gameObject.GetComponent<PlayerGameplayAudioSource>() == null)
        {
            motor.gameObject.AddComponent<PlayerGameplayAudioSource>();
        }

        foreach (EnemyBase enemy in FindObjectsOfType<EnemyBase>())
        {
            if (enemy.GetComponent<EnemyAudioSource>() == null)
                enemy.gameObject.AddComponent<EnemyAudioSource>();
            if (enemy.GetComponent<EnemyFootstepAudioSource>() == null)
                enemy.gameObject.AddComponent<EnemyFootstepAudioSource>();
        }
    }

    private void OnDestroy()
    {
        if (_instance == this) _instance = null;
    }
}
