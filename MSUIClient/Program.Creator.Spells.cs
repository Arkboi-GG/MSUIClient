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

    /// <summary>One user-added emitter: a clone of SourceIndex retargeted at
    /// TextureSlot. Its live index is OriginalEmitterCount + list position.</summary>
    private sealed class CreatorAddedEmitter
    {
        public int SourceIndex;
        public int TextureSlot;
    }

    private enum CreatorAudioCue
    {
        Precast,
        Cast,
        Missile,
        Impact,
        State,
        Channel,
        Area,
    }

    private static readonly CreatorAudioCue[] CreatorAudioCueOrder =
    [
        CreatorAudioCue.Precast,
        CreatorAudioCue.Cast,
        CreatorAudioCue.Missile,
        CreatorAudioCue.Impact,
        CreatorAudioCue.State,
        CreatorAudioCue.Channel,
        CreatorAudioCue.Area,
    ];

    /// <summary>A creator-owned replacement for one phase's SoundEntries cue.
    /// Bytes stay in memory for immediate preview and are embedded into both the
    /// session document and exported patch.</summary>
    private sealed class CreatorAudioTrack
    {
        public required string SourcePath;
        public required string MpqPath;
        public required byte[] Bytes;
        public float Volume = 1f;
        public bool Looping;
        public bool NoDuplicates;
        public uint SoundType = 1;
        public uint ExtraFlags;
        public uint Eax;
        public float MinDistance;
        public float CutoffDistance;
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
        // User-added emitters (clones appended to the M2's emitter array), in
        // append order. The authored array size is OriginalEmitterCount; added
        // emitter i lives at index OriginalEmitterCount + i, so Edits and
        // DisabledEmitters address clones exactly like authored emitters.
        public readonly List<CreatorAddedEmitter> AddedEmitters = [];
        public int OriginalEmitterCount;
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
        public readonly Dictionary<CreatorAudioCue, CreatorAudioTrack> Audio = [];
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
    private double _creatorMissileSoundEndsAt = double.MaxValue;
    private readonly Dictionary<CreatorAudioCue, long> _creatorAudioVoices = [];
    private readonly HashSet<CreatorAudioCue> _creatorSustainedAudio = [];
    private long _creatorAudioPreviewVoice;
    private CreatorAudioCue? _creatorAudioPickerCue;
    private string _creatorAudioPickerDir = "";
    private string _creatorAudioPickerError = "";
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

        UpdateCreatorSustainedAudio();
        if (now >= _creatorMissileSoundEndsAt)
        {
            _creatorMissileSoundEndsAt = double.MaxValue;
            StopCreatorAudio(CreatorAudioCue.Missile);
        }

        if (now >= _creatorLoopCastAt)
        {
            _creatorLoopCastAt = double.MaxValue;
            StopCreatorAudio(CreatorAudioCue.Precast);
            if (PresentSpellEffect(spell, "cast"))
                StartCreatorAudio(CreatorAudioCue.Cast, LocalPlayerGuid,
                    _controller?.Position ?? Vector3.Zero);
        }
        if (now >= _creatorLoopMissileAt)
        {
            _creatorLoopMissileAt = double.MaxValue;
            double flight = SpawnCreatorMissile();
            if (flight > 0)
            {
                StartCreatorAudio(CreatorAudioCue.Missile, LocalPlayerGuid,
                    _controller?.Position ?? Vector3.Zero, forceLoop: true);
                _creatorMissileSoundEndsAt = now + flight;
            }
            if (_creatorLoopImpactPhase)
                _creatorLoopImpactAt = now + (flight > 0 ? flight : 0.5);
        }
        if (now >= _creatorLoopImpactAt)
        {
            _creatorLoopImpactAt = double.MaxValue;
            StopCreatorAudio(CreatorAudioCue.Missile);
            _creatorMissileSoundEndsAt = double.MaxValue;
            ulong target = CreatorMissileTargetGuid();
            ulong anchor = target != 0 ? target : LocalPlayerGuid;
            if (PresentSpellEffect(spell, "impact", target != 0 ? target : null))
            {
                var pose = SpellEffectUnitPose(anchor);
                StartCreatorAudio(CreatorAudioCue.Impact, anchor,
                    pose.Found ? pose.Position : _controller?.Position ?? Vector3.Zero);
            }
        }

        if (now < _creatorLoopNextAt) return;
        _creatorLoopNextAt = now + Math.Max(_creatorLoopPeriod, 0.25f);

        bool any = false;
        if (_creatorLoopPrecast)
        {
            if (PresentSpellEffect(spell, "precast"))
                StartCreatorAudio(CreatorAudioCue.Precast, LocalPlayerGuid,
                    _controller?.Position ?? Vector3.Zero);
            any = true;
        }
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
        if (!any)
        {
            _creatorLoopOn = false;   // nothing checked - stop rather than spin
            StopAllCreatorAudio();
        }
    }

    private uint? CreatorAuthoredSound(CreatorSpellDoc doc, CreatorAudioCue cue)
    {
        if (_spellVisualCatalog is null) return null;
        if (cue == CreatorAudioCue.Missile)
            return doc.Stages.MissileSound is 0 or uint.MaxValue ? null : doc.Stages.MissileSound;
        if (cue == CreatorAudioCue.Area)
            return _spellVisualCatalog.TryGetAreaVisual(doc.Info.VisualId,
                out SpellAreaVisualInfo area) ? area.Sound : null;
        uint kitId = cue switch
        {
            CreatorAudioCue.Precast => doc.Stages.Precast,
            CreatorAudioCue.Cast => doc.Stages.Cast,
            CreatorAudioCue.Impact => doc.Stages.Impact,
            CreatorAudioCue.State => doc.Stages.State,
            CreatorAudioCue.Channel => doc.Stages.Channel,
            _ => 0,
        };
        return kitId != 0 && _spellVisualCatalog.TryGetKit(kitId, out SpellVisualKitInfo kit)
            ? kit.Sound : null;
    }

    private uint CreatorAudioKitId(CreatorSpellDoc doc, CreatorAudioCue cue) => cue switch
    {
        CreatorAudioCue.Precast => doc.Stages.Precast,
        CreatorAudioCue.Cast => doc.Stages.Cast,
        CreatorAudioCue.Impact => doc.Stages.Impact,
        CreatorAudioCue.State => doc.Stages.State,
        CreatorAudioCue.Channel => doc.Stages.Channel,
        CreatorAudioCue.Area => doc.Stages.AreaKit,
        _ => 0,
    };

    private bool CreatorAudioAvailable(CreatorSpellDoc doc, CreatorAudioCue cue) => cue switch
    {
        CreatorAudioCue.Missile => doc.Info.Speed > 0 || doc.MissilePath is { Length: > 0 } ||
                                   doc.Stages.MissileSound != 0,
        _ => CreatorAudioKitId(doc, cue) != 0,
    };

    private long PlayCreatorAudio(CreatorAudioCue cue, ulong owner, Vector3 source,
        bool forceLoop = false)
    {
        if (_creatorSpell is not { } doc || _spellSounds is null) return 0;
        Vector3 listener = _controller?.Position ?? source;
        if (doc.Audio.TryGetValue(cue, out CreatorAudioTrack? custom))
            return _spellSounds.PlayCustom($"creator:{doc.Info.Id}:{cue}", custom.MpqPath,
                custom.Bytes, owner, source, listener, custom.Volume,
                forceLoop || custom.Looping, custom.NoDuplicates,
                custom.MinDistance, custom.CutoffDistance, trackHold: false,
                extraFlags: custom.ExtraFlags, eax: custom.Eax);
        return _spellSounds.Play(CreatorAuthoredSound(doc, cue), owner, source, listener,
            forceLoop, trackHold: false, category: $"creator-{cue.ToString().ToLowerInvariant()}");
    }

    private void StartCreatorAudio(CreatorAudioCue cue, ulong owner, Vector3 source,
        bool forceLoop = false)
    {
        StopCreatorAudio(cue);
        long voice = PlayCreatorAudio(cue, owner, source, forceLoop);
        if (voice != 0) _creatorAudioVoices[cue] = voice;
    }

    private void StopCreatorAudio(CreatorAudioCue cue)
    {
        if (_creatorAudioVoices.Remove(cue, out long voice)) _spellSounds?.Stop(voice);
    }

    private void StopAllCreatorAudio()
    {
        foreach (long voice in _creatorAudioVoices.Values) _spellSounds?.Stop(voice);
        _creatorAudioVoices.Clear();
        _creatorSustainedAudio.Clear();
        _creatorMissileSoundEndsAt = double.MaxValue;
        if (_creatorAudioPreviewVoice != 0) _spellSounds?.Stop(_creatorAudioPreviewVoice);
        _creatorAudioPreviewVoice = 0;
    }

    private void UpdateCreatorSustainedAudio()
    {
        void Sustain(CreatorAudioCue cue, bool wanted)
        {
            if (!wanted)
            {
                if (_creatorSustainedAudio.Remove(cue)) StopCreatorAudio(cue);
                return;
            }
            if (!_creatorSustainedAudio.Add(cue)) return;
            StartCreatorAudio(cue, LocalPlayerGuid,
                _controller?.Position ?? Vector3.Zero);
        }

        Sustain(CreatorAudioCue.State, _creatorLoopStateHold);
        Sustain(CreatorAudioCue.Channel, _creatorLoopChannelHold);
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
                StopCreatorAudio(CreatorAudioCue.Area);
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
            bool customAudio = _creatorSpell.Audio.ContainsKey(CreatorAudioCue.Area);
            bool loopingSound = customAudio
                ? _creatorSpell.Audio[CreatorAudioCue.Area].Looping
                : _spellSounds?.IsAuthoredLoop(area.Sound) == true;
            Action<uint, ulong, Vector3>? birthSound = !customAudio && !loopingSound &&
                area.Emitters.Count != 0
                ? (sound, key, at) => PlaySpellSoundAt(key, sound, at, trackHold: false)
                : null;
            int spawned = _spellEffects!.SpawnAreaVisual(
                CreatorAreaGuid, spell, area, position, _creatorAreaRadius, now, birthSound);
            if (customAudio || loopingSound)
                StartCreatorAudio(CreatorAudioCue.Area, LocalPlayerGuid, position,
                    forceLoop: loopingSound);
            else if (area.Emitters.Count == 0)
                StartCreatorAudio(CreatorAudioCue.Area, LocalPlayerGuid, position);
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
        {
            foreach (CreatorAudioTrack track in _creatorSpell.Audio.Values)
                _spellSounds?.RemoveCustomFile(track.MpqPath);
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
        }
        StopAllCreatorAudio();
        _creatorAudioPickerCue = null;
        _creatorAudioPickerError = "";
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
            model.OriginalEmitterCount = model.Emitters.Count;
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
                geoModel.OriginalEmitterCount = geoModel.Emitters.Count;
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

        // User-added emitters next, so the clones inherit the global dials and
        // everything downstream (per-BLP hues, edits, disables) addresses the
        // grown array. Each clone lands at OriginalEmitterCount + position; a
        // failed clone still consumes its slot (a dead entry keeps later indices
        // stable) - CloneEmitter only fails on malformed headers, which the
        // parse at document build already screened out.
        foreach (var added in model.AddedEmitters)
        {
            if (M2ParticlePatcher.CloneEmitter(working, added.SourceIndex, added.TextureSlot)
                is { } cloned)
                working = cloned.m2Data;
            else
                Console.WriteLine($"[creator] {Path.GetFileName(model.Path)}: emitter clone " +
                    $"of {added.SourceIndex} failed - header rejected");
        }
        // The texture table's emitter back-references must see the clones (the
        // per-BLP hue dials and the UI's texture<->emitter grouping key on them).
        // Reparsed every rebuild so removing the last clone heals the refs too;
        // swaps have not been applied yet, so filenames stay the authored ones.
        model.Textures = Creator.M2TextureParser.ParseTextures(working);

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
                            model.TextureSwaps.Count > 0 || model.DisabledEmitters.Count > 0 ||
                            model.AddedEmitters.Count > 0;
        // Tints live in the renderers' texture layer, not the M2 bytes, but they
        // count as "modified" so the export includes the recolored BLPs.
        model.Modified = bytesPatched || model.TextureTints.Any(t => t.Value.On);
        _spellEffects?.SetModelOverride(model.Path, bytesPatched ? working : null);
        // Geometry-particle models load through the particle system's own cache,
        // not SpellEffectSource - push the same override there (a no-op for
        // paths nothing spawns as geometry).
        _spellParticles?.SetGeometryModelOverride(renderPath, bytesPatched ? working : null);
    }

    /// <summary>The identity color of one texture slot: a stable golden-angle
    /// palette, shown on the texture's row AND on every emitter drawing with it,
    /// so the texture-to-emitter wiring reads at a glance when drilling a phase.</summary>
    private static Vector4 CreatorSlotColor(int slotIndex)
    {
        float hue = slotIndex * 137.508f % 360f / 360f;
        ImGui.ColorConvertHSVtoRGB(hue, 0.72f, 0.95f, out float r, out float g, out float b);
        return new Vector4(r, g, b, 1f);
    }

    /// <summary>Drop one user-added emitter and re-key everything addressed by
    /// emitter index. Only AUTHORED emitters may be clone sources (the UI offers
    /// Duplicate on those alone), so removals never invalidate another clone's
    /// source - but clones after the removed one slide down an index, and their
    /// edits/disables must slide with them.</summary>
    private static void RemoveCreatorAddedEmitter(CreatorModelDoc model, int emitterIndex)
    {
        int pos = emitterIndex - model.OriginalEmitterCount;
        if (pos < 0 || pos >= model.AddedEmitters.Count) return;
        model.AddedEmitters.RemoveAt(pos);

        var edits = model.Edits.Values
            .Where(e => e.EmitterIndex != emitterIndex)
            .ToList();
        model.Edits.Clear();
        foreach (var edit in edits)
        {
            if (edit.EmitterIndex > emitterIndex) edit.EmitterIndex--;
            model.Edits[edit.EmitterIndex] = edit;
        }

        var disabled = model.DisabledEmitters
            .Where(i => i != emitterIndex)
            .Select(i => i > emitterIndex ? i - 1 : i)
            .ToList();
        model.DisabledEmitters.Clear();
        foreach (int i in disabled) model.DisabledEmitters.Add(i);
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

        if (_creatorSpell is not { } doc) return;

        CreatorSection("Spells", "ws-loop", _creatorLoopOn ? "Loop  (running)" : "Loop", true,
            DrawCreatorLoopBody);
        CreatorSection("Spells", "ws-audio", doc.Audio.Count == 0 ? "Audio" : "Audio *", true,
            DrawCreatorAudioBody);

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

        bool advanced = Settings.Creator.SpellAdvancedMode;
        if (ImGui.Checkbox("Advanced mode", ref advanced))
        {
            Settings.Creator.SpellAdvancedMode = advanced;
            SettingsFile?.Save();
        }
        CreatorHelp("Simple mode keeps spell phases, model-wide look controls, images, " +
            "audio, and emitter visibility/on-off switches in view. Advanced mode opens " +
            "individual M2 emitter internals and is where " +
            "bones, animation tracks, ribbons and other format-level controls will live.");
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
            if (!_creatorLoopOn)
            {
                ReapPresentedEffect();
                StopAllCreatorAudio();
            }
        }
        if (!anyPhase && !_creatorLoopOn) ImGui.EndDisabled();

        if (!anyPhase)
            ImGui.TextDisabled("Check at least one phase.");
        else if (_creatorLoopImpactPhase)
            ImGui.TextDisabled(CreatorMissileTargetGuid() != 0
                ? "Impact lands on the spawned target."
                : "Impact lands on you - spawn a target (Target menu) to see it land there.");
    }

    private static string CreatorAudioLabel(CreatorAudioCue cue) => cue switch
    {
        CreatorAudioCue.Precast => "Precast",
        CreatorAudioCue.Cast => "Cast release",
        CreatorAudioCue.Missile => "Missile flight",
        CreatorAudioCue.Impact => "Impact",
        CreatorAudioCue.State => "Persistent state",
        CreatorAudioCue.Channel => "Channel",
        CreatorAudioCue.Area => "Area",
        _ => cue.ToString(),
    };

    private string CreatorAudioDescription(CreatorSpellDoc doc, CreatorAudioCue cue)
    {
        if (doc.Audio.TryGetValue(cue, out CreatorAudioTrack? custom))
            return $"custom: {Path.GetFileName(custom.SourcePath)}";
        uint? sound = CreatorAuthoredSound(doc, cue);
        if (_spellSounds?.TryGetEntry(sound, out SoundEntry entry) == true)
        {
            string variants = string.Join(", ", entry.Variants.Take(2)
                .Select(v => Path.GetFileName(v.Path)));
            if (entry.Variants.Count > 2) variants += $" +{entry.Variants.Count - 2}";
            return $"authored {entry.Id}: {(entry.Name.Length > 0 ? entry.Name : variants)}" +
                   (entry.Looping ? "  (loop)" : "");
        }
        return "no authored cue";
    }

    private void DrawCreatorAudioBody()
    {
        if (_creatorSpell is not { } doc) return;
        float cs = CreatorUiScale;
        ImGui.TextWrapped("Each sound belongs to a spell phase. Preview the original cue or " +
            "replace it with a WAV/MP3; custom audio is carried into both the session " +
            "and the exported patch.");
        CreatorHelp("A phase has one SoundEntries cue, which may itself contain several " +
            "weighted variants. This first creator pass imports one file per phase; " +
            "variant lists will expand here without changing the spell model.");

        if (_creatorAudioPreviewVoice != 0)
        {
            if (ImGui.SmallButton("Stop preview"))
            {
                _spellSounds?.Stop(_creatorAudioPreviewVoice);
                _creatorAudioPreviewVoice = 0;
            }
            ImGui.Spacing();
        }

        foreach (CreatorAudioCue cue in CreatorAudioCueOrder)
        {
            ImGui.PushID($"audio-{cue}");
            bool available = CreatorAudioAvailable(doc, cue);
            bool custom = doc.Audio.TryGetValue(cue, out CreatorAudioTrack? track);
            if (!available) ImGui.BeginDisabled();

            ImGui.TextUnformatted(CreatorAudioLabel(cue));
            ImGui.SameLine();
            ImGui.TextDisabled(CreatorAudioDescription(doc, cue));

            bool hasSound = custom || CreatorAuthoredSound(doc, cue) is not null;
            if (!hasSound) ImGui.BeginDisabled();
            if (ImGui.SmallButton("Preview"))
            {
                if (_creatorAudioPreviewVoice != 0) _spellSounds?.Stop(_creatorAudioPreviewVoice);
                Vector3 source = cue == CreatorAudioCue.Area
                    ? CreatorAreaPosition() : _controller?.Position ?? Vector3.Zero;
                _creatorAudioPreviewVoice = PlayCreatorAudio(cue, LocalPlayerGuid, source,
                    forceLoop: cue == CreatorAudioCue.Missile);
            }
            if (!hasSound) ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.SmallButton(custom ? "Replace file" : "Import file"))
                BeginCreatorAudioImport(cue);
            if (custom)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Restore authored")) RemoveCreatorAudio(cue);

                float volume = track!.Volume;
                ImGui.SetNextItemWidth(150f * cs);
                if (ImGui.SliderFloat("Volume", ref volume, 0f, 1f, "%.2f"))
                    track.Volume = volume;
                bool missileLoop = cue == CreatorAudioCue.Missile;
                bool loop = missileLoop || track.Looping;
                if (missileLoop) ImGui.BeginDisabled();
                bool loopChanged = ImGui.Checkbox("Loop", ref loop);
                if (missileLoop) ImGui.EndDisabled();
                if (!missileLoop && loopChanged)
                {
                    track.Looping = loop;
                    StopCreatorAudio(cue);
                    _creatorSustainedAudio.Remove(cue);
                    if (cue == CreatorAudioCue.Area && _creatorAreaActive)
                    {
                        _spellEffects?.ReapArea(CreatorAreaGuid);
                        _creatorAreaActive = false;
                    }
                }
                CreatorHelp(missileLoop
                    ? "Missile audio loops automatically for exactly the projectile lifetime."
                    : "Looping cues continue until their phase ends.");

                if (Settings.Creator.SpellAdvancedMode)
                {
                    bool noDup = track.NoDuplicates;
                    if (ImGui.Checkbox("No immediate duplicate", ref noDup))
                        track.NoDuplicates = noDup;
                    float min = track.MinDistance, cutoff = track.CutoffDistance;
                    ImGui.SetNextItemWidth(150f * cs);
                    if (ImGui.SliderFloat("Full volume distance", ref min, 0f, 50f, "%.1f yd"))
                        track.MinDistance = Math.Min(min, track.CutoffDistance);
                    ImGui.SetNextItemWidth(150f * cs);
                    if (ImGui.SliderFloat("Cutoff distance", ref cutoff, 0f, 150f, "%.1f yd"))
                    {
                        track.CutoffDistance = cutoff;
                        track.MinDistance = Math.Min(track.MinDistance, cutoff);
                    }
                }
            }

            if (!available)
            {
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("This source visual has no corresponding kit/flight lane yet.");
            }
            ImGui.Separator();
            ImGui.PopID();
        }
    }

    private void BeginCreatorAudioImport(CreatorAudioCue cue)
    {
        _creatorAudioPickerCue = cue;
        _creatorAudioPickerError = "";
        if (Directory.Exists(_creatorAudioPickerDir)) return;
        string music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        _creatorAudioPickerDir = new[] { music, desktop, Environment.CurrentDirectory }
            .FirstOrDefault(Directory.Exists) ?? Environment.CurrentDirectory;
    }

    private static string CreatorAudioAssetToken(string value)
    {
        string token = new(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_'
            ? char.ToLowerInvariant(c) : '_').ToArray());
        while (token.Contains("__", StringComparison.Ordinal)) token = token.Replace("__", "_");
        return token.Trim('_');
    }

    private void ImportCreatorAudio(CreatorAudioCue cue, string file)
    {
        if (_creatorSpell is not { } doc) return;
        try
        {
            string full = Path.GetFullPath(file);
            string ext = Path.GetExtension(full).ToLowerInvariant();
            if (ext is not (".wav" or ".mp3"))
                throw new InvalidDataException("Spell audio must be a WAV or MP3 file.");
            byte[] bytes = File.ReadAllBytes(full);
            if (bytes.Length == 0) throw new InvalidDataException("The selected file is empty.");
            if (bytes.Length > 64 * 1024 * 1024)
                throw new InvalidDataException("The selected file is larger than 64 MB.");
            if (ext == ".wav" && (bytes.Length < 12 ||
                !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
                !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8)))
                throw new InvalidDataException("The selected .wav is not a RIFF/WAVE file.");

            string spell = CreatorAudioAssetToken(doc.Info.Name);
            if (spell.Length == 0) spell = $"spell_{doc.Info.Id}";
            string stem = CreatorAudioAssetToken(Path.GetFileNameWithoutExtension(full));
            if (stem.Length == 0) stem = "audio";
            string cueName = cue.ToString().ToLowerInvariant();
            string mpqPath = $@"Sound\Spells\Custom\{doc.Info.Id}_{spell}\{cueName}_{stem}{ext}";

            uint? sourceId = CreatorAuthoredSound(doc, cue);
            SoundEntry source = default;
            bool hasSource = _spellSounds?.TryGetEntry(sourceId, out source) == true;
            var track = new CreatorAudioTrack
            {
                SourcePath = full,
                MpqPath = mpqPath,
                Bytes = bytes,
                Volume = hasSource ? source.Volume : 1f,
                Looping = cue == CreatorAudioCue.Missile || (hasSource && source.Looping) ||
                          (!hasSource && cue is CreatorAudioCue.State or CreatorAudioCue.Channel or CreatorAudioCue.Area),
                NoDuplicates = hasSource && source.NoDuplicates,
                SoundType = hasSource ? source.Type : 1,
                ExtraFlags = hasSource ? source.Flags & ~(0x200u | 0x20u) : 0,
                Eax = hasSource ? source.Eax : 0,
                MinDistance = hasSource ? source.MinDistance : 10f,
                CutoffDistance = hasSource ? source.CutoffDistance : 80f,
            };
            if (doc.Audio.Remove(cue, out CreatorAudioTrack? old))
                _spellSounds?.RemoveCustomFile(old.MpqPath);
            doc.Audio[cue] = track;
            StopCreatorAudio(cue);
            _creatorSustainedAudio.Remove(cue);
            if (_creatorAudioPreviewVoice != 0) _spellSounds?.Stop(_creatorAudioPreviewVoice);
            _creatorAudioPreviewVoice = 0;
            if (cue == CreatorAudioCue.Area && _creatorAreaActive)
            {
                _spellEffects?.ReapArea(CreatorAreaGuid);
                _creatorAreaActive = false;
            }
            _creatorAudioPickerDir = Path.GetDirectoryName(full) ?? _creatorAudioPickerDir;
            _creatorAudioPickerCue = null;
            _creatorExportStatus = $"Imported {Path.GetFileName(full)} for {CreatorAudioLabel(cue)}.";
        }
        catch (Exception ex)
        {
            _creatorAudioPickerError = ex.Message;
        }
    }

    private void RemoveCreatorAudio(CreatorAudioCue cue)
    {
        if (_creatorSpell is not { } doc || !doc.Audio.Remove(cue, out CreatorAudioTrack? track)) return;
        _spellSounds?.RemoveCustomFile(track.MpqPath);
        StopCreatorAudio(cue);
        _creatorSustainedAudio.Remove(cue);
        if (_creatorAudioPreviewVoice != 0) _spellSounds?.Stop(_creatorAudioPreviewVoice);
        _creatorAudioPreviewVoice = 0;
        if (cue == CreatorAudioCue.Area && _creatorAreaActive)
        {
            _spellEffects?.ReapArea(CreatorAreaGuid);
            _creatorAreaActive = false;
        }
    }

    private void DrawCreatorAudioFilePicker()
    {
        if (_creatorAudioPickerCue is not { } cue) return;
        bool close = false;
        float cs = CreatorUiScale;
        ImGui.SetNextWindowSize(new Vector2(560f * cs, 520f * cs), ImGuiCond.FirstUseEver);
        PushCreatorStyle();
        if (ImGui.Begin("###creator-audio-file-picker", CreatorChromeFlags))
        {
            ClampCreatorWindowOnScreen();
            if (DrawCreatorPanelChrome($"Import {CreatorAudioLabel(cue)} audio")) close = true;
            ImGui.SetWindowFontScale(CreatorTextScale);
            BeginCreatorContent();

            ImGui.TextWrapped("Choose a WAV or MP3. The file is copied into the spell " +
                "session and patch; MSUI never depends on this disk path after import.");
            if (_creatorAudioPickerError.Length > 0)
                ImGui.TextWrapped($"Could not import: {_creatorAudioPickerError}");

            ImGui.TextDisabled(_creatorAudioPickerDir);
            if (ImGui.SmallButton("Up"))
            {
                try
                {
                    DirectoryInfo? parent = Directory.GetParent(_creatorAudioPickerDir);
                    if (parent is not null) _creatorAudioPickerDir = parent.FullName;
                }
                catch (Exception ex) { _creatorAudioPickerError = ex.Message; }
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Cancel")) close = true;

            ImGui.BeginChild("##creator-audio-files", new Vector2(0f, 380f * cs), true);
            try
            {
                var dirs = new List<string>();
                DirectoryInfo? parent = Directory.GetParent(_creatorAudioPickerDir);
                if (parent is null && OperatingSystem.IsWindows())
                    dirs.AddRange(DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName));
                dirs.AddRange(Directory.EnumerateDirectories(_creatorAudioPickerDir)
                    .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Take(200));

                string? nextDir = null;
                foreach (string dir in dirs)
                {
                    string name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar));
                    if (name.Length == 0) name = dir;
                    if (ImGui.Selectable($"[folder] {name}##{dir}")) { nextDir = dir; break; }
                }
                if (nextDir is not null)
                {
                    _creatorAudioPickerDir = nextDir;
                    _creatorAudioPickerError = "";
                }
                else
                {
                    foreach (string file in Directory.EnumerateFiles(_creatorAudioPickerDir)
                                 .Where(f => Path.GetExtension(f).Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
                                             Path.GetExtension(f).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
                                 .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Take(500))
                    {
                        if (!ImGui.Selectable($"{Path.GetFileName(file)}##{file}")) continue;
                        ImportCreatorAudio(cue, file);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _creatorAudioPickerError = ex.Message;
            }
            ImGui.EndChild();
            EndCreatorContent();
        }
        ImGui.End();
        PopCreatorStyle();
        if (close)
        {
            _creatorAudioPickerCue = null;
            _creatorAudioPickerError = "";
        }
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
                model.AddedEmitters.Clear();
                model.HueShift = false;
                model.RateMul = model.ScaleMul = model.LifeMul = model.SpeedMul = 1f;
                model.GravityAdd = 0f;
                RebuildCreatorModel(model);
            }
            foreach (CreatorAudioCue cue in doc.Audio.Keys.ToArray())
                RemoveCreatorAudio(cue);
            StopAllCreatorAudio();
        }
        DrawCreatorSessionBody(doc);
        if (_creatorExportStatus.Length > 0) ImGui.TextWrapped(_creatorExportStatus);
    }

    private void DrawCreatorModelEditor(CreatorModelDoc model, float cs)
    {
        bool dirty = false;
        bool advanced = Settings.Creator.SpellAdvancedMode;

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
                // Identity swatch: the same color marks every emitter below that
                // draws with this image.
                ImGui.ColorButton($"##slotc{tex.Index}", CreatorSlotColor(tex.Index),
                    ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoDragDrop,
                    new Vector2(12f * cs, 12f * cs));
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("This image's identity color - the emitters below " +
                                     "wearing the same swatch draw with this image.");
                ImGui.SameLine();
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
                // Grow the effect from this image: clone an emitter onto this
                // slot (its own emitter when it has one, else any authored one
                // retargeted here) and tune the clone like any other emitter.
                if (advanced && model.OriginalEmitterCount > 0)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton("+Em"))
                    {
                        int source = tex.ReferencedByEmitters
                            .FirstOrDefault(e => e < model.OriginalEmitterCount, 0);
                        model.AddedEmitters.Add(new CreatorAddedEmitter
                        { SourceIndex = source, TextureSlot = tex.Index });
                        dirty = true;
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("ADD an emitter drawing with this image: a clone of " +
                            (tex.ReferencedByEmitters.Count > 0
                                ? "this image's first emitter"
                                : "emitter 0, retargeted to this image") +
                            ", appended to the model. Tune or remove it in EMITTERS below.");
                }
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

        // The emitter list and its enable switch are part of the simple mental
        // model: users need to isolate each visible ingredient to learn what it
        // contributes. Format-level mutation remains advanced.
        // The emitter->texture names come from the texture table's
        // back-references (offset 0x016, the real textureId);
        // EmitterSnapshot.TextureId reads 0x02A which is particleColorIndex.
        var texByEmitter = new Dictionary<int, string>();
        var slotByEmitter = new Dictionary<int, int>();
        foreach (var tex in model.Textures)
            foreach (int e in tex.ReferencedByEmitters)
            {
                texByEmitter[e] = Path.GetFileName(tex.Filename);
                slotByEmitter[e] = tex.Index;
            }

        ImGui.Spacing();
        ImGui.TextDisabled("EMITTERS");
        CreatorHelp(advanced
            ? "Each emitter is one particle source inside the model - its own image, " +
              "blend mode, spray shape and motion. These sliders set ABSOLUTE values (the " +
              "model dials above are multipliers over them). A * on a slider means the " +
              "authored track is animated over time; the slider overrides its first key."
            : "Each emitter is one visible ingredient in this model. Expand an emitter and " +
              "switch it off to isolate what it contributes; Advanced mode exposes its " +
              "blend, shape, motion, duplication and removal controls.");
        foreach (var emitter in model.Emitters)
        {
            // Category id is model path + index: stable while blend/type in the
            // label change as they are edited.
            string texName = texByEmitter.GetValueOrDefault(emitter.Index, "no tex");
            bool emitterOff = model.DisabledEmitters.Contains(emitter.Index);
            bool isAdded = emitter.Index >= model.OriginalEmitterCount;
            // The emitter wears its texture's identity color - the same swatch
            // as the image's row above, so the wiring reads at a glance.
            Vector4? marker = slotByEmitter.TryGetValue(emitter.Index, out int slot)
                ? CreatorSlotColor(slot) : null;
            string formatDetails = advanced
                ? $", blend {emitter.BlendMode}, type {emitter.EmitterType}"
                : "";
            if (!CreatorCategory($"ws-{model.Path}-em{emitter.Index}",
                $"Emitter {emitter.Index}  " +
                $"({texName}{formatDetails})" +
                (isAdded ? "  [added]" : "") +
                (emitterOff ? "  [OFF]" : ""), marker: marker))
                continue;
            ImGui.PushID(emitter.Index);
            ImGui.Indent(10f * cs);

            if (advanced && isAdded)
            {
                if (ImGui.SmallButton("Remove emitter"))
                {
                    RemoveCreatorAddedEmitter(model, emitter.Index);
                    ImGui.Unindent(10f * cs);
                    ImGui.PopID();
                    RebuildCreatorModel(model);
                    break;   // indices shifted - redraw next frame from the new list
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Delete this added emitter (authored emitters can " +
                                     "only be disabled, never removed).");
            }
            else if (advanced && ImGui.SmallButton("Duplicate"))
            {
                model.AddedEmitters.Add(new CreatorAddedEmitter
                {
                    SourceIndex = emitter.Index,
                    TextureSlot = slotByEmitter.GetValueOrDefault(emitter.Index, 0),
                });
                dirty = true;
            }
            if (advanced && !isAdded && ImGui.IsItemHovered())
                ImGui.SetTooltip("ADD a copy of this emitter to the model - then tune the " +
                                 "copy independently (thicker layers, second color, etc).");

            bool emitterOn = !emitterOff;
            if (ImGui.Checkbox("Enabled", ref emitterOn))
            {
                if (emitterOn) model.DisabledEmitters.Remove(emitter.Index);
                else model.DisabledEmitters.Add(emitter.Index);
                dirty = true;
            }
            CreatorHelp("Switch this emitter off wholesale - its emission is zeroed, no " +
                "particles are born, everything else keeps playing. Isolate emitters one " +
                "at a time to learn which does what. Re-enabling restores its authored " +
                "values and any Advanced-mode edits.");
            if (emitterOff || !advanced)
            {
                ImGui.Unindent(10f * cs);
                ImGui.PopID();
                continue;   // simple mode ends at on/off; silenced emitters need no tuning
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
        if (modified.Count == 0 && doc.Audio.Count == 0)
        { _creatorExportStatus = "Nothing modified - nothing to export."; return; }

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

        int audio = 0, audioDbcs = 0;
        try
        {
            (audio, audioDbcs) = AddCreatorAudioToPatch(doc, builder);
        }
        catch (Exception ex)
        {
            _creatorExportStatus = $"Audio patch build FAILED: {ex.Message}";
            Console.WriteLine($"[creator] {_creatorExportStatus}");
            return;
        }

        if (models == 0 && blps == 0 && audio == 0)
        {
            _creatorExportStatus = "Nothing modified - nothing to export.";
            return;
        }
        string output = Path.Combine(CreatorExportDir(), "patch-4.MPQ");
        _creatorExportStatus = builder.Build(output)
            ? $"Wrote {models} model(s) + {blps} tinted BLP(s) + {audio} audio file(s)" +
              (audioDbcs > 0 ? $" + {audioDbcs} audio DBC(s)" : "") + $" to {output}"
            : "MPQ build FAILED - see the console.";
        Console.WriteLine($"[creator] {_creatorExportStatus}");
    }

    /// <summary>
    /// Add custom phase files plus complete SoundEntries/kit/visual DBC overlays.
    /// Direct patch export intentionally rewires the selected SOURCE visual, just
    /// like its M2 tuning overwrites source paths; the session/completer path is
    /// responsible for cloning these rows for an isolated final spell.
    /// </summary>
    private (int AudioFiles, int DbcFiles) AddCreatorAudioToPatch(
        CreatorSpellDoc doc, MpqBuilderService builder)
    {
        if (doc.Audio.Count == 0) return (0, 0);
        if (_mpq is null) throw new InvalidOperationException("The mounted client archives are unavailable.");

        const string kitPath = @"DBFilesClient\SpellVisualKit.dbc";
        const string visualPath = @"DBFilesClient\SpellVisual.dbc";
        byte[] soundBytes = _mpq.ReadFile(SoundEntriesCatalog.MpqPath)
            ?? throw new InvalidDataException("SoundEntries.dbc is missing.");
        byte[] kitBytes = _mpq.ReadFile(kitPath)
            ?? throw new InvalidDataException("SpellVisualKit.dbc is missing.");
        byte[] visualBytes = _mpq.ReadFile(visualPath)
            ?? throw new InvalidDataException("SpellVisual.dbc is missing.");

        DbcWriterService sounds = DbcWriterService.ReadDbc(soundBytes, SoundEntriesCatalog.MpqPath);
        DbcWriterService kits = DbcWriterService.ReadDbc(kitBytes, kitPath);
        DbcWriterService visuals = DbcWriterService.ReadDbc(visualBytes, visualPath);
        if (sounds.FieldCount < 29 || kits.FieldCount < 14 || visuals.FieldCount < 11)
            throw new InvalidDataException("One of the spell-audio DBC schemas is not build 5875 compatible.");

        uint nextSound = sounds.GetMaxId() + 1;
        var kitOwners = new Dictionary<uint, CreatorAudioCue>();
        bool touchedKits = false, touchedVisual = false;
        foreach ((CreatorAudioCue cue, CreatorAudioTrack track) in doc.Audio
                     .OrderBy(pair => Array.IndexOf(CreatorAudioCueOrder, pair.Key)))
        {
            uint soundId = nextSound++;
            var row = new uint[sounds.RecordSize / 4];
            row[0] = soundId;
            row[1] = track.SoundType;
            row[2] = sounds.AddString($"MSUI_{doc.Info.Id}_{cue}");
            row[3] = sounds.AddString(Path.GetFileName(track.MpqPath));
            row[13] = 1; // one weighted variant
            string? directory = Path.GetDirectoryName(track.MpqPath);
            row[23] = sounds.AddString(directory ?? "");
            row[24] = DbcWriterService.FloatToUint(Math.Clamp(track.Volume, 0f, 1f));
            bool looping = cue == CreatorAudioCue.Missile || track.Looping;
            row[25] = track.ExtraFlags | (looping ? 0x200u : 0u) |
                      (track.NoDuplicates ? 0x20u : 0u);
            row[26] = DbcWriterService.FloatToUint(Math.Max(0f, track.MinDistance));
            row[27] = DbcWriterService.FloatToUint(Math.Max(0f, track.CutoffDistance));
            row[28] = track.Eax;
            sounds.AddRow(row);
            builder.AddFile(track.MpqPath, track.Bytes);

            if (cue == CreatorAudioCue.Missile)
            {
                visuals.PatchRow(doc.Info.VisualId, 10, soundId);
                touchedVisual = true;
                continue;
            }

            uint kitId = CreatorAudioKitId(doc, cue);
            if (kitId == 0)
                throw new InvalidDataException($"{CreatorAudioLabel(cue)} has no kit to receive custom audio.");
            if (kitOwners.TryGetValue(kitId, out CreatorAudioCue owner))
                throw new InvalidDataException($"{CreatorAudioLabel(owner)} and {CreatorAudioLabel(cue)} " +
                    $"share kit {kitId}; one kit cannot hold two different sounds.");
            kitOwners[kitId] = cue;
            kits.PatchRow(kitId, 13, soundId);
            touchedKits = true;
        }

        builder.AddFile(SoundEntriesCatalog.MpqPath, sounds.Write());
        int dbcs = 1;
        if (touchedKits) { builder.AddFile(kitPath, kits.Write()); dbcs++; }
        if (touchedVisual) { builder.AddFile(visualPath, visuals.Write()); dbcs++; }
        return (doc.Audio.Count, dbcs);
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

    /// <summary>One model's complete tuning: whole-model dials, per-BLP hues and
    /// tints, swaps, per-emitter absolutes, off-switches, added clones, and the
    /// mesh/ribbon texture list. EVERY workshop modification is represented here -
    /// this object (plus the patched bytes) is what the session export and
    /// MangosSuperUI's Spell Completer consume.</summary>
    private object CreatorModelTuningPayload(CreatorModelDoc m) => new
    {
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
        // Emitter off-switches and user-added clones: without these the
        // consumer resurrects silenced emitters and drops added layers.
        disabledEmitters = m.DisabledEmitters.OrderBy(i => i),
        addedEmitters = m.AddedEmitters.Select((a, i) => new
        {
            index = m.OriginalEmitterCount + i,
            sourceIndex = a.SourceIndex,
            textureSlot = a.TextureSlot,
        }),
        // Ribbon/mesh color tracks are keyframed data the byte patcher
        // cannot reach - the consumer approximates the whole-model hue on
        // these by hue-mapping their BLPs (safe on a cloned spell's own
        // texture copies).
        meshOrRibbonTextures = m.Textures
            .Where(t => t.ReferencedByEmitters.Count == 0 && t.Filename.Length > 0)
            .Select(t => new { slotIndex = t.Index, filename = t.Filename }),
    };

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
                tuning = CreatorModelTuningPayload(m),
            }),
            audio = doc.Audio.OrderBy(pair => Array.IndexOf(CreatorAudioCueOrder, pair.Key))
                .Select(pair => new
                {
                    cue = pair.Key.ToString().ToLowerInvariant(),
                    sourceSoundId = CreatorAuthoredSound(doc, pair.Key),
                    mpqPath = pair.Value.MpqPath,
                    sourceFile = Path.GetFileName(pair.Value.SourcePath),
                    volume = pair.Value.Volume,
                    looping = pair.Key == CreatorAudioCue.Missile || pair.Value.Looping,
                    noDuplicates = pair.Value.NoDuplicates,
                    soundType = pair.Value.SoundType,
                    extraFlags = pair.Value.ExtraFlags,
                    eax = pair.Value.Eax,
                    minDistance = pair.Value.MinDistance,
                    cutoffDistance = pair.Value.CutoffDistance,
                    fileBase64 = Convert.ToBase64String(pair.Value.Bytes),
                }),
        };
        string path = Path.Combine(CreatorExportDir(), $"spell-{doc.Info.Id}-tuning.json");
        File.WriteAllText(path, JsonSerializer.Serialize(payload,
            new JsonSerializerOptions { WriteIndented = true }));
        _creatorExportStatus = $"Wrote {path}";
        Console.WriteLine($"[creator] {_creatorExportStatus}");
    }
}
