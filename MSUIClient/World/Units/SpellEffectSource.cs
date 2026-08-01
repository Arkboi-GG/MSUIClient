using System.Numerics;
using MSUIClient.Formats;

namespace MSUIClient.World.Units;

/// <summary>
/// Dynamic instances of the real SpellVisualEffectName M2 assets. Their M2
/// emitters feed the shared particle renderer, so spell FX use the same BLP,
/// blend, lifespan, gravity and sprite-sheet laws as world effects.
/// </summary>
public sealed class SpellEffectSource
{
    private static readonly Matrix4x4 Basis = new(
        0f, -1f, 0f, 0f, 0f, 0f, 1f, 0f,
        -1f, 0f, 0f, 0f, 0f, 0f, 0f, 1f);

    private sealed class Asset
    {
        public string Path = "";
        public M2Model Model = null!;
        public M2ParticleEmitter[] Emitters = [];
        public string[] Textures = [];
    }
    private sealed class Instance
    {
        public long Id;
        public Asset Asset = null!;
        public ulong Unit;
        public uint Spell;
        public bool Persistent;
        public ushort Attachment;
        public double Started;
        public double Ends;
        public Vector3 From;
        public Vector3 To;
        public bool Missile;
    }

    private readonly MpqMount _mpq;
    private readonly Dictionary<string, Asset?> _assets = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Instance> _instances = [];
    private long _nextId;

    public SpellEffectSource(MpqMount mpq) => _mpq = mpq;
    public int ActiveCount => _instances.Count;
    public IReadOnlyList<string> ActiveModelPaths(uint spell) => _instances
        .Where(instance => instance.Spell == spell)
        .Select(instance => instance.Asset.Path)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public void SpawnKit(ulong unit, uint spell, SpellVisualKitInfo kit, bool persistent,
        double now, double lifetime = 1.25)
    {
        foreach (var (attachment, path) in kit.Effects)
            if (Load(path) is { } asset)
                _instances.Add(new Instance
                {
                    Id = ++_nextId, Asset = asset, Unit = unit, Spell = spell,
                    Persistent = persistent, Attachment = attachment,
                    Started = now, Ends = persistent ? double.PositiveInfinity : now + lifetime,
                });
    }

    public void SpawnMissile(ulong caster, uint spell, string path, Vector3 from, Vector3 to,
        double now, double duration)
    {
        if (Load(path) is not { } asset) return;
        _instances.Add(new Instance
        {
            Id = ++_nextId, Asset = asset, Unit = caster, Spell = spell,
            Started = now, Ends = now + Math.Max(.05, duration), From = from, To = to, Missile = true,
        });
    }

    public void Reap(ulong unit, uint spell)
        => _instances.RemoveAll(i => i.Persistent && i.Unit == unit && i.Spell == spell);

    public void Tick(double now) => _instances.RemoveAll(i => !i.Persistent && now >= i.Ends);

    public IEnumerable<(string Path, Matrix4x4 Transform, M2ParticleEmitter Emitter,
        int EmitterIndex, string TexturePath)> EmitterInstances(double now,
        Func<ulong, (bool Found, Vector3 Position, float Yaw)> unitPose)
    {
        foreach (Instance instance in _instances)
        {
            Vector3 position; float yaw;
            if (instance.Missile)
            {
                float t = (float)Math.Clamp((now - instance.Started) /
                    Math.Max(.001, instance.Ends - instance.Started), 0, 1);
                position = Vector3.Lerp(instance.From, instance.To, t);
                Vector3 d = instance.To - instance.From;
                yaw = d.LengthSquared() > 1e-6f ? MathF.Atan2(d.Y, d.X) : 0f;
            }
            else
            {
                var pose = unitPose(instance.Unit);
                if (!pose.Found) continue;
                position = pose.Position + AttachmentOffset(instance.Attachment, pose.Yaw);
                yaw = pose.Yaw;
            }

            Matrix4x4 transform = Matrix4x4.CreateRotationY(yaw + MathF.PI * .5f)
                * Basis * Matrix4x4.CreateTranslation(position);
            for (int i = 0; i < instance.Asset.Emitters.Length; i++)
                yield return ($"spell:{instance.Asset.Path}#{instance.Id}", transform,
                    instance.Asset.Emitters[i], i, instance.Asset.Textures[i]);
        }
    }

    /// <summary>
    /// The same live visual instances as <see cref="EmitterInstances"/>, but
    /// carrying the effect M2 itself. Spell effects frequently combine mesh
    /// passes with particles; exposing both prevents a fireball whose glowing
    /// shell exists in the archive from being reduced to only its sparks.
    /// </summary>
    public IEnumerable<(string Path, M2Model Model, Matrix4x4 Transform, float Age)> MeshInstances(
        double now, Func<ulong, (bool Found, Vector3 Position, float Yaw)> unitPose)
    {
        foreach (Instance instance in _instances)
        {
            if (!instance.Asset.Model.IsValid) continue;
            if (!TryTransform(instance, now, unitPose, out Matrix4x4 transform)) continue;
            yield return (instance.Asset.Path, instance.Asset.Model, transform,
                (float)Math.Max(0, now - instance.Started));
        }
    }

    private static bool TryTransform(Instance instance, double now,
        Func<ulong, (bool Found, Vector3 Position, float Yaw)> unitPose,
        out Matrix4x4 transform)
    {
        Vector3 position; float yaw;
        if (instance.Missile)
        {
            float t = (float)Math.Clamp((now - instance.Started) /
                Math.Max(.001, instance.Ends - instance.Started), 0, 1);
            position = Vector3.Lerp(instance.From, instance.To, t);
            Vector3 d = instance.To - instance.From;
            yaw = d.LengthSquared() > 1e-6f ? MathF.Atan2(d.Y, d.X) : 0f;
        }
        else
        {
            var pose = unitPose(instance.Unit);
            if (!pose.Found) { transform = default; return false; }
            position = pose.Position + AttachmentOffset(instance.Attachment, pose.Yaw);
            yaw = pose.Yaw;
        }
        transform = Matrix4x4.CreateRotationY(yaw + MathF.PI * .5f)
            * Basis * Matrix4x4.CreateTranslation(position);
        return true;
    }

    private Asset? Load(string rawPath)
    {
        string path = rawPath.Replace('/', '\\');
        if (path.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            path = path[..^4] + ".m2";
        if (_assets.TryGetValue(path, out Asset? cached)) return cached;
        byte[]? bytes = _mpq.ReadFile(path);
        M2Model? model = bytes is null ? null : M2Reader.Parse(bytes);
        if (model is null) { _assets[path] = null; return null; }
        var asset = new Asset { Path = path, Model = model, Emitters = model.ParticleEmitters.ToArray() };
        asset.Textures = new string[asset.Emitters.Length];
        for (int i = 0; i < asset.Emitters.Length; i++)
        {
            int texture = asset.Emitters[i].Texture;
            asset.Textures[i] = texture >= 0 && texture < model.Textures.Count
                ? model.Textures[texture].Filename : "";
        }
        _assets[path] = asset;
        return asset;
    }

    private static Vector3 AttachmentOffset(ushort tag, float yaw)
    {
        Vector3 local = tag switch
        {
            0x14 => new(0, 0, 1.85f), // head
            0x22 => new(0, 0, 1.2f),  // chest
            0x13 => Vector3.Zero,      // base
            0x15 => new(.28f, .18f, 1.05f),
            0x16 => new(.28f, -.18f, 1.05f),
            0x11 => new(.35f, 0, 1.65f),
            _ => new(0, 0, 1.1f),
        };
        float c = MathF.Cos(yaw), s = MathF.Sin(yaw);
        return new Vector3(local.X * c - local.Y * s, local.X * s + local.Y * c, local.Z);
    }
}
