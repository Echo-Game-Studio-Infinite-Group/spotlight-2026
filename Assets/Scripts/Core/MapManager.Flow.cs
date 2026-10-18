using System;
using System.Collections;
using System.Collections.Generic;
using GameJam.Flow;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

public sealed partial class MapManager
{
    private readonly List<FlowRoomInstance> _flowRooms = new List<FlowRoomInstance>();
    private long _nextFlowId;
    public IReadOnlyList<FlowRoomInstance> FlowRooms => _flowRooms;

    public FlowRoomInstance PreloadFlowRoom(RoomVariantDefinition variant, Pose pose, RunFlowController owner)
    {
        if (variant == null || variant.Room == null || variant.Room.Template == null) return null;
        var room = new FlowRoomInstance(++_nextFlowId, variant, pose.position, pose.rotation) { IsLoading = true };
        room.Scene = SceneManager.CreateScene("FlowRoom_" + room.Id + "_" + variant.Room.Id);
        _flowRooms.Add(room);
        StartCoroutine(LoadFlowRoom(room, owner));
        return room;
    }

    private IEnumerator LoadFlowRoom(FlowRoomInstance room, RunFlowController owner)
    {
        // 分帧预备实例，模板在失活容器下装配，敌人和触发器不能在门位就绪前启动。
        yield return null;
        if (owner == null || !room.Scene.IsValid())
        { room.IsLoading = false; TryUnloadFlowRoom(room); yield break; }
        try { BuildFlowRoot(room, owner); }
        catch (Exception exception) { room.Error = exception.Message; LastError = room.Error; }
        room.IsLoading = false;
    }

    private void BuildFlowRoot(FlowRoomInstance room, RunFlowController owner)
    {
        var staging = new GameObject("装配暂存");
        staging.SetActive(false);
        SceneManager.MoveGameObjectToScene(staging, room.Scene);
        try
        {
            FlowRoomTemplate root = Instantiate(room.Variant.Room.Template, staging.transform, false);
            root.gameObject.SetActive(false);
            root.transform.SetParent(null, false);
            root.transform.SetPositionAndRotation(room.Position, room.Rotation);
            room.Root = root;
            root.name = room.Variant.Room.Id + " #" + room.Id;
            root.Bind(owner, room);
            room.WorldBounds = FlowPlacement.TransformBounds(root.GeometryBounds, room.Position, room.Rotation);
            root.gameObject.SetActive(true);
            room.Navigation = root.RegisterNavigation();
            if (root.Navigation != null && !room.Navigation.valid) throw new InvalidOperationException("房间导航数据注册失败 " + room.Variant.Room.Id);
            room.IsReady = true;
        }
        finally { Destroy(staging); }
    }

    public bool TryActivateFlowRoom(FlowRoomInstance room)
    {
        if (room == null || !room.IsReady || room.IsUnloading || !room.Scene.IsValid()) return false;
        foreach (FlowRoomInstance prior in _flowRooms)
        {
            prior.IsCurrent = false;
            if (prior.Root != null) prior.Root.SetCurrent(false);
        }
        SceneManager.SetActiveScene(room.Scene);
        room.IsCurrent = true;
        room.Root.SetCurrent(true);
        CurrentRoomSceneName = room.Scene.name;
        return true;
    }

    public bool TryUnloadFlowRoom(FlowRoomInstance room)
    {
        if (room == null || room.IsCurrent || room.IsCheckpointProtected || room.IsLoading || room.IsUnloading || !_flowRooms.Contains(room)) return false;
        room.IsUnloading = true;
        StartCoroutine(UnloadFlowRoom(room));
        return true;
    }

    private IEnumerator UnloadFlowRoom(FlowRoomInstance room)
    {
        room.IsReady = false;
        room.Root?.Enemies?.Clear();
        if (room.Navigation.valid) room.Navigation.Remove();
        if (room.Root != null) room.Root.gameObject.SetActive(false);
        if (room.Scene.IsValid() && room.Scene.isLoaded)
        {
            AsyncOperation unload = SceneManager.UnloadSceneAsync(room.Scene);
            if (unload != null) yield return unload;
        }
        _flowRooms.Remove(room);
        room.IsUnloading = false;
    }

    public IEnumerator RebuildFlowRoom(FlowRoomInstance room, RoomVariantDefinition variant, RunFlowController owner)
    {
        if (room == null || room.IsLoading || room.IsUnloading || variant == null || variant.Room != room.Variant.Room) yield break;
        FlowRoomTemplate prior = room.Root;
        RoomVariantDefinition priorVariant = room.Variant;
        GameObject[] priorObjects = room.Scene.GetRootGameObjects();
        var priorActive = new bool[priorObjects.Length];
        room.IsReady = false; room.IsLoading = true; room.Error = string.Empty;
        // 池中借出的敌人、弹体和碎块可能是场景根对象，重建不能只销毁模板层级。
        for (int i = 0; i < priorObjects.Length; i++)
        {
            priorActive[i] = priorObjects[i].activeSelf;
            if (priorObjects[i] != room.IncomingConnection) priorObjects[i].SetActive(false);
        }
        if (room.Navigation.valid) room.Navigation.Remove();
        yield return null;
        try
        {
            room.Variant = variant;
            BuildFlowRoot(room, owner);
            room.Root.SetCurrent(room.IsCurrent);
            prior.Enemies.Clear();
            foreach (GameObject item in priorObjects) if (item != room.IncomingConnection) Destroy(item);
        }
        catch (Exception exception)
        {
            if (room.Root != null && room.Root != prior) Destroy(room.Root.gameObject);
            room.Root = prior; room.Variant = priorVariant;
            for (int i = 0; i < priorObjects.Length; i++) if (priorObjects[i] != null) priorObjects[i].SetActive(priorActive[i]);
            room.Navigation = prior.RegisterNavigation();
            prior.SetCurrent(room.IsCurrent);
            room.IsReady = true;
            room.Error = exception.Message;
        }
        room.IsLoading = false;
    }

    public void ReleaseDetachedFlowRooms(FlowRoomInstance[] rooms) => StartCoroutine(ReleaseDetachedRooms(rooms));
    private IEnumerator ReleaseDetachedRooms(FlowRoomInstance[] rooms)
    {
        foreach (FlowRoomInstance room in rooms)
        {
            room.IsCurrent = false; room.IsCheckpointProtected = false;
            while (room.IsLoading) yield return null;
            TryUnloadFlowRoom(room);
        }
    }
}
