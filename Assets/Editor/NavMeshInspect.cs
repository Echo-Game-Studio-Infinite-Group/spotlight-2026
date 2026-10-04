using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

// 导航网格体检（只读）：量「物理坡面边缘」到「NavMesh 边界」差多少，判断烘焙侵蚀是否对称、
// 坡道与地面/平台是否连通、场景里的敌人有没有站在网格外
// 约束（AGENTS.md 目录约定）：放在 Assets/Editor 下，只进编辑器、不进玩家包
// 约束（血泪教训）：本工具绝不修改场景、绝不保存任何资源——保存场景曾把工程写坏过一次
// 为什么同时写文件：MCP 桥在进 Play / 域重载后会断，写文件让 AI 侧不依赖桥也能拿到结果
public static class NavMeshInspect
{
    // 报告落点：放在 AI 能直接读的工作区里（换工作区时改这一行）
    private const string ReportPath =
        @"C:\Users\10093\Documents\deepseek-harness\default-workspace\unity-live\navmesh-report.txt";

    private const float MinSlopeDegrees = 15f;     // 低于这个角度不算"坡"，是地板
    private const float MaxSlopeDegrees = 40f;     // 高于这个角度不是给人走的坡（例如斜板背面）
    private const int LineSamples = 21;            // 沿坡长取多少条横剖面
    private const int LateralSteps = 40;           // 每条横剖面从中心向两侧各探多少步（走 NavMesh.Raycast，不靠步进猜）
    private const float ProbeHeight = 5f;          // 从坡面上方多高往下打射线（判断这一段是不是被埋/被挡）
    private const float CapsuleRadius = 0.5f;      // 敌人胶囊半径：网格边界离物理边缘超过它就会"人还在坡上、脚已在网格外"
    private const float MinWalkableWidth = 1.2f;   // 低于这个可走宽度就是独木桥（agent 直径 1m + 余量）

    [MenuItem("超高速行者/导航网格体检")]
    public static void Run()
    {
        StringBuilder report = new StringBuilder();
        report.AppendLine($"# 导航网格体检  {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"# 场景：{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}  " +
                          $"Play 中：{Application.isPlaying}");
        report.AppendLine();

        List<BoxCollider> ramps = FindRampFaces(report);
        report.AppendLine($"发现候选坡面：{ramps.Count} 个（法线与水平夹角在 {MinSlopeDegrees}~{MaxSlopeDegrees}° 之间的盒子面）");
        report.AppendLine();

        foreach (BoxCollider ramp in ramps) InspectRamp(ramp, report);

        InspectEnemySpawns(report);

        Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
        File.WriteAllText(ReportPath, report.ToString(), new UTF8Encoding(false));

        // Console 里只打摘要，完整表格看文件（Console 里太长会被折叠）
        Debug.Log($"[NavMeshInspect] 体检完成，完整报告：{ReportPath}");
        foreach (string line in report.ToString().Split('\n'))
        {
            if (line.StartsWith("## ") || line.StartsWith("结论") || line.Contains("⚠")) Debug.Log("[NavMeshInspect] " + line.TrimEnd());
        }
    }

    // —— 找出所有"能走的斜面" —— //
    private static List<BoxCollider> FindRampFaces(StringBuilder report)
    {
        List<BoxCollider> result = new List<BoxCollider>();

        foreach (BoxCollider box in Object.FindObjectsOfType<BoxCollider>())
        {
            if (box == null || !box.enabled) continue;
            if (GetTopFace(box, out _, out _, out _, out float slope) && slope >= MinSlopeDegrees && slope <= MaxSlopeDegrees)
            {
                result.Add(box);
                report.AppendLine($"  - {GetPath(box.transform)}：坡角 {slope:F2}°");
            }
        }

        return result;
    }

    // 取盒子的"最朝上的那个面"：返回面中心、面法线、面内两条边向量（非等比缩放下是平行四边形，照样精确）
    private static bool GetTopFace(BoxCollider box, out Vector3 center, out Vector3 normal,
        out Vector3[] edges, out float slopeDegrees)
    {
        center = Vector3.zero;
        normal = Vector3.up;
        edges = new Vector3[2];
        slopeDegrees = 0f;

        Transform t = box.transform;
        Vector3[] axes = { t.right, t.up, t.forward };
        Vector3 size = box.size;

        int bestAxis = -1;
        float bestUp = -1f;
        for (int i = 0; i < 3; i++)
        {
            float up = Mathf.Abs(Vector3.Dot(axes[i], Vector3.up));
            if (up <= bestUp) continue;
            bestUp = up;
            bestAxis = i;
        }

        if (bestAxis < 0) return false;

        // 面的外法线方向取与世界上方同号的那一侧
        float sign = Vector3.Dot(axes[bestAxis], Vector3.up) >= 0f ? 1f : -1f;
        Vector3 worldNormal = (axes[bestAxis] * sign).normalized;

        // 面中心 = 盒子中心 + 沿该轴走半个尺寸（size 是局部空间，用 TransformVector 带上缩放）
        Vector3 localOffset = Vector3.zero;
        localOffset[bestAxis] = size[bestAxis] * 0.5f * sign;
        center = t.TransformPoint(box.center + localOffset);

        // 面内两条边向量（退化时返回 false）
        int e1 = (bestAxis + 1) % 3;
        int e2 = (bestAxis + 2) % 3;
        edges[0] = t.TransformVector(GetAxisVector(e1) * size[e1]);
        edges[1] = t.TransformVector(GetAxisVector(e2) * size[e2]);
        if (edges[0].sqrMagnitude < 1e-8f || edges[1].sqrMagnitude < 1e-8f) return false;

        normal = worldNormal;
        slopeDegrees = Vector3.Angle(worldNormal, Vector3.up);
        return true;
    }

    private static Vector3 GetAxisVector(int axis)
    {
        return axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward;
    }

    // —— 单个坡面的横剖面体检 —— //
    private static void InspectRamp(BoxCollider ramp, StringBuilder report)
    {
        if (!GetTopFace(ramp, out Vector3 faceCenter, out Vector3 normal, out Vector3[] edges, out float slope)) return;

        // 哪条边是"沿坡长"，哪条是"左右两侧"：与世界竖直方向夹角大的那条是沿坡长
        bool firstIsSlope = Mathf.Abs(Vector3.Dot(edges[0].normalized, Vector3.up)) >
                            Mathf.Abs(Vector3.Dot(edges[1].normalized, Vector3.up));
        Vector3 slopeEdge = firstIsSlope ? edges[0] : edges[1];
        Vector3 lateralEdge = firstIsSlope ? edges[1] : edges[0];

        Vector3 slopeDir = slopeEdge.normalized;
        Vector3 lateralDir = lateralEdge.normalized;
        float halfLength = slopeEdge.magnitude * 0.5f;
        float halfWidth = lateralEdge.magnitude * 0.5f;

        report.AppendLine($"## 坡面：{GetPath(ramp.transform)}");
        report.AppendLine($"   坡角 {slope:F2}°，沿坡长 {halfLength * 2f:F2}m，沿侧向 {halfWidth * 2f:F2}m");
        report.AppendLine($"   面中心 {faceCenter}，法线 {normal}");
        report.AppendLine();

        float[] gapPlus = new float[LineSamples];
        float[] gapMinus = new float[LineSamples];
        float[] meshHalfPlus = new float[LineSamples];
        float[] meshHalfMinus = new float[LineSamples];
        int sampled = 0;

        report.AppendLine("   沿坡剖面（从上端到下端）：");
        report.AppendLine("   索引 | 采样点 | 物理半宽 | 网格半宽(+侧/-侧) | 边界缺口(+侧/-侧)");

        for (int i = 0; i < LineSamples; i++)
        {
            // 从"下端到上端"逐个采；两端各内缩一点，避开与地面/平台交界处的棱
            float t = Mathf.Lerp(-1f, 1f, i / (float)(LineSamples - 1)) * 0.94f;
            Vector3 point = faceCenter + slopeDir * (t * halfLength);

            // 被别的几何盖住（埋进平台/地下）的段落跳过：从上往下打，第一下必须打到这个坡面
            if (!Physics.Raycast(point + Vector3.up * ProbeHeight, Vector3.down, out RaycastHit probe, ProbeHeight * 2f) ||
                probe.collider != ramp)
            {
                report.AppendLine($"   {i,4} | {point} | （被遮挡或已埋入其它几何，跳过）");
                continue;
            }

            Vector3 onSurface = probe.point;
            gapPlus[i] = -1f;
            gapMinus[i] = -1f;

            // 先吸附到网格，再从中心向两侧打 NavMesh.Raycast，量到边界的精确距离
            if (!NavMesh.SamplePosition(onSurface, out NavMeshHit snap, 1f, NavMesh.AllAreas))
            {
                report.AppendLine($"   {i,4} | {point} | 该点不在导航网格上（吸附半径 1m 内无网格）");
                continue;
            }

            meshHalfPlus[i] = MeasureMeshHalfWidth(snap.position, lateralDir, CapsuleRadius + halfWidth + 1f);
            meshHalfMinus[i] = MeasureMeshHalfWidth(snap.position, -lateralDir, CapsuleRadius + halfWidth + 1f);
            gapPlus[i] = halfWidth - meshHalfPlus[i];
            gapMinus[i] = halfWidth - meshHalfMinus[i];
            sampled++;

            report.AppendLine($"   {i,4} | ({onSurface.x:F2},{onSurface.y:F2},{onSurface.z:F2}) | {halfWidth:F2} | " +
                              $"{meshHalfPlus[i]:F2}/{meshHalfMinus[i]:F2} | {gapPlus[i]:F2}/{gapMinus[i]:F2}");
        }

        report.AppendLine();
        Summarize("+侧（沿 lateral 正方向）", gapPlus, sampled, report);
        Summarize("-侧（沿 lateral 负方向）", gapMinus, sampled, report);

        // 连通性：从坡底走到坡顶、以及从坡两侧的地面走上坡顶
        Vector3 bottom = faceCenter - slopeDir * (halfLength * 0.9f);
        Vector3 top = faceCenter + slopeDir * (halfLength * 0.9f);
        CheckPath("坡底 → 坡顶", bottom, top, report);
        CheckPath("坡顶 → 坡底", top, bottom, report);
        CheckPath("+侧地面 → 坡顶（两侧应一致）", bottom + lateralDir * (halfWidth + 1.5f), top, report);
        CheckPath("-侧地面 → 坡顶（两侧应一致）", bottom - lateralDir * (halfWidth + 1.5f), top, report);
        report.AppendLine();
    }

    // 从网格上的点朝某方向打一条 NavMesh 射线，返回"到网格边界"的水平距离
    private static float MeasureMeshHalfWidth(Vector3 from, Vector3 direction, float maxDistance)
    {
        if (NavMesh.Raycast(from, from + direction * maxDistance, out NavMeshHit hit, NavMesh.AllAreas))
        {
            Vector3 delta = hit.position - from;
            delta.y = 0f;
            return delta.magnitude;
        }

        return maxDistance;   // 一路都没碰到边界（说明这一侧连成一片，量不到边）
    }

    private static void Summarize(string label, float[] gaps, int sampled, StringBuilder report)
    {
        if (sampled == 0)
        {
            report.AppendLine($"   结论 {label}：没有有效采样点");
            return;
        }

        float min = float.MaxValue, max = float.MinValue, sum = 0f;
        int count = 0;
        foreach (float gap in gaps)
        {
            if (gap < 0f) continue;
            min = Mathf.Min(min, gap);
            max = Mathf.Max(max, gap);
            sum += gap;
            count++;
        }

        float mean = count > 0 ? sum / count : 0f;
        report.AppendLine($"   结论 {label}：缺口 min {min:F2}m / 平均 {mean:F2}m / max {max:F2}m（有效采样 {count} 条）");
        if (min > CapsuleRadius) report.AppendLine($"     ⚠ 该侧缺口普遍超过胶囊半径 {CapsuleRadius:F2}m：敌人贴边站会掉出网格（现在靠「回网格」兜底，但会看到它在边缘蹭）");
    }

    private static void CheckPath(string label, Vector3 from, Vector3 to, StringBuilder report)
    {
        // 起点/终点先各自吸附到网格，否则量的是"点不在网格上"而不是连通性
        bool fromOk = NavMesh.SamplePosition(from, out NavMeshHit a, 2f, NavMesh.AllAreas);
        bool toOk = NavMesh.SamplePosition(to, out NavMeshHit b, 2f, NavMesh.AllAreas);
        if (!fromOk || !toOk)
        {
            report.AppendLine($"   连通性 {label}：起点或终点吸附不到网格（from {fromOk} / to {toOk}）");
            return;
        }

        NavMeshPath path = new NavMeshPath();
        bool ok = NavMesh.CalculatePath(a.position, b.position, NavMesh.AllAreas, path);
        float length = 0f;
        for (int i = 1; i < path.corners.Length; i++) length += Vector3.Distance(path.corners[i - 1], path.corners[i]);

        report.AppendLine($"   连通性 {label}：{(ok ? "通" : "不通")}，拐点 {path.corners.Length}，路径长 {length:F2}m" +
                          (ok && length > Vector3.Distance(a.position, b.position) * 3f ? "  ⚠ 绕行很远" : ""));
    }

    // —— 场景里的敌人有没有站在网格外 —— //
    private static void InspectEnemySpawns(StringBuilder report)
    {
        report.AppendLine("## 场景里的敌人位置");
        int offMesh = 0;

        foreach (MonoBehaviour behaviour in Object.FindObjectsOfType<MonoBehaviour>())
        {
            if (behaviour == null || behaviour.GetType().Name != "EnemyNavWander") continue;

            Vector3 position = behaviour.transform.position;
            bool onMesh = NavMesh.SamplePosition(position, out NavMeshHit hit, 4f, NavMesh.AllAreas);
            Vector3 delta = onMesh ? hit.position - position : Vector3.zero;
            delta.y = 0f;
            float distance = onMesh ? delta.magnitude : -1f;
            if (!onMesh || distance > CapsuleRadius) offMesh++;

            report.AppendLine($"   {GetPath(behaviour.transform)}：位置 ({position.x:F2},{position.y:F2},{position.z:F2})，" +
                              (onMesh ? $"离网格 {distance:F3}m" : "4m 内找不到网格"));
        }

        report.AppendLine(offMesh > 0
            ? $"   ⚠ 有 {offMesh} 个敌人当前位置离网格超过胶囊半径（{CapsuleRadius:F2}m）"
            : "   全部敌人都在网格上（或偏差在胶囊半径内）");
    }

    private static string GetPath(Transform t)
    {
        string path = t.name;
        Transform parent = t.parent;
        while (parent != null)
        {
            path = parent.name + "/" + path;
            parent = parent.parent;
        }

        return path;
    }
}
