// 生成件管理小工具：按名字前缀删除/点名删除 MAPSKIN_* 生成件。
//
// 为什么单独一个工具：`BuildingEnhance.ClearAll` 是"全清"（弧角/幕墙/线脚/支撑一起删），
// 而实际迭代里经常要"只扔掉某一类"（例：用户裁定玻璃幕墙不要了，但弧角墙与线脚要留）。
// 本工具只删指定前缀，且**只认 MAPSKIN_ 开头的对象**——防手滑把地编原物件删掉。
//
// 菜单：MapSkin/工具/① 按前缀删除生成件(玻璃幕墙)  ② 列出现有生成件
// 批处理：MapSkin.MapSkinUtil.RemoveGlazing / MapSkin.MapSkinUtil.ListGenerated
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MapSkin
{
    public static class MapSkinUtil
    {
        const string ScenePath = "Assets/Scenes/Level1.unity";

        /// <summary>确保 Level1 是当前场景。
        /// 踩过：batchmode 下 Unity 默认打开的是空场景，不先打开 Level1 就会"扫到 0 个对象"
        /// —— 按前缀删生成件命中 0 件、列生成件 0 组，而磁盘上的场景其实是有生成件的
        /// （同一轮出图读数与上一轮逐位相同，就是幕墙根本没被删的证据）。</summary>
        static void EnsureScene()
        {
            var cur = EditorSceneManager.GetActiveScene();
            if (cur.IsValid() && cur.path == ScenePath) return;
            if (cur.IsValid() && cur.isDirty && !string.IsNullOrEmpty(cur.path))
                EditorSceneManager.SaveScene(cur);       // 未命名的空场景保存会抛异常，故要求 path 非空
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Debug.Log($"[MapSkin·工具] 已打开场景 {ScenePath}");
        }

        /// <summary>按名字前缀删除生成件（仅限 MAPSKIN_ 前缀，防误删）。返回删除数量。</summary>
        public static int RemoveByPrefix(string prefix, bool save = true)
        {
            EnsureScene();
            var doomed = new List<GameObject>();
            var names = new List<string>();          // 先收名字：DestroyImmediate 之后再读 .name 会抛 MissingReferenceException
            foreach (var t in Object.FindObjectsOfType<Transform>(true))
            {
                if (t == null) continue;
                var n = t.name;
                if (!n.StartsWith("MAPSKIN_")) continue;          // 只动生成件
                if (!n.StartsWith(prefix)) continue;
                names.Add(n);
                doomed.Add(t.gameObject);
            }
            foreach (var go in doomed) Object.DestroyImmediate(go);
            var rep = new StringBuilder();
            rep.AppendLine($"[MapSkin·工具] 按前缀删除 '{prefix}'：命中 {doomed.Count} 件");
            foreach (var n in names) rep.AppendLine($"    - {n}");
            Debug.Log(rep.ToString());
            if (save && doomed.Count > 0) SaveScene();
            return doomed.Count;
        }

        /// <summary>命令行按前缀删除：-executeMethod MapSkin.MapSkinUtil.RemovePrefixCli -prefix MAPSKIN_Trim</summary>
        public static void RemovePrefixCli()
        {
            string prefix = null;
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
                if (args[i] == "-prefix" && i + 1 < args.Length) prefix = args[i + 1];
            if (string.IsNullOrEmpty(prefix)) { Debug.LogError("[MapSkin·工具] 缺少 -prefix"); return; }
            RemoveByPrefix(prefix);
        }

        /// <summary>列出场景里全部 MAPSKIN_* 生成件（按名字分组计数），用于交接与对账。</summary>
        public static void ListGenerated()
        {
            EnsureScene();
            var groups = new Dictionary<string, int>();
            foreach (var t in Object.FindObjectsOfType<Transform>(true))
            {
                if (t == null || !t.name.StartsWith("MAPSKIN_")) continue;
                // 名字形如 MAPSKIN_Arc_12 / MAPSKIN_Glazing_E_Glass → 取前两段做分组
                var parts = t.name.Split('_');
                var key = parts.Length >= 2 ? parts[0] + "_" + parts[1] : t.name;
                groups[key] = groups.TryGetValue(key, out var c) ? c + 1 : 1;
            }
            var rep = new StringBuilder();
            rep.AppendLine($"[MapSkin·工具] 场景内 MAPSKIN_* 生成件分组共 {groups.Count} 组：");
            foreach (var kv in groups) rep.AppendLine($"    {kv.Key,-32} {kv.Value,5} 件");
            Debug.Log(rep.ToString());
        }

        static void SaveScene()
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                Debug.Log($"[MapSkin·工具] 场景已存盘：{scene.path}");
            }
        }

        [MenuItem("MapSkin/工具/① 删除玻璃幕墙(MAPSKIN_Glazing_*)")]
        public static void RemoveGlazingMenu() => RemoveGlazing();

        public static void RemoveGlazing() => RemoveByPrefix("MAPSKIN_Glazing");

        [MenuItem("MapSkin/工具/② 列出现有生成件")]
        public static void ListGeneratedMenu() => ListGenerated();
    }
}
