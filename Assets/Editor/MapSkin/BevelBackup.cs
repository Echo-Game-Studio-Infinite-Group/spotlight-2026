using System.Collections.Generic;
using UnityEngine;

namespace MapSkin
{
    /// <summary>
    /// 全场景倒角化的「原 mesh 备份表」。
    ///
    /// 为什么单独一个资产：`BevelGeometry` 只改每个 MeshFilter 的 `sharedMesh`（对象名/位置/材质/碰撞体都不动），
    /// 没有这份表就无法把对象还原回去。备份按对象记录：
    ///   · globalId       —— Unity 全局对象 ID（对象被改名/挪父级也能找回；场景内最稳的锚）
    ///   · objectPath     —— 层级路径（globalId 失效时的退路）
    ///   · objectName     —— 对象名（最后退路，仅在同名唯一时使用）
    ///   · originalMesh   —— 倒角前的原 mesh 引用（可能是内嵌资产、UV_* 资产或 Unity 内置 Cube）
    ///   · generatedMeshPath —— 本工具生成的 `BEVEL_*.asset` 路径（Clear 时删除）
    ///   · worldSize / originalTris —— 对账信息
    ///
    /// 放在 Editor 程序集里：它只服务 Editor 工具，运行时不需要。
    /// 资产路径固定 `Assets/Art/EnvSkins/BEVEL_Backup.asset`（由 `BevelGeometry` 读写）。
    /// </summary>
    public class BevelBackup : ScriptableObject
    {
        [System.Serializable]
        public class Entry
        {
            public string globalId;
            public string objectPath;
            public string objectName;
            public Mesh originalMesh;
            public string generatedMeshPath;
            public Vector3 worldSize;
            public int originalTris;
        }

        public List<Entry> entries = new List<Entry>();
    }
}
