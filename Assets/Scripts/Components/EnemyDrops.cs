using UnityEngine;
using UnityEngine.SceneManagement;

[DisallowMultipleComponent]
[RequireComponent(typeof(HealthComponent))]
public sealed class EnemyDrops : MonoBehaviour
{
    [SerializeField, Range(0f, 1f), Tooltip("每次死亡掉落一个道具的概率，0 不掉落，1 必掉落。")]
    private float _dropChance = .4f;
    [SerializeField, Range(0f, 1f), Tooltip("成功掉落后选中血包的概率，其余为能量包。")]
    private float _healthShare = .5f;
    [SerializeField] private ResourcePickup _healthPickup;
    [SerializeField] private ResourcePickup _energyPickup;
    [SerializeField] private Vector3 _dropOffset = new Vector3(0f, .25f, 0f);

    private HealthComponent _health;
    private bool _deathHandled;

    private void Awake() => _health = GetComponent<HealthComponent>();

    private void OnEnable()
    {
        _deathHandled = false;
        _health.Died += OnDied;
        _health.Revived += OnRevived;
    }

    private void OnDisable()
    {
        _health.Died -= OnDied;
        _health.Revived -= OnRevived;
    }

    private void OnRevived(HealthComponent source) => _deathHandled = false;

    private void OnDied(HealthComponent source)
    {
        if (_deathHandled) return;
        // 即使这次没掉落，也不能因重复通知再次抽奖；池复用或复活时才重置。
        _deathHandled = true;
        if (_dropChance <= 0f || (_dropChance < 1f && Random.value >= _dropChance)) return;
        bool health = _healthShare >= 1f || (_healthShare > 0f && Random.value < _healthShare);
        ResourcePickup prefab = health ? _healthPickup : _energyPickup;
        if (prefab == null) return;
        ResourcePickup pickup = Instantiate(prefab, transform.position + _dropOffset, Quaternion.identity);
        // 流式房间可能不是活动场景；掉落物归原房间，卸载时一并清理，且不挂在尸体下面。
        SceneManager.MoveGameObjectToScene(pickup.gameObject, gameObject.scene);
    }
}
