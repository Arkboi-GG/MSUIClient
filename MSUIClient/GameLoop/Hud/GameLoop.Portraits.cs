using System.Numerics;
using ImGuiNET;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Spells;
using MSUIClient.World.Units;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private PortraitRenderTarget? _playerPortrait;
    private PortraitRenderTarget? _targetPortrait;
    private PortraitRenderTarget? _petPortrait;
    private PortraitRenderTarget? _paperDoll;
    private PortraitRenderTarget? _inspectPaperDoll;
    private PortraitRenderTarget? _petPaperDoll;
    private PortraitRenderTarget? _dressUpTarget;
    private PortraitGlowLane? _paperDollGlow;
    private PortraitGlowLane? _inspectPaperDollGlow;
    private PortraitGlowLane? _dressUpGlow;
    private bool _portraitGlowUnavailable;
    private bool _playerPortraitDirty = true;
    private bool _paperDollDirty = true;
    private bool _inspectPaperDollDirty = true;
    private bool _inspectPaperDollUsable;
    private bool _petPaperDollDirty = true;
    private bool _petPaperDollUsable;
    private ulong _petPaperDollGuid;
    private ulong _petPaperDollAppearance;
    private float _petPaperDollRotation;
    private float _petPaperDollAnimationTime;
    private double _petPaperDollLastUpdate;
    private ulong _inspectPaperDollGuid;
    private ulong _inspectPaperDollAppearance;
    private bool _playerPortraitUsable;
    private bool _targetPortraitUsable;
    private bool _petPortraitUsable;
    private ulong _petPortraitGuid;
    private ulong _petPortraitAppearance;
    private double _petPortraitRetryAt;
    private bool _playerPortraitFailureDumped;
    private bool _targetPortraitFailureDumped;
    private float _paperDollRotation;
    private float _paperDollAnimationTime;
    private double _paperDollLastUpdate;
    private ulong _portraitTargetGuid;
    private ulong _portraitTargetAppearance;
    private ulong _portraitRequestAppearance;
    private double _playerPortraitRetryAt;
    private double _targetPortraitRetryAt;

    // ── Party member portraits: real 3-D bakes of the streamed bodies ─────────────────────────
    // Baked through TryBakeCreaturePortrait — the SAME booth that bakes the target frame's
    // portrait: the model's authored M2 portrait camera when it has one, the DBC-framed
    // bounds camera when it does not, a blank-bake retry between them, and the same tuning
    // overrides the own-character portrait reads (keyed player:race-gender, so a race tuned
    // for the player frame is tuned for its party frames too). Every member therefore shows
    // its ACTUAL race/geosets/hair/gear instead of the static TemporaryPortrait art, framed
    // like every other portrait in the HUD. Keyed by an appearance hash; one bake per frame.
    private sealed class PartyPortrait
    {
        public PortraitRenderTarget? Target;
        public ulong Appearance;
        public bool Usable;
        public double RetryAt;
    }

    private readonly Dictionary<ulong, PartyPortrait> _partyPortraits = [];

    private static ulong PlayerAppearanceSignature(in WorldEntity unit)
    {
        ulong hash = unchecked((ulong)(uint)unit.DisplayId + 1469598103934665603ul);
        (byte skin, byte face, byte hairStyle, byte hairColor) = unit.Fields.PlayerAppearance;
        hash = unchecked((hash ^ ((ulong)skin << 24 | (ulong)face << 16 |
            (ulong)hairStyle << 8 | hairColor)) * 1099511628211ul);
        hash = unchecked((hash ^ unit.Fields.PlayerFacialHair) * 1099511628211ul);
        for (int slot = 0; slot < 19; slot++)
        {
            hash = unchecked((hash ^ unit.Fields.PlayerVisibleItemEntry(slot)) * 1099511628211ul);
            for (int enchantSlot = 0; enchantSlot < 7; enchantSlot++)
                hash = unchecked((hash ^ unit.Fields.PlayerVisibleItemEnchant(slot, enchantSlot)) *
                    1099511628211ul);
        }
        return hash;
    }

    /// <summary>
    /// A body-model pane is a live model widget, not a cached round unit portrait. Keep its
    /// item/enchant effect source and particle history isolated from the world and from the other
    /// panes: syncing one booth must never retire another booth's glow or draw world spell FX into
    /// a UI render target.
    /// </summary>
    private sealed class PortraitGlowLane : IDisposable
    {
        public readonly SpellEffectSource Source;
        public readonly SpellParticleSystem Particles;
        public readonly SpellRibbonRenderer Ribbons;
        public bool Active;
        public double LastUpdate;

        public PortraitGlowLane(GL gl, ClientConfig config, MpqMount mpq, string shaderDirectory)
        {
            Source = new SpellEffectSource(mpq);
            Particles = new SpellParticleSystem(gl, config, mpq) { FogEnabled = false };
            Particles.LoadShaders(shaderDirectory);
            Ribbons = new SpellRibbonRenderer(gl, mpq) { FogEnabled = false };
            Ribbons.LoadShaders();
        }

        public void Dispose()
        {
            Particles.Dispose();
            Ribbons.Dispose();
        }
    }

    private static ulong PortraitAppearanceSignature(in WorldEntity unit)
    {
        if (unit.IsPlayer) return PlayerAppearanceSignature(unit);
        return unchecked(((ulong)(uint)unit.DisplayId << 32) |
            BitConverter.SingleToUInt32Bits(unit.Scale));
    }

    /// <summary>
    /// A usable baked portrait texture for a party member, or 0. The ROUND copy: the
    /// party frame draws the authored chrome, whose aperture is a circular hole, exactly
    /// like the player/target frames in their non-painterly path.
    /// </summary>
    private uint PartyPortraitHandle(ulong guid) =>
        _partyPortraits.TryGetValue(guid, out PartyPortrait? entry) &&
        entry is { Usable: true, Target: not null } ? entry.Target.CircularTextureHandle : 0;

    private void UpdatePartyPortraits()
    {
        if (_creatures is null || _gl is null) return;

        // The FRAME set, not the wire roster: while a bot is possessed it holds the player
        // frame and the abandoned own character takes a party slot, so that is whose faces
        // need baking. Reap anyone no longer in it before their GL targets pile up.
        PartyMember[] framed = PartyFrameMembers();
        if (_partyPortraits.Count > 0)
        {
            List<ulong>? stale = null;
            foreach (ulong guid in _partyPortraits.Keys)
            {
                bool present = false;
                foreach (PartyMember member in framed)
                    if (member.Guid == guid) { present = true; break; }
                if (!present) (stale ??= []).Add(guid);
            }
            if (stale is not null)
                foreach (ulong guid in stale)
                {
                    _partyPortraits[guid].Target?.Dispose();
                    _partyPortraits.Remove(guid);
                }
        }

        foreach (PartyMember member in framed)
        {
            // The driven unit owns the PLAYER frame and bakes through _playerPortrait.
            if (member.Guid == ControlledGuid) continue;
            if (!_entities.TryGet(member.Guid, out WorldEntity unit) || !unit.IsPlayer) continue;

            ulong appearance = PlayerAppearanceSignature(unit);
            if (!_partyPortraits.TryGetValue(member.Guid, out PartyPortrait? entry))
                _partyPortraits[member.Guid] = entry = new PartyPortrait();
            if (entry.Usable && entry.Appearance == appearance) continue;
            if (NowSeconds() < entry.RetryAt) continue;
            // Still streaming in: no target allocated, no backoff — retry next frame.
            if (!_creatures.TryGetPortraitFraming(unit, out _)) continue;

            try
            {
                entry.Target ??= new PortraitRenderTarget(_gl);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[portrait] party target unavailable: {ex.Message}");
                entry.RetryAt = NowSeconds() + 30.0;
                continue;
            }

            (PortraitTuning tuning, bool storeHit) = ResolveTuningWithHit(PlayerPortraitKey(unit));
            if (!TryBakeCreaturePortrait(entry.Target, unit, tuning, storeHit,
                    out CreaturePortraitBake bake))
                continue;

            entry.Usable = bake.Drawn && bake.Stats.HasSubject;
            if (entry.Usable)
            {
                // Pigment then aperture, in that order and for the reason the player bake
                // gives: the round copy is a SNAPSHOT, so styling has to land in the bake
                // before the disc is cut from it.
                if (PainterlyUi) StylePortrait(entry.Target);
                entry.Target.UpdateCircularCopy();
                entry.Appearance = appearance;
                entry.RetryAt = 0;
            }
            else
            {
                entry.RetryAt = NowSeconds() + 1.0;
            }
            break;   // one bake per frame keeps the render loop smooth
        }
    }
    private readonly VerdictRing _verdicts = new();
    private PortraitOverrideStore? _portraitOverrides;

    private void InitPortraits(GL gl)
    {
        _portraitOverrides = PortraitOverrideStore.Load(_config.RepoRoot);
        try
        {
            _playerPortrait = new PortraitRenderTarget(gl);
            _targetPortrait = new PortraitRenderTarget(gl);
            _petPortrait = new PortraitRenderTarget(gl);
            _paperDoll = new PortraitRenderTarget(gl, 466, 448);
            _inspectPaperDoll = new PortraitRenderTarget(gl, 466, 600);
            _petPaperDoll = new PortraitRenderTarget(gl, 636, 448);
            _dressUpTarget = new PortraitRenderTarget(gl, 632, 702);
        }
        catch (Exception ex)
        {
            _playerPortrait?.Dispose();
            _targetPortrait?.Dispose();
            _petPortrait?.Dispose();
            _paperDoll?.Dispose();
            _inspectPaperDoll?.Dispose();
            _petPaperDoll?.Dispose();
            _dressUpTarget?.Dispose();
            _labPortrait?.Dispose();
            _playerPortrait = null;
            _targetPortrait = null;
            _petPortrait = null;
            _paperDoll = null;
            _inspectPaperDoll = null;
            _petPaperDoll = null;
            _dressUpTarget = null;
            _labPortrait = null;
            Console.WriteLine($"[portrait] render targets unavailable: {ex.Message}");
        }
        if (_config.DevTools)
        {
            try { _labPortrait = new PortraitRenderTarget(gl); }
            catch (Exception ex)
            {
                _labPortrait?.Dispose();
                _labPortrait = null;
                Console.WriteLine($"[portrait] lab render target unavailable: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Force the live portrait bakes to be rebuilt.
    ///
    /// Painterly styling is baked INTO the portrait texture, and a bake only
    /// repeats when its SUBJECT changes - so a portrait painted under one style
    /// keeps it for as long as the player keeps the same gear and target.
    /// Toggling painterly off therefore left a painted portrait sitting in an
    /// unpainted HUD indefinitely, and moving a slider left every portrait
    /// frozen at the settings it was first baked with. The APERTURE no longer
    /// depends on the mode at all (PortraitRenderTarget.CircularTextureHandle);
    /// this exists purely for the pigment.
    /// </summary>
    private void InvalidatePortraitStyling()
    {
        _playerPortraitDirty = true;
        _playerPortraitRetryAt = 0;
        // The target bake has no dirty flag of its own - "not usable" is what
        // makes BakeDirtyPortraits reconsider it on the next pass.
        _targetPortraitUsable = false;
        _targetPortraitRetryAt = 0;
        _petPortraitUsable = false;
        _petPortraitRetryAt = 0;
        // The paper dolls too - they are character renders carrying the same
        // frozen style. Dirty only, NOT "unusable": these rebake solely while
        // their frame is open, and dropping the current image would blank the
        // character sheet for anyone who changed a setting with it closed.
        _paperDollDirty = true;
        _inspectPaperDollDirty = true;
        _petPaperDollDirty = true;
        _dressUpDirty = true;
    }

    private bool RenderPortraitItemGlows(ref PortraitGlowLane? lane, string laneKey,
        Camera camera, IReadOnlyList<ItemGlowPlacement> placements)
    {
        if (_portraitGlowUnavailable || _gl is null || _mpq is null ||
            _spellEffectMeshes is null) return false;

        double now = NowSeconds();
        if (placements.Count == 0)
        {
            if (lane is not null)
            {
                lane.Source.SyncItemGlows(Array.Empty<ItemGlowPlacement>(), now);
                lane.Active = false;
            }
            return false;
        }

        if (lane is null)
        {
            try
            {
                string shaderDirectory = Path.Combine(AppContext.BaseDirectory, "Shaders");
                if (!File.Exists(Path.Combine(shaderDirectory, "spell_particle.vert")))
                    shaderDirectory = Path.Combine(_config.RepoRoot, "MSUIClient", "Shaders");
                lane = new PortraitGlowLane(_gl, _config, _mpq, shaderDirectory);
            }
            catch (Exception ex)
            {
                _portraitGlowUnavailable = true;
                Console.WriteLine($"[portrait-glow] renderer unavailable: {ex.Message}");
                return false;
            }
        }

        ItemGlowPlacement[] scoped = placements.Select(placement => new ItemGlowPlacement(
            $"portrait:{laneKey}:{placement.Key}", placement.Path, placement.Transform,
            placement.RenderMesh)).ToArray();
        lane.Source.SyncItemGlows(scoped, now);
        float dt = lane.LastUpdate > 0
            ? (float)Math.Clamp(now - lane.LastUpdate, 1.0 / 240.0, 0.05)
            : 1f / 30f;
        lane.LastUpdate = now;
        lane.Active = true;

        lane.Particles.FarClip = camera.FarPlane;
        lane.Particles.FogEnabled = false;
        lane.Particles.Simulate(dt, camera.Position, lane.Source.EmitterInstances(
            now, static _ => SpellUnitPose.Missing, cameraWorld: camera.Position,
            cameraForwardWorld: camera.Forward));

        SpellEffectMeshRenderer meshes = _spellEffectMeshes;
        Vector3 savedSunDirection = meshes.SunDirection;
        Vector3 savedSunColor = meshes.SunColor;
        float savedSunIntensity = meshes.SunIntensity;
        Vector3 savedAmbientColor = meshes.AmbientColor;
        float savedAmbientIntensity = meshes.AmbientIntensity;
        Vector3 savedFogColor = meshes.FogColor;
        float savedFogStart = meshes.FogStart;
        float savedFogEnd = meshes.FogEnd;
        float savedFarClip = meshes.FarClip;
        bool savedFogEnabled = meshes.FogEnabled;
        try
        {
            meshes.SunDirection = Vector3.Normalize(new Vector3(0.25f, -0.45f, 0.85f));
            meshes.SunColor = new Vector3(0.85f, 0.82f, 0.78f);
            meshes.SunIntensity = 1f;
            meshes.AmbientColor = new Vector3(0.58f, 0.56f, 0.54f);
            meshes.AmbientIntensity = 1f;
            meshes.FogEnabled = false;
            meshes.FarClip = camera.FarPlane;
            IEnumerable<SpellMeshDraw> draws = lane.Source.MeshInstances(
                now, static _ => SpellUnitPose.Missing).Concat(lane.Particles.GeometryInstances());
            meshes.Render(camera, draws);

            lane.Ribbons.FogEnabled = false;
            lane.Ribbons.FarClip = camera.FarPlane;
            lane.Ribbons.Render(camera, lane.Source.RibbonInstances(
                now, static _ => SpellUnitPose.Missing));
            lane.Particles.Render(camera);
        }
        finally
        {
            meshes.SunDirection = savedSunDirection;
            meshes.SunColor = savedSunColor;
            meshes.SunIntensity = savedSunIntensity;
            meshes.AmbientColor = savedAmbientColor;
            meshes.AmbientIntensity = savedAmbientIntensity;
            meshes.FogColor = savedFogColor;
            meshes.FogStart = savedFogStart;
            meshes.FogEnd = savedFogEnd;
            meshes.FarClip = savedFarClip;
            meshes.FogEnabled = savedFogEnabled;
        }
        return true;
    }

    private void BakeDirtyPortraits()
    {
        // Settle any pending painterly rebake first, so a style change that
        // lands this frame is baked in this frame rather than the next one.
        FlushPainterlyArt();

        // The reference's body-model widgets are live while an item/enchant effect is present.
        // One final bake after an effect disappears clears it; then the ordinary appearance-edge
        // cache takes over again. Round unit portraits deliberately remain one-shot stills.
        if (_inspectPaperDollGlow?.Active == true) _inspectPaperDollDirty = true;
        if (_dressUpOpen) _dressUpDirty = true;
        if (_characterOpen && _characterTab == 1) _petPaperDollDirty = true;
        else _petPaperDollLastUpdate = 0;

        if (!_characterOpen || _characterTab != 0) _paperDollLastUpdate = 0;
        if (_playerPortraitDirty && NowSeconds() >= _playerPortraitRetryAt &&
            _playerPortrait is not null && _character is { Loaded: true, Enabled: true })
        {
            CharacterRenderer.UnitState state = BuildUnitState();
            // Benilla/reference booth law: scale 1, origin-local, frozen pose. A portrait must not
            // inherit live world translation, locomotion heading, animation phase, or display scale.
            state.Position = Vector3.Zero;
            state.Yaw = -_character.HeadingOffsetDegrees * MathF.PI / 180f;
            state.Forward = 0f;
            state.Strafe = 0f;
            float savedModelScale = _character.ModelScale;
            bool savedBindPose = _character.BindPose;
            bool savedFrozenStandPose = _character.FrozenStandPose;
            _character.ModelScale = 1f;
            _character.BindPose = false;
            _character.FrozenStandPose = true;

            string tuningKey = PlayerPortraitKey(_character);
            (PortraitTuning tuning, bool storeHit) = ResolveTuningWithHit(tuningKey);
            M2PortraitCamera authored = default;
            Matrix4x4 portraitTransform = default;
            bool authoredCamera = tuning.ForceSource != PortraitCameraSource.Bounds &&
                _character.TryGetAuthoredPortrait(state, out authored, out portraitTransform);
            // A degenerate authored camera (eye == target) would normalize a zero forward into
            // NaNs and bake garbage; the reference tolerates it via normalize_or_zero. Fall back.
            authoredCamera = authoredCamera &&
                Vector3.DistanceSquared(authored.Position, authored.Target) > 1e-8f;
            bool forcedAuthoredMissing =
                tuning.ForceSource == PortraitCameraSource.Authored && !authoredCamera;
            Camera camera = authoredCamera
                ? AuthoredPortraitCamera(authored, portraitTransform)
                : BoundsPortraitCamera(
                    Vector3.Zero, state.Yaw, _character.BindPoseHeight(), tuning);
            bool usedFallbackCamera = !authoredCamera && !forcedAuthoredMissing;
            bool authoredRetriedAsBounds = false;

            Vector3 sunDirection = _character.SunDirection;
            Vector3 sunColor = _character.SunColor;
            float sunIntensity = _character.SunIntensity;
            Vector3 ambientColor = _character.AmbientColor;
            float ambientIntensity = _character.AmbientIntensity;
            float fogStart = _character.FogStart;
            float fogEnd = _character.FogEnd;
            try
            {
                // Stable portrait-booth light, independent of the zone's time of day.
                _character.SunDirection = Vector3.Normalize(new Vector3(0.25f, -0.45f, 0.85f));
                _character.SunColor = new Vector3(0.85f, 0.82f, 0.78f);
                _character.SunIntensity = 1f;
                _character.AmbientColor = new Vector3(0.58f, 0.56f, 0.54f);
                _character.AmbientIntensity = 1f;
                _character.FogStart = 1000f;
                _character.FogEnd = 2000f;
                _playerPortrait.Bake(() =>
                {
                    if (!forcedAuthoredMissing) _character.Render(camera, state);
                });
                PortraitRenderTarget.ReadbackStats stats = _playerPortrait.Analyze();
                if (!stats.HasSubject && authoredCamera &&
                    tuning.ForceSource != PortraitCameraSource.Authored)
                {
                    Console.WriteLine($"[portrait] player authored bake blank ({stats}); " +
                        $"eye={authored.Position}, target={authored.Target}, fov={authored.FieldOfView:F4}, " +
                        "retrying bounds camera");
                    Camera fallback = BoundsPortraitCamera(
                        Vector3.Zero, state.Yaw, _character.BindPoseHeight(), tuning);
                    _playerPortrait.Bake(() => _character.Render(fallback, state));
                    stats = _playerPortrait.Analyze();
                    camera = fallback;
                    usedFallbackCamera = true;
                    authoredRetriedAsBounds = true;
                }
                _playerPortraitUsable = stats.HasSubject;
                // Reference presentation: the round portrait is an inscribed circle cut from the
                // square bake; the frame ring's corners are transparent and hide nothing.
                //
                // Pigment and aperture are settled here, and they are INDEPENDENT.
                // Painterly styles the bake (an off-screen render target is invisible
                // to the whole-frame pass), and the round copy is built either way,
                // because painterly only squares the UNIT frames - the character
                // sheet, the talent frame and the micro-menu button keep their
                // authored round apertures and need a disc to put in them.
                // Style first, so the copy carries the same pixels as the bake.
                if (_playerPortraitUsable)
                {
                    if (PainterlyUi) StylePortrait(_playerPortrait);
                    _playerPortrait.UpdateCircularCopy();
                }
                Console.WriteLine($"[portrait] player bake " +
                    $"{(_playerPortraitUsable ? "ready" : "BLANK")} ({stats}, " +
                    $"camera={(usedFallbackCamera ? "bounds" : "authored")}, pieces={_character.VisiblePieces})");
                _verdicts.Add(new PortraitVerdict(
                    NowSeconds(),
                    PortraitSubject.Player,
                    _playerPortraitUsable ? PortraitOutcome.Ready : PortraitOutcome.Blank,
                    EffectivePortraitCameraSource(storeHit, tuning, usedFallbackCamera),
                    authoredRetriedAsBounds,
                    stats.SubjectPixels,
                    stats.MinRgb,
                    stats.MaxRgb,
                    stats.MinAlpha,
                    stats.MaxAlpha,
                    _character.VisiblePieces,
                    0,
                    _character.BindPoseHeight(),
                    usedFallbackCamera ? camera.EyeHeight : 0f,
                    usedFallbackCamera ? camera.Distance : 0f,
                    camera.AuthoredVerticalFieldOfViewRadians is float authoredFovy
                        ? authoredFovy * 180f / MathF.PI
                        : camera.FieldOfViewDegrees,
                    camera.NearPlane));
                if (!_playerPortraitUsable && !_playerPortraitFailureDumped)
                {
                    DumpPortrait(_playerPortrait, "player", "blank");
                    _playerPortraitFailureDumped = true;
                }
                if (_playerPortraitUsable)
                {
                    _playerPortraitDirty = false;
                    _playerPortraitRetryAt = 0;
                    _playerPortraitFailureDumped = false;
                }
                else
                {
                    // A transient blank bake must not become the permanent TemporaryPortrait.
                    _playerPortraitDirty = true;
                    _playerPortraitRetryAt = NowSeconds() + 1.0;
                }
            }
            finally
            {
                _character.SunDirection = sunDirection;
                _character.SunColor = sunColor;
                _character.SunIntensity = sunIntensity;
                _character.AmbientColor = ambientColor;
                _character.AmbientIntensity = ambientIntensity;
                _character.FogStart = fogStart;
                _character.FogEnd = fogEnd;
                _character.ModelScale = savedModelScale;
                _character.BindPose = savedBindPose;
                _character.FrozenStandPose = savedFrozenStandPose;
            }
        }

        if (_characterOpen && _characterTab == 0 &&
            (_paperDollDirty || _paperDollLastUpdate > 0) && _paperDoll is not null &&
            _character is { Loaded: true, Enabled: true })
        {
            CharacterRenderer.UnitState state = BuildUnitState();
            float scale = MathF.Max(0.1f, _character.ModelScale);
            state.Position = Vector3.Zero;
            state.Yaw = 0f;
            state.Grounded = true;
            state.VerticalVelocity = 0f;
            state.Flying = false;
            state.Swimming = false;
            state.Engaged = false;
            state.StandState = 0;
            state.FreezePose = false;
            state.Forward = 0f;
            state.Strafe = 0f;
            state.Speed = 0f;
            state.Steering = false;
            state.HasIntent = true;
            double paperDollNow = NowSeconds();
            _paperDollAnimationTime += PaperDollUiLaw.LiveAnimationStep(
                paperDollNow, _paperDollLastUpdate);
            _paperDollLastUpdate = paperDollNow;
            float distance = 4.15f * scale;
            Camera camera = PortraitCamera(state.Position, state.Yaw + _paperDollRotation,
                1.25f * scale, distance);
            camera.FieldOfViewDegrees = 43f;
            camera.AspectRatio = 233f / 224f;
            float? savedStandPreviewTime = _character.StandPreviewTime;
            Matrix4x4? savedMountSeat = _character.MountSeat;
            _character.StandPreviewTime = _paperDollAnimationTime;
            _character.MountSeat = null;
            try
            {
                WithPortraitLighting(() => _paperDoll.Bake(() =>
                {
                    _character.Render(camera, state);
                    RenderPortraitItemGlows(ref _paperDollGlow, "paper-doll", camera,
                        _character.ItemGlowPlacements);
                }, transparent: true));
            }
            finally
            {
                _character.StandPreviewTime = savedStandPreviewTime;
                _character.MountSeat = savedMountSeat;
            }
            // Painted like every other character surface when the mode is on.
            // Safe on a cut-out bake: the style pass writes the SOURCE alpha
            // back, so the transparent background survives styling and the doll
            // does not gain a painted rectangle behind it.
            if (PainterlyUi) StylePortrait(_paperDoll);
            _paperDollDirty = false;
        }

        if (_dressUpOpen && _dressUpDirty && _dressUpTarget is not null &&
            _dressUpRenderer is { Loaded: true, Enabled: true })
        {
            var state = new CharacterRenderer.UnitState
            {
                Position = Vector3.Zero,
                Yaw = 0f,
                Grounded = true,
                HasIntent = true,
            };
            double dressUpNow = NowSeconds();
            float dressUpDt = DressUpFrameUiLaw.LiveAnimationStep(
                dressUpNow, _dressUpPaneLastUpdate);
            _dressUpPaneLastUpdate = dressUpNow;
            _dressUpRenderer.Update(dressUpDt, state);
            Camera camera = PortraitCamera(Vector3.Zero, _dressUpRotation, 1.15f, 4.15f);
            camera.FieldOfViewDegrees = 43f;
            camera.AspectRatio = 316f / 351f;
            WithPortraitLighting(() => _dressUpTarget.Bake(() =>
            {
                _dressUpRenderer.Render(camera, state);
                RenderPortraitItemGlows(ref _dressUpGlow, "dress-up", camera,
                    _dressUpRenderer.ItemGlowPlacements);
            }, transparent: true));
            if (PainterlyUi) StylePortrait(_dressUpTarget);
            _dressUpDirty = false;
        }

        if (_inspectOpen && _inspectPaperDoll is not null && _creatures is not null &&
            _entities.TryGet(_inspectGuid, out WorldEntity inspected) && inspected.IsPlayer)
        {
            ulong appearance = unchecked((ulong)(uint)inspected.DisplayId + 1469598103934665603ul);
            for (int slot = 0; slot < 19; slot++)
            {
                appearance = unchecked((appearance ^ inspected.Fields.PlayerVisibleItemEntry(slot)) *
                    1099511628211ul);
                for (int enchantSlot = 0; enchantSlot < 7; enchantSlot++)
                    appearance = unchecked((appearance ^
                        inspected.Fields.PlayerVisibleItemEnchant(slot, enchantSlot)) *
                        1099511628211ul);
            }
            if (_inspectPaperDollGuid != inspected.Guid ||
                _inspectPaperDollAppearance != appearance)
            {
                _inspectPaperDollDirty = true;
                _inspectPaperDollUsable = false;
            }

            if (_inspectPaperDollDirty &&
                _creatures.TryGetPortraitFraming(inspected, out CreatureRenderer.PortraitFraming framing))
            {
                const float fov = 43f;
                float window = MathF.Max(.75f, framing.Height * 1.10f);
                float distance = (window * .5f) / MathF.Tan(fov * .5f * MathF.PI / 180f);
                Camera camera = PortraitCamera(inspected.Position,
                    inspected.Orientation + _inspectRotation, framing.Height * .52f, distance);
                camera.FieldOfViewDegrees = fov;
                camera.AspectRatio = 233f / 300f;
                camera.NearPlane = MathF.Max(.05f, distance - framing.Height);
                bool drawn = false;
                WithPortraitLighting(camera, () => _inspectPaperDoll.Bake(
                    () =>
                    {
                        _creatures.BeginItemGlowFrame();
                        drawn = _creatures.RenderPortrait(camera, inspected);
                        if (drawn)
                            RenderPortraitItemGlows(ref _inspectPaperDollGlow, "inspect",
                                camera, _creatures.ItemGlowPlacements);
                    }, transparent: true));
                PortraitRenderTarget.ReadbackStats stats = _inspectPaperDoll.Analyze();
                _inspectPaperDollUsable = drawn && stats.HasSubject;
                if (_inspectPaperDollUsable)
                {
                    // After Analyze, whose clear-colour comparison assumes an
                    // unstyled surface; the cut-out survives styling as above.
                    if (PainterlyUi) StylePortrait(_inspectPaperDoll);
                    _inspectPaperDollDirty = _inspectPaperDollGlow?.Active == true;
                    _inspectPaperDollGuid = inspected.Guid;
                    _inspectPaperDollAppearance = appearance;
                }
            }
        }

        UpdatePartyPortraits();

        if (_petPortrait is not null && _creatures is not null &&
            TryGetControlledPet(out WorldEntity portraitPet))
        {
            ulong appearance = PortraitAppearanceSignature(portraitPet);
            bool dirty = !_petPortraitUsable || _petPortraitGuid != portraitPet.Guid ||
                _petPortraitAppearance != appearance;
            if (dirty && NowSeconds() >= _petPortraitRetryAt &&
                _creatures.TryGetPortraitFraming(portraitPet, out _))
            {
                (PortraitTuning tuning, bool storeHit) =
                    ResolveTuningWithHit(CreaturePortraitKey(portraitPet.DisplayId));
                if (TryBakeCreaturePortrait(_petPortrait, portraitPet, tuning, storeHit,
                        out CreaturePortraitBake petBake))
                {
                    _petPortraitUsable = petBake.Drawn && petBake.Stats.HasSubject;
                    if (_petPortraitUsable)
                    {
                        if (PainterlyUi) StylePortrait(_petPortrait);
                        _petPortrait.UpdateCircularCopy();
                        _petPortraitGuid = portraitPet.Guid;
                        _petPortraitAppearance = appearance;
                        _petPortraitRetryAt = 0;
                    }
                    else _petPortraitRetryAt = NowSeconds() + 1;
                }
            }
        }
        else
        {
            _petPortraitUsable = false;
            _petPortraitGuid = 0;
        }

        if (_targetPortrait is null || _creatures is null || _selectionGuid == 0 ||
            !_entities.TryGet(_selectionGuid, out WorldEntity target) || !target.IsUnit ||
            target.Guid == ControlledGuid)
        {
            _portraitTargetGuid = 0;
            _portraitRequestAppearance = 0;
            _targetPortraitUsable = false;
            return;
        }

        if (_characterOpen && _characterTab == 1 && _petPaperDoll is not null &&
            _creatures is not null && TryGetControlledPet(out WorldEntity pet))
        {
            ulong appearance = PortraitAppearanceSignature(pet);
            if (_petPaperDollGuid != pet.Guid || _petPaperDollAppearance != appearance)
            {
                _petPaperDollDirty = true;
                _petPaperDollUsable = false;
                _petPaperDollAnimationTime = 0;
                _petPaperDollLastUpdate = 0;
            }
            if (_petPaperDollDirty &&
                _creatures.TryGetPortraitFraming(pet, out CreatureRenderer.PortraitFraming framing))
            {
                const float fov = 43f;
                float window = MathF.Max(.75f, framing.Height * 1.10f);
                float distance = (window * .5f) / MathF.Tan(fov * .5f * MathF.PI / 180f);
                Camera camera = PortraitCamera(pet.Position,
                    pet.Orientation + _petPaperDollRotation,
                    framing.Height * .52f, distance);
                camera.FieldOfViewDegrees = fov;
                camera.AspectRatio = 318f / 224f;
                camera.NearPlane = MathF.Max(.05f, distance - framing.Height);
                double petDollNow = NowSeconds();
                _petPaperDollAnimationTime += PetPaperDollUiLaw.LiveAnimationStep(
                    petDollNow, _petPaperDollLastUpdate);
                _petPaperDollLastUpdate = petDollNow;
                bool drawn = false;
                WithPortraitLighting(camera, () => _petPaperDoll.Bake(
                    () => drawn = _creatures.RenderPortrait(
                        camera, pet, _petPaperDollAnimationTime), transparent: true));
                PortraitRenderTarget.ReadbackStats stats = _petPaperDoll.Analyze();
                _petPaperDollUsable = drawn && stats.HasSubject;
                if (_petPaperDollUsable)
                {
                    if (PainterlyUi) StylePortrait(_petPaperDoll);
                    _petPaperDollDirty = false;
                    _petPaperDollGuid = pet.Guid;
                    _petPaperDollAppearance = appearance;
                }
            }
        }

        // Benilla keys the frozen booth image by rendered appearance, not by unit GUID.
        // Players include their face/hair/facial-hair and visible equipment in that key: while
        // possessing a bot, targeting the parked session character must show that exact body.
        ulong targetAppearance = PortraitAppearanceSignature(target);
        bool requestChanged = targetAppearance != _portraitRequestAppearance;
        if (requestChanged)
        {
            _portraitRequestAppearance = targetAppearance;
            _targetPortraitRetryAt = 0;
            _targetPortraitFailureDumped = false;
            _targetPortraitUsable = false;
        }

        bool appearanceChanged = targetAppearance != _portraitTargetAppearance;
        if (_targetPortraitUsable && !appearanceChanged)
        {
            _portraitTargetGuid = target.Guid;
            return;
        }
        bool changed = requestChanged || appearanceChanged || !_targetPortraitUsable;
        if (!changed || NowSeconds() < _targetPortraitRetryAt) return;

        string targetTuningKey = target.IsPlayer
            ? PlayerPortraitKey(target)
            : CreaturePortraitKey(target.DisplayId);
        (PortraitTuning targetTuning, bool targetStoreHit) =
            ResolveTuningWithHit(targetTuningKey);
        if (!TryBakeCreaturePortrait(
                _targetPortrait, target, targetTuning, targetStoreHit, out CreaturePortraitBake bake))
            return;
        _targetPortraitUsable = bake.Drawn && bake.Stats.HasSubject;
        _verdicts.Add(new PortraitVerdict(
            NowSeconds(),
            PortraitSubject.Target,
            !bake.Drawn
                ? PortraitOutcome.NotDrawn
                : bake.Stats.HasSubject ? PortraitOutcome.Ready : PortraitOutcome.Blank,
            EffectivePortraitCameraSource(targetStoreHit, targetTuning, bake.UsedBounds),
            bake.AuthoredRetriedAsBounds,
            bake.Stats.SubjectPixels,
            bake.Stats.MinRgb,
            bake.Stats.MaxRgb,
            bake.Stats.MinAlpha,
            bake.Stats.MaxAlpha,
            -1,
            target.DisplayId,
            bake.Framing.Height,
            bake.UsedBounds ? bake.Camera.EyeHeight : 0f,
            bake.UsedBounds ? bake.Camera.Distance : 0f,
            bake.Camera.AuthoredVerticalFieldOfViewRadians is float targetAuthoredFovy
                ? targetAuthoredFovy * 180f / MathF.PI
                : bake.Camera.FieldOfViewDegrees,
            bake.Camera.NearPlane));
        if (_targetPortraitUsable)
        {
            // Pigment then aperture, both unconditional in their own right; see
            // the player bake above for why the round copy is always built.
            if (PainterlyUi) StylePortrait(_targetPortrait);
            _targetPortrait.UpdateCircularCopy();
            _portraitTargetGuid = target.Guid;
            _portraitTargetAppearance = targetAppearance;
            _targetPortraitRetryAt = 0;
            _targetPortraitFailureDumped = false;
        }
        else
        {
            _targetPortraitRetryAt = NowSeconds() + 1.0;
            if (!_targetPortraitFailureDumped)
            {
                Console.WriteLine($"[portrait] target bake BLANK ({bake.Stats}, " +
                    $"kind={(target.IsPlayer ? "player" : "creature")}, display={target.DisplayId})");
                DumpPortrait(_targetPortrait, $"target-{target.DisplayId}", "blank");
                _targetPortraitFailureDumped = true;
            }
        }
    }

    private readonly record struct CreaturePortraitBake(
        bool Drawn,
        PortraitRenderTarget.ReadbackStats Stats,
        Camera Camera,
        bool UsedBounds,
        bool AuthoredRetriedAsBounds,
        CreatureRenderer.PortraitFraming Framing);

    private bool TryBakeCreaturePortrait(
        PortraitRenderTarget renderTarget,
        WorldEntity target,
        PortraitTuning tuning,
        bool overrideHit,
        out CreaturePortraitBake result)
    {
        result = default;
        if (_creatures is null ||
            !_creatures.TryGetPortraitFraming(target, out CreatureRenderer.PortraitFraming framing))
            return false;

        bool deriveFromHeight = overrideHit &&
            (tuning.HeadFraction != PortraitTuning.Default.HeadFraction ||
             tuning.WindowFraction != PortraitTuning.Default.WindowFraction);
        M2PortraitCamera authoredData = default;
        Matrix4x4 transform = default;
        bool usesAuthored = tuning.ForceSource != PortraitCameraSource.Bounds &&
            _creatures.TryGetAuthoredPortrait(target, out authoredData, out transform) &&
            Vector3.DistanceSquared(authoredData.Position, authoredData.Target) > 1e-8f;
        bool forcedAuthoredMissing =
            tuning.ForceSource == PortraitCameraSource.Authored && !usesAuthored;
        Camera camera = usesAuthored
            ? AuthoredPortraitCamera(authoredData, transform)
            : CreatureBoundsPortraitCamera(target, framing, tuning, deriveFromHeight);

        bool drawn = false;
        WithPortraitLighting(camera, () => renderTarget.Bake(() =>
        {
            if (forcedAuthoredMissing) drawn = true;
            else drawn = _creatures.RenderPortrait(camera, target);
        }));
        PortraitRenderTarget.ReadbackStats stats = renderTarget.Analyze();
        bool retriedAsBounds = false;
        if (drawn && !stats.HasSubject && tuning.ForceSource != PortraitCameraSource.Authored)
        {
            Camera fallback = CreatureBoundsPortraitCamera(
                target, framing, tuning, deriveFromHeight);
            WithPortraitLighting(fallback, () =>
                renderTarget.Bake(() => drawn = _creatures.RenderPortrait(fallback, target)));
            stats = renderTarget.Analyze();
            camera = fallback;
            retriedAsBounds = usesAuthored;
        }
        bool usedBounds = !forcedAuthoredMissing && (!usesAuthored || retriedAsBounds);
        result = new CreaturePortraitBake(
            drawn, stats, camera, usedBounds, retriedAsBounds, framing);
        return true;
    }

    private PortraitTuning ResolveTuning(string key)
    {
        if (TryResolveLabTuning(key, out PortraitTuning lab)) return lab;
        return _portraitOverrides?.Find(key) ?? PortraitTuning.Default;
    }

    private (PortraitTuning Tuning, bool StoreHit) ResolveTuningWithHit(string key)
    {
        if (TryResolveLabTuning(key, out PortraitTuning lab)) return (lab, true);
        PortraitTuning? stored = _portraitOverrides?.Find(key);
        return stored is null ? (PortraitTuning.Default, false) : (stored, true);
    }

    private static PortraitCameraSource EffectivePortraitCameraSource(
        bool storeHit, PortraitTuning tuning, bool usedBounds)
    {
        bool overrideChangedPath = storeHit && tuning != PortraitTuning.Default &&
            (usedBounds || tuning.ForceSource is not null);
        if (overrideChangedPath) return PortraitCameraSource.Override;
        return usedBounds ? PortraitCameraSource.Bounds : PortraitCameraSource.Authored;
    }

    private static string PlayerPortraitKey(CharacterRenderer character) =>
        $"player:{character.Race.ToLowerInvariant()}-{character.Gender.ToLowerInvariant()}";

    /// <summary>
    /// The same tuning key for a STREAMED player (party members), built from the wire
    /// bytes rather than the loaded avatar. RaceFolder is what feeds CharacterRenderer.Race,
    /// so "player:nightelf-female" resolves identically whichever side asks for it.
    /// </summary>
    private static string PlayerPortraitKey(WorldEntity unit)
    {
        (byte race, _, byte gender, _) = unit.Fields.Bytes0;
        return $"player:{RaceFolder(race).ToLowerInvariant()}-{(gender == 1 ? "female" : "male")}";
    }

    private static string CreaturePortraitKey(int displayId) => $"creature:{displayId}";

    private uint PetPortraitHandle(ulong guid) =>
        _petPortraitUsable && _petPortraitGuid == guid && _petPortrait is not null
            ? _petPortrait.CircularTextureHandle : 0;

    private static Camera BoundsPortraitCamera(
        Vector3 feet, float modelYaw, float modelHeight, PortraitTuning tuning)
    {
        float head = MathF.Max(0.3f, modelHeight);
        float target = tuning.HeadFraction * head;
        float window = Math.Clamp(
            tuning.WindowFraction * head, tuning.WindowMin, tuning.WindowMax);
        float fovyDegrees = tuning.FovyDegrees;
        float distance = (window * 0.5f) /
            MathF.Tan(fovyDegrees * 0.5f * MathF.PI / 180f);
        Camera camera = PortraitCamera(feet, modelYaw + tuning.YawOffset, target, distance);
        camera.Pitch = tuning.Pitch;
        camera.FieldOfViewDegrees = fovyDegrees;
        camera.NearPlane = MathF.Max(tuning.NearFloor, distance - head);
        return camera;
    }

    private static Camera CreatureBoundsPortraitCamera(WorldEntity target,
        CreatureRenderer.PortraitFraming framing, PortraitTuning tuning,
        bool deriveFromHeight)
    {
        // Creature DBC framing remains the base. Only an explicit head/window override opts
        // into the player-style height derivation so stubborn silhouettes are hand-tunable.
        float eyeHeight = framing.EyeHeight;
        float distance = framing.Distance;
        if (deriveFromHeight)
        {
            float head = MathF.Max(0.3f, framing.Height);
            eyeHeight = tuning.HeadFraction * head;
            float window = Math.Clamp(
                tuning.WindowFraction * head, tuning.WindowMin, tuning.WindowMax);
            distance = (window * 0.5f) /
                MathF.Tan(tuning.FovyDegrees * 0.5f * MathF.PI / 180f);
        }
        Camera camera = PortraitCamera(target.Position, target.Orientation + tuning.YawOffset,
            eyeHeight, distance);
        camera.Pitch = tuning.Pitch;
        camera.FieldOfViewDegrees = tuning.FovyDegrees;
        camera.NearPlane = MathF.Max(tuning.NearFloor, distance - framing.Height);
        return camera;
    }

    private static Camera PortraitCamera(Vector3 feet, float modelYaw, float eyeHeight, float distance) => new()
    {
        Target = feet,
        Yaw = modelYaw + MathF.PI,
        Pitch = 0.02f,
        Distance = distance,
        EffectiveDistance = distance,
        EyeHeight = eyeHeight,
        FieldOfViewDegrees = 38f,
        AspectRatio = 1f,
        NearPlane = 0.05f,
        FarPlane = 100f,
    };

    private static Camera AuthoredPortraitCamera(M2PortraitCamera authored, Matrix4x4 modelTransform)
    {
        Vector3 position = Vector3.Transform(authored.Position, modelTransform);
        Vector3 target = Vector3.Transform(authored.Target, modelTransform);
        Vector3 up = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, modelTransform));
        Vector3 forward = Vector3.Normalize(target - position);
        if (MathF.Abs(authored.Roll) > 0.00001f)
            up = Vector3.Normalize(Vector3.Transform(up,
                Matrix4x4.CreateFromAxisAngle(forward, authored.Roll)));

        float modelScale = Vector3.TransformNormal(Vector3.UnitX, modelTransform).Length();
        modelScale = float.IsFinite(modelScale) && modelScale > 0f ? modelScale : 1f;
        return new Camera
        {
            AuthoredPosition = position,
            AuthoredTarget = target,
            AuthoredUp = up,
            // gxumath treats the M2 value as diagonal FOV. At the portrait
            // path's 4:3 diagonal convention, vertical half-angle is 0.3 * fov.
            AuthoredVerticalFieldOfViewRadians = authored.FieldOfView * 0.6f,
            // The final 256x256 portrait surface is square-true; 4:3 only
            // participates in the diagonal-to-vertical crop conversion above.
            AspectRatio = 1f,
            NearPlane = MathF.Max(0.02f, authored.NearClip * modelScale),
            FarPlane = authored.FarClip * modelScale,
        };
    }

    private void WithPortraitLighting(Action draw) => WithPortraitLighting(null, draw);

    private void WithPortraitLighting(Camera? creatureCamera, Action draw)
    {
        if (_character is null) return;
        Vector3 sunDirection = _character.SunDirection;
        Vector3 sunColor = _character.SunColor;
        float sunIntensity = _character.SunIntensity;
        Vector3 ambientColor = _character.AmbientColor;
        float ambientIntensity = _character.AmbientIntensity;
        float fogStart = _character.FogStart;
        float fogEnd = _character.FogEnd;
        Vector3 creatureSunDirection = _creatures?.SunDirection ?? default;
        Vector3 creatureSunColor = _creatures?.SunColor ?? default;
        float creatureSunIntensity = _creatures?.SunIntensity ?? 0;
        Vector3 creatureAmbientColor = _creatures?.AmbientColor ?? default;
        float creatureAmbientIntensity = _creatures?.AmbientIntensity ?? 0;
        float creatureFogStart = _creatures?.FogStart ?? 0;
        float creatureFogEnd = _creatures?.FogEnd ?? 0;
        try
        {
            Vector3 boothSunDirection = Vector3.Normalize(new Vector3(0.25f, -0.45f, 0.85f));
            Vector3 boothSunColor = new(0.85f, 0.82f, 0.78f);
            Vector3 boothAmbientColor = new(0.58f, 0.56f, 0.54f);
            _character.SunDirection = boothSunDirection;
            _character.SunColor = boothSunColor;
            _character.SunIntensity = 1f;
            _character.AmbientColor = boothAmbientColor;
            _character.AmbientIntensity = 1f;
            _character.FogStart = 1000f;
            _character.FogEnd = 2000f;
            if (_creatures is not null)
            {
                // Streamed players, bots and NPCs render through CreatureRenderer. Give that
                // booth a camera-relative key. Their authored portrait cameras retain the
                // unit's arbitrary world yaw, so copying a fixed world-space direction can put
                // the key behind one face and in front of another. Camera-relative means the
                // key is always above and to the viewer's left, independent of who is framed.
                _creatures.SunDirection = creatureCamera is null
                    ? boothSunDirection
                    : PortraitKeyDirection(creatureCamera);
                _creatures.SunColor = boothSunColor;
                _creatures.SunIntensity = 1f;
                _creatures.AmbientColor = boothAmbientColor;
                _creatures.AmbientIntensity = 1f;
                _creatures.FogStart = 1000f;
                _creatures.FogEnd = 2000f;
            }
            draw();
        }
        finally
        {
            _character.SunDirection = sunDirection;
            _character.SunColor = sunColor;
            _character.SunIntensity = sunIntensity;
            _character.AmbientColor = ambientColor;
            _character.AmbientIntensity = ambientIntensity;
            _character.FogStart = fogStart;
            _character.FogEnd = fogEnd;
            if (_creatures is not null)
            {
                _creatures.SunDirection = creatureSunDirection;
                _creatures.SunColor = creatureSunColor;
                _creatures.SunIntensity = creatureSunIntensity;
                _creatures.AmbientColor = creatureAmbientColor;
                _creatures.AmbientIntensity = creatureAmbientIntensity;
                _creatures.FogStart = creatureFogStart;
                _creatures.FogEnd = creatureFogEnd;
            }
        }
    }

    private static Vector3 PortraitKeyDirection(Camera camera)
    {
        Vector3 forward = camera.Forward;
        Vector3 up = camera.AuthoredUp ?? Vector3.UnitZ;
        if (up.LengthSquared() < 1e-8f) up = Vector3.UnitZ;
        else up = Vector3.Normalize(up);
        Vector3 right = Vector3.Cross(forward, up);
        if (right.LengthSquared() < 1e-8f) right = Vector3.UnitX;
        else right = Vector3.Normalize(right);
        return Vector3.Normalize(-forward + up * 0.55f - right * 0.35f);
    }

    private static void DrawPortrait(uint textureHandle, float size)
    {
        if (textureHandle == 0)
        {
            ImGui.Dummy(new Vector2(size, size));
            return;
        }

        // OpenGL's framebuffer origin is bottom-left; ImGui's image origin is top-left.
        // The Blizzard frame art supplies the circular chrome. AddImageRounded is deliberately
        // avoided: on this backend it produced a single textured fan triangle, not a full disc.
        Vector2 min = ImGui.GetCursorScreenPos();
        Vector2 max = min + new Vector2(size, size);
        ImGui.GetWindowDrawList().AddImage((nint)textureHandle, min, max,
            new Vector2(0, 1), new Vector2(1, 0));
        ImGui.Dummy(new Vector2(size, size));
    }

    private static void DumpPortrait(PortraitRenderTarget target, string name, string suffix)
    {
        try
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "portrait-diagnostics");
            string path = Path.Combine(directory, $"{name}-{suffix}.png");
            target.SavePng(path);
            Console.WriteLine($"[portrait] FBO dumped to {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[portrait] failed to dump {name}: {ex.Message}");
        }
    }

    private void DisposePortraits()
    {
        _playerPortrait?.Dispose();
        _targetPortrait?.Dispose();
        _petPortrait?.Dispose();
        _paperDoll?.Dispose();
        _inspectPaperDoll?.Dispose();
        _petPaperDoll?.Dispose();
        _dressUpTarget?.Dispose();
        _paperDollGlow?.Dispose();
        _inspectPaperDollGlow?.Dispose();
        _dressUpGlow?.Dispose();
        _dressUpRenderer?.Dispose();
        _labPortrait?.Dispose();
        _playerPortrait = null;
        _targetPortrait = null;
        _petPortrait = null;
        _paperDoll = null;
        _inspectPaperDoll = null;
        _petPaperDoll = null;
        _dressUpTarget = null;
        _paperDollGlow = null;
        _inspectPaperDollGlow = null;
        _dressUpGlow = null;
        _dressUpRenderer = null;
        _labPortrait = null;
        foreach (PartyPortrait entry in _partyPortraits.Values) entry.Target?.Dispose();
        _partyPortraits.Clear();
    }
}
