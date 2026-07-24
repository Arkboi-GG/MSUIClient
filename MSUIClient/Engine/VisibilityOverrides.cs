using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MSUIClient.World.Wmo;

namespace MSUIClient.Engine;

/// <summary>
/// What a curated override does to a WMO group.
///   Hide        - never draw it.
///   Show        - draw it, bypassing the heuristic culls (still frustum-tested).
///   HideInside  - hide it only when the camera is inside the WMO (the thief01
///                 entrance-keep case: correct on approach, gone once in the city).
///   ShowInside  - force it visible only when inside.
/// </summary>
public enum OverrideRule { Hide, Show, HideInside, ShowInside }

/// <summary>One hand-authored visibility decision, keyed by WMO root + group index.</summary>
public sealed class VisibilityOverride
{
    /// <summary>Full WMO root path, matched against WmoRenderer instance paths.</summary>
    public string Root { get; set; } = "";

    /// <summary>Root file name, for readability in the JSON and HUD.</summary>
    public string RootFile { get; set; } = "";

    public int GroupIndex { get; set; }
    public OverrideRule Rule { get; set; }
    public string Note { get; set; } = "";

    /// <summary>The vantage it was authored from, for future context.</summary>
    public string Vantage { get; set; } = "";
}

/// <summary>
/// The visibility override database: a hand-authored show/hide list, honoured by
/// ClassifyGroup before any heuristic. This is CURATED TRUTH - when the heuristics
/// cannot get a case right (an exterior keep that stays visible across an open
/// courtyard, say), an entry here makes the frame correct anyway. The DATA is core
/// and ships in a release; the click-to-author UI is dev-only. Persisted to
/// visibility_overrides.json at the repo root and meant to be committed.
/// See FOUNDATION_PLAN.md sec 3.5 / sec 12 and PLAN_04_OVERRIDE_DB.md.
/// </summary>
public sealed class VisibilityOverrides
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly List<VisibilityOverride> _entries;

    private VisibilityOverrides(string path, List<VisibilityOverride> entries)
    {
        _path = path;
        _entries = entries;
    }

    public IReadOnlyList<VisibilityOverride> All => _entries;

    public static VisibilityOverrides Load(string repoRoot)
    {
        string path = Path.Combine(repoRoot, "visibility_overrides.json");
        var list = new List<VisibilityOverride>();

        try
        {
            if (File.Exists(path))
            {
                var parsed = JsonSerializer.Deserialize<List<VisibilityOverride>>(File.ReadAllText(path), Options);
                if (parsed is not null) list = parsed;
                Console.WriteLine($"[overrides] {list.Count} visibility override(s) in {path}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[overrides] could not read {path} - starting empty ({ex.Message})");
            list = new List<VisibilityOverride>();
        }

        return new VisibilityOverrides(path, list);
    }

    /// <summary>
    /// The curated decision for a group, or null to let the heuristics decide.
    /// Returns OverrideHide or OverrideShow; the caller maps those to behaviour.
    /// </summary>
    public WmoReasonCode? Resolve(string root, int groupIndex, bool cameraInside)
    {
        foreach (var e in _entries)
        {
            if (e.GroupIndex != groupIndex) continue;
            if (!string.Equals(e.Root, root, StringComparison.OrdinalIgnoreCase)) continue;

            switch (e.Rule)
            {
                case OverrideRule.Hide: return WmoReasonCode.OverrideHide;
                case OverrideRule.Show: return WmoReasonCode.OverrideShow;
                case OverrideRule.HideInside: if (cameraInside) return WmoReasonCode.OverrideHide; break;
                case OverrideRule.ShowInside: if (cameraInside) return WmoReasonCode.OverrideShow; break;
            }
        }
        return null;
    }

    /// <summary>Add or replace the entry for one (root, group), then persist.</summary>
    public void Set(string root, string rootFile, int groupIndex, OverrideRule rule, string note, string vantage)
    {
        _entries.RemoveAll(e => e.GroupIndex == groupIndex &&
            string.Equals(e.Root, root, StringComparison.OrdinalIgnoreCase));
        _entries.Add(new VisibilityOverride
        {
            Root = root,
            RootFile = rootFile,
            GroupIndex = groupIndex,
            Rule = rule,
            Note = note,
            Vantage = vantage,
        });
        Save();
    }

    /// <summary>Remove any entry for one (root, group), then persist.</summary>
    public void Remove(string root, int groupIndex)
    {
        _entries.RemoveAll(e => e.GroupIndex == groupIndex &&
            string.Equals(e.Root, root, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_entries, Options));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[overrides] could not write {_path} - {ex.Message}");
        }
    }
}
