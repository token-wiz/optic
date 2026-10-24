using System.Globalization;
using System.Text;

namespace OPTIC.Services.Valuation;

/// <summary>Minimal RFC 4180 CSV reader/writer. Imported content is treated as data only.</summary>
public static class Csv
{
    public static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else field.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': quoted = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n': row.Add(field.ToString()); field.Clear(); rows.Add(row); row = new List<string>(); break;
                default: field.Append(c); break;
            }
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        // Drop blank lines and a leading BOM.
        if (rows.Count > 0 && rows[0].Count > 0) rows[0][0] = rows[0][0].TrimStart('﻿');
        return rows.Where(r => r.Any(f => f.Trim().Length > 0)).ToList();
    }

    /// <summary>Rows as dictionaries keyed by lower-case header, with 1-based source row numbers (header = row 1).</summary>
    public static List<(int Row, Dictionary<string, string> Fields)> ParseWithHeader(string text)
    {
        var rows = Parse(text);
        if (rows.Count == 0) return new();
        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        var result = new List<(int, Dictionary<string, string>)>();
        for (var r = 1; r < rows.Count; r++)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < header.Count; c++)
                d[header[c]] = c < rows[r].Count ? rows[r][c].Trim() : "";
            result.Add((r + 1, d));
        }
        return result;
    }

    public static string Field(object? v)
    {
        var s = v switch
        {
            null => "",
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            DateTime t => t.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => v.ToString() ?? "",
        };
        // Neutralise spreadsheet formula injection, then quote when needed.
        if (s.Length > 0 && "=+-@".Contains(s[0]) && !decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
            s = "'" + s;
        return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    public static string Line(params object?[] values) => string.Join(",", values.Select(Field));

    public static decimal? Dec(Dictionary<string, string> f, string key, List<string> errors, int row)
    {
        if (!f.TryGetValue(key, out var s) || s.Length == 0) return null;
        if (decimal.TryParse(s.Replace("_", ""), NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var v)) return v;
        errors.Add($"Row {row}: '{key}' is not a number ({s}).");
        return null;
    }
}
