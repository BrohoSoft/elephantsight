using System.Text;

namespace Flarelytics.Core.Reports;

/// <summary>Il CSV di Google: virgole, e campi tra virgolette che possono contenerne.</summary>
public static class Csv
{
    public static string[] Split(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (c == ',' && !quoted)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else current.Append(c);
        }

        fields.Add(current.ToString());
        return [.. fields];
    }
}
