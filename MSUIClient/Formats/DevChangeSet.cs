using System.Text.Json;
using System.Text.Json.Serialization;

namespace MSUIClient.Formats;

// ─────────────────────────────────────────────────────────────────────────────
// The NPC dev window's change-set file: every in-world edit becomes a packet
// (before + after values, never applied locally); a session's packets accumulate
// into ONE JSON file under dev-changes/ at the repo root, which the owner uploads
// to MangosSuperUI for verify + audited apply (NPC_DEV_WINDOW.md §10/§11 is the
// schema contract — keep this file and that section in sync).
//
// PURE FORMAT LAYER: model + (de)serialization only. No GameLoop, no ImGui.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class DevChangeSet
{
    public int SchemaVersion { get; set; } = 1;
    public DevChangeSession Session { get; set; } = new();
    public List<DevChangePacket> Packets { get; set; } = [];
}

public sealed class DevChangeSession
{
    public DateTime CreatedUtc { get; set; }
    public string Character { get; set; } = "";

    /// <summary>FetchedUtc of the DevWorldData the "before" values were read from —
    /// the staleness anchor MangosSuperUI's verify pass diffs against.</summary>
    public DateTime SourceSnapshotUtc { get; set; }

    public string SuiBase { get; set; } = "";
}

/// <summary>
/// One edit. Types: spawn-move | spawn-timer | spawn-field | spawn-add |
/// spawn-delete | waypoint-path-replace | template-field. `Before` always holds
/// the values as fetched; `After` the requested values. Waypoint packets carry a
/// FULL replacement path in Before/After under "points" (gapless 1-based —
/// vmangos WaypointManager::Cleanup renumbers anything else).
/// </summary>
public sealed class DevChangePacket
{
    public int Id { get; set; }
    public string Type { get; set; } = "";
    public Dictionary<string, object?> Target { get; set; } = new();
    public Dictionary<string, object?> Before { get; set; } = new();
    public Dictionary<string, object?> After { get; set; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object?>? Context { get; set; }
}

public static class DevChangeSetFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string DirectoryFor(string repoRoot) => Path.Combine(repoRoot, "dev-changes");

    /// <summary>Write the set. The first save names the file from the session stamp +
    /// character; later saves reuse <paramref name="existingPath"/> so one session stays
    /// one file. Returns the path written.</summary>
    public static string Save(string repoRoot, string? existingPath, DevChangeSet set)
    {
        string dir = DirectoryFor(repoRoot);
        Directory.CreateDirectory(dir);
        string path = existingPath ?? Path.Combine(dir,
            $"{set.Session.CreatedUtc:yyyyMMdd-HHmmss}-{SanitizeName(set.Session.Character)}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(set, Options));
        return path;
    }

    private static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "session";
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
