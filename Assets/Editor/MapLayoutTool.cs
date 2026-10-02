using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// 地图布局工具：按地面尺寸自动重排「围墙 Boundary」与「环形护栏 CircleRail」
//
// 为什么需要它：地面尺寸一改，围墙和环道就会跟地面脱节（墙站在地面里侧、护栏跑出边界）。
// 手调 96 段护栏不现实，所以把几何换算全部集中到这里，改完尺寸点一下即可。
//
// 几何约定：
//   Ground 是内置 Plane 图元（基准 10×10 米），所以半宽 = 10 × scale / 2
//   围墙中心线 = 地面半径 - 内缩量（默认 0.5，正好让墙外表面与地面边缘齐平）
//   环道外护栏 = 围墙内表面 - 与围墙的间距；环道中心线 = 外护栏 - 环道半宽
//   环道形状跟随地面长宽比：正方形地面得到正圆，长方形地面得到椭圆
//
// 全部构件都是 Cube 图元，所以 localScale 直接等于世界尺寸
public class MapLayoutTool : EditorWindow
{
    private const string PrefabPath = "Assets/Prefabs/Maps/MapTestField.prefab";
    private const string GroundName = "Ground";
    private const string BoundaryName = "Boundary";
    private const string CircleRailName = "CircleRail";

    // Unity 内置 Plane 图元的基准边长
    private const float PlanePrimitiveSize = 10f;

    private const float DefaultWallThickness = 1f;
    private const float DefaultWallHeight = 6f;
    private const float DefaultGroundInset = 0.5f;
    private const float DefaultBoundaryMargin = 3f;
    private const float DefaultTrackHalfWidth = 3f;
    private const float DefaultRailThickness = 0.4f;
    private const float DefaultRailHeight = 3f;
    private const int DefaultSegments = 48;

    [SerializeField] private float _wallThickness = DefaultWallThickness;
    [SerializeField] private float _wallHeight = DefaultWallHeight;
    [SerializeField] private float _groundInset = DefaultGroundInset;
    [SerializeField] private float _boundaryMargin = DefaultBoundaryMargin;
    [SerializeField] private float _trackHalfWidth = DefaultTrackHalfWidth;
    [SerializeField] private float _railThickness = DefaultRailThickness;
    [SerializeField] private float _railHeight = DefaultRailHeight;
    [SerializeField] private int _segments = DefaultSegments;

    // 缓存地面尺寸：LoadPrefabContents 会整个加载预制体，放在 OnGUI 里每帧跑会把编辑器拖卡
    private Vector2 _groundSize;
    private bool _groundSizeValid;

<<<<<<< HEAD
    [MenuItem("超高速行者/地图布局工具")]
=======
    [MenuItem("超高速行者/地图布局工具（围墙 + 环道）")]
>>>>>>> 2ed39e1246f8ab447ed849ace5923eb4bb348f2b
    public static void Open()
    {
        MapLayoutTool window = GetWindow<MapLayoutTool>("地图布局工具");
        window.minSize = new Vector2(380f, 440f);
    }

    private void OnEnable()
    {
        RefreshGroundSize();
    }

    private void RefreshGroundSize()
    {
        _groundSize = ReadGroundSize();
        _groundSizeValid = true;
    }

    private void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "按 Ground 的实际尺寸重排围墙与环形护栏。\n" +
            "地面尺寸改了就点一次「应用到预制体」，不用手调 96 段护栏。",
            MessageType.Info);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("当前地面", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("  实际尺寸", (_groundSizeValid ? _groundSize.x + " × " + _groundSize.y + " 米" : "读取失败"));
        if (GUILayout.Button("刷新", GUILayout.Width(60f)))
        {
            RefreshGroundSize();
        }

        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("围墙 Boundary", EditorStyles.boldLabel);
        _wallThickness = EditorGUILayout.FloatField("  墙厚", Mathf.Max(0.1f, _wallThickness));
        _wallHeight = EditorGUILayout.FloatField("  墙高", Mathf.Max(0.1f, _wallHeight));
        _groundInset = EditorGUILayout.FloatField("  相对地面边缘内缩", Mathf.Max(0f, _groundInset));
        EditorGUILayout.LabelField("  ", "外表面与地面边缘齐平时内缩 = 墙厚的一半 = " + (_wallThickness * 0.5f));

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("环道 CircleRail", EditorStyles.boldLabel);
        _boundaryMargin = EditorGUILayout.FloatField("  离围墙的间距", Mathf.Max(0f, _boundaryMargin));
        _trackHalfWidth = EditorGUILayout.FloatField("  环道半宽", Mathf.Max(0.1f, _trackHalfWidth));
        _railThickness = EditorGUILayout.FloatField("  护栏厚度", Mathf.Max(0.05f, _railThickness));
        _railHeight = EditorGUILayout.FloatField("  护栏高度", Mathf.Max(0.1f, _railHeight));
        _segments = EditorGUILayout.IntSlider("  分段数", _segments, 8, 128);

        EditorGUILayout.Space();
        DrawPreview(_groundSize);

        EditorGUILayout.Space();
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("应用到预制体", GUILayout.Height(28f)))
        {
            Apply(PrefabPath, _wallThickness, _wallHeight, _groundInset,
                _boundaryMargin, _trackHalfWidth, _railThickness, _railHeight, _segments);
        }

        if (GUILayout.Button("恢复默认值", GUILayout.Height(28f)))
        {
            _wallThickness = DefaultWallThickness;
            _wallHeight = DefaultWallHeight;
            _groundInset = DefaultGroundInset;
            _boundaryMargin = DefaultBoundaryMargin;
            _trackHalfWidth = DefaultTrackHalfWidth;
            _railThickness = DefaultRailThickness;
            _railHeight = DefaultRailHeight;
            _segments = DefaultSegments;
        }

        EditorGUILayout.EndHorizontal();
    }

    // 把换算结果显示出来，避免"点完不知道会变成什么样"
    private void DrawPreview(Vector2 groundSize)
    {
        float halfX = groundSize.x * 0.5f;
        float halfZ = groundSize.y * 0.5f;
        float wallCenterX = halfX - _groundInset;
        float wallCenterZ = halfZ - _groundInset;
        float usableX = wallCenterX - _wallThickness * 0.5f - _boundaryMargin;
        float usableZ = wallCenterZ - _wallThickness * 0.5f - _boundaryMargin;
        float centerA = usableX - _trackHalfWidth;
        float centerB = usableZ - _trackHalfWidth;

        EditorGUILayout.LabelField("换算预览", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("  围墙中心线", wallCenterX * 2f + " × " + wallCenterZ * 2f);
        if (centerA <= 0f || centerB <= 0f)
        {
            EditorGUILayout.HelpBox("间距/半宽加起来超过了地面半径，环道放不下。请调小边距或换大地面。", MessageType.Error);
            return;
        }

        EditorGUILayout.LabelField("  环道中心线（长轴 × 短轴）", centerA * 2f + " × " + centerB * 2f);
        EditorGUILayout.LabelField("  外护栏到中心距离", usableX + " × " + usableZ);
    }

    // 从磁盘上的预制体读地面实际尺寸（不依赖是否打开着预制体）
    private static Vector2 ReadGroundSize()
    {
        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            Transform ground = FindDeep(root.transform, GroundName);
            if (ground == null)
            {
                return Vector2.zero;
            }

            return new Vector2(ground.localScale.x * PlanePrimitiveSize, ground.localScale.z * PlanePrimitiveSize);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    // 批次模式入口：用默认参数跑一次
    public static void ApplyDefaults()
    {
        Apply(PrefabPath, DefaultWallThickness, DefaultWallHeight, DefaultGroundInset,
            DefaultBoundaryMargin, DefaultTrackHalfWidth, DefaultRailThickness, DefaultRailHeight, DefaultSegments);
    }

    public static void Apply(
        string prefabPath,
        float wallThickness,
        float wallHeight,
        float groundInset,
        float boundaryMargin,
        float trackHalfWidth,
        float railThickness,
        float railHeight,
        int segments)
    {
        GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
        try
        {
            Transform ground = FindDeep(root.transform, GroundName);
            if (ground == null)
            {
                Debug.LogError("[MapLayoutTool] 预制体里找不到 " + GroundName + "，已中止");
                return;
            }

            // 地面是 Plane 图元，基准 10×10，乘以 scale 才是世界尺寸
            float halfX = ground.localScale.x * PlanePrimitiveSize * 0.5f;
            float halfZ = ground.localScale.z * PlanePrimitiveSize * 0.5f;

            Transform boundary = FindDeep(root.transform, BoundaryName);
            if (boundary == null)
            {
                Debug.LogError("[MapLayoutTool] 预制体里找不到 " + BoundaryName + "，已中止");
                return;
            }

            float wallCenterX = halfX - groundInset;
            float wallCenterZ = halfZ - groundInset;
            LayoutBoundary(boundary, wallCenterX, wallCenterZ, wallThickness, wallHeight);

            Transform circleRail = FindDeep(root.transform, CircleRailName);
            if (circleRail == null)
            {
                Debug.LogError("[MapLayoutTool] 预制体里找不到 " + CircleRailName + "，已中止");
                return;
            }

            float usableX = wallCenterX - wallThickness * 0.5f - boundaryMargin;
            float usableZ = wallCenterZ - wallThickness * 0.5f - boundaryMargin;
            float centerA = usableX - trackHalfWidth;
            float centerB = usableZ - trackHalfWidth;
            if (centerA <= 0.1f || centerB <= 0.1f)
            {
                Debug.LogError("[MapLayoutTool] 边距 + 环道半宽超过了可用半径（可用 " + usableX + " × " + usableZ
                    + "），环道放不下。请调小边距或加大地面。");
                return;
            }

            LayoutRing(circleRail, centerA, centerB, trackHalfWidth, railThickness, railHeight, segments);

            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            Debug.Log("[MapLayoutTool] 已更新 " + prefabPath
                + " ｜ 地面 " + (halfX * 2f) + "×" + (halfZ * 2f)
                + " ｜ 围墙中心线 ±" + wallCenterX + "/±" + wallCenterZ
                + " ｜ 环道中心线 " + (centerA * 2f) + "×" + (centerB * 2f)
                + " ｜ " + segments + " 段 × 2 条护栏");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    // 四面围墙：N/S 横跨整个宽度，E/W 补上长度，内表面围成闭合矩形且四角无缝
    private static void LayoutBoundary(Transform boundary, float centerX, float centerZ,
        float thickness, float height)
    {
        float spanX = centerX * 2f + thickness;
        float spanZ = centerZ * 2f + thickness;
        float y = height * 0.5f;

        SetBox(GetOrCreate(boundary, "Perimeter_N"), new Vector3(0f, y, centerZ), new Vector3(spanX, height, thickness));
        SetBox(GetOrCreate(boundary, "Perimeter_S"), new Vector3(0f, y, -centerZ), new Vector3(spanX, height, thickness));
        SetBox(GetOrCreate(boundary, "Perimeter_E"), new Vector3(centerX, y, 0f), new Vector3(thickness, height, spanZ));
        SetBox(GetOrCreate(boundary, "Perimeter_W"), new Vector3(-centerX, y, 0f), new Vector3(thickness, height, spanZ));
    }

    // 环道：内外两条护栏沿椭圆排布，每条分段都是贴着弧线的一段直杆
    private static void LayoutRing(Transform circleRail, float a, float b, float halfWidth,
        float thickness, float height, int segments)
    {
        List<Transform> existing = new List<Transform>();
        foreach (Transform child in circleRail)
        {
            existing.Add(child);
        }

        if (existing.Count == 0)
        {
            Debug.LogError("[MapLayoutTool] " + CircleRailName + " 下没有任何护栏，无法克隆出模板，已跳过环道");
            return;
        }

        Transform template = existing[0];
        int required = segments * 2;

        // 克隆补齐到需要的数量（克隆保留网格/材质/碰撞体）
        while (existing.Count < required)
        {
            Transform clone = Instantiate(template, circleRail);
            clone.name = template.name;
            existing.Add(clone);
        }

        // 多出来的藏起来而不是删掉：参数调小后可复用，避免反复克隆产生垃圾
        for (int i = required; i < existing.Count; i++)
        {
            existing[i].gameObject.SetActive(false);
        }

        float aOuter = a + halfWidth;
        float bOuter = b + halfWidth;
        float aInner = Mathf.Max(0.1f, a - halfWidth);
        float bInner = Mathf.Max(0.1f, b - halfWidth);
        float y = height * 0.5f;

        for (int i = 0; i < segments; i++)
        {
            float t0 = (float)i / segments * Mathf.PI * 2f;
            float t1 = (float)(i + 1) / segments * Mathf.PI * 2f;

            PlaceSegment(existing[i], aOuter, bOuter, t0, t1, thickness, height, y);
            PlaceSegment(existing[segments + i], aInner, bInner, t0, t1, thickness, height, y);
        }
    }

    private static void PlaceSegment(Transform rail, float a, float b, float t0, float t1,
        float thickness, float height, float y)
    {
        rail.gameObject.SetActive(true);

        Vector3 p0 = new Vector3(a * Mathf.Cos(t0), y, b * Mathf.Sin(t0));
        Vector3 p1 = new Vector3(a * Mathf.Cos(t1), y, b * Mathf.Sin(t1));

        // 段长取弦长并留 6% 重叠：椭圆上相邻两点的切线不连续，不重叠会在拐角露缝
        float length = Vector3.Distance(p0, p1) * 1.06f;

        Vector3 tangent = p1 - p0;
        rail.localPosition = (p0 + p1) * 0.5f;
        rail.localRotation = Quaternion.LookRotation(tangent.normalized, Vector3.up);
        rail.localScale = new Vector3(thickness, height, length);
    }

    private static void SetBox(Transform target, Vector3 position, Vector3 size)
    {
        target.localPosition = position;
        target.localRotation = Quaternion.identity;
        target.localScale = size;
    }

    // 找不到就克隆第一个子物体当模板（保留材质），没有子物体才新建 Cube
    private static Transform GetOrCreate(Transform parent, string name)
    {
        Transform found = FindDirectChild(parent, name);
        if (found != null)
        {
            return found;
        }

        if (parent.childCount > 0)
        {
            Transform clone = Instantiate(parent.GetChild(0), parent);
            clone.name = name;
            return clone;
        }

        GameObject created = GameObject.CreatePrimitive(PrimitiveType.Cube);
        created.name = name;
        created.transform.SetParent(parent, false);
        return created.transform;
    }

    private static Transform FindDirectChild(Transform parent, string name)
    {
        foreach (Transform child in parent)
        {
            if (child.name == name)
            {
                return child;
            }
        }

        return null;
    }

    private static Transform FindDeep(Transform root, string name)
    {
        if (root.name == name)
        {
            return root;
        }

        foreach (Transform child in root)
        {
            Transform found = FindDeep(child, name);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }
}
