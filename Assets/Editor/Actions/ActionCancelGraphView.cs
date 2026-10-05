using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace GameJam.Actions.Editor
{
    public sealed class ActionCancelGraphView
    {
        [Serializable] private struct SavedNode { public string Guid; public Vector2 Position; }
        [Serializable] private struct SavedLayout { public List<SavedNode> Nodes; public Vector2 Pan; public float Zoom; }
        private const float NodeWidth = 178;
        private const float NodeHeight = 76;
        private readonly Dictionary<ActionDefinition, Vector2> _positions = new Dictionary<ActionDefinition, Vector2>();
        private readonly List<ActionDefinition> _nodes = new List<ActionDefinition>();
        private List<ActionCancelEdge> _edges = new List<ActionCancelEdge>();
        private ActionCatalog _catalog;
        private Vector2 _pan = new Vector2(30, 30);
        private float _zoom = 1;
        private ActionDefinition _dragging;
        private bool _panning;
        public bool ShowLabels = true;
        public bool OnlyRelated;

        public void Refresh(ActionCatalog catalog, List<ActionCancelEdge> edges)
        {
            if (_catalog != catalog)
            {
                SaveLayout();
                _catalog = catalog;
                _positions.Clear();
                _pan = new Vector2(30, 30);
                _zoom = 1;
                LoadLayout();
            }
            _edges = edges;
            _nodes.Clear();
            if (catalog?.Actions != null)
                foreach (ActionDefinition action in catalog.Actions) AddNode(action);
            foreach (ActionCancelEdge edge in _edges) { AddNode(edge.Source); AddNode(edge.Target); }
        }

        private void AddNode(ActionDefinition action)
        {
            if (action == null || _nodes.Contains(action)) return;
            int i = _nodes.Count;
            _nodes.Add(action);
            if (!_positions.ContainsKey(action)) _positions[action] = new Vector2(i % 4 * 250, i / 4 * 145);
        }

        public void AutoLayout()
        {
            int columns = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(_nodes.Count)));
            for (int i = 0; i < _nodes.Count; i++) _positions[_nodes[i]] = new Vector2(i % columns * 250, i / columns * 145);
            _pan = new Vector2(30, 30);
            _zoom = 1;
            SaveLayout();
        }

        public void Fit(Vector2 size)
        {
            if (_nodes.Count == 0) return;
            Rect bounds = Bounds();
            _zoom = Mathf.Clamp(Mathf.Min((size.x - 50) / bounds.width, (size.y - 50) / bounds.height), 0.25f, 1.5f);
            _pan = (size - bounds.size * _zoom) * 0.5f - bounds.position * _zoom;
        }

        public void Draw(Rect viewport, ActionDefinition selected, Action<ActionDefinition, int> select)
        {
            GUI.BeginGroup(viewport);
            Rect canvas = new Rect(Vector2.zero, viewport.size);
            EditorGUI.DrawRect(canvas, new Color(0.085f, 0.1f, 0.13f));
            Handles.BeginGUI();
            Handles.color = new Color(0.15f, 0.18f, 0.23f);
            float grid = 32 * _zoom;
            for (float x = _pan.x % grid; x < canvas.width; x += grid) Handles.DrawLine(new Vector3(x, 0), new Vector3(x, canvas.height));
            for (float y = _pan.y % grid; y < canvas.height; y += grid) Handles.DrawLine(new Vector3(0, y), new Vector3(canvas.width, y));
            Event current = Event.current;
            bool selectedEdge = false;
            for (int i = 0; i < _edges.Count; i++)
            {
                ActionCancelEdge edge = _edges[i];
                if (OnlyRelated && selected != null && edge.Source != selected && edge.Target != selected) continue;
                Curve(edge, Lane(i), out Vector2 a, out Vector2 b, out Vector2 c, out Vector2 d);
                a = Screen(a); b = Screen(b); c = Screen(c); d = Screen(d);
                bool relevant = edge.Source == selected || edge.Target == selected;
                Color color = EdgeColor(edge);
                if (selected != null && !relevant) color.a = 0.35f;
                Handles.DrawBezier(a, d, b, c, color, null, relevant ? 3 : 1.6f);
                Vector2 direction = (d - c).normalized;
                Vector2 normal = new Vector2(-direction.y, direction.x);
                Handles.color = color;
                Handles.DrawAAConvexPolygon(d, d - direction * 11 + normal * 5, d - direction * 11 - normal * 5);
                if (!ShowLabels) continue;
                Vector2 midpoint = Bezier(a, b, c, d, 0.5f);
                string text = edge.Window.DisplayName + " " + (edge.RangeValid ? "[" + edge.StartFrame + "," + edge.EndFrame + ")" : "区间无效");
                var style = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = relevant ? Color.white : new Color(0.8f, 0.84f, 0.9f) }, fontSize = Mathf.Max(9, Mathf.RoundToInt(11 * _zoom)), alignment = TextAnchor.MiddleCenter };
                float width = Mathf.Min(260, style.CalcSize(new GUIContent(text)).x + 12);
                Rect label = new Rect(midpoint.x - width * 0.5f, midpoint.y - 11, width, 22);
                EditorGUI.DrawRect(label, new Color(0.12f, 0.17f, 0.24f, 0.95f));
                GUI.Label(label, new GUIContent(text, Tooltip(edge)), style);
                if (current.type == EventType.MouseDown && current.button == 0 && label.Contains(current.mousePosition) && !NodeAt(current.mousePosition, out _))
                {
                    select(edge.Source, edge.WindowIndex);
                    selectedEdge = true;
                    current.Use();
                }
            }
            Handles.EndGUI();
            foreach (ActionDefinition node in _nodes)
            {
                Rect rect = NodeRect(node);
                bool member = _catalog != null && _catalog.Actions.Contains(node);
                EditorGUI.DrawRect(rect, node == selected ? new Color(0.2f, 0.46f, 0.65f) : member ? new Color(0.17f, 0.23f, 0.31f) : new Color(0.43f, 0.2f, 0.2f));
                EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 3), node == selected ? new Color(0.5f, 0.87f, 1) : new Color(0.35f, 0.6f, 0.78f));
                var style = new GUIStyle(EditorStyles.whiteLabel) { fontSize = Mathf.Max(9, Mathf.RoundToInt(12 * _zoom)) };
                GUI.Label(new Rect(rect.x + 8, rect.y + 8, rect.width - 16, 20 * _zoom), node.Label, style);
                style.fontSize = Mathf.Max(8, Mathf.RoundToInt(10 * _zoom));
                GUI.Label(new Rect(rect.x + 8, rect.y + 30 * _zoom, rect.width - 16, 18 * _zoom), node.ActionId, style);
                GUI.Label(new Rect(rect.x + 8, rect.y + 49 * _zoom, rect.width - 16, 20 * _zoom),
                    node.TotalFrames + " 动作帧 · 预输入 " + (node.Input?.PreInputFrames ?? 0) + " 采样帧" + (member ? "" : " · 集外目标"), style);
            }
            if (!selectedEdge) HandleInput(current, canvas, select);
            GUI.EndGroup();
        }

        private void HandleInput(Event current, Rect canvas, Action<ActionDefinition, int> select)
        {
            if (current.type == EventType.ScrollWheel && canvas.Contains(current.mousePosition))
            {
                Vector2 world = (current.mousePosition - _pan) / _zoom;
                _zoom = Mathf.Clamp(_zoom * Mathf.Pow(1.1f, -current.delta.y), 0.25f, 2);
                _pan = current.mousePosition - world * _zoom;
                SaveLayout(); current.Use();
            }
            if (current.type == EventType.MouseDown && canvas.Contains(current.mousePosition))
            {
                if (current.button == 0 && NodeAt(current.mousePosition, out ActionDefinition node))
                { _dragging = node; select(node, -1); current.Use(); }
                else if (current.button == 2 || current.button == 0) { _panning = true; current.Use(); }
            }
            if (current.type == EventType.MouseDrag)
            {
                if (_dragging != null) { _positions[_dragging] += current.delta / _zoom; current.Use(); }
                else if (_panning) { _pan += current.delta; current.Use(); }
            }
            if (current.type == EventType.MouseUp && (_dragging != null || _panning))
            { _dragging = null; _panning = false; SaveLayout(); current.Use(); }
        }

        private bool NodeAt(Vector2 point, out ActionDefinition node)
        {
            for (int i = _nodes.Count - 1; i >= 0; i--)
                if (NodeRect(_nodes[i]).Contains(point)) { node = _nodes[i]; return true; }
            node = null;
            return false;
        }
        private Vector2 Screen(Vector2 point) => point * _zoom + _pan;
        private Rect NodeRect(ActionDefinition node) => new Rect(Screen(_positions[node]), new Vector2(NodeWidth, NodeHeight) * _zoom);
        private float Lane(int index)
        {
            ActionCancelEdge edge = _edges[index];
            int rank = 0, count = 0;
            for (int i = 0; i < _edges.Count; i++)
                if (_edges[i].Source == edge.Source && _edges[i].Target == edge.Target) { if (i < index) rank++; count++; }
            // 自取消的两条环不能用对称偏移，否则取绝对值后会画到同一路径上。
            return edge.Source == edge.Target ? rank * 48 : (rank - (count - 1) * 0.5f) * 48;
        }
        private void Curve(ActionCancelEdge edge, float lane, out Vector2 a, out Vector2 b, out Vector2 c, out Vector2 d)
        {
            Vector2 source = _positions[edge.Source], target = _positions[edge.Target];
            a = source + new Vector2(NodeWidth, NodeHeight * 0.5f);
            d = target + new Vector2(0, NodeHeight * 0.5f);
            float reach = Mathf.Max(70, Mathf.Abs(d.x - a.x) * 0.45f);
            b = a + new Vector2(reach, lane);
            c = d + new Vector2(-reach, lane);
            if (edge.Source == edge.Target)
            {
                d = source + new Vector2(NodeWidth * 0.65f, 0);
                b = a + new Vector2(85, -85 - Mathf.Abs(lane));
                c = d + new Vector2(30, -85 - Mathf.Abs(lane));
            }
        }
        private static Vector2 Bezier(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float t)
        { float u = 1 - t; return u * u * u * a + 3 * u * u * t * b + 3 * u * t * t * c + t * t * t * d; }
        private static Color EdgeColor(ActionCancelEdge edge) => !edge.RangeValid ? new Color(1, 0.35f, 0.35f) :
            (edge.Window.RequireAll?.Count ?? 0) > 0 || (edge.Window.RequireAny?.Count ?? 0) > 0 ? new Color(1, 0.72f, 0.3f) : new Color(0.35f, 0.77f, 0.98f);
        public static string Tooltip(ActionCancelEdge edge) => edge.Source.Label + " → " + edge.Target.Label + "\n" + edge.Window.DisplayName +
            "\n全部条件：" + string.Join(", ", edge.Window.RequireAll ?? new List<string>()) + "\n任一条件：" + string.Join(", ", edge.Window.RequireAny ?? new List<string>()) + "\n窗口优先级：" + edge.Window.Priority;

        private Rect Bounds()
        {
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue), max = new Vector2(float.MinValue, float.MinValue);
            foreach (ActionDefinition node in _nodes) { min = Vector2.Min(min, _positions[node]); max = Vector2.Max(max, _positions[node] + new Vector2(NodeWidth, NodeHeight)); }
            // 环和反向边会超出节点边界，适配视图和导出必须同时包含它们的控制点。
            for (int i = 0; i < _edges.Count; i++)
            {
                Curve(_edges[i], Lane(i), out Vector2 a, out Vector2 b, out Vector2 c, out Vector2 d);
                min = Vector2.Min(min, Vector2.Min(Vector2.Min(a, b), Vector2.Min(c, d)));
                max = Vector2.Max(max, Vector2.Max(Vector2.Max(a, b), Vector2.Max(c, d)));
            }
            return new Rect(min - new Vector2(90, 90), max - min + new Vector2(180, 180));
        }
        private string LayoutKey => "GameJam.ActionGraph:" + Application.dataPath + ":" + AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(_catalog));
        public void SaveLayout()
        {
            if (_catalog == null || string.IsNullOrEmpty(AssetDatabase.GetAssetPath(_catalog))) return;
            var saved = new SavedLayout { Nodes = new List<SavedNode>(), Pan = _pan, Zoom = _zoom };
            foreach (var entry in _positions)
                if (entry.Key != null) saved.Nodes.Add(new SavedNode { Guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(entry.Key)), Position = entry.Value });
            EditorPrefs.SetString(LayoutKey, JsonUtility.ToJson(saved));
        }
        private void LoadLayout()
        {
            if (_catalog == null || string.IsNullOrEmpty(AssetDatabase.GetAssetPath(_catalog))) return;
            string json = EditorPrefs.GetString(LayoutKey, "");
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                SavedLayout saved = JsonUtility.FromJson<SavedLayout>(json);
                if (saved.Nodes != null)
                    foreach (SavedNode node in saved.Nodes)
                    {
                        ActionDefinition action = AssetDatabase.LoadAssetAtPath<ActionDefinition>(AssetDatabase.GUIDToAssetPath(node.Guid));
                        if (action != null) _positions[action] = node.Position;
                    }
                _pan = saved.Pan;
                _zoom = Mathf.Clamp(saved.Zoom, 0.25f, 2);
            }
            catch (ArgumentException) { _positions.Clear(); }
        }

        public string ExportSvg()
        {
            if (_nodes.Count == 0) return "<svg xmlns=\"http://www.w3.org/2000/svg\"/>";
            Rect bounds = Bounds();
            var svg = new StringBuilder();
            svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"").Append(F(bounds.x)).Append(' ').Append(F(bounds.y)).Append(' ').Append(F(bounds.width)).Append(' ').Append(F(bounds.height)).Append("\">");
            svg.Append("<defs><marker id=\"arrow\" viewBox=\"0 0 10 10\" refX=\"9\" refY=\"5\" markerWidth=\"7\" markerHeight=\"7\" orient=\"auto\"><path d=\"M 0 0 L 10 5 L 0 10 z\" fill=\"#83c8ed\"/></marker></defs>");
            svg.Append("<rect x=\"").Append(F(bounds.x)).Append("\" y=\"").Append(F(bounds.y)).Append("\" width=\"").Append(F(bounds.width)).Append("\" height=\"").Append(F(bounds.height)).Append("\" fill=\"#161d27\"/>");
            for (int i = 0; i < _edges.Count; i++)
            {
                ActionCancelEdge edge = _edges[i];
                Curve(edge, Lane(i), out Vector2 a, out Vector2 b, out Vector2 c, out Vector2 d);
                string color = "#" + ColorUtility.ToHtmlStringRGB(EdgeColor(edge));
                svg.Append("<path fill=\"none\" stroke=\"").Append(color).Append("\" stroke-width=\"2\" marker-end=\"url(#arrow)\" d=\"M ").Append(P(a)).Append(" C ").Append(P(b)).Append(' ').Append(P(c)).Append(' ').Append(P(d)).Append("\"><title>").Append(Escape(Tooltip(edge))).Append("</title></path>");
                Vector2 mid = Bezier(a, b, c, d, 0.5f);
                Text(svg, mid, edge.Window.DisplayName + " [" + edge.StartFrame + "," + edge.EndFrame + ")", color, 10);
            }
            foreach (ActionDefinition node in _nodes)
            {
                Vector2 p = _positions[node];
                svg.Append("<rect x=\"").Append(F(p.x)).Append("\" y=\"").Append(F(p.y)).Append("\" width=\"178\" height=\"76\" rx=\"5\" fill=\"#2a3b50\" stroke=\"#558ab2\"/>");
                Text(svg, p + new Vector2(10, 23), node.Label, "#ffffff", 13);
                Text(svg, p + new Vector2(10, 43), node.ActionId, "#b0cde1", 10);
                Text(svg, p + new Vector2(10, 62), node.TotalFrames + " 动作帧 / 预输入 " + (node.Input?.PreInputFrames ?? 0) + " 采样帧", "#b0cde1", 10);
            }
            return svg.Append("</svg>").ToString();
        }
        private static void Text(StringBuilder svg, Vector2 p, string text, string color, int size)
        { svg.Append("<text x=\"").Append(F(p.x)).Append("\" y=\"").Append(F(p.y)).Append("\" fill=\"").Append(color).Append("\" font-size=\"").Append(size).Append("\" font-family=\"Microsoft YaHei, sans-serif\">").Append(Escape(text)).Append("</text>"); }
        private static string F(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        private static string P(Vector2 value) => F(value.x) + "," + F(value.y);
        private static string Escape(string value) => System.Security.SecurityElement.Escape(value ?? "");
    }
}
