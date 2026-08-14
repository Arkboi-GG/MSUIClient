using MSUIClient.Formats;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Creator texture-swap probe: a scripted end-to-end reproduction of "I swapped
// a BLP and nothing changed". Activated by the MSUI_CREATOR_PROBE environment
// variable; the client boots straight into the creator world, selects the
// spell, loops its cast, records which textures the particle and mesh
// renderers ACTUALLY draw, applies the swap through the exact code path the
// Swap button uses, records again, and prints a verdict. Two screenshots
// (dumps/gameplay-creator-probe-before/after.png) capture the visual truth.
//
//   MSUI_CREATOR_PROBE="spell=Cone of Cold;slot=CLOUDS;to=SNOWFLAKE2"
//
// `slot` is a substring of the texture filename to swap (first match across
// the spell's models, cast-phase models preferred). `to` is either a full
// BLP path or a substring resolved against the spell's own texture tables.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private static readonly string? ProbeSpec =
        Environment.GetEnvironmentVariable("MSUI_CREATOR_PROBE");

    private int _probeStage = -1;          // -1 idle, 0 boot, 1 world-wait, 2 before, 3 after, 4 exit
    private double _probeStageAt;
    private string _probeSpell = "Cone of Cold";
    private string _probeSlot = "CLOUDS";
    private string _probeTo = "SNOWFLAKE2";
    private readonly SortedSet<string> _probeBillboardBefore = new(StringComparer.OrdinalIgnoreCase);
    private readonly SortedSet<string> _probeBillboardAfter = new(StringComparer.OrdinalIgnoreCase);
    private readonly SortedSet<string> _probeMeshBefore = new(StringComparer.OrdinalIgnoreCase);
    private readonly SortedSet<string> _probeMeshAfter = new(StringComparer.OrdinalIgnoreCase);
    private string _probeSwappedFrom = "";
    private string _probeSwappedTo = "";
    private bool _probeDumpTaken;

    /// <summary>Screenshot mid-burst, not on a timer: the cast lives ~1s of a 2s
    /// loop, so a fixed-time dump usually catches the gap between casts.</summary>
    private void ProbeDumpWhenLive(string vantage)
    {
        if (_probeDumpTaken || _spellParticles is null) return;
        if (!_spellParticles.Diagnostics().Any(d => d.Live > 10)) return;
        _probeDumpTaken = true;
        _currentVantage = vantage;
        ArmGameplayDump();
    }

    private void UpdateCreatorProbe()
    {
        if (ProbeSpec is null) return;
        double now = NowSeconds();

        if (_probeStage < 0)
        {
            foreach (string part in ProbeSpec.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string key = part[..eq].Trim().ToLowerInvariant();
                string value = part[(eq + 1)..].Trim();
                if (key == "spell") _probeSpell = value;
                else if (key == "slot") _probeSlot = value;
                else if (key == "to") _probeTo = value;
            }
            Console.WriteLine($"[probe] armed: spell='{_probeSpell}' slot~'{_probeSlot}' to~'{_probeTo}'");
            _probeStage = 0;
            _probeStageAt = now;
            return;
        }

        switch (_probeStage)
        {
            case 0:   // boot: enter the creator world as soon as GL is up
                if (_gl is null || _worldLoadStarted) return;
                if (now - _probeStageAt < 1.0) return;   // let the glue settle a frame or two
                Console.WriteLine("[probe] entering creator world");
                EnterOfflineWorld();
                _probeStage = 1;
                _probeStageAt = now;
                return;

            case 1:   // world-wait: then select the spell and start a cast-only loop
                if (_worldLoading || !_creatorWorldRequested || _spellCatalog is null ||
                    _spellEffects is null || _spellParticles is null) return;
                if (now - _probeStageAt < 2.0) return;   // settle after the load fade
                var match = _spellCatalog.Spells
                    .Where(s => s.VisualId != 0 &&
                        s.Name.Contains(_probeSpell, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(s => s.Name.Equals(_probeSpell, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(s => s.Id)
                    .ToList();
                if (match.Count == 0)
                {
                    Console.WriteLine($"[probe] FAIL: no spell matching '{_probeSpell}'");
                    _probeStage = 4; _probeStageAt = now;
                    return;
                }
                Console.WriteLine($"[probe] selected spell {match[0].Id} '{match[0].Name}'");
                SelectCreatorSpell(match[0]);
                ProbeLogSpellInventory();
                _creatorLoopPrecast = false;
                _creatorLoopCast = true;
                _creatorLoopMissilePhase = false;
                _creatorLoopImpactPhase = false;
                _creatorLoopStateHold = false;
                _creatorLoopChannelHold = false;
                _creatorLoopAreaHold = false;
                _creatorLoopPeriod = 2f;
                _creatorLoopNextAt = 0;
                _creatorLoopOn = true;
                _probeStage = 2;
                _probeStageAt = now;
                return;

            case 2:   // accumulate the BEFORE census over several cast cycles, then swap
                if (now - _probeStageAt > 2.0)
                {
                    ProbeAccumulate(_probeBillboardBefore, _probeMeshBefore);
                    ProbeDumpWhenLive("creator-probe-before");
                }
                if (now - _probeStageAt < 8.0) return;
                Console.WriteLine("[probe] ===== CENSUS BEFORE SWAP =====");
                ProbePrintCensus(_probeBillboardBefore, _probeMeshBefore);
                ProbeApplySwap();
                _probeDumpTaken = false;
                _probeStage = 3;
                _probeStageAt = now;
                return;

            case 3:   // accumulate the AFTER census, then verdict + screenshot
                if (now - _probeStageAt > 2.0)
                {
                    ProbeAccumulate(_probeBillboardAfter, _probeMeshAfter);
                    ProbeDumpWhenLive("creator-probe-after");
                }
                if (now - _probeStageAt < 8.0) return;
                Console.WriteLine("[probe] ===== CENSUS AFTER SWAP =====");
                ProbePrintCensus(_probeBillboardAfter, _probeMeshAfter);
                ProbeVerdict();
                _probeStage = 4;
                _probeStageAt = now;
                return;

            case 4:   // linger long enough for the dump to flush, then quit
                if (now - _probeStageAt < 2.0) return;
                Console.WriteLine("[probe] done, quitting");
                Console.Out.Flush();
                _quitRequested = true;
                _probeStage = 5;
                return;
        }
    }

    /// <summary>The money table: every model in the selected spell, its texture
    /// slots, and per-emitter texture/geometry/recursion wiring. If an emitter
    /// has a geometry model, its on-screen pixels come from THAT file's texture
    /// table, not from the host slot the emitter's texture index names.</summary>
    private void ProbeLogSpellInventory()
    {
        if (_creatorSpell is not { } doc) return;
        foreach (var model in doc.Models.Values)
        {
            string kind = doc.GeometryHosts.TryGetValue(model.Path, out string? host)
                ? $"geometry (spawned by {host})" : "phase model";
            Console.WriteLine($"[probe] model {model.Path} ({kind})");
            foreach (var tex in model.Textures)
                Console.WriteLine($"[probe]   slot {tex.Index}: '{tex.Filename}' " +
                    $"emitters=[{string.Join(",", tex.ReferencedByEmitters)}]");
            if (M2Reader.Parse(model.Original) is not { } parsed) continue;
            for (int i = 0; i < parsed.ParticleEmitters.Count; i++)
            {
                M2ParticleEmitter e = parsed.ParticleEmitters[i];
                string tex = e.Texture >= 0 && e.Texture < parsed.Textures.Count
                    ? parsed.Textures[e.Texture].Filename : "<oob>";
                string geo = e.GeometryModel.Length > 0 ? $" GEOMETRY='{e.GeometryModel}'" : "";
                string rec = e.RecursionModel.Length > 0 ? $" RECURSION='{e.RecursionModel}'" : "";
                Console.WriteLine($"[probe]   emitter {i}: texIdx={e.Texture} tex='{tex}'{geo}{rec}" +
                    (geo.Length > 0 ? "  <-- billboards NOT drawn; per-particle meshes use the geometry file's own textures" : ""));
            }
        }
    }

    private void ProbeAccumulate(SortedSet<string> billboard, SortedSet<string> mesh)
    {
        if (_spellParticles is not null)
            foreach (var d in _spellParticles.Diagnostics())
                if (d.Live > 0 && d.Texture.Length > 0)
                    billboard.Add($"{d.Texture} (e{d.Emitter}{(d.TextureReady ? "" : ", TEXTURE MISSING")})");
        if (_spellEffectMeshes is not null)
            foreach (string bound in _spellEffectMeshes.BoundTexturesLastFrame)
                mesh.Add(bound);
    }

    private static void ProbePrintCensus(SortedSet<string> billboard, SortedSet<string> mesh)
    {
        Console.WriteLine(billboard.Count == 0
            ? "[probe] billboard particles drawn: (none)"
            : $"[probe] billboard particles drawn: {string.Join(" | ", billboard)}");
        Console.WriteLine(mesh.Count == 0
            ? "[probe] mesh batches drawn: (none)"
            : $"[probe] mesh batches drawn: {string.Join(" | ", mesh)}");
    }

    /// <summary>Apply the swap exactly the way the picker's Swap button does.</summary>
    private void ProbeApplySwap()
    {
        if (_creatorSpell is not { } doc) return;

        // Target slot: prefer a CAST-phase model, then any model, whose texture
        // filename contains the requested substring.
        var castPaths = doc.PhaseModels.Where(p => p.Stage == SpellStage.Cast)
            .Select(p => SpellVisualCatalog.ModelPath(p.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = doc.Models.Values
            .SelectMany(m => m.Textures.Select(t => (Model: m, Tex: t)))
            .Where(c => c.Tex.Filename.Contains(_probeSlot, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => castPaths.Contains(SpellVisualCatalog.ModelPath(c.Model.Path)) ? 0 : 1)
            .ToList();
        if (candidates.Count == 0)
        {
            Console.WriteLine($"[probe] FAIL: no texture slot matching '{_probeSlot}' in this spell");
            return;
        }

        // Replacement: a full path as given, else resolved from the spell's own tables.
        string replacement = _probeTo.Contains('\\') || _probeTo.Contains('/')
            ? _probeTo
            : doc.Models.Values.SelectMany(m => m.Textures)
                .FirstOrDefault(t => t.Filename.Contains(_probeTo, StringComparison.OrdinalIgnoreCase) &&
                                     !t.Filename.Contains(_probeSlot, StringComparison.OrdinalIgnoreCase))
                ?.Filename ?? "";
        if (replacement.Length == 0)
        {
            Console.WriteLine($"[probe] FAIL: no replacement texture matching '{_probeTo}'");
            return;
        }

        var (model, tex) = candidates[0];
        _probeSwappedFrom = tex.Filename;
        _probeSwappedTo = replacement;
        Console.WriteLine($"[probe] SWAP: {Path.GetFileName(model.Path)} slot {tex.Index} " +
                          $"'{tex.Filename}' -> '{replacement}' (all models: {_texSwapAllModels})");
        ApplyCreatorTextureSwapEverywhere(doc, model, tex.Index, replacement);
    }

    private void ProbeVerdict()
    {
        string oldName = Path.GetFileName(_probeSwappedFrom);
        string newName = Path.GetFileName(_probeSwappedTo);
        bool billboardStale = oldName.Length > 0 && _probeBillboardAfter.Any(t =>
            t.Contains(oldName, StringComparison.OrdinalIgnoreCase));
        bool meshStale = oldName.Length > 0 && _probeMeshAfter.Any(t =>
            t.Contains(oldName, StringComparison.OrdinalIgnoreCase));
        bool newSeen = newName.Length > 0 &&
            (_probeBillboardAfter.Any(t => t.Contains(newName, StringComparison.OrdinalIgnoreCase)) ||
             _probeMeshAfter.Any(t => t.Contains(newName, StringComparison.OrdinalIgnoreCase)));

        bool meshChanged = !_probeMeshBefore.SetEquals(_probeMeshAfter);
        bool billboardChanged = !_probeBillboardBefore.SetEquals(_probeBillboardAfter);

        Console.WriteLine("[probe] ===== VERDICT =====");
        Console.WriteLine($"[probe] billboard census changed: {billboardChanged}; " +
                          $"mesh census changed: {meshChanged}");
        if (_probeSwappedFrom.Length == 0)
            Console.WriteLine("[probe] VERDICT: swap was never applied (see FAIL above)");
        else if (billboardStale || meshStale)
            Console.WriteLine($"[probe] VERDICT: SWAP DID NOT TAKE - '{oldName}' still drawn after the swap " +
                $"(billboard={billboardStale} mesh={meshStale}); replacement '{newName}' seen={newSeen}");
        else if (_probeMeshBefore.Count > 0 && !meshChanged)
            Console.WriteLine("[probe] VERDICT: byte-swap took in the particle layer, but the MESH-DRAWN " +
                "art (geometry particles / glow planes) is unchanged - the visible pixels did NOT change");
        else if (!newSeen)
            Console.WriteLine($"[probe] VERDICT: old texture gone but replacement '{newName}' NOT drawn - " +
                "check the fx-load/mesh-build lines above for where it was dropped");
        else
            Console.WriteLine($"[probe] VERDICT: swap took effect - '{oldName}' gone, '{newName}' drawn");
    }
}
