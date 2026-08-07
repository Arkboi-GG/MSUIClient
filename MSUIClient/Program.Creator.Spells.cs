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
// Every effect model's TEXTURE TABLE is exposed: each phase lists the actual
// BLPs it is built from (with thumbnails), and each BLP that particles
// reference gets its OWN hue dial - so a phase composed of several images can
// be recolored per image, not just as a whole. The whole-model hue shift
// remains as a master dial; per-BLP dials override it for their emitters.
//
// Export writes the patched M2s at their ORIGINAL paths into a patch MPQ
// (drop into WoW/Data to see the tune in any client), plus a tuning JSON that
// MangosSuperUI's ApplySpellTuning pipeline can consume for a proper isolated
// custom-spell build.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private static readonly string[] CreatorBlendModes = { "0 Opaque", "1 Mod", "2 Alpha", "3 Add-Alpha", "4 Additive" };
    private static readonly string[] CreatorEmitterTypes = { "0 Point", "1 Sphere", "2 Plane", "3 Spline" };

    /// <summary>A per-BLP hue dial: rotate every particle color of the emitters that
    /// reference this texture toward Color, preserving luminance/saturation.</summary>
    private sealed class CreatorTexHue
    {
        public bool On;
        public Vector3 Color = new(1f, 0.4f, 0.1f);
    }

    /// <summary>Everything the workshop knows about one effect M2 being tuned.</summary>
    private sealed class CreatorModelDoc
    {
        public required string Path;
        public required byte[] Original;
        public byte[] Working = [];
        public List<EmitterSnapshot> Emitters = [];
        public readonly Dictionary<int, EmitterPatch> Edits = [];
        // The M2's texture table (the actual BLPs), with emitter back-references.
        public List<Creator.M2TextureEntry> Textures = [];
        public readonly Dictionary<int, CreatorTexHue> TextureHues = [];
        // True palette swaps: the BLP's own pixels are hue-mapped (live via the
        // renderers' tint layer, and baked into the export). For textures whose
        // color is authored into the art, where the emitter-hue dial does nothing.
        public readonly Dictionary<int, CreatorTexHue> TextureTints = [];
        // Slot -> replacement BLP path: splice any existing image into this model
        // (patched into the M2's texture table, so it exports too). Also the way
        // to recolor a SHARED BLP for one phase only: swap the slot to different
        // art first, then tint the swap - path-keyed tints stop bleeding.
        public readonly Dictionary<int, string> TextureSwaps = [];
        // Emitters switched off wholesale (their emission is zeroed in the bytes).
        public readonly HashSet<int> DisabledEmitters = [];
        // Emitter index -> resolved geometry-model path for emitters that spawn a
        // per-particle M2. Those emitters NEVER draw their billboard texture: the
        // on-screen pixels come from the geometry file's OWN texture table, so a
        // swap on their host slot must follow the pixels into that model.
        public readonly Dictionary<int, string> EmitterGeometry = [];
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
        /// <summary>Geometry-particle models (per-particle M2s like cloud puffs):
        /// path -> the host model file that spawns them. Their art lives in their
        /// OWN texture tables, invisible from the host's slots.</summary>
        public readonly Dictionary<string, string> GeometryHosts = new(StringComparer.OrdinalIgnoreCase);
    }

    private CreatorSpellDoc? _creatorSpell;
    private readonly byte[] _creatorSpellSearchBuf = new byte[64];
    private List<SpellInfo>? _creatorSpellResults;
    private bool _creatorSpellSearchDirty = true;

    // Which phases the loop plays each cycle - the user picks EXACTLY what loops.
    private bool _creatorLoopPrecast;
    private bool _creatorLoopCast = true;
    private bool _creatorLoopMissilePhase;
    private bool _creatorLoopImpactPhase = true;
    private bool _creatorLoopStateHold;
    private bool _creatorLoopChannelHold;
    private bool _creatorLoopAreaHold;

    // The area machine (Blizzard / Rain of Fire / Flamestrike rains): the live
    // client drives it from the server's DynamicObject; the creator drives the
    // SAME SpawnAreaVisual machinery from a synthetic key at the target spot.
    private const ulong CreatorAreaGuid = 0xF000_0000_0000_00F0UL;
    private float _creatorAreaRadius = 8f;
    private bool _creatorAreaActive;

    private bool _creatorLoopOn;
    private float _creatorLoopPeriod = 2f;
    private double _creatorLoopNextAt;
    private double _creatorLoopCastAt = double.MaxValue;
    private double _creatorLoopMissileAt = double.MaxValue;
    private double _creatorLoopImpactAt = double.MaxValue;
    private string _creatorExportStatus = "";

    // ── loop machine (called every Update while the creator owns the world) ──
    //
    // Each cycle sequences the CHECKED phases: precast fires at the tick, cast
    // releases after a short precast hold, the missile launches with the cast,
    // and impact lands when the missile arrives (or shortly after the cast when
    // no missile is looped). Impact is anchored ON the spawned target when one
    // exists. State/channel are holds, re-presented each tick so byte-patches
    // keep landing.

    private void UpdateCreatorSpellLoop()
    {
        if (!_creatorWorldRequested) return;
        UpdateCreatorAreaVisual(NowSeconds());
        if (!_creatorLoopOn || _creatorSpell is null) return;
        double now = NowSeconds();
        uint spell = _creatorSpell.Info.Id;

        if (now >= _creatorLoopCastAt)
        {
            _creatorLoopCastAt = double.MaxValue;
            PresentSpellEffect(spell, "cast");
        }
        if (now >= _creatorLoopMissileAt)
        {
            _creatorLoopMissileAt = double.MaxValue;
            double flight = SpawnCreatorMissile();
            if (_creatorLoopImpactPhase)
                _creatorLoopImpactAt = now + (flight > 0 ? flight : 0.5);
        }
        if (now >= _creatorLoopImpactAt)
        {
            _creatorLoopImpactAt = double.MaxValue;
            ulong target = CreatorMissileTargetGuid();
            PresentSpellEffect(spell, "impact", target != 0 ? target : null);
        }

        if (now < _creatorLoopNextAt) return;
        _creatorLoopNextAt = now + Math.Max(_creatorLoopPeriod, 0.25f);

        bool any = false;
        if (_creatorLoopPrecast) { PresentSpellEffect(spell, "precast"); any = true; }
        // Cast (and the missile it releases) waits out a short precast hold when
        // precast is looped too; otherwise it fires at the tick.
        double castDelay = _creatorLoopPrecast ? Math.Min(_creatorLoopPeriod * 0.35, 1.2) : 0.0;
        if (_creatorLoopCast) { _creatorLoopCastAt = now + castDelay; any = true; }
        if (_creatorLoopMissilePhase) { _creatorLoopMissileAt = now + castDelay; any = true; }
        else if (_creatorLoopImpactPhase)
        {
            // No missile to carry it: impact lands shortly after the cast release.
            double impactDelay = castDelay +
                (_creatorLoopCast ? Math.Min(_creatorLoopPeriod * 0.45, 0.9) : 0.0);
            _creatorLoopImpactAt = now + impactDelay;
            any = true;
        }
        if (_creatorLoopStateHold) { PresentSpellEffect(spell, "state"); any = true; }
        if (_creatorLoopChannelHold) { PresentSpellEffect(spell, "channel"); any = true; }
        if (_creatorLoopAreaHold) any = true;   // the hold runs continuously below
        if (!any) _creatorLoopOn = false;   // nothing checked - stop rather than spin
    }

    /// <summary>
    /// Keep the DynamicObject-style area visual (the falling-shard rain +
    /// looping centre model) alive at the target spot while the loop wants it -
    /// the exact machinery UpdateDynamicObjectVisuals drives on the live path,
    /// minus the server. Runs every Update so the rain follows a moving target
    /// and dies the moment the loop stops.
    /// </summary>
    private void UpdateCreatorAreaVisual(double now)
    {
        bool want = _creatorLoopOn && _creatorLoopAreaHold && _creatorSpell is { } doc &&
                    _spellEffects is not null &&
                    _spellVisualCatalog?.TryGetAreaVisual(doc.Info.VisualId,
                        out SpellAreaVisualInfo _) == true;
        if (!want)
        {
            if (_creatorAreaActive)
            {
                _spellEffects?.ReapArea(CreatorAreaGuid);
                _spellSounds?.StopHold(CreatorAreaGuid);
                _creatorAreaActive = false;
            }
            return;
        }

        var spell = _creatorSpell!.Info.Id;
        _spellVisualCatalog!.TryGetAreaVisual(_creatorSpell.Info.VisualId,
            out SpellAreaVisualInfo area);
        Vector3 position = CreatorAreaPosition();
        if (!_creatorAreaActive)
        {
            bool loopingSound = _spellSounds?.IsAuthoredLoop(area.Sound) == true;
            Action<uint, ulong, Vector3>? birthSound = !loopingSound && area.Emitters.Count != 0
                ? (sound, key, at) => PlaySpellSoundAt(key, sound, at, trackHold: false)
                : null;
            int spawned = _spellEffects!.SpawnAreaVisual(
                CreatorAreaGuid, spell, area, position, _creatorAreaRadius, now, birthSound);
            if (loopingSound)
                PlaySpellSoundAt(CreatorAreaGuid, area.Sound, position, forceLoop: true);
            _creatorAreaActive = true;
            Console.WriteLine($"[creator] area visual up: spell {spell} radius {_creatorAreaRadius:0.0} " +
                              $"emitters {area.Emitters.Count} " +
                              $"rate {area.Emitters.Sum(e => e.InstancesPerSecond):0.0}/s loaded {spawned}");
        }
        else
        {
            _spellEffects!.UpdateAreaVisual(CreatorAreaGuid, spell, position, _creatorAreaRadius);
        }
    }

    /// <summary>Where the area rain stands: on the targeted/last spawn, else 15 yd
    /// ahead of the player on the ground.</summary>
    private Vector3 CreatorAreaPosition()
    {
        ulong target = CreatorMissileTargetGuid();
        if (target != 0 && _entities.TryGet(target, out var entity)) return entity.Position;
        if (_controller is null) return default;
        var forward = new Vector3(MathF.Cos(_controller.Yaw), MathF.Sin(_controller.Yaw), 0f);
        Vector3 spot = _controller.Position + forward * 15f;
        if (_terrain?.SampleHeight(spot.X, spot.Y) is float ground) spot.Z = ground;
        return spot;
    }

    /// <summary>Launch the missile at the creator target (or straight ahead) and
    /// return its flight time in seconds, -1 when this spell has no missile.</summary>
    private double SpawnCreatorMissile()
    {
        if (_creatorSpell?.MissilePath is not { Length: > 0 } path ||
            _spellEffects is null || _controller is null) return -1;

        Vector3 from = _controller.Position with { Z = _controller.Position.Z + 1.5f };
        Vector3 to;
        ulong targetGuid = CreatorMissileTargetGuid();
        if (targetGuid != 0 && _entities.TryGet(targetGuid, out var dummy))
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
        return duration;
    }

    // ── document build / patch / hot-swap ────────────────────────────────────

    /// <summary>The BLP a texture slot actually renders with: the user's swap if
    /// one is set, else the authored filename.</summary>
    private static string EffectiveTexturePath(CreatorModelDoc model, int texIndex)
    {
        if (model.TextureSwaps.TryGetValue(texIndex, out string? swapped)) return swapped;
        return model.Textures.FirstOrDefault(t => t.Index == texIndex)?.Filename ?? "";
    }

    /// <summary>Push (or clear) one texture's palette swap into all three effect
    /// renderers so the world shows it immediately. Keys on the slot's EFFECTIVE
    /// path so tints follow swaps.</summary>
    private void SetCreatorTextureTint(CreatorModelDoc model, int texIndex, Vector3? color)
    {
        string path = EffectiveTexturePath(model, texIndex);
        if (path.Length == 0) return;
        uint? packed = color is { } c ? PackArgb(c) & 0x00FFFFFF : null;
        _spellParticles?.SetTextureTint(path, packed);
        _spellEffectMeshes?.SetTextureTint(path, packed);
        _spellRibbons?.SetTextureTint(path, packed);
    }

    /// <summary>Swap (or with null, restore) a texture slot's BLP. An active tint
    /// moves with the slot: cleared from the old path, re-applied on the new.</summary>
    private void ApplyCreatorTextureSwap(CreatorModelDoc model, int texIndex, string? newPath)
    {
        bool tintOn = model.TextureTints.TryGetValue(texIndex, out var tint) && tint.On;
        if (tintOn) SetCreatorTextureTint(model, texIndex, null);
        if (newPath is null or { Length: 0 }) model.TextureSwaps.Remove(texIndex);
        else model.TextureSwaps[texIndex] = newPath;
        if (tintOn) SetCreatorTextureTint(model, texIndex, tint!.Color);
        RebuildCreatorModel(model);
    }

    /// <summary>
    /// The swap the picker actually performs. The same BLP usually appears in
    /// SEVERAL of a spell's phase models (CLOUDS.BLP lives in the precast hand
    /// AND the cast hand of Cone of Cold) - swapping one slot while the phase
    /// you are watching draws another model's copy looks like "the swap did
    /// nothing". With <see cref="_texSwapAllModels"/> (default) the swap follows
    /// the IMAGE: every model in the spell whose table holds the same original
    /// gets the replacement too.
    /// </summary>
    private void ApplyCreatorTextureSwapEverywhere(
        CreatorSpellDoc doc, CreatorModelDoc primary, int texIndex, string? newPath)
    {
        string original = primary.Textures.FirstOrDefault(t => t.Index == texIndex)?.Filename ?? "";
        ApplyCreatorTextureSwap(primary, texIndex, newPath);
        PropagateSwapToGeometry(doc, primary, texIndex, newPath);
        if (!_texSwapAllModels || original.Length == 0) return;
        foreach (var other in doc.Models.Values)
        {
            if (ReferenceEquals(other, primary)) continue;
            foreach (var tex in other.Textures)
                if (string.Equals(tex.Filename, original, StringComparison.OrdinalIgnoreCase))
                {
                    ApplyCreatorTextureSwap(other, tex.Index, newPath);
                    PropagateSwapToGeometry(doc, other, tex.Index, newPath);
                }
        }
    }

    /// <summary>
    /// The swap must FOLLOW THE PIXELS. An emitter with a geometry model never
    /// draws its billboard texture - each particle is a little M2 whose art
    /// lives in the geometry file's OWN texture table (Cone of Cold's hand
    /// lists CLOUDS.BLP, but the visible puffs are ConeofCold_Geo meshes
    /// textured with CLOUDS4.BLP). Swapping the host slot alone is invisible,
    /// which read as "the swap does nothing". So: when a swapped slot's
    /// emitters spawn geometry, apply the same replacement (or restore) to the
    /// geometry model's texture slots too.
    /// </summary>
    private void PropagateSwapToGeometry(
        CreatorSpellDoc doc, CreatorModelDoc model, int texIndex, string? newPath)
    {
        var tex = model.Textures.FirstOrDefault(t => t.Index == texIndex);
        if (tex is null || model.EmitterGeometry.Count == 0) return;
        foreach (string geoPath in tex.ReferencedByEmitters
                     .Select(e => model.EmitterGeometry.GetValueOrDefault(e))
                     .Where(p => p is { Length: > 0 })
                     .Distinct(StringComparer.OrdinalIgnoreCase)!)
        {
            if (!doc.Models.TryGetValue(geoPath, out var geoDoc) ||
                ReferenceEquals(geoDoc, model)) continue;
            Console.WriteLine($"[creator] slot {texIndex} of {Path.GetFileName(model.Path)} is drawn " +
                $"as {Path.GetFileName(geoPath)} per-particle meshes - " +
                $"{(newPath is null ? "restoring" : "swapping")} that model's art too");
            foreach (var geoTex in geoDoc.Textures)
                ApplyCreatorTextureSwap(geoDoc, geoTex.Index, newPath);
        }
    }

    private void SelectCreatorSpell(in SpellInfo info)
    {
        // Clear any overrides, tints and color hues the previous document installed.
        if (_creatorSpell is not null)
            foreach (var model in _creatorSpell.Models.Values)
            {
                _spellEffects?.SetModelOverride(model.Path, null);
                string renderPath = SpellVisualCatalog.ModelPath(model.Path);
                _spellParticles?.SetGeometryModelOverride(renderPath, null);
                _spellRibbons?.SetModelColorHue(renderPath, null);
                _spellEffectMeshes?.SetModelColorHue(renderPath, null);
                _spellEffectMeshes?.InvalidateModel(renderPath);
                foreach (int texIndex in model.TextureTints.Keys)
                    SetCreatorTextureTint(model, texIndex, null);
            }
        _spellEffects?.ReapArea(CreatorAreaGuid);
        _spellSounds?.StopHold(CreatorAreaGuid);
        _creatorAreaActive = false;

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
            model.Textures = Creator.M2TextureParser.ParseTextures(model.Original);
            doc.Models[path] = model;
        }

        // GEOMETRY-PARTICLE models: an emitter can spawn a little M2 per particle
        // (Cone of Cold's cloud puffs) whose art lives in ITS OWN texture table -
        // completely invisible from the host model's slots, which is why editing
        // the host's CLOUDS slot changed nothing on screen. Pull each geometry
        // model into the workshop as a first-class editable model.
        foreach (var (hostPath, host) in doc.Models.ToList())
        {
            if (M2Reader.Parse(host.Original) is not { } parsed) continue;
            for (int emitterIndex = 0; emitterIndex < parsed.ParticleEmitters.Count; emitterIndex++)
            {
                M2ParticleEmitter emitter = parsed.ParticleEmitters[emitterIndex];
                if (emitter.GeometryModel.Length == 0) continue;
                string geoPath = SpellVisualCatalog.ModelPath(emitter.GeometryModel);
                host.EmitterGeometry[emitterIndex] = geoPath;
                if (doc.Models.ContainsKey(geoPath))
                {
                    doc.GeometryHosts.TryAdd(geoPath, Path.GetFileName(hostPath));
                    continue;
                }
                byte[]? original = _spellEffects?.ReadOriginalModel(geoPath);
                if (original is null) continue;
                var geoModel = new CreatorModelDoc { Path = geoPath, Original = original };
                geoModel.Working = (byte[])original.Clone();
                geoModel.Emitters = M2EmitterParser.ReadEmitters(geoModel.Working);
                geoModel.Textures = Creator.M2TextureParser.ParseTextures(geoModel.Original);
                doc.Models[geoPath] = geoModel;
                doc.GeometryHosts[geoPath] = Path.GetFileName(hostPath);
                Console.WriteLine($"[creator] geometry model {Path.GetFileName(geoPath)} " +
                                  $"(spawned by {Path.GetFileName(hostPath)}) joined the workshop");
            }
        }

        _creatorSpell = doc;
        _creatorLoopNextAt = 0;   // fire the loop immediately on next tick
    }

    /// <summary>Original bytes -> whole-model dials -> per-BLP hue dials ->
    /// per-emitter absolute edits -> hot-swap into SpellEffectSource. Rebuilt from
    /// Original every time, so the multipliers never compound.</summary>
    private void RebuildCreatorModel(CreatorModelDoc model)
    {
        bool globalsActive = model.HueShift || model.GravityAdd != 0f ||
            model.RateMul != 1f || model.ScaleMul != 1f || model.LifeMul != 1f || model.SpeedMul != 1f;
        bool texHuesActive = model.TextureHues.Any(h => h.Value.On);
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

        // Per-BLP hue dials AFTER the master hue: the per-texture target simply
        // wins for the emitters that reference that texture (the rotation sets
        // the target hue absolutely, it does not stack).
        if (texHuesActive)
        {
            foreach (var (texIndex, hue) in model.TextureHues)
            {
                if (!hue.On) continue;
                var tex = model.Textures.FirstOrDefault(t => t.Index == texIndex);
                if (tex is null || tex.ReferencedByEmitters.Count == 0) continue;
                M2ParticlePatcher.HueShiftEmitters(working,
                    PackArgb(hue.Color) & 0x00FFFFFF, tex.ReferencedByEmitters);
            }
        }

        foreach (var edit in model.Edits.Values)
            M2EmitterParser.ApplyEmitterPatch(working, edit);

        // Wholesale emitter off-switches (zeroed emission), after the per-emitter
        // edits so a disabled emitter stays silent whatever its sliders say.
        foreach (int disabled in model.DisabledEmitters)
            M2ParticlePatcher.DisableEmitter(working, disabled);

        // Texture-slot swaps LAST: longer paths append at EOF (resize), which
        // never moves the fixed-offset structures the patchers above wrote.
        if (model.TextureSwaps.Count > 0)
        {
            (working, int swapped) = Creator.M2TextureParser.PatchTextureFilenamesResize(
                working, model.TextureSwaps);
            Console.WriteLine($"[creator] {Path.GetFileName(model.Path)}: " +
                $"{swapped}/{model.TextureSwaps.Count} texture slot(s) swapped " +
                $"({string.Join(", ", model.TextureSwaps.Select(sw => $"{sw.Key}->{Path.GetFileName(sw.Value)}"))})");
        }

        // The byte patcher cannot reach ribbon or MESH color tracks (keyframed
        // data), so the whole-model hue routes to those renderers' color layers -
        // otherwise "hue everything" left the missile trail and the mesh planes
        // (glow sheets, cloud swirls) unmoved.
        string renderPath = SpellVisualCatalog.ModelPath(model.Path);
        uint? modelHue = model.HueShift ? PackArgb(model.HueColor) & 0x00FFFFFF : null;
        _spellRibbons?.SetModelColorHue(renderPath, modelHue);
        _spellEffectMeshes?.SetModelColorHue(renderPath, modelHue);

        // Meshes bake their textures at build time - drop the cached mesh so
        // byte patches and texture swaps show on the mesh-drawn parts too.
        _spellEffectMeshes?.InvalidateModel(renderPath);

        model.Working = working;
        model.Emitters = M2EmitterParser.ReadEmitters(working);
        bool bytesPatched = globalsActive || texHuesActive || model.Edits.Count > 0 ||
                            model.TextureSwaps.Count > 0 || model.DisabledEmitters.Count > 0;
        // Tints live in the renderers' texture layer, not the M2 bytes, but they
        // count as "modified" so the export includes the recolored BLPs.
        model.Modified = bytesPatched || model.TextureTints.Any(t => t.Value.On);
        _spellEffects?.SetModelOverride(model.Path, bytesPatched ? working : null);
        // Geometry-particle models load through the particle system's own cache,
        // not SpellEffectSource - push the same override there (a no-op for
        // paths nothing spawns as geometry).
        _spellParticles?.SetGeometryModelOverride(renderPath, bytesPatched ? working : null);
    }

    private static uint PackArgb(Vector3 rgb) =>
        0xFF000000u |
        ((uint)Math.Clamp((int)(rgb.X * 255f), 0, 255) << 16) |
        ((uint)Math.Clamp((int)(rgb.Y * 255f), 0, 255) << 8) |
        (uint)Math.Clamp((int)(rgb.Z * 255f), 0, 255);

    // ── the panel (registered sections; see Program.Creator.Ui.cs) ───────────

    private partial void RegisterCreatorSpellsSections()
    {
        CreatorSection("Spells", "ws-spell", _creatorSpell is null
            ? "Spell" : $"Spell: {_creatorSpell.Info.Name}", true, DrawCreatorSpellPickerBody);

        if (_creatorSpell is not { } doc || doc.Models.Count == 0) return;

        CreatorSection("Spells", "ws-loop", _creatorLoopOn ? "Loop  (running)" : "Loop", true,
            DrawCreatorLoopBody);

        // Per-model editors, grouped under the phases that use them. The id is
        // the model path (stable); the label's * marker may change per frame.
        foreach (var model in doc.Models.Values)
        {
            var m = model;
            string label;
            if (doc.GeometryHosts.TryGetValue(m.Path, out string? geoHost))
            {
                // A per-particle model (cloud puffs etc.) - name who spawns it.
                label = $"geometry: {Path.GetFileName(m.Path)} (in {geoHost})";
            }
            else
            {
                string phases = string.Join("+", doc.PhaseModels
                    .Where(p => string.Equals(p.Path, m.Path, StringComparison.OrdinalIgnoreCase))
                    .Select(p => p.Stage.ToString().ToLowerInvariant()).Distinct());
                if (phases.Length == 0 &&
                    string.Equals(doc.MissilePath, m.Path, StringComparison.OrdinalIgnoreCase))
                    phases = "missile";
                label = $"{phases}: {Path.GetFileName(m.Path)}";
            }
            if (m.Modified) label += " *";
            CreatorSection("Spells", $"ws-{m.Path}", label, false,
                () => DrawCreatorModelEditor(m, CreatorUiScale));
        }

        CreatorSection("Spells", "ws-export", "Export", false, DrawCreatorExportBody);
    }

    private void DrawCreatorSpellPickerBody()
    {
        float cs = CreatorUiScale;
        if (_spellCatalog is null || _spellVisualCatalog is null || _spellEffects is null)
        {
            ImGui.TextWrapped("Spell catalogs are unavailable - check the console.");
            return;
        }

        ImGui.SetNextItemWidth(220f * cs);
        if (ImGui.InputText("##spell-search", _creatorSpellSearchBuf,
                (uint)_creatorSpellSearchBuf.Length))
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
            if (BeginCreatorResults("##spell-results", results.Count))
            {
                foreach (var spell in results)
                {
                    string rank = spell.Rank.Length > 0 ? $" ({spell.Rank})" : "";
                    if (CreatorResultRow($"{spell.Id}  {spell.Name}{rank}"))
                    {
                        SelectCreatorSpell(spell);
                        Array.Clear(_creatorSpellSearchBuf);
                        _creatorSpellSearchDirty = true;
                    }
                }
            }
            EndCreatorResults();
        }

        if (_creatorSpell is { } doc && doc.Models.Count == 0)
            ImGui.TextWrapped("This spell's visual has no effect models to tune " +
                              "(or the models failed to load).");
    }

    private void DrawCreatorLoopBody()
    {
        float cs = CreatorUiScale;
        var doc = _creatorSpell;
        if (doc is null) { ImGui.TextDisabled("Pick a spell first."); return; }

        // One checkbox per phase - loop EXACTLY the combination you want.
        // Phases this spell's visual does not author are shown disabled.
        ImGui.TextDisabled("PHASES TO LOOP");
        CreatorHelp("A spell visual is a chain of phases; check exactly what you want " +
            "repeating:\n\n" +
            "Precast - the charge-up on the caster's hands while casting.\n" +
            "Cast - the release burst when the cast completes.\n" +
            "Missile - the projectile flying to the target.\n" +
            "Impact - what lands ON the victim.\n" +
            "State - the persistent effect while a buff/debuff holds.\n" +
            "Channel - held on the caster while channeling.\n" +
            "Area (rain) - the ground effect at the target spot: a looping centre " +
            "model plus one-shot shards scattered over the radius (Blizzard's " +
            "snowfall, Rain of Fire's meteors). This is a separate machine from " +
            "Missile - it is where the 'many missiles' of an AoE come from.\n\n" +
            "Greyed phases are not authored by this spell's visual.");
        bool PhaseBox(string label, ref bool value, bool available, bool sameLine)
        {
            if (sameLine) ImGui.SameLine();
            if (!available)
            {
                value = false;
                ImGui.BeginDisabled();
                bool off = false;
                ImGui.Checkbox(label, ref off);
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("This spell's visual has no " + label + " kit.");
                return false;
            }
            return ImGui.Checkbox(label, ref value);
        }

        bool hasArea = _spellVisualCatalog?.TryGetAreaVisual(doc.Info.VisualId,
            out SpellAreaVisualInfo _) == true;
        PhaseBox("Precast", ref _creatorLoopPrecast, doc.Stages.Precast != 0, false);
        PhaseBox("Cast", ref _creatorLoopCast, doc.Stages.Cast != 0, true);
        PhaseBox("Missile", ref _creatorLoopMissilePhase, doc.MissilePath is { Length: > 0 }, true);
        PhaseBox("Impact", ref _creatorLoopImpactPhase, doc.Stages.Impact != 0, true);
        PhaseBox("State", ref _creatorLoopStateHold, doc.Stages.State != 0, false);
        PhaseBox("Channel", ref _creatorLoopChannelHold, doc.Stages.Channel != 0, true);
        PhaseBox("Area (rain)", ref _creatorLoopAreaHold, hasArea, true);
        if (hasArea && _creatorLoopAreaHold)
        {
            ImGui.SetNextItemWidth(120f * cs);
            ImGui.SliderFloat("Area radius", ref _creatorAreaRadius, 2f, 30f, "%.0f yd");
            if (CreatorResetKnob("arearad")) _creatorAreaRadius = 8f;
            CreatorHelp("Radius of the ground disc the shards fall over. On the live " +
                "server this comes from the spell's area object; here it is yours to dial.");
        }

        ImGui.SetNextItemWidth(110f * cs);
        ImGui.SliderFloat("Period", ref _creatorLoopPeriod, 0.5f, 6f, "%.1fs");
        if (CreatorResetKnob("period")) _creatorLoopPeriod = 2f;
        CreatorHelp("Seconds between loop cycles. Each cycle plays the checked phases " +
            "in sequence (precast, then cast, missile in flight, impact on arrival).");
        ImGui.SameLine();
        bool anyPhase = _creatorLoopPrecast || _creatorLoopCast || _creatorLoopMissilePhase ||
                        _creatorLoopImpactPhase || _creatorLoopStateHold || _creatorLoopChannelHold ||
                        _creatorLoopAreaHold;
        if (!anyPhase && !_creatorLoopOn) ImGui.BeginDisabled();
        if (CreatorButton(_creatorLoopOn ? "Stop" : "Loop", 70f * cs))
        {
            _creatorLoopOn = !_creatorLoopOn;
            _creatorLoopNextAt = 0;
            _creatorLoopCastAt = _creatorLoopMissileAt = _creatorLoopImpactAt = double.MaxValue;
            if (!_creatorLoopOn) ReapPresentedEffect();
        }
        if (!anyPhase && !_creatorLoopOn) ImGui.EndDisabled();

        if (!anyPhase)
            ImGui.TextDisabled("Check at least one phase.");
        else if (_creatorLoopImpactPhase)
            ImGui.TextDisabled(CreatorMissileTargetGuid() != 0
                ? "Impact lands on the spawned target."
                : "Impact lands on you - spawn a target (Target menu) to see it land there.");
    }

    private void DrawCreatorExportBody()
    {
        var doc = _creatorSpell;
        if (doc is null) { ImGui.TextDisabled("Pick a spell first."); return; }

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
                model.TextureHues.Clear();
                foreach (int texIndex in model.TextureTints.Keys)
                    SetCreatorTextureTint(model, texIndex, null);
                model.TextureTints.Clear();
                model.TextureSwaps.Clear();
                model.DisabledEmitters.Clear();
                model.HueShift = false;
                model.RateMul = model.ScaleMul = model.LifeMul = model.SpeedMul = 1f;
                model.GravityAdd = 0f;
                RebuildCreatorModel(model);
            }
        }
        if (_creatorExportStatus.Length > 0) ImGui.TextWrapped(_creatorExportStatus);
    }

    private void DrawCreatorModelEditor(CreatorModelDoc model, float cs)
    {
        bool dirty = false;

        // Whole-model dials, each with its own reset and explainer.
        ImGui.TextDisabled("MODEL DIALS (multipliers over the authored values)");
        dirty |= ImGui.Checkbox("Hue (particles + trail)", ref model.HueShift);
        if (model.HueShift)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(140f * cs);
            dirty |= ImGui.ColorEdit3("##hue", ref model.HueColor, ImGuiColorEditFlags.NoInputs);
        }
        CreatorHelp("Rotates every particle's start/mid/end colors AND the ribbon trail's " +
            "color track toward this color, keeping each one's brightness (a bright core " +
            "stays bright, a dark swirl stays dark).\n\nIt does NOT repaint the texture " +
            "images themselves - art whose color is baked into the BLP needs the per-image " +
            "Tint below. For full recolors combine this with Tint on each image.");

        bool Dial(string label, ref float value, float min, float max, float reset,
            string format, string help, bool log = false)
        {
            ImGui.SetNextItemWidth(CreatorControlWidth);
            bool changed = ImGui.SliderFloat(label, ref value, min, max, format,
                log ? ImGuiSliderFlags.Logarithmic : ImGuiSliderFlags.None);
            if (CreatorResetKnob(label)) { value = reset; changed = true; }
            CreatorHelp(help);
            return changed;
        }

        dirty |= Dial("Density", ref model.RateMul, 0.1f, 10f, 1f, "%.2fx",
            "Emission-rate multiplier over every emitter: how many particles are born " +
            "per second. Higher = thicker, fuller effect; lower = sparse wisps.", log: true);
        dirty |= Dial("Size", ref model.ScaleMul, 0.1f, 10f, 1f, "%.2fx",
            "Particle size multiplier (scales each particle's start/mid/end size " +
            "together). The emitter shape and count stay the same.", log: true);
        dirty |= Dial("Duration", ref model.LifeMul, 0.1f, 5f, 1f, "%.2fx",
            "Lifespan multiplier: how long each particle lives before fading. Longer " +
            "lifetimes read as lingering smoke/trails; shorter as sharp crackle.", log: true);
        dirty |= Dial("Speed", ref model.SpeedMul, 0.1f, 5f, 1f, "%.2fx",
            "Emission-speed multiplier: how fast particles fly away from their emitter. " +
            "Faster = explosive burst; slower = hovering shimmer.", log: true);
        dirty |= Dial("Gravity +", ref model.GravityAdd, -10f, 10f, 0f, "%.2f",
            "ADDED to every emitter's authored gravity. Positive pulls particles down " +
            "(rain, embers falling); negative makes them rise (smoke, sparks floating up).");

        // The actual BLPs this model is built from, each with its own hue dial
        // when particles reference it. A phase made of several images recolors
        // per image here; the master dial above moves everything at once.
        if (model.Textures.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("TEXTURES (the BLPs behind this phase)");
            CreatorHelp("Every image this model draws with. Hue recolors the PARTICLES " +
                "that use an image; Tint repaints the image's own pixels; Swap replaces " +
                "the image with any other BLP in the game - the splicing tool.\n\n" +
                "NOTE: Tint is keyed by image path, so tinting a BLP that other phases " +
                "or spells share changes it everywhere it appears. To recolor ONE phase " +
                "only, Swap that slot to different art first, then Tint the swap.");
            foreach (var tex in model.Textures)
            {
                ImGui.PushID(1000 + tex.Index);
                string effectivePath = EffectiveTexturePath(model, tex.Index);
                float thumb = MathF.Min(26f * cs, 40f);
                uint art = effectivePath.Length > 0 ? _gameplayArt?.AdditiveHandle(effectivePath) ?? 0 : 0;
                if (art != 0)
                {
                    ImGui.Image((nint)art, new Vector2(thumb, thumb));
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.TextUnformatted(effectivePath);
                        ImGui.Image((nint)art, new Vector2(128f, 128f));
                        ImGui.EndTooltip();
                    }
                    ImGui.SameLine();
                }
                bool swapped = model.TextureSwaps.ContainsKey(tex.Index);
                string name = tex.Filename.Length > 0 ? Path.GetFileName(tex.Filename) : $"(slot {tex.Index})";
                if (swapped) name = $"{name} -> {Path.GetFileName(effectivePath)}";
                bool used = tex.ReferencedByEmitters.Count > 0;

                // Hue: rotates the emitter particle colors (particle-driven only).
                if (used)
                {
                    var hue = model.TextureHues.TryGetValue(tex.Index, out var found)
                        ? found : new CreatorTexHue();
                    bool on = hue.On;
                    if (ImGui.Checkbox($"Hue##tex{tex.Index}", ref on))
                    {
                        hue.On = on;
                        model.TextureHues[tex.Index] = hue;
                        dirty = true;
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Recolor the PARTICLES drawn with this image (their " +
                                         "start/mid/end colors), leaving the image pixels alone. " +
                                         "Overrides the whole-model hue for these emitters.");
                    if (hue.On)
                    {
                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(140f * cs);
                        if (ImGui.ColorEdit3($"##texhue{tex.Index}", ref hue.Color,
                                ImGuiColorEditFlags.NoInputs))
                        {
                            model.TextureHues[tex.Index] = hue;
                            dirty = true;
                        }
                    }
                    ImGui.SameLine();
                }

                // Tint: the TRUE palette swap - hue-maps the BLP's own pixels, so
                // art whose color is baked into the image really changes color.
                // Available on every texture, including mesh/ribbon ones the
                // particle hue can't reach.
                if (tex.Filename.Length > 0)
                {
                    var tint = model.TextureTints.TryGetValue(tex.Index, out var foundTint)
                        ? foundTint : new CreatorTexHue();
                    bool tintOn = tint.On;
                    if (ImGui.Checkbox($"Tint##textint{tex.Index}", ref tintOn))
                    {
                        tint.On = tintOn;
                        model.TextureTints[tex.Index] = tint;
                        SetCreatorTextureTint(model, tex.Index, tintOn ? tint.Color : null);
                        dirty = true;
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Palette-swap the image itself (recolors the BLP's pixels).\n" +
                                         "Use this when Hue does nothing - the color is baked into the art.");
                    if (tint.On)
                    {
                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(140f * cs);
                        if (ImGui.ColorEdit3($"##textint{tex.Index}", ref tint.Color,
                                ImGuiColorEditFlags.NoInputs))
                        {
                            model.TextureTints[tex.Index] = tint;
                            SetCreatorTextureTint(model, tex.Index, tint.Color);
                            dirty = true;
                        }
                    }
                    ImGui.SameLine();
                }

                if (ImGui.SmallButton("Swap"))
                    _texSwapTarget = (model.Path, tex.Index);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Replace this image with any other BLP - from this " +
                                     "spell, any other spell, or a typed path.");
                if (swapped)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton("x##unswap"))
                    {
                        if (_creatorSpell is { } curDoc)
                            ApplyCreatorTextureSwapEverywhere(curDoc, model, tex.Index, null);
                        else
                            ApplyCreatorTextureSwap(model, tex.Index, null);
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Restore the authored image");
                }
                ImGui.SameLine();
                ImGui.TextUnformatted(name);
                ImGui.SameLine();
                // A slot whose emitters spawn per-particle geometry M2s never draws
                // this image directly - say where the pixels really come from.
                string? geoDrawn = tex.ReferencedByEmitters
                    .Select(e => model.EmitterGeometry.GetValueOrDefault(e))
                    .FirstOrDefault(p => p is { Length: > 0 });
                ImGui.TextDisabled(used
                    ? $"emitter{(tex.ReferencedByEmitters.Count == 1 ? "" : "s")} " +
                      string.Join(",", tex.ReferencedByEmitters) +
                      (geoDrawn is not null
                          ? $"  (drawn as {Path.GetFileName(geoDrawn)} meshes - edits follow)"
                          : "")
                    : "(mesh - use Tint)");
                ImGui.PopID();
            }
        }

        // Per-emitter absolute values. The emitter->texture names come from the
        // texture table's back-references (offset 0x016, the real textureId);
        // EmitterSnapshot.TextureId reads 0x02A which is particleColorIndex.
        var texByEmitter = new Dictionary<int, string>();
        foreach (var tex in model.Textures)
            foreach (int e in tex.ReferencedByEmitters)
                texByEmitter[e] = Path.GetFileName(tex.Filename);

        ImGui.Spacing();
        ImGui.TextDisabled("EMITTERS");
        CreatorHelp("Each emitter is one particle source inside the model - its own image, " +
            "blend mode, spray shape and motion. These sliders set ABSOLUTE values (the " +
            "model dials above are multipliers over them). A * on a slider means the " +
            "authored track is animated over time; the slider overrides its first key.");
        foreach (var emitter in model.Emitters)
        {
            // Category id is model path + index: stable while blend/type in the
            // label change as they are edited.
            string texName = texByEmitter.GetValueOrDefault(emitter.Index, "no tex");
            bool emitterOff = model.DisabledEmitters.Contains(emitter.Index);
            if (!CreatorCategory($"ws-{model.Path}-em{emitter.Index}",
                $"Emitter {emitter.Index}  " +
                $"({texName}, blend {emitter.BlendMode}, type {emitter.EmitterType})" +
                (emitterOff ? "  [OFF]" : "")))
                continue;
            ImGui.PushID(emitter.Index);
            ImGui.Indent(10f * cs);

            bool emitterOn = !emitterOff;
            if (ImGui.Checkbox("Enabled", ref emitterOn))
            {
                if (emitterOn) model.DisabledEmitters.Remove(emitter.Index);
                else model.DisabledEmitters.Add(emitter.Index);
                dirty = true;
            }
            CreatorHelp("Switch this emitter off wholesale - its emission is zeroed, no " +
                "particles are born, everything else keeps playing. Isolate emitters one " +
                "at a time to learn which does what. Sliders below show 0 while off; " +
                "re-enabling restores the authored values.");
            if (emitterOff)
            {
                ImGui.Unindent(10f * cs);
                ImGui.PopID();
                continue;   // no point editing a silenced emitter
            }

            EmitterPatch edit = model.Edits.TryGetValue(emitter.Index, out var found)
                ? found : new EmitterPatch { EmitterIndex = emitter.Index };

            int blend = edit.BlendMode ?? emitter.BlendMode;
            ImGui.SetNextItemWidth(130f * cs);
            if (ImGui.Combo("Blend", ref blend, CreatorBlendModes, CreatorBlendModes.Length))
            { edit.BlendMode = blend; dirty = true; model.Edits[emitter.Index] = edit; }
            if (CreatorResetKnob("blend") && edit.BlendMode is not null)
            { edit.BlendMode = null; model.Edits[emitter.Index] = edit; dirty = true; }
            CreatorHelp("How the particles composite with the world:\n" +
                "0 Opaque - solid, no transparency.\n" +
                "1 Mod - hard cutout / multiply (darkens).\n" +
                "2 Alpha - soft transparency by the image's alpha.\n" +
                "3 Add-Alpha - glow shaped by alpha.\n" +
                "4 Additive - pure light: the image's brightness ADDS to the scene, " +
                "black is invisible. Most fire/magic glows are 4.");

            int type = edit.EmitterType ?? emitter.EmitterType;
            ImGui.SetNextItemWidth(130f * cs);
            if (ImGui.Combo("Emitter type", ref type, CreatorEmitterTypes, CreatorEmitterTypes.Length))
            { edit.EmitterType = type; dirty = true; model.Edits[emitter.Index] = edit; }
            if (CreatorResetKnob("etype") && edit.EmitterType is not null)
            { edit.EmitterType = null; model.Edits[emitter.Index] = edit; dirty = true; }
            CreatorHelp("The shape particles are born from:\n" +
                "0 Point - a single spot (sprays a cone).\n" +
                "1 Sphere - the surface/volume of a sphere around the point.\n" +
                "2 Plane - a flat rectangle (see Area L/W).\n" +
                "3 Spline - along an authored path.");

            bool TrackSlider(string label, string track, float min, float max,
                Func<EmitterPatch, float?> get, Action<EmitterPatch, float?> set, string help)
            {
                float? authored = emitter.TrackValues.GetValueOrDefault(track);
                if (authored is null) return false;   // no keyframes - nothing to patch
                float value = get(edit) ?? authored.Value;
                int keys = emitter.TrackKeyframeCounts.GetValueOrDefault(track);
                ImGui.SetNextItemWidth(CreatorControlWidth);
                bool moved = ImGui.SliderFloat(keys > 1 ? $"{label} *" : label, ref value, min, max, "%.3f");
                if (moved) { set(edit, value); model.Edits[emitter.Index] = edit; }
                if (CreatorResetKnob(track) && get(edit) is not null)
                { set(edit, null); model.Edits[emitter.Index] = edit; moved = true; }
                CreatorHelp(help + $"\n\nAuthored value: {authored.Value:0.###}" +
                    (keys > 1 ? $" (animated, {keys} keys - the slider overrides the first)" : ""));
                return moved;
            }

            dirty |= TrackSlider("Rate", "emissionRate", 0f, 200f,
                e => e.EmissionRate, (e, v) => e.EmissionRate = v,
                "Particles born per second from this emitter.");
            dirty |= TrackSlider("Speed", "emissionSpeed", 0f, 30f,
                e => e.EmissionSpeed, (e, v) => e.EmissionSpeed = v,
                "Initial velocity (yards/second) each particle leaves the emitter with.");
            dirty |= TrackSlider("Speed var", "speedVariation", 0f, 2f,
                e => e.SpeedVariation, (e, v) => e.SpeedVariation = v,
                "Random speed spread as a fraction of Speed - 0 is uniform, higher " +
                "makes some particles crawl and others shoot out.");
            dirty |= TrackSlider("Gravity", "gravity", -20f, 20f,
                e => e.Gravity, (e, v) => e.Gravity = v,
                "Downward acceleration on each particle. Positive falls, negative rises, " +
                "0 drifts straight.");
            dirty |= TrackSlider("Lifespan", "lifespan", 0.05f, 10f,
                e => e.Lifespan, (e, v) => e.Lifespan = v,
                "Seconds each particle lives before it fades out.");
            dirty |= TrackSlider("Spread V", "verticalRange", 0f, MathF.PI,
                e => e.VerticalRange, (e, v) => e.VerticalRange = v,
                "Vertical emission cone, in radians: 0 fires flat, pi sprays over the " +
                "full vertical fan.");
            dirty |= TrackSlider("Spread H", "horizontalRange", 0f, MathF.PI,
                e => e.HorizontalRange, (e, v) => e.HorizontalRange = v,
                "Horizontal emission cone, in radians: 0 fires straight ahead, pi sprays " +
                "across the full half-circle.");
            dirty |= TrackSlider("Area L", "emissionAreaLength", 0f, 20f,
                e => e.EmissionAreaLength, (e, v) => e.EmissionAreaLength = v,
                "Length (yards) of the plane/sphere region particles are born across - " +
                "bigger areas make wider, more diffuse sources.");
            dirty |= TrackSlider("Area W", "emissionAreaWidth", 0f, 20f,
                e => e.EmissionAreaWidth, (e, v) => e.EmissionAreaWidth = v,
                "Width (yards) of the emission region, paired with Area L.");

            var scale = new Vector3(
                edit.ScaleStart ?? emitter.ScaleStart,
                edit.ScaleMid ?? emitter.ScaleMid,
                edit.ScaleEnd ?? emitter.ScaleEnd);
            ImGui.SetNextItemWidth(CreatorControlWidth);
            if (ImGui.SliderFloat3("Scale s/m/e", ref scale, 0f, 8f, "%.2f"))
            {
                edit.ScaleStart = scale.X; edit.ScaleMid = scale.Y; edit.ScaleEnd = scale.Z;
                model.Edits[emitter.Index] = edit;
                dirty = true;
            }
            if (CreatorResetKnob("scale") &&
                (edit.ScaleStart is not null || edit.ScaleMid is not null || edit.ScaleEnd is not null))
            {
                edit.ScaleStart = edit.ScaleMid = edit.ScaleEnd = null;
                model.Edits[emitter.Index] = edit;
                dirty = true;
            }
            CreatorHelp("Particle size over its life: at birth (s), at the authored midpoint (m), " +
                $"and at death (e). Authored: {emitter.ScaleStart:0.##} / {emitter.ScaleMid:0.##} / " +
                $"{emitter.ScaleEnd:0.##}. Grow-then-shrink shapes read as puffs; " +
                "shrink-to-zero as dissolving sparks.");

            if (ImGui.SmallButton("Reset emitter") && model.Edits.Remove(emitter.Index)) dirty = true;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Clear every override on this emitter.");
            ImGui.Unindent(10f * cs);
            ImGui.PopID();
        }

        if (dirty) RebuildCreatorModel(model);
    }

    // ── texture swap picker ──────────────────────────────────────────────────
    // Splice any existing BLP into a texture slot: pick from this spell's own
    // images, browse ANY other spell's images, or type a path directly.

    private (string ModelPath, int TexIndex)? _texSwapTarget;
    private bool _texSwapAllModels = true;
    private readonly byte[] _texSwapPathBuf = new byte[160];
    private readonly byte[] _texSwapSpellBuf = new byte[64];
    private List<SpellInfo>? _texSwapSpellResults;
    private bool _texSwapSpellDirty = true;
    private string _texSwapSourceName = "";
    private List<string> _texSwapSourceTextures = [];

    private void DrawCreatorTextureSwapPicker()
    {
        if (_texSwapTarget is not { } target) return;
        if (_creatorSpell is not { } doc ||
            !doc.Models.TryGetValue(target.ModelPath, out CreatorModelDoc? model))
        {
            _texSwapTarget = null;
            return;
        }

        _activePanelTune = "Swap Texture";
        float cs = CreatorUiScale;
        float s = MathF.Max(ImGui.GetIO().DisplaySize.Y / GlueCanvasH, 0.5f) * cs;
        var cond = _creatorLayoutResetFrames > 0 ? ImGuiCond.Always : ImGuiCond.FirstUseEver;
        ImGui.SetNextWindowPos(new Vector2(540f * s, 90f * s), cond);
        ImGui.SetNextWindowSize(new Vector2(430f * cs, 520f * cs), cond);
        ImGui.SetNextWindowSizeConstraints(new Vector2(300f * cs, 220f * cs),
            new Vector2(float.MaxValue, float.MaxValue));
        PushCreatorStyle();
        bool open = true;
        if (ImGui.Begin("###creator-swap-texture", CreatorChromeFlags))
        {
            ClampCreatorWindowOnScreen();
            if (DrawCreatorPanelChrome("Swap Texture", "Swap Texture")) open = false;
            ImGui.SetWindowFontScale(CreatorTextScale);
            BeginCreatorContent();

            var slot = model.Textures.FirstOrDefault(t => t.Index == target.TexIndex);
            string original = slot?.Filename ?? "";
            string effective = EffectiveTexturePath(model, target.TexIndex);
            ImGui.TextUnformatted($"{Path.GetFileName(model.Path)}  slot {target.TexIndex}");
            ImGui.TextDisabled($"authored: {original}");
            if (!string.Equals(effective, original, StringComparison.OrdinalIgnoreCase))
            {
                ImGui.TextDisabled($"current:  {effective}");
                if (CreatorButton("Restore original"))
                    ApplyCreatorTextureSwapEverywhere(doc, model, target.TexIndex, null);
            }

            ImGui.Checkbox("Apply to every phase using this image", ref _texSwapAllModels);
            CreatorHelp("The same BLP usually appears in several of this spell's phase models " +
                "(precast AND cast both draw CLOUDS.BLP on Cone of Cold). Checked, the swap " +
                "replaces the image EVERYWHERE in this spell, so what you see always follows. " +
                "Uncheck to swap only this one model's slot.");

            void SwapRow(string path)
            {
                ImGui.PushID(path);
                float thumb = MathF.Min(24f * cs, 40f);
                uint art = _gameplayArt?.AdditiveHandle(path) ?? 0;
                if (art != 0) { ImGui.Image((nint)art, new Vector2(thumb, thumb)); ImGui.SameLine(); }
                bool current = string.Equals(path, effective, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable($"{Path.GetFileName(path)}##row", current))
                    ApplyCreatorTextureSwapEverywhere(doc, model, target.TexIndex,
                        string.Equals(path, original, StringComparison.OrdinalIgnoreCase) ? null : path);
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted(path);
                    if (art != 0) ImGui.Image((nint)art, new Vector2(128f, 128f));
                    ImGui.EndTooltip();
                }
                ImGui.PopID();
            }

            ImGui.Spacing();
            if (CreatorCategory("swap-own", "This spell's images", defaultOpen: true))
            {
                ImGui.Indent(10f * cs);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var other in doc.Models.Values)
                    foreach (var tex in other.Textures)
                        if (tex.Filename.Length > 0 && seen.Add(tex.Filename))
                            SwapRow(tex.Filename);
                ImGui.Unindent(10f * cs);
            }

            if (CreatorCategory("swap-other", "From another spell", defaultOpen: true))
            {
                ImGui.Indent(10f * cs);
                ImGui.SetNextItemWidth(200f * cs);
                if (ImGui.InputText("##swap-spell-search", _texSwapSpellBuf, (uint)_texSwapSpellBuf.Length))
                    _texSwapSpellDirty = true;
                CreatorHelp("Search any spell by name or id, click it, and its images " +
                    "appear below - splice a Frostbolt cloud into a Fireball, or " +
                    "anything into anything.");
                string query = BufToString(_texSwapSpellBuf);
                if (_texSwapSpellDirty)
                {
                    _texSwapSpellDirty = false;
                    _texSwapSpellResults = query.Length >= 2 && _spellCatalog is not null
                        ? (uint.TryParse(query, out uint asId)
                            ? _spellCatalog.Spells.Where(sp => sp.Id == asId).ToList()
                            : _spellCatalog.Spells
                                .Where(sp => sp.VisualId != 0 &&
                                             sp.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                                .OrderBy(sp => sp.Name, StringComparer.OrdinalIgnoreCase)
                                .ThenBy(sp => sp.Id).Take(20).ToList())
                        : null;
                }
                if (_texSwapSpellResults is { Count: > 0 } spells && query.Length >= 2)
                {
                    if (BeginCreatorResults("##swap-spells", spells.Count, 0.30f))
                        foreach (var sp in spells)
                            if (CreatorResultRow($"{sp.Id}  {sp.Name}##src{sp.Id}"))
                                LoadSwapSourceTextures(sp);
                    EndCreatorResults();
                }
                if (_texSwapSourceTextures.Count > 0)
                {
                    ImGui.TextDisabled(_texSwapSourceName);
                    foreach (string path in _texSwapSourceTextures)
                        SwapRow(path);
                }
                ImGui.Unindent(10f * cs);
            }

            if (CreatorCategory("swap-path", "Typed path"))
            {
                ImGui.Indent(10f * cs);
                ImGui.SetNextItemWidth(280f * cs);
                ImGui.InputText("##swap-path", _texSwapPathBuf, (uint)_texSwapPathBuf.Length);
                ImGui.SameLine();
                if (CreatorButton("Use", 60f * cs))
                {
                    string typed = BufToString(_texSwapPathBuf).Trim();
                    if (typed.Length > 0)
                    {
                        if (_gameplayArt?.Handle(typed) is > 0)
                            ApplyCreatorTextureSwapEverywhere(doc, model, target.TexIndex, typed);
                        else
                            Console.WriteLine($"[creator] swap path not found in MPQs: {typed}");
                    }
                }
                CreatorHelp(@"Any BLP path inside the game archives, e.g. Spells\Frost_Cloud.blp " +
                    "or Textures\\SunGlare.blp. If nothing changes, the path was not found " +
                    "(check the console).");
                ImGui.Unindent(10f * cs);
            }
            EndCreatorContent();
            ImGui.SetWindowFontScale(1f);
        }
        ImGui.End();
        PopCreatorStyle();
        _activePanelTune = null;
        if (!open) _texSwapTarget = null;
    }

    /// <summary>Collect the distinct texture paths of another spell's effect models
    /// (all stages + missile), for the splice picker.</summary>
    private void LoadSwapSourceTextures(in SpellInfo info)
    {
        _texSwapSourceName = $"{info.Id}  {info.Name}";
        _texSwapSourceTextures = [];
        if (_spellVisualCatalog?.TryGetStages(info.VisualId, out SpellVisualStages stages) != true)
            return;

        var paths = new List<string>();
        void AddKit(uint kitId)
        {
            if (kitId == 0 || _spellVisualCatalog?.TryGetKit(kitId, out SpellVisualKitInfo kit) != true) return;
            foreach (var effect in kit.Effects)
                if (effect.ModelPath.Length > 0) paths.Add(effect.ModelPath);
        }
        AddKit(stages.Precast);
        AddKit(stages.Cast);
        AddKit(stages.Impact);
        AddKit(stages.State);
        AddKit(stages.Channel);
        if (_spellVisualCatalog?.MissilePath(stages) is { Length: > 0 } missile) paths.Add(missile);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string modelPath in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            byte[]? bytes = _spellEffects?.ReadOriginalModel(modelPath);
            if (bytes is null) continue;
            foreach (var tex in Creator.M2TextureParser.ParseTextures(bytes))
                if (tex.Filename.Length > 0 && seen.Add(tex.Filename))
                    _texSwapSourceTextures.Add(tex.Filename);
        }
        Console.WriteLine($"[creator] swap source '{info.Name}': {_texSwapSourceTextures.Count} image(s)");
    }

    // ── export ───────────────────────────────────────────────────────────────

    private string CreatorExportDir()
    {
        string dir = Path.Combine(_config.RepoRoot, "creator-exports");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Patched M2s (and palette-swapped BLPs) at their original paths in a
    /// patch MPQ - drop it into the client's Data folder (and delete the WDB cache)
    /// to see the tune everywhere.</summary>
    private void ExportCreatorPatch(CreatorSpellDoc doc)
    {
        var modified = doc.Models.Values.Where(m => m.Modified).ToList();
        if (modified.Count == 0) { _creatorExportStatus = "Nothing modified - nothing to export."; return; }

        var builder = new MpqBuilderService(new Creator.ILogger<MpqBuilderService>());
        int models = 0;
        foreach (var model in modified)
        {
            // Byte-patched M2s only; a tint-only model has original bytes.
            if (!model.Working.AsSpan().SequenceEqual(model.Original))
            {
                builder.AddFile(model.Path, model.Working);
                models++;
            }
        }

        // Palette-swapped BLPs, re-encoded at their original paths. NOTE: a BLP
        // shared by other spells changes everywhere the client uses it.
        int blps = 0;
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in modified)
        {
            foreach (var (texIndex, tint) in model.TextureTints)
            {
                if (!tint.On) continue;
                string path = EffectiveTexturePath(model, texIndex);
                if (path.Length == 0 || !written.Add(path)) continue;
                byte[]? blp = BuildTintedBlp(path, PackArgb(tint.Color) & 0x00FFFFFF);
                if (blp is null)
                {
                    Console.WriteLine($"[creator] tinted BLP encode failed for {path}");
                    continue;
                }
                builder.AddFile(path, blp);
                blps++;
            }
        }

        if (models == 0 && blps == 0)
        {
            _creatorExportStatus = "Nothing modified - nothing to export.";
            return;
        }
        string output = Path.Combine(CreatorExportDir(), "patch-4.MPQ");
        _creatorExportStatus = builder.Build(output)
            ? $"Wrote {models} model(s) + {blps} tinted BLP(s) to {output}"
            : "MPQ build FAILED - see the console.";
        Console.WriteLine($"[creator] {_creatorExportStatus}");
    }

    /// <summary>Decode a BLP, hue-map its pixels toward targetRgb (0x00RRGGBB), and
    /// re-encode it as DXT3 BLP for the patch MPQ.</summary>
    private byte[]? BuildTintedBlp(string path, uint targetRgb)
    {
        try
        {
            var decoded = Formats.AdtTerrainReader.ReadBlpPixels(_config.ClientDataPath, path);
            if (decoded is not { } d) return null;
            BlpRecolor.HueMapBgra(d.bgra, targetRgb);
            using var bitmap = new SkiaSharp.SKBitmap(d.width, d.height,
                SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Unpremul);
            System.Runtime.InteropServices.Marshal.Copy(d.bgra, 0, bitmap.GetPixels(), d.bgra.Length);
            var writer = new BlpWriterService();
            return writer.EncodeBitmapToBlp(bitmap, useDxt1: false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[creator] BuildTintedBlp({path}) failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>The tuning as JSON: whole-model dials + per-BLP hues + per-emitter
    /// absolute values, keyed by model path - the document MangosSuperUI's tuning
    /// pipeline consumes.</summary>
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
                textureHues = m.TextureHues.Where(h => h.Value.On).Select(h => new
                {
                    slotIndex = h.Key,
                    filename = m.Textures.FirstOrDefault(t => t.Index == h.Key)?.Filename,
                    hueColor = $"#{PackArgb(h.Value.Color) & 0xFFFFFF:x6}",
                    emitters = m.Textures.FirstOrDefault(t => t.Index == h.Key)?.ReferencedByEmitters,
                }),
                textureTints = m.TextureTints.Where(t => t.Value.On).Select(t => new
                {
                    slotIndex = t.Key,
                    filename = EffectiveTexturePath(m, t.Key),
                    tintColor = $"#{PackArgb(t.Value.Color) & 0xFFFFFF:x6}",
                }),
                textureSwaps = m.TextureSwaps.Select(sw => new
                {
                    slotIndex = sw.Key,
                    original = m.Textures.FirstOrDefault(x => x.Index == sw.Key)?.Filename,
                    replacement = sw.Value,
                }),
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
