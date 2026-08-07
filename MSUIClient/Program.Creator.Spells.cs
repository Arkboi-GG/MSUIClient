using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using MSUIClient.Creator;
using MSUIClient.Formats;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// The spell workshop: pick any spell, loop any phase, and tune its emitters in
// realtime. Edits are byte-patches over the effect M2s (the MangosSuperUI
// patchers, ported in Creator/), hot-swapped into SpellEffectSource via its
// model-override layer - the respawning loop shows every change within one
// cycle, no MPQ rebuild, no restart.
//
// Export writes the patched M2s at their ORIGINAL paths into a patch MPQ
// (drop into WoW/Data to see the tune in any client), plus a tuning JSON that
// MangosSuperUI's ApplySpellTuning pipeline can consume for a proper isolated
// custom-spell build.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private enum CreatorLoopMode { Cast, Impact, CastImpact, Missile, PrecastHold, StateHold, ChannelHold }

    private static readonly string[] CreatorLoopModeLabels =
        { "Cast", "Impact", "Cast + Impact", "Missile", "Precast (hold)", "State (hold)", "Channel (hold)" };

    private static readonly string[] CreatorBlendModes = { "0 Opaque", "1 Mod", "2 Alpha", "3 Add-Alpha", "4 Additive" };
    private static readonly string[] CreatorEmitterTypes = { "0 Point", "1 Sphere", "2 Plane", "3 Spline" };

    /// <summary>Everything the workshop knows about one effect M2 being tuned.</summary>
    private sealed class CreatorModelDoc
    {
        public required string Path;
        public required byte[] Original;
        public byte[] Working = [];
        public List<EmitterSnapshot> Emitters = [];
        public readonly Dictionary<int, EmitterPatch> Edits = [];
        // Whole-model dials (multipliers over the authored values + hue shift).
        public bool HueShift;
        public Vector3 HueColor = new(1f, 0.4f, 0.1f);
        public float RateMul = 1f, ScaleMul = 1f, LifeMul = 1f, SpeedMul = 1f, GravityAdd;
        public bool Modified;
    }

    private sealed class CreatorSpellDoc
    {
        public required SpellInfo Info;
        public SpellVisualStages Stages;
        public readonly List<(SpellStage Stage, string Path)> PhaseModels = [];
        public string? MissilePath;
        public readonly Dictionary<string, CreatorModelDoc> Models = new(StringComparer.OrdinalIgnoreCase);
    }

    private CreatorSpellDoc? _creatorSpell;
    private readonly byte[] _creatorSpellSearchBuf = new byte[64];
    private List<SpellInfo>? _creatorSpellResults;
    private bool _creatorSpellSearchDirty = true;

    private bool _creatorLoopOn;
    private int _creatorLoopMode = (int)CreatorLoopMode.CastImpact;
    private float _creatorLoopPeriod = 2f;
    private double _creatorLoopNextAt;
    private double _creatorLoopImpactAt = double.MaxValue;
    private string _creatorExportStatus = "";

    // ── loop machine (called every Update while the creator owns the world) ──

    private void UpdateCreatorSpellLoop()
    {
        if (!_creatorWorldRequested || !_creatorLoopOn || _creatorSpell is null) return;
        double now = NowSeconds();

        if (now >= _creatorLoopImpactAt)
        {
            _creatorLoopImpactAt = double.MaxValue;
            PresentSpellEffect(_creatorSpell.Info.Id, "impact");
        }
        if (now < _creatorLoopNextAt) return;
        _creatorLoopNextAt = now + Math.Max(_creatorLoopPeriod, 0.25f);

        uint spell = _creatorSpell.Info.Id;
        switch ((CreatorLoopMode)_creatorLoopMode)
        {
            case CreatorLoopMode.Cast: PresentSpellEffect(spell, "cast"); break;
            case CreatorLoopMode.Impact: PresentSpellEffect(spell, "impact"); break;
            case CreatorLoopMode.CastImpact:
                PresentSpellEffect(spell, "cast");
                _creatorLoopImpactAt = now + Math.Min(_creatorLoopPeriod * 0.5f, 0.9f);
                break;
            case CreatorLoopMode.Missile: SpawnCreatorMissile(); break;
            // Holds are respawned each tick so byte-patches keep landing.
            case CreatorLoopMode.PrecastHold: PresentSpellEffect(spell, "precast"); break;
            case CreatorLoopMode.StateHold: PresentSpellEffect(spell, "state"); break;
            case CreatorLoopMode.ChannelHold: PresentSpellEffect(spell, "channel"); break;
        }
    }

    private void SpawnCreatorMissile()
    {
        if (_creatorSpell?.MissilePath is not { Length: > 0 } path ||
            _spellEffects is null || _controller is null) return;

        Vector3 from = _controller.Position with { Z = _controller.Position.Z + 1.5f };
        Vector3 to;
        if (_creatorDummySpawned && _entities.TryGet(CreatorDummyGuid, out var dummy))
            to = dummy.Position with { Z = dummy.Position.Z + 1.5f };
        else
        {
            float yaw = _controller.Yaw;
            to = from + new Vector3(MathF.Cos(yaw), MathF.Sin(yaw), 0f) * 20f;
        }
        float speed = _creatorSpell.Info.Speed > 1f ? _creatorSpell.Info.Speed : 20f;
        double duration = Vector3.Distance(from, to) / speed;
        _spellEffects.SpawnMissile(LocalPlayerGuid, _creatorSpell.Info.Id, path,
            from, to, NowSeconds(), duration);
    }

    // ── document build / patch / hot-swap ────────────────────────────────────

    private void SelectCreatorSpell(in SpellInfo info)
    {
        // Clear any overrides the previous document installed.
        if (_creatorSpell is not null && _spellEffects is not null)
            foreach (var model in _creatorSpell.Models.Values)
                _spellEffects.SetModelOverride(model.Path, null);

        var doc = new CreatorSpellDoc { Info = info };
        if (_spellVisualCatalog?.TryGetStages(info.VisualId, out doc.Stages) != true)
        {
            _creatorSpell = doc;   // selectable, but the panel will say "no visual"
            return;
        }

        void AddKit(SpellStage stage, uint kitId)
        {
            if (kitId == 0 || _spellVisualCatalog?.TryGetKit(kitId, out SpellVisualKitInfo kit) != true) return;
            foreach (var effect in kit.Effects)
                if (effect.ModelPath.Length > 0) doc.PhaseModels.Add((stage, effect.ModelPath));
        }
        AddKit(SpellStage.Precast, doc.Stages.Precast);
        AddKit(SpellStage.Cast, doc.Stages.Cast);
        AddKit(SpellStage.Impact, doc.Stages.Impact);
        AddKit(SpellStage.State, doc.Stages.State);
        AddKit(SpellStage.Channel, doc.Stages.Channel);
        doc.MissilePath = _spellVisualCatalog?.MissilePath(doc.Stages);

        var paths = doc.PhaseModels.Select(p => p.Path).ToList();
        if (doc.MissilePath is { Length: > 0 }) paths.Add(doc.MissilePath);
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            byte[]? original = _spellEffects?.ReadOriginalModel(path);
            if (original is null) continue;
            var model = new CreatorModelDoc { Path = path, Original = original };
            model.Working = (byte[])original.Clone();
            model.Emitters = M2EmitterParser.ReadEmitters(model.Working);
            doc.Models[path] = model;
        }
        _creatorSpell = doc;
        _creatorLoopNextAt = 0;   // fire the loop immediately on next tick
    }

    /// <summary>Original bytes -> whole-model dials -> per-emitter absolute edits ->
    /// hot-swap into SpellEffectSource. Rebuilt from Original every time, so the
    /// multipliers never compound.</summary>
    private void RebuildCreatorModel(CreatorModelDoc model)
    {
        bool globalsActive = model.HueShift || model.GravityAdd != 0f ||
            model.RateMul != 1f || model.ScaleMul != 1f || model.LifeMul != 1f || model.SpeedMul != 1f;
        byte[] working;
        if (globalsActive)
        {
            var globals = new M2ParticlePatcher.ParticlePatchParams
            {
                UseHueShift = model.HueShift,
                HueShiftColor = model.HueShift ? PackArgb(model.HueColor) : 0,
                EmissionRateMultiplier = model.RateMul == 1f ? null : model.RateMul,
                ScaleMultiplier = model.ScaleMul == 1f ? null : model.ScaleMul,
                LifespanMultiplier = model.LifeMul == 1f ? null : model.LifeMul,
                EmissionSpeedMultiplier = model.SpeedMul == 1f ? null : model.SpeedMul,
                GravityAdd = model.GravityAdd == 0f ? null : model.GravityAdd,
            };
            working = M2ParticlePatcher.PatchParticles(model.Original, globals)
                      ?? (byte[])model.Original.Clone();
        }
        else working = (byte[])model.Original.Clone();

        foreach (var edit in model.Edits.Values)
            M2EmitterParser.ApplyEmitterPatch(working, edit);

        model.Working = working;
        model.Emitters = M2EmitterParser.ReadEmitters(working);
        model.Modified = globalsActive || model.Edits.Count > 0;
        _spellEffects?.SetModelOverride(model.Path, model.Modified ? working : null);
    }

    private static uint PackArgb(Vector3 rgb) =>
        0xFF000000u |
        ((uint)Math.Clamp((int)(rgb.X * 255f), 0, 255) << 16) |
        ((uint)Math.Clamp((int)(rgb.Y * 255f), 0, 255) << 8) |
        (uint)Math.Clamp((int)(rgb.Z * 255f), 0, 255);

    // ── the panel ────────────────────────────────────────────────────────────

    private partial void DrawCreatorSpellsPanel()
    {
        if (!BeginCreatorPanel("Spell Workshop", 460f)) return;
        float cs = CreatorUiScale;

        if (_spellCatalog is null || _spellVisualCatalog is null || _spellEffects is null)
        {
            ImGui.TextWrapped("Spell catalogs are unavailable - check the console.");
            EndCreatorPanel();
            return;
        }

        // Spell picker.
        ImGui.SetNextItemWidth(220f * cs);
        if (ImGui.InputText("##spell-search", _creatorSpellSearchBuf, (uint)_creatorSpellSearchBuf.Length))
            _creatorSpellSearchDirty = true;
        ImGui.SameLine();
        ImGui.TextDisabled(_creatorSpell is null ? "pick a spell"
            : $"{_creatorSpell.Info.Id} {_creatorSpell.Info.Name}");

        string query = BufToString(_creatorSpellSearchBuf);
        if (_creatorSpellSearchDirty)
        {
            _creatorSpellSearchDirty = false;
            _creatorSpellResults = query.Length >= 2
                ? (uint.TryParse(query, out uint asId)
                    ? _spellCatalog.Spells.Where(s => s.Id == asId).ToList()
                    : _spellCatalog.Spells
                        .Where(s => s.VisualId != 0 &&
                                    s.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(s => s.Id).Take(40).ToList())
                : null;
        }
        if (_creatorSpellResults is { Count: > 0 } results && query.Length >= 2)
        {
            if (ImGui.BeginChild("##spell-results", new Vector2(0f, 130f * cs), true))
            {
                foreach (var spell in results)
                {
                    string rank = spell.Rank.Length > 0 ? $" ({spell.Rank})" : "";
                    if (ImGui.Selectable($"{spell.Id}  {spell.Name}{rank}"))
                    {
                        SelectCreatorSpell(spell);
                        Array.Clear(_creatorSpellSearchBuf);
                        _creatorSpellSearchDirty = true;
                    }
                }
            }
            ImGui.EndChild();
        }

        if (_creatorSpell is null) { EndCreatorPanel(); return; }
        var doc = _creatorSpell;

        if (doc.Models.Count == 0)
        {
            ImGui.TextWrapped("This spell's visual has no effect models to tune " +
                              "(or the models failed to load).");
            EndCreatorPanel();
            return;
        }

        // Loop controls, their own drill-down.
        ImGui.Spacing();
        if (CreatorCategory("ws-loop", _creatorLoopOn ? "Loop  (running)" : "Loop", defaultOpen: true))
        {
            ImGui.Indent(10f * cs);
            ImGui.SetNextItemWidth(CreatorComboWidth(CreatorLoopModeLabels));
            ImGui.Combo("##loop-mode", ref _creatorLoopMode, CreatorLoopModeLabels, CreatorLoopModeLabels.Length);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(110f * cs);
            ImGui.SliderFloat("##loop-period", ref _creatorLoopPeriod, 0.5f, 6f, "%.1fs");
            ImGui.SameLine();
            if (CreatorButton(_creatorLoopOn ? "Stop" : "Loop", 70f * cs))
            {
                _creatorLoopOn = !_creatorLoopOn;
                _creatorLoopNextAt = 0;
                if (!_creatorLoopOn && _presentedEffectSpell != 0 && _spellEffects is not null)
                {
                    _spellEffects.Reap(LocalPlayerGuid, _presentedEffectSpell);
                    _presentedEffectSpell = 0;
                }
            }
            if ((CreatorLoopMode)_creatorLoopMode == CreatorLoopMode.Missile && doc.MissilePath is null)
                ImGui.TextDisabled("This spell has no missile.");
            ImGui.Unindent(10f * cs);
            ImGui.Spacing();
        }

        // Per-model editors, grouped under the phases that use them, each a
        // vanilla +/- drill-down (id is the path; the label's * marker may change).
        ImGui.Spacing();
        foreach (var model in doc.Models.Values)
        {
            string phases = string.Join("+", doc.PhaseModels
                .Where(p => string.Equals(p.Path, model.Path, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Stage.ToString().ToLowerInvariant()).Distinct());
            if (phases.Length == 0 &&
                string.Equals(doc.MissilePath, model.Path, StringComparison.OrdinalIgnoreCase))
                phases = "missile";
            string label = $"{phases}: {Path.GetFileName(model.Path)}" + (model.Modified ? " *" : "");
            if (!CreatorCategory($"ws-{model.Path}", label)) continue;
            ImGui.PushID(model.Path);
            ImGui.Indent(10f * cs);
            DrawCreatorModelEditor(model, cs);
            ImGui.Unindent(10f * cs);
            ImGui.PopID();
        }

        // Export.
        ImGui.Spacing();
        ImGui.Separator();
        if (CreatorButton("Export patch MPQ"))
            ExportCreatorPatch(doc);
        ImGui.SameLine();
        if (CreatorButton("Save tuning JSON"))
            ExportCreatorTuningJson(doc);
        ImGui.SameLine();
        if (CreatorButton("Reset all"))
        {
            foreach (var model in doc.Models.Values)
            {
                model.Edits.Clear();
                model.HueShift = false;
                model.RateMul = model.ScaleMul = model.LifeMul = model.SpeedMul = 1f;
                model.GravityAdd = 0f;
                RebuildCreatorModel(model);
            }
        }
        if (_creatorExportStatus.Length > 0) ImGui.TextWrapped(_creatorExportStatus);

        EndCreatorPanel();
    }

    private void DrawCreatorModelEditor(CreatorModelDoc model, float cs)
    {
        bool dirty = false;

        // Whole-model dials.
        ImGui.TextDisabled("MODEL DIALS (multipliers over the authored values)");
        dirty |= ImGui.Checkbox("Hue shift", ref model.HueShift);
        if (model.HueShift)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(140f * cs);
            dirty |= ImGui.ColorEdit3("##hue", ref model.HueColor, ImGuiColorEditFlags.NoInputs);
        }
        ImGui.SetNextItemWidth(180f * cs);
        dirty |= ImGui.SliderFloat("Density", ref model.RateMul, 0.1f, 10f, "%.2fx",
            ImGuiSliderFlags.Logarithmic);
        ImGui.SetNextItemWidth(180f * cs);
        dirty |= ImGui.SliderFloat("Size", ref model.ScaleMul, 0.1f, 10f, "%.2fx",
            ImGuiSliderFlags.Logarithmic);
        ImGui.SetNextItemWidth(180f * cs);
        dirty |= ImGui.SliderFloat("Duration", ref model.LifeMul, 0.1f, 5f, "%.2fx",
            ImGuiSliderFlags.Logarithmic);
        ImGui.SetNextItemWidth(180f * cs);
        dirty |= ImGui.SliderFloat("Speed", ref model.SpeedMul, 0.1f, 5f, "%.2fx",
            ImGuiSliderFlags.Logarithmic);
        ImGui.SetNextItemWidth(180f * cs);
        dirty |= ImGui.SliderFloat("Gravity +", ref model.GravityAdd, -10f, 10f, "%.2f");

        // Per-emitter absolute values.
        foreach (var emitter in model.Emitters)
        {
            // Category id is model path + index: stable while blend/type in the
            // label change as they are edited.
            if (!CreatorCategory($"ws-{model.Path}-em{emitter.Index}",
                $"Emitter {emitter.Index}  " +
                $"(tex {emitter.TextureId}, blend {emitter.BlendMode}, type {emitter.EmitterType})"))
                continue;
            ImGui.PushID(emitter.Index);
            ImGui.Indent(10f * cs);

            EmitterPatch edit = model.Edits.TryGetValue(emitter.Index, out var found)
                ? found : new EmitterPatch { EmitterIndex = emitter.Index };

            int blend = edit.BlendMode ?? emitter.BlendMode;
            ImGui.SetNextItemWidth(130f * cs);
            if (ImGui.Combo("Blend", ref blend, CreatorBlendModes, CreatorBlendModes.Length))
            { edit.BlendMode = blend; dirty = true; model.Edits[emitter.Index] = edit; }

            int type = edit.EmitterType ?? emitter.EmitterType;
            ImGui.SetNextItemWidth(130f * cs);
            if (ImGui.Combo("Emitter type", ref type, CreatorEmitterTypes, CreatorEmitterTypes.Length))
            { edit.EmitterType = type; dirty = true; model.Edits[emitter.Index] = edit; }

            bool TrackSlider(string label, string track, float min, float max,
                Func<EmitterPatch, float?> get, Action<EmitterPatch, float> set)
            {
                float? authored = emitter.TrackValues.GetValueOrDefault(track);
                if (authored is null) return false;   // no keyframes - nothing to patch
                float value = get(edit) ?? authored.Value;
                int keys = emitter.TrackKeyframeCounts.GetValueOrDefault(track);
                ImGui.SetNextItemWidth(180f * cs);
                bool moved = ImGui.SliderFloat(keys > 1 ? $"{label} *" : label, ref value, min, max, "%.3f");
                if (moved) { set(edit, value); model.Edits[emitter.Index] = edit; }
                return moved;
            }

            dirty |= TrackSlider("Rate", "emissionRate", 0f, 200f, e => e.EmissionRate, (e, v) => e.EmissionRate = v);
            dirty |= TrackSlider("Speed", "emissionSpeed", 0f, 30f, e => e.EmissionSpeed, (e, v) => e.EmissionSpeed = v);
            dirty |= TrackSlider("Speed var", "speedVariation", 0f, 2f, e => e.SpeedVariation, (e, v) => e.SpeedVariation = v);
            dirty |= TrackSlider("Gravity", "gravity", -20f, 20f, e => e.Gravity, (e, v) => e.Gravity = v);
            dirty |= TrackSlider("Lifespan", "lifespan", 0.05f, 10f, e => e.Lifespan, (e, v) => e.Lifespan = v);
            dirty |= TrackSlider("Spread V", "verticalRange", 0f, MathF.PI, e => e.VerticalRange, (e, v) => e.VerticalRange = v);
            dirty |= TrackSlider("Spread H", "horizontalRange", 0f, MathF.PI, e => e.HorizontalRange, (e, v) => e.HorizontalRange = v);
            dirty |= TrackSlider("Area L", "emissionAreaLength", 0f, 20f, e => e.EmissionAreaLength, (e, v) => e.EmissionAreaLength = v);
            dirty |= TrackSlider("Area W", "emissionAreaWidth", 0f, 20f, e => e.EmissionAreaWidth, (e, v) => e.EmissionAreaWidth = v);

            var scale = new Vector3(
                edit.ScaleStart ?? emitter.ScaleStart,
                edit.ScaleMid ?? emitter.ScaleMid,
                edit.ScaleEnd ?? emitter.ScaleEnd);
            ImGui.SetNextItemWidth(200f * cs);
            if (ImGui.SliderFloat3("Scale s/m/e", ref scale, 0f, 8f, "%.2f"))
            {
                edit.ScaleStart = scale.X; edit.ScaleMid = scale.Y; edit.ScaleEnd = scale.Z;
                model.Edits[emitter.Index] = edit;
                dirty = true;
            }

            if (ImGui.SmallButton("Reset emitter") && model.Edits.Remove(emitter.Index)) dirty = true;
            ImGui.Unindent(10f * cs);
            ImGui.PopID();
        }

        if (dirty) RebuildCreatorModel(model);
    }

    // ── export ───────────────────────────────────────────────────────────────

    private string CreatorExportDir()
    {
        string dir = Path.Combine(_config.RepoRoot, "creator-exports");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Patched M2s at their original paths in a patch MPQ - drop it into the
    /// client's Data folder (and delete the WDB cache) to see the tune everywhere.</summary>
    private void ExportCreatorPatch(CreatorSpellDoc doc)
    {
        var modified = doc.Models.Values.Where(m => m.Modified).ToList();
        if (modified.Count == 0) { _creatorExportStatus = "Nothing modified - nothing to export."; return; }

        var builder = new MpqBuilderService(new Creator.ILogger<MpqBuilderService>());
        foreach (var model in modified) builder.AddFile(model.Path, model.Working);
        string output = Path.Combine(CreatorExportDir(), "patch-4.MPQ");
        _creatorExportStatus = builder.Build(output)
            ? $"Wrote {modified.Count} model(s) to {output}"
            : "MPQ build FAILED - see the console.";
        Console.WriteLine($"[creator] {_creatorExportStatus}");
    }

    /// <summary>The tuning as JSON: whole-model dials + per-emitter absolute values,
    /// keyed by model path - the document MangosSuperUI's tuning pipeline consumes.</summary>
    private void ExportCreatorTuningJson(CreatorSpellDoc doc)
    {
        var payload = new
        {
            spellId = doc.Info.Id,
            spellName = doc.Info.Name,
            exportedBy = "MSUIClient creator mode",
            models = doc.Models.Values.Where(m => m.Modified).Select(m => new
            {
                path = m.Path,
                dials = new
                {
                    hueShift = m.HueShift,
                    hueColor = m.HueShift ? $"#{PackArgb(m.HueColor) & 0xFFFFFF:x6}" : null,
                    rateMultiplier = m.RateMul,
                    scaleMultiplier = m.ScaleMul,
                    lifespanMultiplier = m.LifeMul,
                    speedMultiplier = m.SpeedMul,
                    gravityAdd = m.GravityAdd,
                },
                emitters = m.Edits.Values,
            }),
        };
        string path = Path.Combine(CreatorExportDir(), $"spell-{doc.Info.Id}-tuning.json");
        File.WriteAllText(path, JsonSerializer.Serialize(payload,
            new JsonSerializerOptions { WriteIndented = true }));
        _creatorExportStatus = $"Wrote {path}";
        Console.WriteLine($"[creator] {_creatorExportStatus}");
    }
}
