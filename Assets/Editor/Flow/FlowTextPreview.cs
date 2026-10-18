using System.Collections.Generic;
using System.Linq;
using GameJam.Flow;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace GameJam.FlowEditor
{
    [InitializeOnLoad]
    public static class FlowTextPreview
    {
        private static readonly List<GameObject> Copies = new List<GameObject>();
        private static readonly List<GameObject> Hidden = new List<GameObject>();
        private static double _lastTime;
        static FlowTextPreview()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
            EditorApplication.playModeStateChanged += _ => Stop();
            PrefabStage.prefabStageClosing += _ => Stop();
            PrefabStage.prefabSaving += _ => Stop();
            EditorSceneManager.sceneSaving += (_, __) => Stop();
        }

        public static void Start(RoomVariantDefinition variant, bool animate)
        {
            Stop();
            FlowRoomTemplate template = FlowEnemySpawnAuthoring.OpenTemplate(variant);
            if (template == null) return;
            foreach (FlowTextPoint point in FlowTextAuthoring.Points(template))
            {
                FlowTextBinding binding = (variant.Texts ?? new FlowTextBinding[0]).FirstOrDefault(item => item != null && item.Enabled && item.PointId == point.Id);
                if (binding == null || point.Visual == null) continue;
                bool included = true;
                foreach (FlowContentGroup group in point.GetComponentsInParent<FlowContentGroup>(true))
                    if (!(variant.ContentGroups ?? new string[0]).Contains(group.Id)) included = false;
                if (!included) continue;
                var copy = Object.Instantiate(point.gameObject);
                copy.hideFlags = HideFlags.HideAndDontSave;
                SceneManager.MoveGameObjectToScene(copy, template.gameObject.scene);
                copy.transform.SetPositionAndRotation(point.transform.position, point.transform.rotation);
                copy.transform.localScale = point.transform.lossyScale; copy.SetActive(true); Copies.Add(copy);
                FlowTextPoint preview = copy.GetComponent<FlowTextPoint>(); preview.Visual.Configure(binding);
                if (animate) preview.Visual.Play(binding.Appearance ?? point.DefaultAppearance, binding.Disappearance ?? point.DefaultDisappearance,
                    binding.HoldSeconds, binding.Display == FlowTextDisplay.Persistent, binding.AppearanceSpeed, binding.DisappearanceSpeed);
                else preview.Visual.ShowFinal(binding.Appearance ?? point.DefaultAppearance);
                if (!SceneVisibilityManager.instance.IsHidden(point.gameObject))
                { SceneVisibilityManager.instance.Hide(point.gameObject, true); Hidden.Add(point.gameObject); }
            }
            _lastTime = EditorApplication.timeSinceStartup; EditorApplication.update += Tick; SceneView.RepaintAll();
        }

        private static void Tick()
        {
            double now = EditorApplication.timeSinceStartup; float delta = (float)(now - _lastTime); _lastTime = now;
            foreach (GameObject copy in Copies) if (copy != null) copy.GetComponent<FlowTextPoint>().Visual.Advance(delta);
            SceneView.RepaintAll();
        }

        public static void Stop()
        {
            EditorApplication.update -= Tick;
            foreach (GameObject copy in Copies) if (copy != null) Object.DestroyImmediate(copy);
            Copies.Clear();
            foreach (GameObject original in Hidden) if (original != null) SceneVisibilityManager.instance.Show(original, true);
            Hidden.Clear(); SceneView.RepaintAll();
        }
    }
}
