using System.Text;

namespace FileOrbit.Engines.Data;

/// <summary>RFC 4180 CSV reading and writing with delimiter detection.</summary>
public static class CsvFormat
{
    public static readonly char[] CandidateDelimiters = [',', ';', '\t', '|'];

    /// <summary>Quotes a field when it contains the delimiter, quotes or line breaks.</summary>
    public static string Escape(string value) => Escape(value, ',');

    public static string Escape(string value, char delimiter)
    {
        if (LooksLikeFormula(value))
        {
            // Neutralise spreadsheet formula injection: Excel evaluates such cells when the CSV is opened.
            value = "'" + value;
        }

        var needsQuotes = value.IndexOf(delimiter) >= 0 || value.IndexOfAny(['"', '\r', '\n']) >= 0 || value.StartsWith(' ') || value.EndsWith(' ');
        return needsQuotes ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    /// <summary>
    /// True for text Excel would execute as a formula ("=...", "@...", or "+"/"-" followed by a
    /// non-numeric character). Plain numbers and phone numbers such as "+44 20 7946 0000" are left alone.
    /// </summary>
    public static bool LooksLikeFormula(string value)
    {
        if (value.Length < 2)
        {
            return false;
        }

        return value[0] switch
        {
            '=' or '@' => true,
            '+' or '-' => !(char.IsDigit(value[1]) || value[1] is ' ' or '.' or '(' or '-' or '+'),
            _ => false,
        };
    }

    /// <summary>Guesses the delimiter from the first lines, ignoring characters inside quotes.</summary>
    public static char DetectDelimiter(string sample)
    {
        var lines = sample.Replace("\r\n", "\n").Split('\n').Take(20).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0)
        {
            return ',';
        }

        var best = ',';
        var bestScore = -1.0;
        foreach (var delimiter in CandidateDelimiters)
        {
            var counts = lines.Select(l => CountOutsideQuotes(l, delimiter)).ToList();
            if (counts.All(c => c == 0))
            {
                continue;
            }

            // Prefer delimiters that appear often and consistently on every line.
            var average = counts.Average();
            var consistency = counts.Count(c => c == counts[0]) / (double)counts.Count;
            var score = average * consistency;
            if (score > bestScore)
            {
                best = delimiter;
                bestScore = score;
            }
        }

        return best;
    }

    public static List<List<string>> Read(TextReader reader, char delimiter, int maxRows = int.MaxValue)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var fieldStarted = false;

        int read;
        while ((read = reader.Read()) != -1)
        {
            var c = (char)read;
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        field.Append('"');
                        reader.Read();
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            if (c == '"' && field.Length == 0)
            {
                inQuotes = true;
                fieldStarted = true;
            }
            else if (c == delimiter)
            {
                row.Add(field.ToString());
                field.Clear();
                fieldStarted = false;
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && reader.Peek() == '\n')
                {
                    reader.Read();
                }

                if (row.Count > 0 || field.Length > 0 || fieldStarted)
                {
                    row.Add(field.ToString());
                    rows.Add(row);
                    if (rows.Count >= maxRows)
                    {
                        return rows;
                    }
                }

                row = [];
                field.Clear();
                fieldStarted = false;
            }
            else
            {
                field.Append(c);
            }
        }

        if (row.Count > 0 || field.Length > 0 || fieldStarted)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }

    public static void Write(TextWriter writer, IEnumerable<IReadOnlyList<string>> rows, char delimiter)
    {
        foreach (var row in rows)
        {
            for (var i = 0; i < row.Count; i++)
            {
                if (i > 0)
                {
                    writer.Write(delimiter);
                }

                writer.Write(Escape(row[i], delimiter));
            }

            writer.Write("\r\n");
        }
    }

    private static int CountOutsideQuotes(string line, char delimiter)
    {
        var count = 0;
        var inQuotes = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == delimiter && !inQuotes)
            {
                count++;
            }
        }

        return count;
    }
}
