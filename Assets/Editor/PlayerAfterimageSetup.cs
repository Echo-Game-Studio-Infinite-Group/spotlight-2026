using System;
using System.Linq;
using DeferredOutline;
using GameJam.Actions;
using GameJam.Actions.Editor;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;

public static class PlayerAfterimageSetup
{
    public const string SettingsPath = "Assets/Settings/PlayerAfterimages.asset";
    public const string MaterialPath = "Assets/Art/Materials/PlayerAfterimage.mat";
    public const string OutlinePath = "Assets/Shaders/Outline/Presets/PlayerMobilityOutline.asset";

    [MenuItem("超高速行者/动作序列/接入蓝色机动残影")]
    public static void Install()
    {
        if (EditorApplication.isPlaying)
        {
            EditorApplication.isPlaying = false;
            EditorApplication.delayCall += Install;
            return;
        }
        Shader shader = Shader.Find("GameJam/PlayerAfterimage");
        if (shader == null) throw new InvalidOperationException("缺少 PlayerAfterimage Shader");
        Material material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (material == null)
        {
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, MaterialPath);
        }
        PlayerAfterimageSettings settings = AssetDatabase.LoadAssetAtPath<PlayerAfterimageSettings>(SettingsPath);
        if (settings == null)
        {
            settings = ScriptableObject.CreateInstance<PlayerAfterimageSettings>();
            settings.Material = material;
            AssetDatabase.CreateAsset(settings, SettingsPath);
        }
        OutlinePreset preset = AssetDatabase.LoadAssetAtPath<OutlinePreset>(OutlinePath);
        if (preset == null)
        {
            preset = ScriptableObject.CreateInstance<OutlinePreset>();
            preset.appearDuration = .035f;
            preset.disappearDuration = .1f;
            preset.motionFlameStartSpeed = 8f;
            preset.motionFlameFullSpeed = 40f;
            preset.idleExtensionHeight = .01f;
            preset.movingExtensionHeight = .025f;
            AssetDatabase.CreateAsset(preset, OutlinePath);
        }
        GameObject player = PrefabUtility.LoadPrefabContents(ActionPlayerSetup.PlayerPath);
        try
        {
            OutlineTarget target = player.GetComponent<OutlineTarget>();
            OutlineController controller = player.GetComponent<OutlineController>();
            if (target == null || controller == null) throw new InvalidOperationException("玩家缺少已有的描边组件");
            PlayerAfterimageEffect effect = player.GetComponent<PlayerAfterimageEffect>();
            if (effect == null) effect = player.AddComponent<PlayerAfterimageEffect>();
            SkinnedMeshRenderer[] sources = target.explicitRenderers.OfType<SkinnedMeshRenderer>().ToArray();
            if (sources.Length == 0) throw new InvalidOperationException("描边目标未指定人物蒙皮网格");
            effect.Configure(settings, sources);
            target.presetOverride = preset;
            target.startOn = false;
            target.loopInDemo = false;
            target.overrideOutlineAppearance = true;
            target.outlineWidthPixels = 2.2f;
            target.flameStrength = .25f;
            target.outlineJitterPixels = -.6f;
            target.innerOutlineColor = new Color(.015f, .12f, .65f, 1f);
            target.outerOutlineColor = new Color(.04f, .7f, 3.5f, .9f);
            controller.SetEnabled(false);
            bool bound = false;
            for (int i = 0; i < effect.EmissionChanged.GetPersistentEventCount(); i++)
                bound |= effect.EmissionChanged.GetPersistentTarget(i) == controller
                    && effect.EmissionChanged.GetPersistentMethodName(i) == nameof(OutlineController.SetEnabled);
            // 描边位于默认程序集，通过序列化事件接线，避免运行时程序集反向依赖。
            if (!bound) UnityEventTools.AddPersistentListener(effect.EmissionChanged, controller.SetEnabled);
            EditorUtility.SetDirty(effect);
            EditorUtility.SetDirty(target);
            EditorUtility.SetDirty(controller);
            PrefabUtility.SaveAsPrefabAsset(player, ActionPlayerSetup.PlayerPath);
        }
        finally { PrefabUtility.UnloadPrefabContents(player); }
        RemoveEvents(AssetDatabase.LoadAssetAtPath<ActionDefinition>(PlayerMobilitySetup.SprintPath));
        EnsureEvents(AssetDatabase.LoadAssetAtPath<ActionDefinition>(PlayerMobilitySetup.DashPath), false);
        EnsureEvents(AssetDatabase.LoadAssetAtPath<ActionDefinition>(PlayerMobilitySetup.HighJumpPath), true);
        AssetDatabase.SaveAssets();
        Debug.Log("[PlayerAfterimageSetup] 蓝色描边与姿态残影已接入能量冲刺、高跳和右键时停；普通冲刺关闭。");
    }

    private static void EnsureEvents(ActionDefinition action, bool ascendingOnly)
    {
        if (action == null || action.Timeline.Count == 0) throw new InvalidOperationException("缺少机动动作时间轴");
        if (!action.Timeline.Any(segment => segment.Events.Any(e => e.EventKey == PlayerAfterimageEffect.StartEventKey)))
            action.Timeline[0].Events.Add(new ActionFrameEvent
            { EventKey = PlayerAfterimageEffect.StartEventKey, Value = ascendingOnly ? 1f : 0f });
        if (!action.Timeline.Any(segment => segment.Events.Any(e => e.EventKey == PlayerAfterimageEffect.StopEventKey)))
        {
            ActionSegment end = ascendingOnly ? action.Timeline[action.Timeline.Count - 1]
                : action.Timeline.First(segment => segment.SegmentId == "recovery");
            end.Events.Add(new ActionFrameEvent
            { EventKey = PlayerAfterimageEffect.StopEventKey, Frame = ascendingOnly ? end.DurationFrames - 1 : 0 });
        }
        EditorUtility.SetDirty(action);
    }

    private static void RemoveEvents(ActionDefinition action)
    {
        if (action == null) throw new InvalidOperationException("缺少普通冲刺动作");
        foreach (ActionSegment segment in action.Timeline)
            segment.Events.RemoveAll(e => e.EventKey == PlayerAfterimageEffect.StartEventKey
                || e.EventKey == PlayerAfterimageEffect.StopEventKey);
        EditorUtility.SetDirty(action);
    }
}
