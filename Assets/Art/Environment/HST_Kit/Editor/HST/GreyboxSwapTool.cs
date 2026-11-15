using System.Linq;
using UnityEditor;
using UnityEngine;

namespace HST.EditorTools
{
    /// <summary>
    /// 灰盒替换工位：把地图配表工具搭出来的灰盒对象（ProBuilder 方块/楼梯等）按名字整批换成 kit 预制体。
    /// 契约：对象名关键字匹配 → 新预制体继承原世界位置/旋转/父级/同层位次，替换前后包围盒写 Console 供抽查。
    /// 用法：场景里选中若干灰盒 → 填关键字与替换预制体 → 点「替换选中的灰盒」。
    /// 菜单：HST/收尾批/②灰盒替换工具
    /// </summary>
    public class GreyboxSwapTool : EditorWindow
    {
        GameObject prefab;
        string matchKeyword = "Stair";
        bool keepScale = true;

        [MenuItem("HST/收尾批/②灰盒替换工具")]
        static void Open() => GetWindow<GreyboxSwapTool>("灰盒替换");

        void OnGUI()
        {
            prefab = (GameObject)EditorGUILayout.ObjectField("替换成", prefab, typeof(GameObject), false);
            matchKeyword = EditorGUILayout.TextField("对象名包含", matchKeyword);
            keepScale = EditorGUILayout.Toggle(new GUIContent("保留灰盒缩放", "灰盒常用 localScale=尺寸；楼梯预制体是标准梯段尺寸，一般不勾"), keepScale);
            if (GUILayout.Button("替换选中的灰盒", GUILayout.Height(28)))
                Swap();
            EditorGUILayout.HelpBox(
                "选中若干灰盒对象 → 填关键字 → 点替换。\n" +
                "新对象继承原世界位置/旋转/父级，名字 = 预制体名_序号；替换前后包围盒写 Console 抽查。\n" +
                "楼梯批口径：count=12 rise=0.1667 run=0.28 width=3.0，pivot=首踏前缘中点 z=0，上升方向 Unity -X。",
                MessageType.Info);
        }

        void Swap()
        {
            if (prefab == null) { Debug.LogError("[灰盒替换] 未指定替换预制体"); return; }
            var targets = Selection.gameObjects.Where(o => o.name.Contains(matchKeyword)).ToArray();
            if (targets.Length == 0)
            {
                Debug.LogWarning("[灰盒替换] 选中对象里没有名字含「" + matchKeyword + "」的");
                return;
            }
            int n = 0;
            foreach (var t in targets)
            {
                var parent = t.transform.parent;
                var pos = t.transform.position;
                var rot = t.transform.rotation;
                var scl = t.transform.localScale;
                var idx = t.transform.GetSiblingIndex();
                var srcBounds = BoundsOf(t);

                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
                Undo.RegisterCreatedObjectUndo(go, "灰盒替换");
                go.transform.SetSiblingIndex(idx);
                go.transform.position = pos;
                go.transform.rotation = rot;
                go.transform.localScale = keepScale ? scl : Vector3.one;
                go.name = prefab.name + "_" + n.ToString("00");
                Undo.DestroyObjectImmediate(t);

                var dstBounds = BoundsOf(go);
                Debug.Log("[灰盒替换] " + go.name + " 灰盒尺寸 " + srcBounds.size + " → kit 尺寸 " + dstBounds.size);
                n++;
            }
            Debug.Log("[灰盒替换] 完成 " + n + " 处");
            AssetDatabase.SaveAssets();
        }

        static Bounds BoundsOf(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return new Bounds(go.transform.position, Vector3.zero);
            var b = rs[0].bounds;
            foreach (var r in rs.Skip(1)) b.Encapsulate(r.bounds);
            return b;
        }
    }
}
