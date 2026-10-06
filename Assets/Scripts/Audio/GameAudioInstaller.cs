using UnityEngine;

[DefaultExecutionOrder(-90)]
[DisallowMultipleComponent]
public sealed class GameAudioInstaller : MonoBehaviour
{
    [SerializeField] private GameAudioCatalog _catalog;
    [SerializeField] private PlayerAudioProfile _profile;

    private void Awake()
    {
        GameAudio.Configure(_catalog, _profile);
    }
}
