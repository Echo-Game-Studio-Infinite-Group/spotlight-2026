using System;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class ChaosRoomVariants : MonoBehaviour
{
    [SerializeField] private ChaosRoomVariant[] _variants = Array.Empty<ChaosRoomVariant>();
    [Tooltip("待策划确认：默认进入房间、回点时刷新；预加载的后继仍随数值更新。")]
    [SerializeField] private bool _applyImmediately;
    private ChaosManager _chaos;
    private MapManager _map;
    private GameObject _content;
    private int _selected = -1;
    public string CurrentVariantId => _selected >= 0 ? _variants[_selected].Id : string.Empty;
    public GameObject CurrentContent => _content;
    public int AppliedChaos { get; private set; }
    public int VariantCount => _variants.Length;

    public void Configure(ChaosRoomVariant[] variants, bool applyImmediately = false)
    {
        Validate(variants);
        _variants = variants;
        _applyImmediately = applyImmediately;
        if (Application.isPlaying && isActiveAndEnabled) Refresh(true);
    }

    private void OnEnable() { if (Application.isPlaying) Bind(); }
    private void Start() { Bind(); Refresh(); }
    private void OnDisable()
    {
        if (_chaos != null) { _chaos.Changed -= OnChanged; _chaos.RunReset -= OnRunReset; }
        if (_map != null) _map.RoomEntered -= OnRoomEntered;
        _chaos = null; _map = null;
        RemoveContent();
    }

    private void Bind()
    {
        if (_chaos == null && ChaosManager.Exists)
        {
            _chaos = ChaosManager.Instance;
            _chaos.Changed += OnChanged;
            _chaos.RunReset += OnRunReset;
        }
        if (_map == null && MapManager.Exists) { _map = MapManager.Instance; _map.RoomEntered += OnRoomEntered; }
    }

    private void OnChanged(int previous, int current)
    {
        if (_applyImmediately || _map == null || _map.CurrentRoomSceneName != gameObject.scene.name) Refresh();
    }
    private void OnRunReset() => Refresh(true);
    private void OnRoomEntered(string sceneName) { if (sceneName == gameObject.scene.name) Refresh(); }

    public bool Refresh(bool rebuild = false)
    {
        Bind();
        if (_chaos == null || _variants.Length == 0) return false;
        int selected = Array.FindIndex(_variants, item => item.Matches(_chaos.Value));
        if (selected < 0) { RemoveContent(); return false; }
        AppliedChaos = _chaos.Value;
        if (!rebuild && selected == _selected && _content != null) return true;
        RemoveContent();
        _selected = selected;
        // 在失活容器内装配，未选中内容不会提前启用敌人、灯光或生成器。
        _content = new GameObject("差分运行实例_" + _variants[selected].Id);
        _content.SetActive(false);
        _content.transform.SetParent(transform, false);
        GameObject instance = Instantiate(_variants[selected].ContentPrefab, _content.transform, false);
        instance.SetActive(true);
        _content.SetActive(true);
        return true;
    }

    private void RemoveContent()
    {
        if (_content != null) { _content.SetActive(false); Destroy(_content); }
        _content = null; _selected = -1;
    }

    private static void Validate(ChaosRoomVariant[] variants)
    {
        if (variants == null || variants.Length < 1 || variants.Length > 6)
            throw new ArgumentException("每个房间需要 1—6 个差分");
        for (int i = 0; i < variants.Length; i++)
        {
            var item = variants[i];
            if (item == null || string.IsNullOrWhiteSpace(item.Id) || item.ContentPrefab == null || item.MinimumChaos > item.MaximumChaos)
                throw new ArgumentException("差分需要编号、内容预制体和有效范围");
            for (int j = 0; j < i; j++)
                if (variants[j].Id == item.Id || item.MinimumChaos <= variants[j].MaximumChaos && item.MaximumChaos >= variants[j].MinimumChaos)
                    throw new ArgumentException("差分编号不能重复，混乱度区间不能重叠");
        }
    }
}
