using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImGuiNET;
using MSUIClient.Net;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// The spell SESSION: the design-phase product of creator mode. Each tuned spell
// is fully specified here - complete tuning metadata PLUS the patched M2 bytes,
// tinted BLPs and custom audio, base64-embedded, so nothing downstream depends
// on this machine's archives. MangosSuperUI's Spell Completer takes it from
// there for the data phase: real name, class, damage, ranks - and the unified
// patch build.
//
// A finished design leaves here two ways, and BOTH produce the same document:
//
//   PUSH  - POST straight to MangosSuperUI (/SpellCompleter/Push), where it lands
//           in the Completer's inbox ready to be named and costed. The direct
//           path, and the only one that carries the custom audio all the way.
//   FILE  - appended to one ongoing spell-session.json in the directory
//           MSUIClient was launched from. The offline record and the fallback
//           when this machine cannot reach the web app; the Completer still
//           accepts it by hand.
//
// Design phase lives here; data phase lives in MangosSuperUI.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    public const string CreatorSessionFileName = "spell-session.json";
    private const int CreatorSessionFormatVersion = 2;

    /// <summary>The launch directory, captured before anything can chdir - "the
    /// directory we launched MSUIClient from" is the contract for where the
    /// session file lands.</summary>
    private static readonly string CreatorSessionDir = Environment.CurrentDirectory;

    private readonly byte[] _creatorSessionNameBuf = new byte[48];
    private List<(string Name, int Models, int Audio)>? _creatorSessionEntries;   // null = reload

    private SpellPushClient? _spellPush;
    private SpellPushClient SpellPush => _spellPush ??= new SpellPushClient();

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

    private List<(string Name, int Models, int Audio)> CreatorSessionEntries()
    {
        if (_creatorSessionEntries is not null) return _creatorSessionEntries;
        _creatorSessionEntries = [];
        if (File.Exists(CreatorSessionPath) &&
            LoadCreatorSession()["spells"] is JsonArray spells)
            foreach (JsonNode? node in spells)
                if (node?["tempName"]?.GetValue<string>() is { Length: > 0 } name)
                    _creatorSessionEntries.Add((name,
                        (node["models"] as JsonArray)?.Count ?? 0,
                        (node["audio"] as JsonArray)?.Count ?? 0));
        return _creatorSessionEntries;
    }

    // ── the export itself ────────────────────────────────────────────────────

    /// <summary>Append (or replace, when the temp name already exists) one fully
    /// specified spell into the session file - the offline record of the design.</summary>
    private void AddCreatorSpellToSession(CreatorSpellDoc doc, string tempName)
    {
        if (BuildCreatorSpellEntry(doc, tempName) is not { } spellEntry) return;

        JsonObject session = LoadCreatorSession();
        session["version"] = CreatorSessionFormatVersion;
        var spells = (JsonArray)session["spells"]!;
        int existing = IndexOfSessionSpell(spells, tempName);
        if (existing >= 0) spells[existing] = spellEntry;
        else spells.Add(spellEntry);
        SaveCreatorSession(session);

        _creatorExportStatus = $"{(existing >= 0 ? "Replaced" : "Added")} '{tempName}' " +
            $"({CountOf(spellEntry, "models")} model(s), {CountOf(spellEntry, "tintedBlps")} tinted BLP(s), " +
            $"{CountOf(spellEntry, "audio")} audio track(s)) in {CreatorSessionPath}";
        Console.WriteLine($"[creator] {_creatorExportStatus}");
    }

    /// <summary>Send this design straight to MangosSuperUI's Spell Completer, where
    /// it becomes a card on the page instead of a file someone has to carry. The
    /// SAME document the session file would have received - so a spell that was
    /// pushed and a spell that was uploaded are indistinguishable downstream.</summary>
    private void PushCreatorSpell(CreatorSpellDoc doc, string tempName)
    {
        if (BuildCreatorSpellEntry(doc, tempName) is not { } spellEntry) return;

        string url = SuiWebAppUrl;
        if (url.Length == 0)
        {
            // BeginPush("") would build "/SpellCompleter/Push" and HttpClient throws the
            // opaque "An invalid request URI was provided" - say the useful thing instead.
            _creatorExportStatus = "No MSUI Web connection - set the web app address on " +
                "the login screen (MSUI Web Connection), or in Data source below.";
            Console.WriteLine($"[creator] {_creatorExportStatus}");
            return;
        }
        SpellPush.BeginPush(url, spellEntry);
        _creatorExportStatus = $"Pushing '{tempName}' to {url}…";
        Console.WriteLine($"[creator] {_creatorExportStatus}");
    }

    private static int CountOf(JsonObject entry, string key) => (entry[key] as JsonArray)?.Count ?? 0;

    /// <summary>Build one fully specified spell: the tuning metadata per model AND
    /// the patched M2 bytes AND the tinted BLPs AND the custom phase audio - the
    /// authoritative product of the design phase, and the single document both the
    /// session file and the push send onward. Null when there is nothing to send,
    /// with the reason already in the status line.</summary>
    private JsonObject? BuildCreatorSpellEntry(CreatorSpellDoc doc, string tempName)
    {
        var modified = doc.Models.Values.Where(m => m.Modified).ToList();
        if (modified.Count == 0 && doc.Audio.Count == 0)
        {
            _creatorExportStatus = "Nothing modified - tune the look or audio before adding it.";
            return null;
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

        var audio = new JsonArray();
        foreach ((CreatorAudioCue cue, CreatorAudioTrack track) in doc.Audio
                     .OrderBy(pair => Array.IndexOf(CreatorAudioCueOrder, pair.Key)))
        {
            audio.Add(new JsonObject
            {
                ["cue"] = cue.ToString().ToLowerInvariant(),
                ["sourceSoundId"] = CreatorAuthoredSound(doc, cue),
                ["mpqPath"] = track.MpqPath,
                ["sourceFile"] = Path.GetFileName(track.SourcePath),
                ["volume"] = track.Volume,
                ["looping"] = cue == CreatorAudioCue.Missile || track.Looping,
                ["noDuplicates"] = track.NoDuplicates,
                ["soundType"] = track.SoundType,
                ["extraFlags"] = track.ExtraFlags,
                ["eax"] = track.Eax,
                ["minDistance"] = track.MinDistance,
                ["cutoffDistance"] = track.CutoffDistance,
                ["fileBase64"] = Convert.ToBase64String(track.Bytes),
            });
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

        return new JsonObject
        {
            ["tempName"] = tempName,
            ["sourceSpellId"] = doc.Info.Id,
            ["sourceSpellName"] = doc.Info.Name,
            ["sourceVisualId"] = doc.Info.VisualId,
            ["exportedAtUtc"] = DateTime.UtcNow.ToString("o"),
            ["models"] = models,
            ["tintedBlps"] = blps,
            ["audio"] = audio,
        };
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
        ImGui.TextDisabled("SESSION (hand this spell to the Spell Completer)");
        CreatorHelp("The design-phase product: give this tuned spell a TEMP name, then " +
            "PUSH it to MangosSuperUI - it appears on the Spell Completer page under " +
            "Gameplay Tuning > Spells, ready for the data phase: proper name, class, " +
            "damage, ranks, and the final patch.\n\n" +
            "'Add to session' writes the same design to a local file instead, for when " +
            "this machine cannot reach the web app - the Completer still accepts that " +
            "file by hand, but only a PUSH carries the custom audio through.\n\n" +
            "Either way the design is complete: tuning data, patched models, recolored " +
            "images and custom audio. Same temp name = replace.\n\n" +
            "Push target: " + (SuiWebAppUrl.Length > 0
                ? SuiWebAppUrl
                : "not set up - login screen > MSUI Web Connection") +
            "\nFile: " + CreatorSessionPath);

        ImGui.TextDisabled(SuiWebAppUrl.Length > 0
            ? $"Push target: {SuiWebAppUrl}"
            : "No MSUI Web connection - pushing is off. Set the address on the login " +
              "screen under 'MSUI Web Connection', or here in Data source.");

        ImGui.SetNextItemWidth(180f * cs);
        ImGui.InputText("##session-name", _creatorSessionNameBuf,
            (uint)_creatorSessionNameBuf.Length);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Temp name for this spell in the session");
        ImGui.SameLine();
        string tempName = BufToString(_creatorSessionNameBuf).Trim();
        bool nameOk = tempName.Length > 0;
        bool pushing = SpellPush.Pushing;
        // Gated on the address being SET, never on a verify outcome: a probe can fail for
        // reasons a push would survive, and the user must stay able to just try.
        bool webOk = SuiWebAppUrl.Length > 0;
        if (!nameOk || pushing || !webOk) ImGui.BeginDisabled();
        if (CreatorButton("Push to Completer"))
            PushCreatorSpell(doc, tempName);
        if (!nameOk || pushing || !webOk) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(webOk
                ? $"Send this design to {SuiWebAppUrl}\n" +
                  "It shows up under Gameplay Tuning > Spells > Spell Completer.\n" +
                  "Address: login screen > MSUI Web Connection."
                : "No MSUI Web connection. Set the web app address on the login screen " +
                  "under 'MSUI Web Connection', or in Data source below.");

        ImGui.SameLine();
        if (!nameOk) ImGui.BeginDisabled();
        if (CreatorButton("Add to session"))
            AddCreatorSpellToSession(doc, tempName);
        if (!nameOk) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Write it to the local session file instead (no audio downstream)");

        if (!nameOk)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("name it first");
        }
        else if (!webOk)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("no web connection");
        }

        // The push outcome, held until the next push replaces it. A failure names
        // the server, because the usual cause is that this machine cannot reach it.
        if (pushing)
        {
            ImGui.TextDisabled($"pushing to {SuiWebAppUrl}…");
        }
        else if (SpellPush.Result is { } push)
        {
            if (push.Ok)
                ImGui.TextColored(new Vector4(0.45f, 0.85f, 0.45f, 1f),
                    $"'{push.TempName}' is waiting in the Spell Completer.");
            else
                ImGui.TextColored(new Vector4(0.9f, 0.45f, 0.45f, 1f),
                    $"Push of '{push.TempName}' failed: {push.Error}");
            ImGui.SameLine();
            if (ImGui.SmallButton("dismiss")) SpellPush.ClearResult();
        }

        var entries = CreatorSessionEntries();
        if (entries.Count == 0)
        {
            ImGui.TextDisabled("Session is empty.");
            return;
        }
        foreach ((string name, int modelCount, int audioCount) in entries)
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
            ImGui.TextUnformatted($"{name}  ({modelCount} model(s), {audioCount} audio)");
            ImGui.PopID();
        }
    }
}
