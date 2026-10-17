using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace GameJam.Flow
{
    public sealed class FlowRoomInstance
    {
        public long Id { get; }
        public RoomVariantDefinition Variant { get; internal set; }
        public Scene Scene { get; internal set; }
        public FlowRoomTemplate Root { get; internal set; }
        public Vector3 Position { get; internal set; }
        public Quaternion Rotation { get; internal set; }
        public Bounds WorldBounds { get; internal set; }
        public bool IsReady { get; internal set; }
        public bool IsLoading { get; internal set; }
        public bool IsUnloading { get; internal set; }
        public bool IsCurrent { get; internal set; }
        public bool IsCheckpointProtected { get; internal set; }
        public string Error { get; internal set; }
        public HashSet<string> BrokenDevices { get; } = new HashSet<string>();
        public GameObject IncomingConnection { get; internal set; }
        internal NavMeshDataInstance Navigation;
        internal FlowRoomInstance(long id, RoomVariantDefinition variant, Vector3 position, Quaternion rotation)
        { Id = id; Variant = variant; Position = position; Rotation = rotation; }
    }
}
