using System.Collections.Generic;
using UnityEngine;

namespace GameJam.Flow
{
    public sealed class FlowRoomEnemies
    {
        private readonly FlowRoomTemplate _template;
        private readonly RoomVariantDefinition _variant;
        private readonly Dictionary<string, FlowEnemySpawnPoint> _points = new Dictionary<string, FlowEnemySpawnPoint>();
        private readonly Dictionary<string, GameObject> _instances = new Dictionary<string, GameObject>();
        private readonly List<string> _errors = new List<string>();
        private ObjectPool _pool;
        private Transform _liveRoot;
        private bool _spawned;
        public IReadOnlyDictionary<string, GameObject> Instances => _instances;
        public IReadOnlyList<string> Errors => _errors;

        public FlowRoomEnemies(FlowRoomTemplate template, RoomVariantDefinition variant)
        {
            _template = template; _variant = variant;
            foreach (FlowEnemySpawnPoint point in template.GetComponentsInChildren<FlowEnemySpawnPoint>(true))
            {
                if (string.IsNullOrWhiteSpace(point.Id) || _points.ContainsKey(point.Id))
                { Error(point.Id, "模板中的出生点编号为空或重复"); continue; }
                _points.Add(point.Id, point);
            }
        }

        public void SetCurrent(bool current)
        {
            if (!current)
            {
                if (_liveRoot == null) return;
                // 死亡回收计时在失活时会复位，离房前先回收尸体，避免再次激活后永久残留。
                foreach (GameObject instance in _instances.Values)
                    if (instance != null && instance.activeSelf && !instance.GetComponent<HealthComponent>().IsAlive) _pool.Return(instance);
                _liveRoot.gameObject.SetActive(false);
                return;
            }
            if (_spawned) { if (_liveRoot != null) _liveRoot.gameObject.SetActive(true); return; }
            _spawned = true;
            Physics.SyncTransforms();
            var seen = new HashSet<string>();
            foreach (FlowEnemySpawnBinding binding in _variant.EnemySpawns ?? new FlowEnemySpawnBinding[0])
            {
                if (binding == null || !binding.Enabled) continue;
                if (string.IsNullOrWhiteSpace(binding.PointId) || !seen.Add(binding.PointId))
                { Error(binding.PointId, "差分出生点编号为空或重复"); continue; }
                if (!_points.TryGetValue(binding.PointId, out FlowEnemySpawnPoint point))
                { Error(binding.PointId, "模板中找不到出生点"); continue; }
                if (binding.EnemyPrefab == null || binding.EnemyPrefab.GetComponent<EnemyBase>() == null)
                { Error(binding.PointId, "未配置有效的敌人预制体"); continue; }
                if (!point.gameObject.activeInHierarchy) continue;
                if (!point.TryGetPose(binding.EnemyPrefab, out Pose pose, out string error))
                { Error(binding.PointId, error); continue; }
                if (_pool == null) CreatePool();
                // 在失活容器中取出敌人，先补齐死亡回收再启用，避免 AI 在初始预制体位置先启动。
                _liveRoot.gameObject.SetActive(false);
                GameObject instance = _pool.Spawn(binding.EnemyPrefab, pose.position, pose.rotation, _liveRoot);
                EnemyPooledLifecycle lifecycle = instance.GetComponent<EnemyPooledLifecycle>();
                if (lifecycle == null) lifecycle = instance.AddComponent<EnemyPooledLifecycle>();
                lifecycle.SetOwner(null);
                _instances.Add(binding.PointId, instance);
            }
            if (_liveRoot != null) _liveRoot.gameObject.SetActive(true);
        }

        public void Clear()
        {
            if (_pool != null)
                foreach (GameObject instance in _instances.Values) if (instance != null) _pool.Return(instance);
            _instances.Clear();
            if (_liveRoot != null) _liveRoot.gameObject.SetActive(false);
        }

        private void CreatePool()
        {
            var host = new GameObject("出生点敌人池");
            host.SetActive(false); host.transform.SetParent(_template.transform, false);
            var idle = new GameObject("空闲敌人"); idle.transform.SetParent(host.transform, false); idle.SetActive(false);
            var live = new GameObject("本次访问敌人"); live.transform.SetParent(host.transform, false); live.SetActive(false);
            _liveRoot = live.transform;
            _pool = host.AddComponent<ObjectPool>(); _pool.SetIdleRoot(idle.transform);
            host.SetActive(true);
        }

        private void Error(string pointId, string reason)
        {
            string message = "差分 " + _variant.Id + " / 出生点 " + pointId + "：" + reason;
            _errors.Add(message); Debug.LogError("[流程敌人] " + message, _template);
        }
    }
}
