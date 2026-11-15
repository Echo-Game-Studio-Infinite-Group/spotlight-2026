using System;
using UnityEngine;

namespace GameJam.Flow
{
    [Serializable]
    public sealed class FlowEnemySpawnBinding
    {
        [InspectorName("出生点编号"), Tooltip("对应地图模板中的稳定出生点编号。")] public string PointId;
        [InspectorName("启用"), Tooltip("进入本差分时是否在这个出生点生成敌人。")] public bool Enabled;
        [InspectorName("敌人预制体"), Tooltip("此差分在该点生成的敌人，每次访问只生成一只。")] public GameObject EnemyPrefab;
    }
}
