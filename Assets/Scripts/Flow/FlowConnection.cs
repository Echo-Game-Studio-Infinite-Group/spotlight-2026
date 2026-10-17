using UnityEngine;
using UnityEngine.SceneManagement;

namespace GameJam.Flow
{
    public static class FlowConnection
    {
        public static GameObject Create(FlowRoomInstance source, FlowRoomInstance target, RunFlowConfig config)
        {
            var root = new GameObject("房间连接段");
            SceneManager.MoveGameObjectToScene(root, target.Scene);
            Vector3 start = source.Root.Exit.position, end = target.Root.Entry.position;
            root.transform.SetPositionAndRotation((start + end) * .5f, source.Root.Exit.rotation);
            float width = Mathf.Min(source.Root.ExitSize.x, target.Root.EntrySize.x);
            float length = Vector3.Distance(start, end);
            GameObject floor = Box(root.transform, "连续碰撞地板", new Vector3(0, -config.FloorThickness * .5f, 0),
                new Vector3(width, config.FloorThickness, length), config.ConnectionMaterial);
            floor.AddComponent<FlowConnectionSurface>().Configure(source, target);
            for (int side = -1; side <= 1; side += 2)
                Box(root.transform, "连接段护栏", new Vector3(side * (width + config.RailThickness) * .5f, config.RailHeight * .5f, 0),
                    new Vector3(config.RailThickness, config.RailHeight, length), config.ConnectionMaterial);
            target.IncomingConnection = root;
            return root;
        }
        private static GameObject Box(Transform parent, string name, Vector3 position, Vector3 size, Material material)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name; box.transform.SetParent(parent, false); box.transform.localPosition = position; box.transform.localScale = size;
            if (material != null) box.GetComponent<MeshRenderer>().sharedMaterial = material;
            return box;
        }
    }
}
