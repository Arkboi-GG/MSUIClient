using System.Numerics;
using MSUIClient;
using MSUIClient.Formats;
using MSUIClient.World.Units;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: spell-area-visual-check <client-config.json>");
    return 2;
}

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}

ClientConfig config = ClientConfig.Load(args[0]);
using var mpq = new MpqMount(config.ClientDataPath);
DbcFile kits = ReadDbc(@"DBFilesClient\SpellVisualKit.dbc");
DbcFile visuals = ReadDbc(@"DBFilesClient\SpellVisual.dbc");
DbcFile spells = ReadDbc(@"DBFilesClient\Spell.dbc");
SpellVisualCatalog catalog = SpellVisualCatalog.Load(mpq)
    ?? throw new InvalidOperationException("SpellVisual catalog unavailable");
SoundEntriesCatalog sounds = SoundEntriesCatalog.Load(mpq)
    ?? throw new InvalidOperationException("SoundEntries catalog unavailable");

Check((kits.RecordCount, kits.FieldCount, kits.RecordSize) == (1772, 35, 140),
    "SpellVisualKit is not the build-5875 1772x35x140 table");
Check((visuals.RecordCount, visuals.FieldCount, visuals.RecordSize) == (2165, 16, 64),
    "SpellVisual is not the build-5875 2165x16x64 table");

var expected = new Dictionary<uint, (int Selector, float Rate)>
{
    [609] = (0, 5f), [692] = (1, 5f), [700] = (2, .7f), [845] = (2, .7f),
    [2389] = (4, 5f), [3229] = (5, 5f), [4229] = (6, 3f), [6591] = (0, 15f),
};
var actual = new Dictionary<uint, SpellAreaEmitterInfo>();
for (int row = 0; row < kits.RecordCount; row++)
    for (int lane = 0; lane < 4; lane++)
    {
        int type = kits.GetInt(row, 15 + lane);
        if (type != 9) continue;
        var proc = new SpellVisualCharProc(type,
            Enumerable.Range(0, 4).Select(p => kits.GetFloat(row, 19 + p * 4 + lane)).ToArray());
        Check(SpellAreaVisualLaw.TryResolveEmitter(proc, out SpellAreaEmitterInfo emitter),
            $"type-9 kit {kits.GetUInt(row, 0)} has an invalid selector/rate");
        Check(proc.Parameters[2] == 0f && proc.Parameters[3] == 0f,
            $"type-9 kit {kits.GetUInt(row, 0)} gained an unimplemented parameter");
        Check(actual.TryAdd(kits.GetUInt(row, 0), emitter),
            $"type-9 kit {kits.GetUInt(row, 0)} has multiple lanes; extend the pinned census");
    }
Check(actual.Count == expected.Count && actual.Keys.Order().SequenceEqual(expected.Keys.Order()),
    "the build-5875 type-9 kit set changed");
foreach ((uint kit, (int selector, float rate)) in expected)
{
    SpellAreaEmitterInfo emitter = actual[kit];
    Check(emitter.Selector == selector && emitter.InstancesPerSecond == rate,
        $"type-9 kit {kit} differs from selector={selector}, rate={rate:R}");
    Console.WriteLine($"[area-law] kit={kit} selector={selector} rate={rate:R}/s model={emitter.ModelPath}");
}

Check(!SpellAreaVisualLaw.TryResolveEmitter(new(9, [-1f, 1f, 0f, 0f]), out _),
    "negative selector was accepted");
Check(!SpellAreaVisualLaw.TryResolveEmitter(new(9, [7f, 1f, 0f, 0f]), out _),
    "out-of-range selector was clamped instead of rejected");
Check(!SpellAreaVisualLaw.TryResolveEmitter(new(9, [.5f, 1f, 0f, 0f]), out _),
    "fractional selector was accepted");
Check(!SpellAreaVisualLaw.TryResolveEmitter(new(9, [float.NaN, 1f, 0f, 0f]), out _),
    "NaN selector was accepted");

foreach (string path in SpellAreaVisualLaw.ClientShardModels)
{
    byte[]? bytes = mpq.ReadFile(path);
    M2Model? model = bytes is null ? null : M2Reader.Parse(bytes);
    Check(model is not null && SpellAttachment.HasVisibleContent(model),
        $"client shard-table asset is missing or has no visible content: {path}");
    Check(SpellAttachment.SelfTerminatingSpan(model!) > 0,
        $"client shard-table asset has no usable lifetime: {path}");
    Console.WriteLine($"[area-asset] {path} bytes={bytes!.Length} " +
        $"sequences={model!.Sequences.Count} span={SpellAttachment.SelfTerminatingSpan(model):0.###}s " +
        $"mesh={model.Submeshes.Count} emitters={model.ParticleEmitters.Count}");
}

var knownVisuals = new Dictionary<uint, (int Selector, float Rate)>
{
    [259] = (0, 5f), [329] = (1, 5f), [125] = (2, .7f), [986] = (2, .7f),
    [2255] = (4, 5f), [3300] = (5, 5f), [5780] = (6, 3f), [7608] = (0, 15f),
};
var checkedSounds = new HashSet<uint>();
foreach ((uint visual, (int selector, float rate)) in knownVisuals)
{
    Check(catalog.TryGetAreaVisual(visual, out SpellAreaVisualInfo area),
        $"area visual {visual} did not resolve");
    Check(area.LoopingModelPath is { Length: > 0 } && area.Emitters.Count == 1,
        $"area visual {visual} did not resolve one loop plus one emitter");
    Check(area.Emitters[0].Selector == selector && area.Emitters[0].InstancesPerSecond == rate,
        $"area visual {visual} resolved the wrong type-9 law");
    if (area.Sound is uint sound && checkedSounds.Add(sound))
    {
        Check(sounds.TryGet(sound, out SoundEntry entry) && entry.Variants.Count != 0,
            $"area visual {visual} references missing/empty sound {sound}");
        Check(entry.Variants.All(v => mpq.ReadFile(v.Path) is { Length: > 0 }),
            $"area sound {sound} has a missing authored variant");
        Console.WriteLine($"[area-sound] id={sound} name={entry.Name} " +
            $"variants={entry.Variants.Count} authored-loop={entry.Looping}");
    }
}
bool gatedResolved = catalog.TryGetAreaVisual(1, out SpellAreaVisualInfo gated);
Check(!gatedResolved || gated.LoopingModelPath is null,
    "SpellVisual area gate 0 did not suppress its field-12 model");

Check(catalog.TryGetAreaVisual(259, out SpellAreaVisualInfo blizzard),
    "Blizzard area visual unavailable for lifecycle check");
var source = new SpellEffectSource(mpq);
Vector3 original = new(10f, 20f, 3f), moved = new(100f, 200f, 7f);
const ulong dynamicObject = 0xF130_0000_1234UL;
var birthSounds = new List<(uint Sound, ulong Key, Vector3 Position)>();
Check(source.SpawnAreaVisual(dynamicObject, 10, blizzard, original, 8f, 5000,
        (sound, key, position) => birthSounds.Add((sound, key, position))) == 2,
    "Blizzard did not arm one loop and one type-9 emitter");
source.Tick(5000.21, _ => default);
var born = source.Snapshot(10, 5000.21, _ => default);
Check(born.Count(x => x.Stage == "AREA_LOOP") == 1 &&
      born.Count(x => x.Stage == "AREA_SHARD") == 1,
    "5/s emitter did not birth exactly one shard after 0.21 seconds");
Vector3 firstShard = born.Single(x => x.Stage == "AREA_SHARD").Position;
Check(Vector2.Distance(new(firstShard.X, firstShard.Y), new(original.X, original.Y)) <= 8f,
    "born shard escaped the original DynamicObject radius");
Check(birthSounds is [{ Sound: 7, Key: dynamicObject }] && birthSounds[0].Position == firstShard,
    "non-looping Blizzard impact bank did not fire at the shard birth position");

source.UpdateAreaVisual(dynamicObject, 10, moved, 2f);
var updated = source.Snapshot(10, 5000.21, _ => default);
Check(updated.Single(x => x.Stage == "AREA_LOOP").Position == moved,
    "live DynamicObject movement did not move the persistent loop");
Check(updated.Single(x => x.Stage == "AREA_SHARD").Position == firstShard,
    "live DynamicObject movement incorrectly dragged an already-born shard");
source.Tick(5000.41, _ => default);
var afterMove = source.Snapshot(10, 5000.41, _ => default);
Vector3 movedShard = afterMove.Where(x => x.Stage == "AREA_SHARD")
    .Select(x => x.Position).Single(p => p != firstShard);
Check(Vector2.Distance(new(movedShard.X, movedShard.Y), new(moved.X, moved.Y)) <= 2f,
    "new shard did not use the updated DynamicObject position/radius");
Check(birthSounds.Count == 2 && birthSounds[1].Position == movedShard,
    "updated type-9 birth did not carry its one-shot sound position");

source.ReapArea(dynamicObject);
int draining = source.Snapshot(10, 5000.41, _ => default).Count;
Check(draining == 2, "despawn did not remove the loop while preserving shard tails");
source.Tick(5000.61, _ => default);
Check(source.Snapshot(10, 5000.61, _ => default).Count == draining,
    "despawned DynamicObject continued birthing shards");
Check(birthSounds.Count == 2, "despawned DynamicObject continued birthing sounds");
source.Tick(5004, _ => default);
Check(source.Snapshot(10, 5004, _ => default).Count == 0,
    "self-terminating shard tails did not drain on their model clocks");
Console.WriteLine("[area-lifecycle] live-update, birth frame, despawn, and tail drain PASS");

int persistentSpells = 0;
var persistentVisuals = new HashSet<uint>();
var authoredPersistentVisuals = new HashSet<uint>();
for (int row = 0; row < spells.RecordCount; row++)
{
    if (!Enumerable.Range(0, 3).Any(i => spells.GetUInt(row, 61 + i) == 27)) continue;
    persistentSpells++;
    uint visual = spells.GetUInt(row, 115);
    persistentVisuals.Add(visual);
    if (catalog.TryGetAreaVisual(visual, out _)) authoredPersistentVisuals.Add(visual);
}
Check(persistentSpells > 100 && persistentVisuals.Count == 30,
    "persistent-area spell census differs from the mounted build");
Console.WriteLine($"[area-census] persistent-spells={persistentSpells} " +
    $"visuals={persistentVisuals.Count} authored-visuals={authoredPersistentVisuals.Count} type9-kits={actual.Count}");

ulong state = 0x1234_5678_9ABC_DEF0UL;
const float radius = 12f;
double sumRadiusSquared = 0;
int[] quadrants = new int[4];
for (int i = 0; i < 100_000; i++)
{
    Vector2 p = SpellAreaVisualLaw.NextDiscOffset(ref state, radius);
    Check(p.LengthSquared() <= radius * radius * 1.00001f, "disc sample escaped wire radius");
    sumRadiusSquared += p.LengthSquared();
    quadrants[(p.X < 0 ? 1 : 0) | (p.Y < 0 ? 2 : 0)]++;
}
double normalizedMeanRadiusSquared = sumRadiusSquared / 100_000 / (radius * radius);
Check(Math.Abs(normalizedMeanRadiusSquared - .5) < .005,
    "disc samples are not uniform by area");
Check(quadrants.All(q => Math.Abs(q - 25_000) < 500), "disc samples are quadrant-biased");
Console.WriteLine($"[area-distribution] mean-r2={normalizedMeanRadiusSquared:0.0000} " +
    $"quadrants={string.Join('/', quadrants)}");
Console.WriteLine($"[area-law] PASS checks={checks}");
return 0;

DbcFile ReadDbc(string path)
    => mpq.ReadFile(path) is { } bytes && DbcFile.Parse(bytes) is { } dbc
        ? dbc : throw new InvalidOperationException($"Missing or invalid {path}");
