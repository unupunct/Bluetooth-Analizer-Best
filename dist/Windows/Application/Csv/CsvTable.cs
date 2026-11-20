using System.Text;

namespace BluetoothFleetMonitor.Application.Csv;

/// <summary>Minimal RFC 4180 reader/writer: quoted fields, embedded commas, quotes and line breaks.</summary>
public static class CsvTable
{
    public static string Write(IEnumerable<IReadOnlyList<string?>> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            sb.Append(string.Join(',', row.Select(Escape)));
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    private static string Escape(string? v)
    {
        v ??= "";
        // A leading = + - @ can be executed as a formula when the file is opened in a spreadsheet.
        if (v.Length > 0 && v[0] is '=' or '+' or '@' || (v.Length > 1 && v[0] == '-' && !char.IsDigit(v[1])))
            v = "'" + v;
        return v.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    public static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var any = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': inQuotes = true; any = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); any = true; break;
                case '\r': break;
                case '\n':
                    if (any || field.Length > 0) { row.Add(field.ToString()); rows.Add(row.ToArray()); }
                    row.Clear(); field.Clear(); any = false;
                    break;
                default: field.Append(c); any = true; break;
            }
        }
        if (any || field.Length > 0) { row.Add(field.ToString()); rows.Add(row.ToArray()); }
        return rows;
    }
}
