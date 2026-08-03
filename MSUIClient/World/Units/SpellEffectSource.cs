using System.Diagnostics;
using System.Numerics;
using MSUIClient.Formats;

namespace MSUIClient.World.Units;

/// <summary>
/// The single runtime owner for every SpellVisual model instance. Kits,
/// channels, aura state, missiles, and impacts all leave this class through the
/// same mesh/particle/ribbon feeds and therefore share attachment, animation,
/// lifetime, and fallback behavior.
/// </summary>
public sealed class SpellEffectSource
{
    private sealed class Asset
    {
        public string Path = "";
        public M2Model Model = null!;
        public M2ParticleEmitter[] Emitters = [];
        public string[] EmitterTextures = [];
        public M2Animator? Animator;
        public Matrix4x4[] Skin = [];
    }

    private sealed class Instance
    {
        public long Id;
        public Asset? Asset;
        public ulong Unit;
        public uint Spell;
        public StageLife Life;
        public ushort Attachment;
        public double Started;
        public double Ends;
        public string Stage = "";

        /// <summary>Fixed world-point anchor (dynamic-object area visuals): the instance sits
        /// at <see cref="Position"/> with identity rotation, no unit pose involved.</summary>
        public bool Area;
        public bool Missile;
        public ulong Target;
        public ushort DestinationAttachment;
        public Vector3 Position;
        public Vector3 Direction = Vector3.UnitX;
        public Vector3? FixedDestination;
        public float Speed;
        public double ReleaseAt;
        public double Remaining;
        public double TravelSeconds;
        public double LastMotionAt;
        public bool Launched;
        public bool Missed;
        public byte MissReason;
        public bool HasReleaseMarker;
        public ushort ReleaseBone;
        public Vector3 ReleasePosition;
        public Action<ulong, uint, bool, byte>? Arrived;
        public Action? LaunchEvent;
        public Action? EndEvent;
        public string? CustomTexture;
    }

    private readonly MpqMount _mpq;
    private readonly ItemDisplayTable? _itemDisplays;
    private readonly Dictionary<string, Asset> _assets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _assetFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Instance> _instances = [];
    private long _nextId;

    public SpellEffectSource(MpqMount mpq)
    {
        _mpq = mpq;
        _itemDisplays = mpq.ReadFile(ItemDisplayTable.MpqPath) is { } bytes
            ? ItemDisplayTable.Parse(bytes) : null;
    }
    public int ActiveCount => _instances.Count;

    public uint ItemSpellVisual(uint displayId) => _itemDisplays?.Find(displayId)?.SpellVisualId ?? 0;

    public string? AmmoModelPath(uint? displayId)
    {
        if (displayId is not uint id || _itemDisplays?.Find(id) is not { } row) return null;
        string model = row.ModelName1;
        string folder = "Weapon";
        if (model.Length == 0) { model = row.ModelName2; folder = "Ammo"; }
        if (model.Length == 0) return null;
        string stem = Path.GetFileNameWithoutExtension(model);
        return $@"Item\ObjectComponents\{folder}\{stem}.m2";
    }

    public string? AmmoTexturePath(uint? displayId)
    {
        if (displayId is not uint id || _itemDisplays?.Find(id) is not { } row) return null;
        string texture = row.ModelName1.Length > 0 ? row.ModelTexture1 : row.ModelTexture2;
        string folder = row.ModelName1.Length > 0 ? "Weapon" : "Ammo";
        if (texture.Length == 0) return null;
        return $@"Item\ObjectComponents\{folder}\{Path.GetFileNameWithoutExtension(texture)}.blp";
    }

    public IReadOnlyList<string> ActiveModelPaths(uint spell) => _instances
        .Where(i => i.Spell == spell && i.Asset is not null)
        .Select(i => i.Asset!.Path)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public int SpawnKit(ulong unit, uint spell, SpellVisualKitInfo kit, bool persistent,
        double now, string stage, double lifetime = 1.25)
        => SpawnKit(unit, spell, kit, persistent ? StageLife.Persistent : StageLife.SelfTerminating,
            now, stage, lifetime);

    public int SpawnKit(ulong unit, uint spell, SpellVisualKitInfo kit, StageLife life,
        double now, string stage, double? lifetime = null)
    {
        if (life != StageLife.SelfTerminating)
            _instances.RemoveAll(i => i.Unit == unit && i.Spell == spell && i.Life == life);
        int spawned = 0;
        foreach (var (attachment, path) in kit.Effects)
        {
            if (Load(path) is not { } asset || !SpellAttachment.HasVisibleContent(asset.Model)) continue;
            double span = lifetime ?? SpellAttachment.SelfTerminatingSpan(asset.Model);
            _instances.Add(new Instance
            {
                Id = ++_nextId,
                Asset = asset,
                Unit = unit,
                Spell = spell,
                Life = life,
                Attachment = attachment,
                Started = now,
                Ends = life == StageLife.SelfTerminating ? now + span : double.PositiveInfinity,
                Stage = stage,
            });
            spawned++;
        }
        return spawned;
    }

    /// <summary>Legacy fixed-endpoint entry used by diagnostics.</summary>
    public void SpawnMissile(ulong caster, uint spell, string path, Vector3 from, Vector3 to,
        double now, double duration)
    {
        _instances.Add(new Instance
        {
            Id = ++_nextId, Asset = Load(path), Unit = caster, Spell = spell,
            Life = StageLife.SelfTerminating, Started = now, Ends = now + Math.Max(.05, duration),
            Missile = true, Launched = true, Position = from, Direction = to - from,
            FixedDestination = to, Target = 0, ReleaseAt = now,
            Remaining = Math.Max(.05, duration), TravelSeconds = Math.Max(.05, duration),
            LastMotionAt = now,
            Stage = "MISSILE", DestinationAttachment = 0x22,
        });
    }

    /// <summary>
    /// Queue one projectile. Its visible launch waits for the caster model's authored release
    /// event (or the animation-end/.25s backstop). At that edge, travel time is derived from the
    /// current source/destination and reduced by time already queued since GO. The destination is
    /// then re-resolved from the target's live pose every frame.
    /// </summary>
    public void SpawnMissile(ulong caster, uint spell, string? path, ulong target,
        ushort destinationAttachment, float speed, double now, bool missed, byte missReason,
        ushort? castAnimation, Func<ulong, SpellUnitPose> unitPose,
        Action<ulong, uint, bool, byte> arrived, string? customTexture = null,
        Action? launched = null, Action? ended = null)
    {
        SpellUnitPose source = unitPose(caster);
        Vector3 from = ResolveUnitPoint(source, 0x15);
        ReleasePoint release = FindReleasePoint(source.Model, castAnimation);
        _instances.Add(new Instance
        {
            Id = ++_nextId,
            Asset = string.IsNullOrWhiteSpace(path) ? null : Load(path),
            Unit = caster,
            Spell = spell,
            Life = StageLife.SelfTerminating,
            Started = now,
            Ends = double.PositiveInfinity,
            Missile = true,
            Target = target,
            DestinationAttachment = destinationAttachment,
            Position = from,
            Speed = speed,
            ReleaseAt = now + release.Delay,
            LastMotionAt = now,
            Missed = missed,
            MissReason = missReason,
            HasReleaseMarker = release.HasMarker,
            ReleaseBone = release.Bone,
            ReleasePosition = release.Position,
            Arrived = arrived,
            LaunchEvent = launched,
            EndEvent = ended,
            CustomTexture = customTexture,
            Stage = "MISSILE",
        });
    }

    /// <summary>
    /// Spawn a kit anchored at a fixed world point — the dynamic-object area visual
    /// (Blizzard's falling snow, Rain of Fire, consecrate rings). Keyed by the dynobj
    /// guid so <see cref="Reap"/> removes it when the object despawns. Attachment is
    /// forced to the base tag (0x13) so ground-anchored mesh batches drape terrain.
    /// </summary>
    public int SpawnKitAtLocation(ulong key, uint spell, SpellVisualKitInfo kit,
        Vector3 position, double now, string stage)
    {
        _instances.RemoveAll(i => i.Unit == key && i.Spell == spell &&
            i.Life != StageLife.SelfTerminating);
        int spawned = 0;
        foreach (var (_, path) in kit.Effects)
        {
            _instances.Add(new Instance
            {
                Id = ++_nextId, Asset = Load(path), Unit = key, Spell = spell,
                Life = StageLife.Persistent, Attachment = 0x13, Started = now,
                Ends = double.PositiveInfinity, Stage = stage, Area = true,
                Position = position,
            });
            spawned++;
        }
        return spawned;
    }

    public readonly record struct VisualInstance(long Id, string Stage, string Path, Vector3 Position,
        bool Missile, float Progress, int Emitters, bool MeshValid, int Ribbons);

    public IReadOnlyList<VisualInstance> Snapshot(uint spell, double now,
        Func<ulong, SpellUnitPose> unitPose)
    {
        var rows = new List<VisualInstance>();
        foreach (Instance instance in _instances.Where(i => i.Spell == spell))
        {
            if (!TryTransform(instance, unitPose, out Matrix4x4 transform)) continue;
            float progress = instance.Missile && instance.TravelSeconds > 0
                ? (float)Math.Clamp(1 - instance.Remaining / instance.TravelSeconds, 0, 1)
                : 0;
            rows.Add(new(instance.Id, instance.Stage, instance.Asset?.Path ?? "(invisible)",
                new Vector3(transform.M41, transform.M42, transform.M43), instance.Missile,
                progress, instance.Asset?.Emitters.Length ?? 0,
                instance.Asset?.Model.IsValid == true, instance.Asset?.Model.RibbonEmitters.Count ?? 0));
        }
        return rows;
    }

    /// <summary>Remove every area-anchored visual keyed to a despawned dynamic object.</summary>
    public void ReapArea(ulong key) => _instances.RemoveAll(i => i.Area && i.Unit == key);

    public void Reap(ulong unit, uint spell, StageLife? life = null)
        => _instances.RemoveAll(i => i.Unit == unit && i.Spell == spell &&
            i.Life != StageLife.SelfTerminating && (life is null || i.Life == life));

    public void Tick(double now, Func<ulong, SpellUnitPose> unitPose)
    {
        for (int i = _instances.Count - 1; i >= 0; i--)
        {
            Instance instance = _instances[i];
            if (!instance.Missile)
            {
                if (instance.Life == StageLife.SelfTerminating && now >= instance.Ends)
                    _instances.RemoveAt(i);
                continue;
            }

            if (!instance.Launched)
            {
                if (now < instance.ReleaseAt) continue;
                SpellUnitPose launchPose = unitPose(instance.Unit);
                SpellUnitPose targetPose = unitPose(instance.Target);
                if (!launchPose.Found || instance.Target != 0 && !targetPose.Found)
                {
                    _instances.RemoveAt(i);
                    instance.EndEvent?.Invoke();
                    continue;
                }
                instance.Position = instance.HasReleaseMarker
                    ? ResolveModelPoint(launchPose, instance.ReleaseBone, instance.ReleasePosition)
                    : ResolveUnitPoint(launchPose, 0x15);
                Vector3 launchDestination = instance.Target == 0
                    ? instance.FixedDestination ?? instance.Position
                    : ResolveMissileDestination(targetPose, instance.DestinationAttachment);
                double queuedSeconds = Math.Max(0, now - instance.Started);
                instance.Remaining = instance.Speed > 0
                    ? Vector3.Distance(instance.Position, launchDestination) / instance.Speed - queuedSeconds
                    : 0;
                instance.TravelSeconds = Math.Max(0, instance.Remaining);
                if (instance.Remaining <= 0)
                {
                    _instances.RemoveAt(i);
                    instance.Arrived?.Invoke(instance.Target, instance.Spell, instance.Missed,
                        instance.MissReason);
                    continue;
                }
                instance.Launched = true;
                instance.LastMotionAt = now;
                instance.LaunchEvent?.Invoke();
            }

            SpellUnitPose liveTarget = instance.Target == 0 ? default : unitPose(instance.Target);
            if (instance.Target != 0 && !liveTarget.Found)
            {
                _instances.RemoveAt(i);
                instance.EndEvent?.Invoke();
                continue;
            }
            Vector3 destination = instance.Target == 0
                ? instance.FixedDestination ?? instance.Position
                : ResolveMissileDestination(liveTarget, instance.DestinationAttachment);

            float dt = (float)Math.Clamp(now - instance.LastMotionAt, 0, .1);
            if (instance.Remaining <= dt)
            {
                // The impact handoff happens before a final visual snap, matching the reference
                // projectile integrator and avoiding a one-frame teleport on moving targets.
                _instances.RemoveAt(i);
                instance.EndEvent?.Invoke();
                instance.Arrived?.Invoke(instance.Target, instance.Spell, instance.Missed,
                    instance.MissReason);
                continue;
            }
            Vector3 gap = destination - instance.Position;
            instance.Position += gap * (float)(dt / instance.Remaining);
            if (gap.LengthSquared() > 1e-8f) instance.Direction = gap;
            instance.Remaining -= dt;
            instance.LastMotionAt = now;
        }
    }

    public IEnumerable<(string Path, Matrix4x4 Transform, M2ParticleEmitter Emitter,
        int EmitterIndex, string TexturePath, double AnimationTime, int AnimationId,
        Vector3? LocalOrigin, Quaternion? LocalRotation, bool Attached)> EmitterInstances(double now,
        Func<ulong, SpellUnitPose> unitPose)
    {
        foreach (Instance instance in _instances)
        {
            if (instance.Asset is not { } asset || !instance.Launched && instance.Missile) continue;
            if (!TryTransform(instance, unitPose, out Matrix4x4 transform)) continue;
            double age = Math.Max(0, now - (instance.Missile ? Math.Max(instance.Started, instance.ReleaseAt) : instance.Started));
            int animationId = instance.Missile ? 144 : asset.Model.Sequences.FirstOrDefault()?.AnimationId ?? 0;
            Matrix4x4[]? effectSkin = null;
            if (asset.Animator is { } animator)
            {
                effectSkin = asset.Skin;
                M2Animator.Clip? clip = animator.Find(animationId) ?? animator.Clips.Values.FirstOrDefault();
                animator.Evaluate(clip, (float)age, (float)age, effectSkin);
            }
            for (int i = 0; i < asset.Emitters.Length; i++)
            {
                M2ParticleEmitter emitter = asset.Emitters[i];
                Vector3? origin = null;
                Quaternion? rotation = null;
                if (effectSkin is not null && emitter.Bone < effectSkin.Length &&
                    emitter.Bone < asset.Model.Bones.Count)
                {
                    origin = Vector3.Transform(new Vector3(emitter.PosX, emitter.PosY, emitter.PosZ),
                        effectSkin[emitter.Bone]);
                    Matrix4x4 global = Matrix4x4.CreateTranslation(asset.Model.Bones[emitter.Bone].Pivot) *
                        effectSkin[emitter.Bone];
                    if (Matrix4x4.Decompose(global, out _, out Quaternion q, out _)) rotation = q;
                }
                yield return ($"spell:{asset.Path}#{instance.Id}", transform,
                    emitter, i, asset.EmitterTextures[i], age, animationId, origin, rotation,
                    !instance.Missile);
            }
        }
    }

    public IEnumerable<(long Id, string Path, M2Model Model, Matrix4x4 Transform,
        float Age, int AnimationId, bool GroundAnchor, string? CustomTexture)> MeshInstances(double now, Func<ulong, SpellUnitPose> unitPose)
    {
        foreach (Instance instance in _instances)
        {
            if (instance.Asset is not { } asset || !asset.Model.IsValid ||
                !instance.Launched && instance.Missile) continue;
            if (!TryTransform(instance, unitPose, out Matrix4x4 transform)) continue;
            yield return (instance.Id, asset.Path, asset.Model, transform,
                (float)Math.Max(0, now - (instance.Missile ? Math.Max(instance.Started, instance.ReleaseAt) : instance.Started)),
                instance.Missile ? 144 : asset.Model.Sequences.FirstOrDefault()?.AnimationId ?? 0,
                !instance.Missile && instance.Attachment == 0x13, instance.CustomTexture);
        }
    }

    public IEnumerable<(long Id, string Path, M2Model Model, Matrix4x4 Transform,
        float Age, int AnimationId)> RibbonInstances(double now, Func<ulong, SpellUnitPose> unitPose)
    {
        foreach (Instance instance in _instances)
        {
            if (instance.Asset is not { } asset || asset.Model.RibbonEmitters.Count == 0 ||
                !instance.Launched && instance.Missile) continue;
            if (!TryTransform(instance, unitPose, out Matrix4x4 transform)) continue;
            yield return (instance.Id, asset.Path, asset.Model, transform,
                (float)Math.Max(0, now - (instance.Missile ? Math.Max(instance.Started, instance.ReleaseAt) : instance.Started)),
                instance.Missile ? 144 : asset.Model.Sequences.FirstOrDefault()?.AnimationId ?? 0);
        }
    }

    private static bool TryTransform(Instance instance, Func<ulong, SpellUnitPose> unitPose,
        out Matrix4x4 transform)
    {
        if (instance.Area)
        {
            transform = Matrix4x4.CreateTranslation(instance.Position);
            return true;
        }
        if (instance.Missile)
        {
            if (!instance.Launched) { transform = default; return false; }
            Vector3 forward = instance.Direction.LengthSquared() > 1e-8f
                ? Vector3.Normalize(instance.Direction) : Vector3.UnitX;
            Vector3 referenceUp = MathF.Abs(Vector3.Dot(forward, Vector3.UnitZ)) > .98f
                ? Vector3.UnitY : Vector3.UnitZ;
            Vector3 side = Vector3.Normalize(Vector3.Cross(referenceUp, forward));
            Vector3 up = Vector3.Normalize(Vector3.Cross(forward, side));
            transform = new Matrix4x4(
                forward.X, forward.Y, forward.Z, 0,
                side.X, side.Y, side.Z, 0,
                up.X, up.Y, up.Z, 0,
                instance.Position.X, instance.Position.Y, instance.Position.Z, 1);
            return true;
        }

        SpellUnitPose pose = unitPose(instance.Unit);
        if (!pose.Found) { transform = default; return false; }
        if (pose.Model is not null && SpellAttachment.Resolve(pose.Model, instance.Attachment) is { } point)
        {
            transform = SpellAttachment.World(pose.Model, point, pose.UnitTransform, pose.BoneMatrix);
            // Some creature models resolve a kit attach (e.g. the impact chest tag 0x22) to a bone
            // that sits far from the body — a mis-scaled prop joint that throws the effect dozens of
            // yards off the target (same failure the missile destination hit). Clamp an implausible
            // result to the unit's chest so an impact can't fly off; mirrors ResolveMissileDestination.
            var at = new Vector3(transform.M41, transform.M42, transform.M43);
            if (Vector3.Distance(at, pose.Position) > PlausibleAttachRadius)
            {
                transform.M41 = pose.Position.X + ChestOffset.X;
                transform.M42 = pose.Position.Y + ChestOffset.Y;
                transform.M43 = pose.Position.Z + ChestOffset.Z;
            }
        }
        else transform = pose.UnitTransform;
        return true;
    }

    /// <summary>A real body/head/hand attach sits within a couple of yards of the unit even on
    /// large creatures; anything past this is a mis-scaled prop joint, not a homing/impact target.</summary>
    private const float PlausibleAttachRadius = 12f;

    /// <summary>Approximate chest-height offset above a unit's base (world Z-up), used as the
    /// body-centre fallback when an attach resolves implausibly far.</summary>
    private static readonly Vector3 ChestOffset = new(0f, 0f, 1.5f);

    private static Vector3 ResolveUnitPoint(in SpellUnitPose pose, ushort attachment)
    {
        if (!pose.Found) return pose.Position;
        Matrix4x4 transform = pose.UnitTransform;
        if (pose.Model is not null && SpellAttachment.Resolve(pose.Model, attachment) is { } point)
            transform = SpellAttachment.World(pose.Model, point, pose.UnitTransform, pose.BoneMatrix);
        return new Vector3(transform.M41, transform.M42, transform.M43);
    }

    /// <summary>
    /// The world point a missile homes to on its target. Benilla resolves the DBC dest-attach
    /// (SpellVisual field 9) to a body point; but some vanilla NPC models map that same tag
    /// (0x22 for Fireball) to a bone that sits far from the body in model space and is amplified
    /// by the model's render scale — resolving 60+ yards off, which sends the projectile arcing
    /// into the sky. When the attach resolves implausibly far from the unit's base, fall back to
    /// the target's body centre (the reference's practical body-homing target — see
    /// benilla missile.rs, "homing aims at the dest attach point ... same body point in practice").
    /// </summary>
    private static Vector3 ResolveMissileDestination(in SpellUnitPose pose, ushort attachment)
    {
        if (!pose.Found) return pose.Position;
        Vector3 attach = ResolveUnitPoint(pose, attachment);
        return Vector3.Distance(attach, pose.Position) > PlausibleAttachRadius
            ? pose.Position + ChestOffset   // chest-height body centre
            : attach;
    }

    private readonly record struct ReleasePoint(double Delay, bool HasMarker,
        ushort Bone, Vector3 Position);

    private static ReleasePoint FindReleasePoint(M2Model? model, ushort? animationId)
    {
        if (model is null || animationId is null) return default;
        int sequenceIndex = model.TryFindSequenceIndexByAnimationId(animationId.Value);
        if (sequenceIndex < 0) return default;
        M2Sequence sequence = model.Sequences[sequenceIndex];
        string[] release = ["$CSL", "$CSR", "$CST", "$BWR"];
        var found = model.Events.SelectMany(e => e.Times
                .Where(t => t >= sequence.StartTimestamp && t <= sequence.EndTimestamp)
                .Select(t => (Event: e, Time: t)))
            .Where(x => release.Contains(x.Event.Identifier, StringComparer.Ordinal))
            .OrderBy(x => x.Time).FirstOrDefault();
        if (found.Event is not null)
            return new ReleasePoint((found.Time - sequence.StartTimestamp) / 1000.0,
                true, found.Event.Bone, found.Event.Position);
        double duration = (sequence.EndTimestamp - sequence.StartTimestamp) / 1000.0;
        // With no authored marker, the cast one-shot's completion is the
        // release. A degenerate/missing playback gets the client's .25s poll
        // backstop instead of waiting forever.
        return new ReleasePoint(duration > .001 ? duration : .25, false, 0, default);
    }

    private static Vector3 ResolveModelPoint(in SpellUnitPose pose, ushort bone, Vector3 local)
    {
        if (!pose.Found) return pose.Position;
        // The event-marker Position is MODEL SPACE (same convention as M2Attachment.Position), so
        // it rides the bone's RAW skinning matrix — subtract the pivot before the posed frame
        // (T(pivot)·Skin) exactly like SpellAttachment.World, or the launch lands ~a pivot off the
        // hand (the release marker sat ~1yd in front of the caster's hand before this).
        if (pose.BoneMatrix(bone) is not { } model) return pose.Position;
        Vector3 pivot = pose.Model is { } m && bone < m.Bones.Count ? m.Bones[bone].Pivot : Vector3.Zero;
        Matrix4x4 world = Matrix4x4.CreateTranslation(local - pivot) * model * pose.UnitTransform;
        return new Vector3(world.M41, world.M42, world.M43);
    }

    private Asset? Load(string rawPath)
    {
        string path = SpellVisualCatalog.ModelPath(rawPath);
        if (_assets.TryGetValue(path, out Asset? cached)) return cached;
        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        if (_assetFailures.TryGetValue(path, out double failedAt) && now - failedAt < 10.0)
            return null;
        long t0 = Stopwatch.GetTimestamp();
        byte[]? bytes = _mpq.ReadFile(path);
        long t1 = Stopwatch.GetTimestamp();
        M2Model? model = bytes is null ? null : M2Reader.Parse(bytes);
        long t2 = Stopwatch.GetTimestamp();
        if (model is null) { _assetFailures[path] = now; return null; }
        var asset = new Asset { Path = path, Model = model, Emitters = model.ParticleEmitters.ToArray(),
            Animator = M2Animator.Build(model, model.Sequences.Select(s => (int)s.AnimationId),
                includeStaticSequences: true) };
        long t3 = Stopwatch.GetTimestamp();
        double ToMs(long a, long b) => (b - a) * 1000.0 / Stopwatch.Frequency;
        if (ToMs(t0, t3) > 2)
            Console.WriteLine($"[fx-load] asset {Path.GetFileName(path)} " +
                $"read={ToMs(t0, t1):0.0}ms parse={ToMs(t1, t2):0.0}ms animator={ToMs(t2, t3):0.0}ms");
        if (asset.Animator is { } animator)
            asset.Skin = new Matrix4x4[animator.BoneCount];
        asset.EmitterTextures = new string[asset.Emitters.Length];
        for (int i = 0; i < asset.Emitters.Length; i++)
        {
            int texture = asset.Emitters[i].Texture;
            asset.EmitterTextures[i] = texture >= 0 && texture < model.Textures.Count
                ? model.Textures[texture].Filename : "";
        }
        _assetFailures.Remove(path);
        _assets[path] = asset;
        return asset;
    }
}
