using System.Text;

internal sealed class RosterDeletionFence
{
    private readonly HashSet<string> _agentCreatedNames;

    private RosterDeletionFence(HashSet<string> agentCreatedNames)
    {
        _agentCreatedNames = agentCreatedNames;
    }

    public static RosterDeletionFence Load(string rosterCsv)
    {
        if (!File.Exists(rosterCsv))
            throw new FileNotFoundException("NIGHT roster ledger is required before deletion", rosterCsv);

        string[] lines = File.ReadAllLines(rosterCsv);
        if (lines.Length == 0)
            throw new InvalidDataException("NIGHT roster ledger has no header");

        string[] header = ParseCsvLine(lines[0]);
        int nameIndex = Array.FindIndex(header, value => value.Equals("name", StringComparison.OrdinalIgnoreCase));
        int sourceIndex = Array.FindIndex(header, value => value.Equals("creation_source", StringComparison.OrdinalIgnoreCase));
        if (nameIndex < 0 || sourceIndex < 0)
            throw new InvalidDataException("NIGHT roster ledger must contain name and creation_source columns");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] fields = ParseCsvLine(line);
            if (fields.Length <= Math.Max(nameIndex, sourceIndex)) continue;
            if (fields[sourceIndex].Equals("AGENT_CREATED", StringComparison.OrdinalIgnoreCase))
                names.Add(fields[nameIndex]);
        }
        return new RosterDeletionFence(names);
    }

    public bool Authorize(string name, out string reason)
    {
        if (!name.StartsWith("NB", StringComparison.OrdinalIgnoreCase))
        {
            reason = "REFUSED_PREFIX";
            return false;
        }
        if (!_agentCreatedNames.Contains(name))
        {
            reason = "REFUSED_NOT_AGENT_CREATED_IN_LEDGER";
            return false;
        }
        reason = "AUTHORIZED";
        return true;
    }

    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var value = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    value.Append('"');
                    i++;
                }
                else quoted = !quoted;
            }
            else if (c == ',' && !quoted)
            {
                fields.Add(value.ToString());
                value.Clear();
            }
            else value.Append(c);
        }
        fields.Add(value.ToString());
        return fields.ToArray();
    }
}
