using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using GameJam.Flow;
using UnityEditor;

namespace GameJam.FlowEditor
{
    public static class RoomPoolCsv
    {
        private const string Header = "地图池编号,差分编号,差分GUID,基础权重,启用";
        public static string Export(RunFlowConfig config)
        {
            var text = new StringBuilder(Header + "\r\n");
            foreach (RoomPoolDefinition pool in FlowConfigAssets.Collect(config).OfType<RoomPoolDefinition>())
                foreach (RoomPoolEntry entry in pool.Entries)
                {
                    string guid = entry.Variant != null ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(entry.Variant)) : string.Empty;
                    text.AppendLine(string.Join(",", Quote(pool.Id), Quote(entry.Variant != null ? entry.Variant.Id : string.Empty), Quote(guid),
                        entry.Weight.ToString("R", CultureInfo.InvariantCulture), entry.Enabled ? "1" : "0"));
                }
            return text.ToString();
        }

        public static void Import(RunFlowConfig config, string csv)
        {
            List<string[]> rows = Parse(csv);
            if (rows.Count == 0 || string.Join(",", rows[0]) != Header) throw new FormatException("CSV表头不匹配，请先导出模板");
            Dictionary<string, RoomPoolDefinition> pools = FlowConfigAssets.Collect(config).OfType<RoomPoolDefinition>().ToDictionary(pool => pool.Id);
            Dictionary<string, RoomVariantDefinition> variants = FlowConfigAssets.Collect(config).OfType<RoomVariantDefinition>().ToDictionary(variant => variant.Id);
            var updates = new Dictionary<RoomPoolDefinition, List<RoomPoolEntry>>();
            for (int index = 1; index < rows.Count; index++)
            {
                string[] row = rows[index];
                if (row.Length != 5) throw new FormatException("第" + (index + 1) + "行应有5列");
                if (!pools.TryGetValue(row[0], out RoomPoolDefinition pool)) throw new FormatException("未知地图池 " + row[0]);
                if (!variants.TryGetValue(row[1], out RoomVariantDefinition variant)) throw new FormatException("未知差分 " + row[1]);
                if (!string.IsNullOrEmpty(row[2]) && AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(variant)) != row[2]) throw new FormatException("差分编号与GUID不一致 " + row[1]);
                if (row[4] != "0" && row[4] != "1") throw new FormatException("启用只能填0或1，行" + (index + 1));
                if (!float.TryParse(row[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float weight) || weight < 0 || (row[4] == "1" && weight == 0) || float.IsInfinity(weight) || float.IsNaN(weight)) throw new FormatException("启用条目的权重必须为有限正数，行" + (index + 1));
                if (pool.ChangesChaos != (variant.StabilizerGain > 0)) throw new FormatException("地图池类别与装置增量不一致 " + row[1]);
                if (!updates.TryGetValue(pool, out List<RoomPoolEntry> entries)) updates.Add(pool, entries = new List<RoomPoolEntry>());
                if (entries.Any(entry => entry.Variant == variant)) throw new FormatException("地图池重复条目 " + row[1]);
                entries.Add(new RoomPoolEntry { Variant = variant, Weight = weight, Enabled = row[4] == "1" });
            }
            // 所有行验证通过才修改资产，错误CSV不能留下半导入状态。
            foreach (var update in updates)
            {
                Undo.RecordObject(update.Key, "导入地图池CSV");
                update.Key.Entries = update.Value.ToArray(); EditorUtility.SetDirty(update.Key);
            }
            AssetDatabase.SaveAssets();
        }

        public static List<string[]> Parse(string csv)
        {
            var rows = new List<string[]>(); var row = new List<string>(); var field = new StringBuilder();
            bool quoted = false, closed = false;
            csv = csv.TrimStart('\uFEFF');
            for (int i = 0; i < csv.Length; i++)
            {
                char c = csv[i];
                if (c == '"')
                {
                    if (quoted && i + 1 < csv.Length && csv[i + 1] == '"') { field.Append('"'); i++; }
                    else if (quoted) { quoted = false; closed = true; }
                    else if (field.Length == 0 && !closed) quoted = true;
                    else throw new FormatException("CSV引号位置错误");
                }
                else if (!quoted && (c == ',' || c == '\r' || c == '\n'))
                {
                    row.Add(field.ToString()); field.Clear(); closed = false;
                    if (c == ',') continue;
                    if (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n') i++;
                    if (row.Count > 1 || row[0].Length > 0) rows.Add(row.ToArray()); row.Clear();
                }
                else { if (closed) throw new FormatException("CSV引号后存在多余字符"); field.Append(c); }
            }
            if (quoted) throw new FormatException("CSV引号未闭合");
            if (field.Length > 0 || row.Count > 0 || closed) { row.Add(field.ToString()); rows.Add(row.ToArray()); }
            return rows;
        }
        private static string Quote(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
    }
}
