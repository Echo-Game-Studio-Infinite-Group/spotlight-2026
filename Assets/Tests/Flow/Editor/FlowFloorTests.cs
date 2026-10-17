using System.Collections.Generic;
using GameJam.Flow;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GameJam.FlowTests
{
    public sealed class FlowFloorTests
    {
        private readonly List<Object> _objects = new List<Object>();
        [TearDown]
        public void TearDown() { foreach (Object value in _objects) Object.DestroyImmediate(value); _objects.Clear(); }
        private GameObject Box(string name, Vector3 position, Vector3 size)
        {
            GameObject value = GameObject.CreatePrimitive(PrimitiveType.Cube); value.name = name;
            value.transform.position = position; value.transform.localScale = size; _objects.Add(value); return value;
        }
        private FlowRoomTemplate Room(Vector3 position, Vector3 size)
        {
            var parent = new GameObject("room"); _objects.Add(parent);
            FlowRoomTemplate room = parent.AddComponent<FlowRoomTemplate>();
            GameObject box = Box("floor", position, size); box.transform.SetParent(parent.transform, true); return room;
        }
        private float Area(Mesh mesh, Transform transform)
        {
            _objects.Add(mesh); Vector3[] vertices = mesh.vertices; int[] triangles = mesh.triangles; float result = 0;
            for (int i = 0; i < triangles.Length; i += 3)
            {
                Vector3 a = transform.TransformPoint(vertices[triangles[i]]), b = transform.TransformPoint(vertices[triangles[i + 1]]), c = transform.TransformPoint(vertices[triangles[i + 2]]);
                result += Vector3.Cross(b - a, c - a).magnitude * .5f;
            }
            return result;
        }
        [Test]
        public void ConnectionFloorSubtractsOnlyTheCoveredVisibleArea()
        {
            GameObject floor = Box("connection", new Vector3(0, -.5f, 0), new Vector3(10, 1, 10));
            FlowRoomTemplate left = Room(new Vector3(-2.5f, -.5f, 0), new Vector3(5, 1, 10));
            Mesh mesh = FlowFloorMesh.Create(floor.transform, left);
            Assert.That(Area(mesh, floor.transform), Is.EqualTo(50).Within(.001f));
            Assert.That(floor.GetComponent<BoxCollider>().size, Is.EqualTo(Vector3.one));
        }
        [Test]
        public void OverlappingRoomsCannotLeaveDuplicateConnectionFloorFaces()
        {
            GameObject floor = Box("connection", new Vector3(0, -.5f, 0), new Vector3(10, 1, 10));
            FlowRoomTemplate left = Room(new Vector3(-2.5f, -.5f, 0), new Vector3(5, 1, 10));
            FlowRoomTemplate right = Room(new Vector3(2.5f, -.5f, 0), new Vector3(5, 1, 10));
            Assert.That(Area(FlowFloorMesh.Create(floor.transform, left, right), floor.transform), Is.Zero.Within(.001f));
            Assert.That(Area(FlowFloorMesh.Create(floor.transform, right), floor.transform), Is.EqualTo(50).Within(.001f));
        }
    }
}
