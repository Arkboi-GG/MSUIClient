using System.Diagnostics;
using System.Numerics;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.Net;
using Shader = MSUIClient.Engine.Shader;
using Texture = MSUIClient.Engine.Texture;

namespace MSUIClient.World.Units;

// Draws the networked entity stream: every CREATURE/NPC as its M2 model at the
// server-given position / orientation / scale, SKINNED, ANIMATED, TEXTURED, and (for
// humanoid NPCs) GEOSET-FILTERED so only the right hairstyle/beard/armour variants draw.
//
// TRANSFORM (camera-relative, matches CharacterRenderer):
//   Scale * RotationY(heading) * Basis * Translate(pos), eye subtracted from the row.
//
// TEXTURES: resolved BY M2 TEXTURE TYPE — 0 embedded, 11/12/13 monster-skin variations,
//   type-1 CHAR_SKIN via CreatureDisplayInfoExtra (baked atlas or default body skin).
//
// GEOSETS (new): a character-model NPC's M2 holds EVERY variant (all hairstyles, beards,
//   sleeves...). CharacterGeosets.Visible() (benilla visible_geosets) computes the set of
//   skinSectionIds to draw from the NPC's CreatureDisplayInfoExtra hair/facial/equipment;
//   any submesh not in the set is skipped. Beasts are unfiltered (they have no variants).
//
// ANIMATION: one M2Animator per model, per-instance clock; idle/walk/run from spline speed.

public sealed class CreatureRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly MpqMount _mpq;
    private readonly ClientConfig _config;
    private readonly string _attachmentShaderDir;
    private Shader? _shader;
    private CreatureModelResolver? _resolver;
    private ItemDisplayTable? _itemDisplay;
    private CharacterGeosets? _geosets;
    private readonly Dictionary<string, LoadedModel?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private sealed class UnitAttachments
    {
        public AttachedItemRenderer Renderer = null!;
        public string Signature = "";
    }
    private readonly Dictionary<ulong, UnitAttachments> _unitAttachments = [];
    private readonly Matrix4x4[] _bindSkin = Enumerable.Repeat(Matrix4x4.Identity, M2Animator.MaxBones).ToArray();

    public bool Enabled { get; set; } = true;
    public bool Ok { get; private set; }

    public float HeadingOffsetDegrees { get; set; } = 90f;
    public float ScaleMultiplier { get; set; } = 1f;
    public int DrawnLastFrame { get; private set; }
    public int AnimatedLastFrame { get; private set; }
    public long CombatActionsTriggered { get; private set; }
    public int CombatActionsActive => _combatActions.Count;
    public ulong HoveredGuid { get; set; }
    public ulong SelectedGuid { get; set; }
    public Action<string, int, M2Animator.Resolution>? AnimationResolved { get; set; }

    public readonly record struct PortraitSpecimen(int DisplayId, string ModelPath);
    private IReadOnlyList<PortraitSpecimen> _portraitSpecimens = Array.Empty<PortraitSpecimen>();
    public IReadOnlyList<PortraitSpecimen> PortraitSpecimens => _portraitSpecimens;

    private const int BaseAnimationTrack = 0;
    private const int ActionAnimationTrack = 1;
    private const int SpellHoldAnimationTrack = 2;

    /// <summary>Master animation switch (off = static bind pose).</summary>
    public bool Animate { get; set; } = true;

    /// <summary>Beyond this range a creature draws its static bind pose (skinning you couldn't see anyway).</summary>
    public float AnimateDistance { get; set; } = 130f;

    /// <summary>Rendered scale used by the CPU targeting proxy.</summary>
    public float PickScale(WorldEntity entity)
        => UnitRenderScale(entity.Scale, ScaleMultiplier);

    public static float UnitRenderScale(float objectFieldScale, float tuningMultiplier = 1f)
        => MathF.Max(0.01f, objectFieldScale) * tuningMultiplier;

    /// <summary>Filter humanoid-NPC geosets to the correct variants (off = draw every geoset, the old blob).</summary>
    public bool GeosetFilter { get; set; } = true;

    private static readonly Vector3 SunDir = Vector3.Normalize(new Vector3(0.45f, 0.35f, 0.82f));
    private static readonly Vector3 FogColor = new(0.56f, 0.71f, 0.85f);
    private const float FogStart = 350f, FogEnd = 900f;

    private static readonly int[] CreatureAnims =
        { 0, 1, 4, 5, 6, 7, 9, 13, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28,
          30, 41, 42, 85, 87, 88, 117 };
    private const float DefaultWalkSpeed = 2.5f;
    private const float MovingEpsilon = 0.1f;

    private static readonly Matrix4x4 Basis = new(
        0f, -1f, 0f, 0f,
        0f, 0f, 1f, 0f,
        -1f, 0f, 0f, 0f,
        0f, 0f, 0f, 1f);

    private const int FloatsPerVertex = 16;   // pos3 + norm3 + uv2 + weight4 + index4
    private const int LoadsPerFrame = 4;
    private int _diagLogged;

    private readonly Matrix4x4[] _skin = new Matrix4x4[M2Animator.MaxBones];
    private readonly float[] _packed = new float[M2Animator.MaxBones * 12];

    private readonly Dictionary<ulong, float> _animTime = new();
    private readonly Dictionary<ulong, CombatAction> _combatActions = new();
    private readonly Dictionary<ulong, int> _spellHolds = new();
    private readonly HashSet<ulong> _knownAlive = new();
    private readonly HashSet<ulong> _observedDead = new();
    private readonly Dictionary<ulong, float> _deathTime = new();
    private readonly HashSet<ulong> _seen = new();
    private readonly List<ulong> _stale = new();
    private float _globalTime;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastSeconds;

    private readonly record struct CombatAction(int AnimationId, float StartedAt, float ExpiresAt,
        bool AuthoredExact = false);

    public void TriggerCombatSwing(ulong guid, bool offHand)
    {
        _combatActions[guid] = new CombatAction(offHand ? 87 : 16, _globalTime, _globalTime + 3f);
        CombatActionsTriggered++;
    }

    public void TriggerCombatReaction(ulong guid, uint victimState, bool landedHit)
    {
        int animationId = victimState switch
        {
            2 or 8 => 30, // dodge / deflect
            3 => 20,      // unarmed parry fallback
            5 => 24,      // shield block
            _ when landedHit => 9, // CombatWound
            _ => -1,
        };
        if (animationId >= 0)
        {
            _combatActions[guid] = new CombatAction(animationId, _globalTime, _globalTime + 3f);
            CombatActionsTriggered++;
        }
    }

    public void BeginSpellVisual(ulong guid, ushort? animationId)
    {
        if (animationId is { } id && id != 0) _spellHolds[guid] = id;
        else _spellHolds.Remove(guid);
        _animTime[guid] = 0f;
    }

    public void ReleaseSpellVisual(ulong guid, ushort? animationId)
    {
        _spellHolds.Remove(guid);
        if (animationId is { } id && id != 0)
        {
            _combatActions[guid] = new CombatAction(id, _globalTime, _globalTime + 4f,
                AuthoredExact: true);
            CombatActionsTriggered++;
        }
    }

    public void CancelSpellVisual(ulong guid) => _spellHolds.Remove(guid);

    private sealed class LoadedModel
    {
        public uint Vao, Vbo, Ebo;
        public readonly List<DrawBatch> Batches = new();
        public M2Animator? Animator;
        public int BoneCount;
        public float MinHeight;
        public float MaxHeight;
        public float HorizontalRadius;
        public M2PortraitCamera? PortraitCamera;
        public M2Model Source = null!;
        public HashSet<int>? VisibleGeosets;   // null = draw all (beasts, or filter disabled/failed)
    }
    private struct DrawBatch { public int Start, Count; public Texture? Tex; public int Blend; public int GeosetId; }

    public CreatureRenderer(GL gl, MpqMount mpq, ClientConfig config)
    {
        _gl = gl;
        _mpq = mpq;
        _config = config;
        _attachmentShaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        if (!File.Exists(Path.Combine(_attachmentShaderDir, "attached.vert")))
            _attachmentShaderDir = Path.Combine(config.RepoRoot, "MSUIClient", "Shaders");
        try
        {
            var diBytes = mpq.ReadFile(CreatureDisplayInfoTable.MpqPath);
            var mdBytes = mpq.ReadFile(CreatureModelDataTable.MpqPath);
            var exBytes = mpq.ReadFile(CreatureDisplayExtraTable.MpqPath);
            var di = diBytes is null ? null : CreatureDisplayInfoTable.Parse(diBytes);
            var md = mdBytes is null ? null : CreatureModelDataTable.Parse(mdBytes);
            var ex = exBytes is null ? null : CreatureDisplayExtraTable.Parse(exBytes);
            if (di is not null && md is not null)
            {
                _resolver = new CreatureModelResolver(di, md, ex);
                _portraitSpecimens = di.All
                    .Select(row => _resolver.TryResolve((int)row.Id, out CreatureModelInfo info)
                        ? new PortraitSpecimen((int)row.Id, info.ModelPath)
                        : default)
                    .Where(specimen => specimen.DisplayId > 0)
                    .OrderBy(specimen => specimen.DisplayId)
                    .ToArray();
                _shader = Shader.FromSource(_gl, "creature", VertSrc, FragSrc);

                // Geoset visibility for humanoid NPCs (best-effort — filter degrades to naked defaults).
                var idBytes = mpq.ReadFile(ItemDisplayTable.MpqPath);
                _itemDisplay = idBytes is null ? null : ItemDisplayTable.Parse(idBytes);
                var hairBytes = mpq.ReadFile(CharHairGeosetsTable.MpqPath);
                var facialBytes = mpq.ReadFile(CharacterFacialHairTable.MpqPath);
                var helmBytes = mpq.ReadFile(HelmetGeosetVisTable.MpqPath);
                _geosets = new CharacterGeosets(
                    hairBytes is null ? null : CharHairGeosetsTable.Parse(hairBytes),
                    facialBytes is null ? null : CharacterFacialHairTable.Parse(facialBytes),
                    helmBytes is null ? null : HelmetGeosetVisTable.Parse(helmBytes));

                Ok = true;
                Console.WriteLine($"[creature] renderer ready ({di.Count} display rows, {md.Count} models, " +
                                  $"{(ex?.Count ?? 0)} extended-npc rows, geosets={(_geosets.Ok ? "on" : "no-dbc")})");
            }
            else Console.WriteLine("[creature] CreatureDisplayInfo/CreatureModelData DBCs missing — unit rendering off");
        }
        catch (Exception e) { Console.WriteLine($"[creature] init failed: {e.Message}"); Ok = false; }
    }

    public void Render(Camera camera, EntityStore entities)
    {
        DrawnLastFrame = 0;
        AnimatedLastFrame = 0;
        if (!Ok || !Enabled || _shader is null || _resolver is null) return;

        double nowS = _clock.Elapsed.TotalSeconds;
        float dt = (float)Math.Clamp(nowS - _lastSeconds, 0.0, 0.1);
        _lastSeconds = nowS;
        _globalTime += dt;

        Vector3 camPos = camera.Position;
        Matrix4x4 viewProj = camera.RelativeViewProjection;
        float heading0 = HeadingOffsetDegrees * MathF.PI / 180f;
        int loadsThisFrame = 0;

        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.Blend);
        _shader.Use();
        _shader.Set("uViewProj", viewProj);
        _shader.Set("uSunDir", SunDir);
        _shader.Set("uAmbientColor", new Vector3(.45f));
        _shader.Set("uDiffuseColor", new Vector3(.55f));
        _shader.Set("uFogColor", FogColor);
        _shader.Set("uFogStart", FogStart);
        _shader.Set("uFogEnd", FogEnd);
        _shader.Set("uTex", 0);
        _seen.Clear();

        foreach (var e in entities.Units)
        {
            if (!e.IsCreature) continue;
            if (e.DisplayId <= 0) continue;
            if (!_resolver.TryResolve(e.DisplayId, out CreatureModelInfo info)) continue;

            string key = CacheKey(info);
            if (!_cache.TryGetValue(key, out var model))
            {
                if (loadsThisFrame >= LoadsPerFrame) continue;
                loadsThisFrame++;
                model = LoadModel(info);
                _cache[key] = model;
            }
            if (model is null) continue;

            _seen.Add(e.Guid);
            TrackLifeState(e);

            // UNIT_FIELD_SCALE_X is already the complete unit render scale. vmangos folds
            // CreatureModelData × CreatureDisplayInfo into it; applying DbcScale again
            // squares native sub-1 scales and makes wolves/critters tiny.
            float scale = UnitRenderScale(e.Scale, ScaleMultiplier);
            float heading = e.Orientation + heading0;
            Matrix4x4 m = Matrix4x4.CreateScale(scale)
                * Matrix4x4.CreateRotationY(heading)
                * Basis
                * Matrix4x4.CreateTranslation(e.Position);
            m.M41 -= camPos.X; m.M42 -= camPos.Y; m.M43 -= camPos.Z;
            _shader.Set("uModel", m);
            _shader.Set("uHighlight", e.Guid == HoveredGuid || e.Guid == SelectedGuid ? 64f / 255f : 0f);

            int boneCount = 0;
            if (Animate && model.Animator is not null && model.BoneCount > 0 &&
                (e.IsDead || Vector3.Distance(e.Position, camPos) <= AnimateDistance))
            {
                string unit = $"creature:{e.DisplayId}";
                if (!_animTime.TryGetValue(e.Guid, out float at)) at = InitialPhase(e.Guid);
                M2Animator.Clip? clip;
                float rate;
                if (e.IsDead)
                {
                    clip = model.Animator.Resolve(unit, ActionAnimationTrack, 1, false, 6, 0);
                    rate = 1f;
                    float deathAt = _deathTime.GetValueOrDefault(e.Guid, float.PositiveInfinity);
                    at = float.IsPositiveInfinity(deathAt)
                        ? clip?.DurationSeconds ?? 0f
                        : MathF.Min(deathAt + dt, clip?.DurationSeconds ?? deathAt + dt);
                    _deathTime[e.Guid] = at;
                }
                else if (_combatActions.TryGetValue(e.Guid, out CombatAction action) &&
                    ResolveCombatClip(model.Animator, unit, action) is { } actionClip)
                {
                    clip = actionClip;
                    rate = 1f;
                    float actionTime = _globalTime - action.StartedAt;
                    if (actionTime >= actionClip.DurationSeconds)
                        _combatActions.Remove(e.Guid);
                    at = actionTime;
                }
                else if (_spellHolds.TryGetValue(e.Guid, out int heldAnimation) &&
                    model.Animator.Resolve(unit, SpellHoldAnimationTrack, heldAnimation, true) is { } holdClip)
                {
                    clip = holdClip;
                    rate = 1f;
                    at += dt;
                }
                else
                {
                    _combatActions.Remove(e.Guid);
                    clip = SelectClip(e, model.Animator, unit, out rate);
                    at += dt * rate;
                }
                if (float.IsNaN(at) || float.IsInfinity(at)) at = 0f;
                _animTime[e.Guid] = at;

                if (clip is not null)
                {
                    boneCount = Math.Min(model.BoneCount, M2Animator.MaxBones);
                    model.Animator.Evaluate(clip, at, _globalTime, _skin);
                    M2Animator.Pack(_skin, boneCount, _packed);
                    _shader.SetVec4Array("uBones", _packed, boneCount * 3);
                    AnimatedLastFrame++;
                }
            }
            _shader.Set("uBoneCount", boneCount);

            bool filter = GeosetFilter && model.VisibleGeosets is not null;
            _gl.BindVertexArray(model.Vao);
            foreach (var b in model.Batches)
            {
                if (filter && !model.VisibleGeosets!.Contains(b.GeosetId)) continue;

                bool additive = b.Blend is 3 or 4;
                bool alphaKey = b.Blend == 1;
                if (additive) { _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One); _gl.DepthMask(false); }
                else if (b.Blend >= 2) { _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha); _gl.DepthMask(false); }
                else { _gl.BlendFunc(BlendingFactor.One, BlendingFactor.Zero); _gl.DepthMask(true); }
                _shader.Set("uAlphaCut", alphaKey ? 0.5f : 0.02f);
                b.Tex?.Bind(0);
                DrawElements(b.Start, b.Count);
            }
            DrawnLastFrame++;

            DrawVirtualWeapons(camera, e, model, m, boneCount > 0 ? _skin : _bindSkin);
            // The attachment path has its own shader; restore ours before the
            // next streamed unit uploads its model/bone uniforms.
            _gl.Enable(EnableCap.Blend);
            _gl.DepthMask(true);
            _shader.Use();
        }
        _gl.BindVertexArray(0);
        _gl.DepthMask(true);

        PruneAnimState();
    }

    private void DrawVirtualWeapons(Camera camera, WorldEntity entity, LoadedModel model,
        Matrix4x4 transform, Matrix4x4[] skin)
    {
        uint d0 = entity.Fields.VirtualItemDisplay(0);
        uint d1 = entity.Fields.VirtualItemDisplay(1);
        uint d2 = entity.Fields.VirtualItemDisplay(2);
        if ((d0 | d1 | d2) == 0 || model.Source.Attachments.Count == 0) return;

        string signature = $"{d0}:{entity.Fields.VirtualItemInfo(0)}:{entity.Fields.VirtualItemSheath(0)}|" +
            $"{d1}:{entity.Fields.VirtualItemInfo(1)}:{entity.Fields.VirtualItemSheath(1)}|" +
            $"{d2}:{entity.Fields.VirtualItemInfo(2)}:{entity.Fields.VirtualItemSheath(2)}";
        if (!_unitAttachments.TryGetValue(entity.Guid, out UnitAttachments? state))
        {
            var renderer = new AttachedItemRenderer(_gl, _config);
            renderer.LoadShaders(_attachmentShaderDir);
            state = new UnitAttachments { Renderer = renderer };
            _unitAttachments[entity.Guid] = state;
        }
        if (state.Signature != signature)
        {
            var equipment = new CharacterEquipment();
            AddVirtualPiece(equipment, entity.Fields, 0, d0);
            AddVirtualPiece(equipment, entity.Fields, 1, d1);
            AddVirtualPiece(equipment, entity.Fields, 2, d2);
            equipment.Resolve(_itemDisplay);
            state.Renderer.Rebuild(equipment);
            state.Signature = signature;
        }
        state.Renderer.SheathState = entity.Fields.SheathState;
        state.Renderer.Render(camera, transform, model.Source, skin);
    }

    private static void AddVirtualPiece(CharacterEquipment equipment, ObjectFields fields,
        int heldSlot, uint display)
    {
        if (display == 0) return;
        var info = fields.VirtualItemInfo(heldSlot);
        int inventory = info.InventoryType;
        if (inventory == 0)
            inventory = heldSlot switch
            {
                0 => CharacterEquipment.Slot.MainHand,
                1 => CharacterEquipment.Slot.OffHand,
                _ => 15,
            };
        equipment.Add($"virtual weapon {heldSlot}", display, inventory, 15 + heldSlot,
            info.Class, info.Subclass, info.Material, fields.VirtualItemSheath(heldSlot));
    }

    public readonly record struct PortraitFraming(float EyeHeight, float Distance, float Height);

    public bool TryGetAuthoredPortrait(WorldEntity entity, out M2PortraitCamera camera,
        out Matrix4x4 modelTransform)
    {
        if (!TryGetModel(entity, out LoadedModel? model) || model?.PortraitCamera is not { } authored)
        {
            camera = default;
            modelTransform = default;
            return false;
        }

        float heading = entity.Orientation + HeadingOffsetDegrees * MathF.PI / 180f;
        modelTransform = Matrix4x4.CreateScale(UnitRenderScale(entity.Scale, ScaleMultiplier))
            * Matrix4x4.CreateRotationY(heading)
            * Basis
            * Matrix4x4.CreateTranslation(entity.Position);
        camera = authored;
        return true;
    }

    /// <summary>Loads the selected display if needed and derives a tight, model-space portrait camera.</summary>
    public bool TryGetPortraitFraming(WorldEntity entity, out PortraitFraming framing)
    {
        framing = default;
        if (!TryGetModel(entity, out LoadedModel? model) || model is null) return false;

        float scale = UnitRenderScale(entity.Scale, ScaleMultiplier);
        float min = model.MinHeight * scale;
        float max = model.MaxHeight * scale;
        float height = MathF.Max(0.5f, max - min);
        float eye = min + height * 0.92f;
        float window = Math.Clamp(
            MathF.Max(0.34f * height, 0.9f * model.HorizontalRadius * scale),
            0.55f, 1.10f);
        const float fovyDegrees = 0.5f * 180f / MathF.PI;
        float distance = (window * 0.5f) /
            MathF.Tan(fovyDegrees * 0.5f * MathF.PI / 180f);
        framing = new PortraitFraming(eye, MathF.Max(0.8f, distance), height);
        return true;
    }

    public float SelectionRadius(WorldEntity entity)
    {
        if (TryGetModel(entity, out LoadedModel? model) && model is not null)
            return MathF.Max(0.35f, model.HorizontalRadius * UnitRenderScale(entity.Scale, ScaleMultiplier));
        return 0.7f * UnitRenderScale(entity.Scale, ScaleMultiplier);
    }

    public bool TryGetOverheadHeight(WorldEntity entity, out float height)
    {
        height = 0f;
        if (!TryGetModel(entity, out LoadedModel? model) || model is null) return false;
        float scale = UnitRenderScale(entity.Scale, ScaleMultiplier);
        height = MathF.Max(0.3f, model.MaxHeight * scale);
        return true;
    }

    /// <summary>
    /// Draw exactly one creature for an offscreen portrait. This deliberately
    /// does not advance, track, count, or prune world animation state.
    /// </summary>
    public bool RenderPortrait(Camera camera, WorldEntity entity)
    {
        if (_shader is null || !TryGetModel(entity, out LoadedModel? model) || model is null) return false;

        Vector3 camPos = camera.Position;
        float heading = entity.Orientation + HeadingOffsetDegrees * MathF.PI / 180f;
        Matrix4x4 transform = Matrix4x4.CreateScale(UnitRenderScale(entity.Scale, ScaleMultiplier))
            * Matrix4x4.CreateRotationY(heading)
            * Basis
            * Matrix4x4.CreateTranslation(entity.Position);
        transform.M41 -= camPos.X; transform.M42 -= camPos.Y; transform.M43 -= camPos.Z;

        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.Blend);
        _shader.Use();
        _shader.Set("uViewProj", camera.RelativeViewProjection);
        _shader.Set("uModel", transform);
        _shader.Set("uSunDir", Vector3.Normalize(new Vector3(.25f, -.45f, .85f)));
        _shader.Set("uAmbientColor", new Vector3(.58f, .56f, .54f));
        _shader.Set("uDiffuseColor", new Vector3(.85f, .82f, .78f));
        _shader.Set("uFogColor", FogColor);
        _shader.Set("uFogStart", 1000f);
        _shader.Set("uFogEnd", 2000f);
        _shader.Set("uTex", 0);
        _shader.Set("uHighlight", 0f);

        int boneCount = 0;
        if (model.Animator is not null && model.BoneCount > 0)
        {
            // benilla portrait/booth.rs:48-66,203-216: a fresh Stand instance frozen at t=0.
            M2Animator.Clip? clip = model.Animator.Find(0);
            boneCount = Math.Min(model.BoneCount, M2Animator.MaxBones);
            model.Animator.Evaluate(clip, 0f, 0f, _skin);
            M2Animator.Pack(_skin, boneCount, _packed);
            _shader.SetVec4Array("uBones", _packed, boneCount * 3);
        }
        _shader.Set("uBoneCount", boneCount);

        bool filter = GeosetFilter && model.VisibleGeosets is not null;
        _gl.BindVertexArray(model.Vao);
        foreach (var batch in model.Batches)
        {
            if (filter && !model.VisibleGeosets!.Contains(batch.GeosetId)) continue;
            bool additive = batch.Blend is 3 or 4;
            bool alphaKey = batch.Blend == 1;
            if (additive) { _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One); _gl.DepthMask(false); }
            else if (batch.Blend >= 2) { _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha); _gl.DepthMask(false); }
            else { _gl.BlendFunc(BlendingFactor.One, BlendingFactor.Zero); _gl.DepthMask(true); }
            _shader.Set("uAlphaCut", alphaKey ? 0.5f : 0.02f);
            batch.Tex?.Bind(0);
            DrawElements(batch.Start, batch.Count);
        }
        _gl.BindVertexArray(0);
        _gl.DepthMask(true);
        return true;
    }

    private bool TryGetModel(WorldEntity entity, out LoadedModel? model)
    {
        model = null;
        if (!Ok || !Enabled || _resolver is null || !entity.IsCreature || entity.DisplayId <= 0 ||
            !_resolver.TryResolve(entity.DisplayId, out CreatureModelInfo info)) return false;
        string key = CacheKey(info);
        if (!_cache.TryGetValue(key, out model))
        {
            model = LoadModel(info);
            _cache[key] = model;
        }
        return model is not null;
    }

    private static M2Animator.Clip? SelectClip(
        WorldEntity e, M2Animator animator, string unit, out float rate)
    {
        rate = 1f;
        float speed = e.Spline?.AverageSpeed ?? 0f;
        if (e.Spline is null || speed <= MovingEpsilon)
            return e.Engaged
                ? animator.Resolve(unit, BaseAnimationTrack, 25, false, 26, 27, 28, 0)
                : animator.Resolve(unit, BaseAnimationTrack, 0, false);

        float walk = e.Speeds is { Length: > 0 } sp && sp[0] > 0f ? sp[0] : DefaultWalkSpeed;
        M2Animator.Clip? clip = speed > 2f * walk
            ? animator.Resolve(unit, BaseAnimationTrack, 5, false, 4, 0)
            : animator.Resolve(unit, BaseAnimationTrack, 4, false, 5, 0);

        if (clip is not null && clip.MoveSpeed > 0.01f)
            rate = Math.Clamp(speed / clip.MoveSpeed, 0.25f, 3f);
        return clip;
    }

    private static M2Animator.Clip? ResolveCombatClip(
        M2Animator animator, string unit, in CombatAction action)
    {
        if (action.AuthoredExact)
            return animator.Resolve(unit, ActionAnimationTrack, action.AnimationId, true);
        return action.AnimationId switch
        {
            16 => animator.Resolve(unit, ActionAnimationTrack, 16, false, 17, 18, 19, 85),
            87 => animator.Resolve(unit, ActionAnimationTrack, 87, false, 88, 117, 16),
            20 => animator.Resolve(unit, ActionAnimationTrack, 20, false, 21, 22, 23, 9, 0),
            7 => animator.Resolve(unit, ActionAnimationTrack, 7, false, 0),
            _ => animator.Resolve(unit, ActionAnimationTrack, action.AnimationId, false, 9, 0),
        };
    }

    private void TrackLifeState(WorldEntity entity)
    {
        if (entity.IsDead)
        {
            bool witnessedAlive = _knownAlive.Remove(entity.Guid);
            if (_observedDead.Add(entity.Guid))
                _deathTime[entity.Guid] = witnessedAlive ? 0f : float.PositiveInfinity;
            _combatActions.Remove(entity.Guid);
            return;
        }

        bool resurrected = _observedDead.Remove(entity.Guid);
        _deathTime.Remove(entity.Guid);
        _knownAlive.Add(entity.Guid);
        if (resurrected)
            _combatActions[entity.Guid] = new CombatAction(7, _globalTime, _globalTime + 3f);
    }

    private static float InitialPhase(ulong guid) => (guid % 977) / 977f * 5f;

    private void PruneAnimState()
    {
        _stale.Clear();
        foreach (var k in _animTime.Keys) if (!_seen.Contains(k)) _stale.Add(k);
        foreach (var pair in _combatActions)
            if (!_seen.Contains(pair.Key) || pair.Value.ExpiresAt <= _globalTime)
                if (!_stale.Contains(pair.Key)) _stale.Add(pair.Key);
        foreach (var k in _stale)
        {
            _animTime.Remove(k);
            _combatActions.Remove(k);
            _spellHolds.Remove(k);
            _knownAlive.Remove(k);
            _observedDead.Remove(k);
            _deathTime.Remove(k);
            if (_unitAttachments.Remove(k, out UnitAttachments? attachments))
                attachments.Renderer.Dispose();
        }
    }

    private static string CacheKey(in CreatureModelInfo info) =>
        info.HasExtended
            ? $"{info.ModelPath}|npc:{info.ExtRace}/{info.ExtSex}/{info.ExtSkin}/{info.ExtHairStyle}/{info.ExtFacialHair}/{info.BakeName}/{string.Join('.', info.ExtEquipment)}"
            : $"{info.ModelPath}|{string.Join(",", info.Textures)}";

    // Build the NPC's EquipGeosets from its 10 CreatureDisplayInfoExtra equipment display ids.
    private EquipGeosets? BuildNpcEquip(in CreatureModelInfo info)
    {
        if (_itemDisplay is null || info.ExtEquipment.Length < 10) return null;
        var eq = info.ExtEquipment;   // [head, shoulder, shirt, chest, belt, pants, boots, wrist, gloves, tabard]
        var e = new EquipGeosets();
        for (int i = 0; i < 8; i++)   // bodyslots = shirt..tabard = eq[2..9]
        {
            uint disp = eq[2 + i];
            e.Bodyslots[i] = disp != 0 ? _itemDisplay.Find(disp) : null;
        }
        if (eq[0] != 0 && _itemDisplay.Find(eq[0]) is { } head)   // helm hides hair
            e.HelmVis = (head.HelmetGeosetVis1, head.HelmetGeosetVis2);
        return e;   // NPCs carry no cloak column
    }

    private unsafe LoadedModel? LoadModel(in CreatureModelInfo info)
    {
        string path = info.ModelPath;
        try
        {
            byte[]? bytes = _mpq.ReadFile(path);
            if (bytes is null) { Console.WriteLine($"[creature] model '{path}' not in MPQ"); return null; }
            M2Model? m2 = M2Reader.Parse(bytes);
            if (m2 is null || !m2.IsValid) return null;

            var lm = new LoadedModel { PortraitCamera = m2.PortraitCamera, Source = m2 };

            var animator = M2Animator.Build(m2, CreatureAnims);
            if (animator is not null && animator.BoneCount <= M2Animator.MaxBones)
            {
                animator.ResolutionSink = (unit, track, resolution) =>
                    AnimationResolved?.Invoke(unit, track, resolution);
                lm.Animator = animator;
                lm.BoneCount = animator.BoneCount;
            }

            // Geoset visibility for humanoid NPCs (character models). Beasts stay unfiltered.
            if (info.HasExtended && _geosets is not null)
            {
                var eq = BuildNpcEquip(info);
                var vis = _geosets.Visible(info.ExtRace, info.ExtSex, info.ExtHairStyle, info.ExtFacialHair, eq);
                // Fail-safe: if the computed set matches no submesh, don't hide the whole NPC.
                int match = 0;
                foreach (var sm in m2.Submeshes) if (vis.Contains(sm.Id)) match++;
                lm.VisibleGeosets = match > 0 ? vis : null;
                if (match == 0)
                    Console.WriteLine($"[creature] {path}: geoset set matched 0 submeshes — drawing all (check DBC layout)");
            }

            var verts = new float[m2.Vertices.Count * FloatsPerVertex];
            float minHeight = float.PositiveInfinity, maxHeight = float.NegativeInfinity;
            float horizontalRadius = 0f;
            for (int i = 0; i < m2.Vertices.Count; i++)
            {
                var v = m2.Vertices[i]; int o = i * FloatsPerVertex;
                verts[o + 0] = v.PosX; verts[o + 1] = v.PosY; verts[o + 2] = v.PosZ;
                verts[o + 3] = v.NormX; verts[o + 4] = v.NormY; verts[o + 5] = v.NormZ;
                verts[o + 6] = v.TexU; verts[o + 7] = v.TexV;

                float total = v.BoneWeight0 + v.BoneWeight1 + v.BoneWeight2 + v.BoneWeight3;
                if (total <= 0f)
                {
                    verts[o + 8] = 1f; verts[o + 9] = 0f; verts[o + 10] = 0f; verts[o + 11] = 0f;
                    verts[o + 12] = 0f; verts[o + 13] = 0f; verts[o + 14] = 0f; verts[o + 15] = 0f;
                }
                else
                {
                    verts[o + 8] = v.BoneWeight0 / total; verts[o + 9] = v.BoneWeight1 / total;
                    verts[o + 10] = v.BoneWeight2 / total; verts[o + 11] = v.BoneWeight3 / total;
                    verts[o + 12] = ClampBone(v.BoneIndex0); verts[o + 13] = ClampBone(v.BoneIndex1);
                    verts[o + 14] = ClampBone(v.BoneIndex2); verts[o + 15] = ClampBone(v.BoneIndex3);
                }

                Vector3 worldBasis = Vector3.Transform(new Vector3(v.PosX, v.PosY, v.PosZ), Basis);
                minHeight = MathF.Min(minHeight, worldBasis.Z);
                maxHeight = MathF.Max(maxHeight, worldBasis.Z);
                horizontalRadius = MathF.Max(horizontalRadius,
                    MathF.Sqrt(worldBasis.X * worldBasis.X + worldBasis.Y * worldBasis.Y));
            }
            lm.MinHeight = float.IsFinite(minHeight) ? minHeight : 0f;
            lm.MaxHeight = float.IsFinite(maxHeight) ? maxHeight : 2f;
            lm.HorizontalRadius = MathF.Max(0.25f, horizontalRadius);
            ushort[] idx = m2.Indices.ToArray();

            lm.Vao = _gl.GenVertexArray(); _gl.BindVertexArray(lm.Vao);
            lm.Vbo = _gl.GenBuffer(); _gl.BindBuffer(BufferTargetARB.ArrayBuffer, lm.Vbo);
            fixed (float* p = verts) _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(verts.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
            lm.Ebo = _gl.GenBuffer(); _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, lm.Ebo);
            fixed (ushort* p = idx) _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(idx.Length * sizeof(ushort)), p, BufferUsageARB.StaticDraw);
            int stride = FloatsPerVertex * sizeof(float);
            _gl.EnableVertexAttribArray(0); _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, (uint)stride, (void*)0);
            _gl.EnableVertexAttribArray(1); _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, (uint)stride, (void*)(3 * sizeof(float)));
            _gl.EnableVertexAttribArray(2); _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, (uint)stride, (void*)(6 * sizeof(float)));
            _gl.EnableVertexAttribArray(3); _gl.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, (uint)stride, (void*)(8 * sizeof(float)));
            _gl.EnableVertexAttribArray(4); _gl.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, (uint)stride, (void*)(12 * sizeof(float)));
            _gl.BindVertexArray(0);

            string modelDir = path.Contains('\\') ? path[..path.LastIndexOf('\\')] : "";
            int textured = 0;
            string firstTex = "NONE";
            foreach (var b in m2.Batches)
            {
                if (b.SubmeshIndex >= m2.Submeshes.Count) continue;
                var sm = m2.Submeshes[b.SubmeshIndex];

                Texture? tex = null;
                if (b.TextureIndex < m2.TextureLookup.Count)
                {
                    int t = m2.TextureLookup[b.TextureIndex];
                    if (t >= 0 && t < m2.Textures.Count)
                    {
                        var candidates = ResolveBatchTexture(m2.Textures[t].Type, m2.Textures[t].Filename, modelDir, info);
                        tex = LoadTexture(candidates, out string hit);
                        if (tex is not null) { textured++; if (firstTex == "NONE") firstTex = hit; }
                    }
                }

                int blend = b.MaterialIndex < m2.RenderFlags.Count ? m2.RenderFlags[b.MaterialIndex].BlendingMode : 0;
                lm.Batches.Add(new DrawBatch { Start = sm.IndexStart, Count = sm.IndexCount, Tex = tex, Blend = blend, GeosetId = sm.Id });
            }

            if (_diagLogged < 30)
            {
                _diagLogged++;
                int vis = lm.VisibleGeosets?.Count ?? -1;
                Console.WriteLine($"[creature] {path} ext={info.HasExtended} bones={lm.BoneCount} " +
                                  $"clips={lm.Animator?.Clips.Count ?? 0} batches={lm.Batches.Count} " +
                                  $"textured={textured}/{lm.Batches.Count} visgeosets={vis} first=[{firstTex}]");
            }
            return lm;
        }
        catch (Exception e) { Console.WriteLine($"[creature] model '{path}' failed: {e.Message}"); return null; }
    }

    private static float ClampBone(byte index) => index < M2Animator.MaxBones ? index : 0f;

    // ── texture resolution ────────────────────────────────────────────────────────

    private static IReadOnlyList<string> ResolveBatchTexture(uint type, string embedded, string modelDir, in CreatureModelInfo info)
    {
        if (!string.IsNullOrEmpty(embedded)) return new[] { embedded };

        switch (type)
        {
            case 11: case 12: case 13:
            {
                int slot = (int)type - 11;
                string name = slot < info.Textures.Length && !string.IsNullOrEmpty(info.Textures[slot])
                    ? info.Textures[slot]
                    : (info.Textures.Length > 0 ? info.Textures[0] : "");
                if (string.IsNullOrEmpty(name)) return Array.Empty<string>();
                return new[] { UnderDir(modelDir, name) };
            }
            case 1:
                return NpcBodySkinCandidates(info);
            default:
                if (info.Textures.Length > 0 && !string.IsNullOrEmpty(info.Textures[0]))
                    return new[] { UnderDir(modelDir, info.Textures[0]) };
                return Array.Empty<string>();
        }
    }

    private static string UnderDir(string dir, string stem) =>
        dir.Length > 0 ? dir + "\\" + stem + ".blp" : stem + ".blp";

    private static IReadOnlyList<string> NpcBodySkinCandidates(in CreatureModelInfo info)
    {
        if (!info.HasExtended) return Array.Empty<string>();
        var list = new List<string>(3);

        if (!string.IsNullOrEmpty(info.BakeName))
        {
            string bake = info.BakeName.EndsWith(".blp", StringComparison.OrdinalIgnoreCase) ? info.BakeName : info.BakeName + ".blp";
            list.Add(bake.Contains('\\') ? bake : "Textures\\BakedNpcTextures\\" + bake);
        }

        string race = RaceFolder(info.ExtRace);
        string gender = info.ExtSex == 1 ? "Female" : "Male";
        list.Add($"Character\\{race}\\{gender}\\{race}{gender}Skin{(int)info.ExtSkin:00}_00.blp");
        list.Add($"Character\\{race}\\{gender}\\{race}{gender}Skin00_00.blp");
        return list;
    }

    private static string RaceFolder(byte race) => race switch
    {
        1 => "Human", 2 => "Orc", 3 => "Dwarf", 4 => "NightElf",
        5 => "Scourge", 6 => "Tauren", 7 => "Gnome", 8 => "Troll", _ => "Human"
    };

    private readonly Dictionary<string, Texture?> _texCache = new(StringComparer.OrdinalIgnoreCase);
    private Texture? LoadTexture(IReadOnlyList<string> candidates, out string hitPath)
    {
        hitPath = "NONE";
        foreach (var path in candidates)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (_texCache.TryGetValue(path, out var cached))
            {
                if (cached is not null) { hitPath = path; return cached; }
                continue;
            }
            Texture? tex = null;
            try
            {
                byte[]? blp = _mpq.ReadFile(path);
                if (blp is not null) { byte[] bgra = BlpDecoder.GetPixels(blp, 0, out int w, out int h); tex = Texture.From2D(_gl, bgra, w, h, mipmaps: true, repeat: true); }
            }
            catch { /* leave null */ }
            _texCache[path] = tex;
            if (tex is not null) { hitPath = path; return tex; }
        }
        return null;
    }

    private unsafe void DrawElements(int start, int count)
        => _gl.DrawElements(PrimitiveType.Triangles, (uint)count, DrawElementsType.UnsignedShort, (void*)(start * sizeof(ushort)));

    public void Dispose()
    {
        ClearPortraitCache();
        foreach (var attachments in _unitAttachments.Values) attachments.Renderer.Dispose();
        _unitAttachments.Clear();
        _shader?.Dispose();
    }

    /// <summary>Release synchronously loaded specimen assets between bounded batch chunks.</summary>
    public void ClearPortraitCache()
    {
        foreach (var m in _cache.Values)
        {
            if (m is null) continue;
            if (m.Vbo != 0) _gl.DeleteBuffer(m.Vbo);
            if (m.Ebo != 0) _gl.DeleteBuffer(m.Ebo);
            if (m.Vao != 0) _gl.DeleteVertexArray(m.Vao);
        }
        _cache.Clear();
        foreach (var t in _texCache.Values) t?.Dispose();
        _texCache.Clear();
    }

    private const string VertSrc = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNorm;
layout(location=2) in vec2 aUv;
layout(location=3) in vec4 aBoneWeights;
layout(location=4) in vec4 aBoneIndices;
uniform mat4 uModel;
uniform mat4 uViewProj;
const int MAX_BONES = 160;
uniform vec4 uBones[MAX_BONES * 3];
uniform int uBoneCount;
out vec3 vNorm;
out vec2 vUv;
out float vDist;
vec3 skinPoint(vec3 p, int b){
    vec4 h = vec4(p, 1.0);
    return vec3(dot(uBones[b*3+0], h), dot(uBones[b*3+1], h), dot(uBones[b*3+2], h));
}
vec3 skinVec(vec3 v, int b){
    return vec3(dot(uBones[b*3+0].xyz, v), dot(uBones[b*3+1].xyz, v), dot(uBones[b*3+2].xyz, v));
}
void main(){
    vec3 position = aPos;
    vec3 normal = aNorm;
    if (uBoneCount > 0){
        vec3 sp = vec3(0.0); vec3 sn = vec3(0.0); float total = 0.0;
        for (int i = 0; i < 4; i++){
            float w = aBoneWeights[i];
            if (w <= 0.0) continue;
            int b = int(aBoneIndices[i] + 0.5);
            if (b < 0 || b >= uBoneCount) continue;
            sp += skinPoint(aPos, b) * w;
            sn += skinVec(aNorm, b) * w;
            total += w;
        }
        if (total > 0.0001){ position = sp / total; normal = sn / total; }
    }
    vec4 rel = uModel * vec4(position, 1.0);
    gl_Position = uViewProj * rel;
    vNorm = normalize(mat3(uModel) * normal);
    vUv = aUv;
    vDist = length(rel.xyz);
}";

    private const string FragSrc = @"#version 330 core
in vec3 vNorm;
in vec2 vUv;
in float vDist;
uniform sampler2D uTex;
uniform vec3 uSunDir;
uniform vec3 uFogColor;
uniform float uFogStart;
uniform float uFogEnd;
uniform float uAlphaCut;
uniform float uHighlight;
uniform vec3 uAmbientColor;
uniform vec3 uDiffuseColor;
out vec4 frag;
void main(){
    vec4 t = texture(uTex, vUv);
    if (t.a < uAlphaCut) discard;
    float ndl = max(dot(normalize(vNorm), normalize(uSunDir)), 0.0);
    vec3 light = uAmbientColor + uDiffuseColor * ndl + vec3(uHighlight);
    float fog = clamp((vDist - uFogStart) / (uFogEnd - uFogStart), 0.0, 1.0);
    frag = vec4(mix(t.rgb * light, uFogColor, fog), t.a);
}";
}
