using System.Text.Json;
using System.Text.Json.Nodes;
using ImGuiNET;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// The spell SESSION: the design-phase product of creator mode. Each tuned spell
// is given a temp name and appended to one ongoing JSON file in the directory
// MSUIClient was launched from; the file accumulates one or more fully-specified
// spells (complete tuning metadata PLUS the patched M2 bytes and tinted BLPs,
// base64-embedded, so nothing depends on this machine's archives). That file is
// then uploaded to MangosSuperUI's Spell Completer page, where the data phase
// happens: real name, class, damage, ranks - and the unified patch build.
//
// Design phase lives here; data phase lives in MangosSuperUI.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    public const string CreatorSessionFileName = "spell-session.json";
    private const int CreatorSessionFormatVersion = 1;

    /// <summary>The launch directory, captured before anything can chdir - "the
    /// directory we launched MSUIClient from" is the contract for where the
    /// session file lands.</summary>
    private static readonly string CreatorSessionDir = Environment.CurrentDirectory;

    private readonly byte[] _creatorSessionNameBuf = new byte[48];
    private List<(string Name, int Models)>? _creatorSessionEntries;   // null = reload

    private static string CreatorSessionPath => Path.Combine(CreatorSessionDir, CreatorSessionFileName);

    // ── file plumbing ────────────────────────────────────────────────────────

    private static JsonObject LoadCreatorSession()
    {
        try
        {
            if (File.Exists(CreatorSessionPath) &&
                JsonNode.Parse(File.ReadAllText(CreatorSessionPath)) is JsonObject existing &&
                existing["spells"] is JsonArray)
                return existing;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[creator] session file unreadable, starting fresh: {ex.Message}");
        }
        return new JsonObject
        {
            ["format"] = "msui-spell-session",
            ["version"] = CreatorSessionFormatVersion,
            ["exportedBy"] = "MSUIClient creator mode",
            ["spells"] = new JsonArray(),
        };
    }

    private void SaveCreatorSession(JsonObject session)
    {
        File.WriteAllText(CreatorSessionPath, session.ToJsonString(
            new JsonSerializerOptions { WriteIndented = true }));
        _creatorSessionEntries = null;   // re-list on next draw
    }

    private List<(string Name, int Models)> CreatorSessionEntries()
    {
        if (_creatorSessionEntries is not null) return _creatorSessionEntries;
        _creatorSessionEntries = [];
        if (File.Exists(CreatorSessionPath) &&
            LoadCreatorSession()["spells"] is JsonArray spells)
            foreach (JsonNode? node in spells)
                if (node?["tempName"]?.GetValue<string>() is { Length: > 0 } name)
                    _creatorSessionEntries.Add(
                        (name, (node["models"] as JsonArray)?.Count ?? 0));
        return _creatorSessionEntries;
    }

    // ── the export itself ────────────────────────────────────────────────────

    /// <summary>Append (or replace, when the temp name already exists) one fully
    /// specified spell into the session file. Carries EVERYTHING: the tuning
    /// metadata per model AND the patched M2 bytes AND the tinted BLPs - the
    /// authoritative product of the design phase.</summary>
    private void AddCreatorSpellToSession(CreatorSpellDoc doc, string tempName)
    {
        var modified = doc.Models.Values.Where(m => m.Modified).ToList();
        if (modified.Count == 0)
        {
            _creatorExportStatus = "Nothing modified - tune something before adding it.";
            return;
        }

        var models = new JsonArray();
        foreach (var model in modified)
        {
            bool byteModified = !model.Working.AsSpan().SequenceEqual(model.Original);
            var entry = new JsonObject
            {
                ["path"] = model.Path,
                ["phases"] = CreatorModelPhases(doc, model),
                ["byteModified"] = byteModified,
                // The patched model at its full fidelity - the consumer writes
                // these bytes (at a cloned path) instead of re-deriving them.
                ["m2Base64"] = byteModified ? Convert.ToBase64String(model.Working) : null,
                ["tuning"] = JsonSerializer.SerializeToNode(CreatorModelTuningPayload(model)),
                ["textures"] = JsonSerializer.SerializeToNode(model.Textures.Select(t => new
                {
                    slotIndex = t.Index,
                    filename = t.Filename,
                    effective = EffectiveTexturePath(model, t.Index),
                    particleEmitters = t.ReferencedByEmitters,
                })),
            };
            models.Add(entry);
        }

        // Tinted BLPs, re-encoded once here so the completer needs no access to
        // this machine's game archives. Keyed by the ORIGINAL path; the consumer
        // decides the isolated path it re-homes them to.
        var blps = new JsonArray();
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in modified)
            foreach (var (texIndex, tint) in model.TextureTints)
            {
                if (!tint.On) continue;
                string path = EffectiveTexturePath(model, texIndex);
                if (path.Length == 0 || !written.Add(path)) continue;
                byte[]? blp = BuildTintedBlp(path, PackArgb(tint.Color) & 0x00FFFFFF);
                if (blp is null)
                {
                    Console.WriteLine($"[creator] session: tinted BLP encode failed for {path}");
                    continue;
                }
                blps.Add(new JsonObject
                {
                    ["path"] = path,
                    ["tintColor"] = $"#{PackArgb(tint.Color) & 0xFFFFFF:x6}",
                    ["blpBase64"] = Convert.ToBase64String(blp),
                });
            }

        // Whole-model hue on mesh/ribbon art: those color tracks are keyframed
        // data the byte patcher cannot reach (in the workshop the hue rides the
        // renderers' color layers), so the export bakes it by hue-mapping each
        // mesh/ribbon BLP - the completer treats these exactly like tints. An
        // explicit user tint on the same image wins (the `written` set).
        foreach (var model in modified)
        {
            if (!model.HueShift) continue;
            foreach (var tex in model.Textures)
            {
                if (tex.ReferencedByEmitters.Count > 0) continue;
                string path = EffectiveTexturePath(model, tex.Index);
                if (path.Length == 0 || !written.Add(path)) continue;
                byte[]? blp = BuildTintedBlp(path, PackArgb(model.HueColor) & 0x00FFFFFF);
                if (blp is null)
                {
                    Console.WriteLine($"[creator] session: mesh-hue BLP encode failed for {path}");
                    continue;
                }
                blps.Add(new JsonObject
                {
                    ["path"] = path,
                    ["tintColor"] = $"#{PackArgb(model.HueColor) & 0xFFFFFF:x6}",
                    ["reason"] = "meshRibbonHue",
                    ["blpBase64"] = Convert.ToBase64String(blp),
                });
            }
        }

        var spellEntry = new JsonObject
        {
            ["tempName"] = tempName,
            ["sourceSpellId"] = doc.Info.Id,
            ["sourceSpellName"] = doc.Info.Name,
            ["sourceVisualId"] = doc.Info.VisualId,
            ["exportedAtUtc"] = DateTime.UtcNow.ToString("o"),
            ["models"] = models,
            ["tintedBlps"] = blps,
        };

        JsonObject session = LoadCreatorSession();
        var spells = (JsonArray)session["spells"]!;
        int existing = IndexOfSessionSpell(spells, tempName);
        if (existing >= 0) spells[existing] = spellEntry;
        else spells.Add(spellEntry);
        SaveCreatorSession(session);

        _creatorExportStatus = $"{(existing >= 0 ? "Replaced" : "Added")} '{tempName}' " +
            $"({models.Count} model(s), {blps.Count} tinted BLP(s)) in {CreatorSessionPath}";
        Console.WriteLine($"[creator] {_creatorExportStatus}");
    }

    private void RemoveCreatorSessionSpell(string tempName)
    {
        JsonObject session = LoadCreatorSession();
        var spells = (JsonArray)session["spells"]!;
        int index = IndexOfSessionSpell(spells, tempName);
        if (index < 0) return;
        spells.RemoveAt(index);
        SaveCreatorSession(session);
        _creatorExportStatus = $"Removed '{tempName}' from the session.";
    }

    private static int IndexOfSessionSpell(JsonArray spells, string tempName)
    {
        for (int i = 0; i < spells.Count; i++)
            if (string.Equals(spells[i]?["tempName"]?.GetValue<string>(), tempName,
                    StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>Which phases of the spell draw this model ("precast+cast",
    /// "missile", "geometry (in host.m2)") - context the completer shows.</summary>
    private static string CreatorModelPhases(CreatorSpellDoc doc, CreatorModelDoc model)
    {
        if (doc.GeometryHosts.TryGetValue(model.Path, out string? host))
            return $"geometry (in {host})";
        string phases = string.Join("+", doc.PhaseModels
            .Where(p => string.Equals(p.Path, model.Path, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Stage.ToString().ToLowerInvariant()).Distinct());
        if (phases.Length == 0 &&
            string.Equals(doc.MissilePath, model.Path, StringComparison.OrdinalIgnoreCase))
            phases = "missile";
        return phases;
    }

    // ── the SESSION area of the Export section ───────────────────────────────

    private void DrawCreatorSessionBody(CreatorSpellDoc doc)
    {
        float cs = CreatorUiScale;
        ImGui.Spacing();
        ImGui.TextDisabled("SESSION (the file the Spell Completer uploads)");
        CreatorHelp("The design-phase product: give this tuned spell a TEMP name and add " +
            "it to the ongoing session file. The file accumulates every spell you add " +
            "(same temp name = replace) and carries the complete design - tuning data, " +
            "patched models and recolored images - so MangosSuperUI's Spell Completer " +
            "page can build the real spell from it: proper name, class, damage, ranks, " +
            "and the final patch.\n\nFile: " + CreatorSessionPath);

        ImGui.SetNextItemWidth(180f * cs);
        ImGui.InputText("##session-name", _creatorSessionNameBuf,
            (uint)_creatorSessionNameBuf.Length);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Temp name for this spell in the session");
        ImGui.SameLine();
        string tempName = BufToString(_creatorSessionNameBuf).Trim();
        bool nameOk = tempName.Length > 0;
        if (!nameOk) ImGui.BeginDisabled();
        if (CreatorButton("Add to session"))
            AddCreatorSpellToSession(doc, tempName);
        if (!nameOk) ImGui.EndDisabled();
        if (!nameOk)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("name it first");
        }

        var entries = CreatorSessionEntries();
        if (entries.Count == 0)
        {
            ImGui.TextDisabled("Session is empty.");
            return;
        }
        foreach ((string name, int modelCount) in entries)
        {
            ImGui.PushID($"sess-{name}");
            if (ImGui.SmallButton("x"))
            {
                RemoveCreatorSessionSpell(name);
                ImGui.PopID();
                break;   // the list just changed - redraw next frame
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Remove this spell from the session");
            ImGui.SameLine();
            ImGui.TextUnformatted($"{name}  ({modelCount} model(s))");
            ImGui.PopID();
        }
    }
}
