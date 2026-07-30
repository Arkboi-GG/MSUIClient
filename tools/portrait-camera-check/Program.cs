using System.Buffers.Binary;
using System.Numerics;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.Formats.Mpq;

string data = args.Length > 0 ? args[0] : Path.GetFullPath(Path.Combine("GameData", "Data"));
data = Path.GetFullPath(data);
bool provenanceOnly = args.Length > 1 &&
    args[1].Equals("--provenance", StringComparison.OrdinalIgnoreCase);
string[] models = args.Length > 1 ? args.Skip(1).ToArray() :
[
    @"Character\Dwarf\Male\DwarfMale.m2",
    @"Character\Human\Male\HumanMale.m2",
    @"Creature\Wolf\Wolf.m2",
];

CheckDefaultTuningIdentity();
CheckArchiveOrdering();
if (provenanceOnly)
{
    string[] requestedPaths = args.Skip(2).ToArray();
    if (requestedPaths.Length == 0)
        throw new ArgumentException("--provenance requires at least one internal MPQ path");
    string[] beforeChain = LegacyDiagnosticLoadOrder(data).ToArray();
    string[] afterChain = DiagnosticLoadOrder(data).ToArray();
    foreach (string requestedPath in requestedPaths)
    {
        string before = TryReadWithProvenance(beforeChain, requestedPath)?.Archive ?? "not found";
        string after = TryReadWithProvenance(afterChain, requestedPath)?.Archive ?? "not found";
        Console.WriteLine($"[camera-check] provenance path={requestedPath} " +
            $"before={Path.GetFileName(before)} after={Path.GetFileName(after)}");
    }
    return;
}
using var mpq = new MpqMount(data);
if (mpq.ArchiveCount == 0) throw new InvalidOperationException($"No MPQs mounted from {data}");
CheckCreatureSpecimenEnumeration(mpq);
string[] diagnosticChain = DiagnosticLoadOrder(data).ToArray();
Console.WriteLine($"[camera-check] data={data}");
Console.WriteLine($"[camera-check] archive-chain={string.Join(" > ", diagnosticChain.Select(Path.GetFileName))}");
bool failed = false;

foreach (string path in models)
{
    byte[] bytes = mpq.ReadFile(path) ?? throw new FileNotFoundException(path);
    (string sourceArchive, byte[] sourceBytes) = ReadWithProvenance(diagnosticChain, path);
    Console.WriteLine($"[camera-check] model={path} archive={Path.GetFileName(sourceArchive)} " +
        $"bytes={bytes.Length} sharedBytesMatch={bytes.AsSpan().SequenceEqual(sourceBytes)}");
    PrintCameraHeader(bytes);
    M2Model model = M2Reader.Parse(bytes) ?? throw new InvalidDataException($"Could not parse {path}");
    Console.WriteLine($"[camera-check] parsed version={model.Version} portraitCamera={(model.PortraitCamera is null ? "null" : "present")}");
    if (model.PortraitCamera is not { } camera)
    {
        Console.WriteLine($"[camera-check] FAIL no parsed portrait camera: {path}");
        failed = true;
        continue;
    }
    var view = new Camera
    {
        AuthoredPosition = camera.Position,
        AuthoredTarget = camera.Target,
        AuthoredUp = Vector3.UnitY,
        AuthoredVerticalFieldOfViewRadians = camera.FieldOfView * 0.6f,
        AspectRatio = 1f,
        NearPlane = camera.NearClip,
        FarPlane = camera.FarClip,
    };

    int inFront = 0, inside = 0;
    Vector2 ndcMin = new(float.PositiveInfinity), ndcMax = new(float.NegativeInfinity);
    foreach (M2Vertex vertex in model.Vertices)
    {
        Vector3 relative = new Vector3(vertex.PosX, vertex.PosY, vertex.PosZ) - view.Position;
        Vector4 clip = Vector4.Transform(new Vector4(relative, 1f), view.RelativeViewProjection);
        if (clip.W <= 0f || !float.IsFinite(clip.W)) continue;
        inFront++;
        Vector3 ndc = new(clip.X / clip.W, clip.Y / clip.W, clip.Z / clip.W);
        ndcMin = Vector2.Min(ndcMin, new Vector2(ndc.X, ndc.Y));
        ndcMax = Vector2.Max(ndcMax, new Vector2(ndc.X, ndc.Y));
        if (MathF.Abs(ndc.X) <= 1f && MathF.Abs(ndc.Y) <= 1f && ndc.Z is >= 0f and <= 1f) inside++;
    }

    Console.WriteLine($"{path}: vertices={model.Vertices.Count}, inFront={inFront}, inside={inside}");
    Console.WriteLine($"  eye={camera.Position}, target={camera.Target}, fov={camera.FieldOfView:F6}, " +
        $"clip={camera.NearClip:F4}..{camera.FarClip:F1}, ndc={ndcMin}..{ndcMax}");
    if (inside == 0) throw new InvalidDataException($"Authored camera clips every vertex for {path}");

    // Exercise MSUI's renderer-facing basis path too. This catches a camera/model transform that
    // looks valid in local space but ceases to cancel once Camera.RelativeView is involved.
    Matrix4x4 basis = new(
         0f, -1f, 0f, 0f,
         0f,  0f, 1f, 0f,
        -1f,  0f, 0f, 0f,
         0f,  0f, 0f, 1f);
    Vector3 eyeWorld = Vector3.Transform(camera.Position, basis);
    Vector3 targetWorld = Vector3.Transform(camera.Target, basis);
    Vector3 upWorld = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, basis));
    var transformedView = new Camera
    {
        AuthoredPosition = eyeWorld,
        AuthoredTarget = targetWorld,
        AuthoredUp = upWorld,
        AuthoredVerticalFieldOfViewRadians = camera.FieldOfView * 0.6f,
        AspectRatio = 1f,
        NearPlane = camera.NearClip,
        FarPlane = camera.FarClip,
    };
    int transformedInside = 0;
    foreach (M2Vertex vertex in model.Vertices)
    {
        Matrix4x4 relativeModel = basis;
        relativeModel.M41 -= transformedView.Position.X;
        relativeModel.M42 -= transformedView.Position.Y;
        relativeModel.M43 -= transformedView.Position.Z;
        Vector4 clip = Vector4.Transform(new Vector4(vertex.PosX, vertex.PosY, vertex.PosZ, 1f),
            relativeModel * transformedView.RelativeViewProjection);
        if (clip.W <= 0f) continue;
        Vector3 ndc = new(clip.X / clip.W, clip.Y / clip.W, clip.Z / clip.W);
        if (MathF.Abs(ndc.X) <= 1f && MathF.Abs(ndc.Y) <= 1f && ndc.Z is >= 0f and <= 1f)
            transformedInside++;
    }
    Console.WriteLine($"  transformed renderer path inside={transformedInside}");
    if (transformedInside != inside)
        throw new InvalidDataException($"Renderer basis changed portrait coverage for {path}: {inside} -> {transformedInside}");
}

if (failed) throw new InvalidDataException("One or more models had no parsed portrait camera; see diagnostics above.");
Console.WriteLine("portrait camera check passed");

static void CheckDefaultTuningIdentity()
{
    PortraitTuning tuning = PortraitTuning.Default;
    foreach (float modelHeight in new[] { 0.3f, 0.75f, 1.8f, 4.25f, 12f })
    {
        float head = MathF.Max(0.3f, modelHeight);
        float oldTarget = 0.92f * head;
        float newTarget = tuning.HeadFraction * head;
        float oldWindow = Math.Clamp(0.34f * head, 0.55f, 1.10f);
        float newWindow = Math.Clamp(
            tuning.WindowFraction * head, tuning.WindowMin, tuning.WindowMax);
        float oldFovy = 0.5f * 180f / MathF.PI;
        float newFovy = tuning.FovyDegrees;
        float oldDistance = (oldWindow * 0.5f) /
            MathF.Tan(oldFovy * 0.5f * MathF.PI / 180f);
        float newDistance = (newWindow * 0.5f) /
            MathF.Tan(newFovy * 0.5f * MathF.PI / 180f);
        SameBits(oldTarget, newTarget, "player target");
        SameBits(oldWindow, newWindow, "player window");
        SameBits(oldFovy, newFovy, "player fovy");
        SameBits(oldDistance, newDistance, "player distance");
        SameBits(MathF.Max(0.02f, oldDistance - head),
            MathF.Max(tuning.NearFloor, newDistance - head), "player near");
    }
    SameBits(0.42f, tuning.YawOffset, "yaw offset");
    SameBits(0.02f, tuning.Pitch, "pitch");
    SameBits(0.5f * 180f / MathF.PI, tuning.FovyDegrees, "creature fovy");
    Console.WriteLine("[camera-check] portrait tuning defaults are float-bit identical");
}

static void SameBits(float expected, float actual, string field)
{
    if (BitConverter.SingleToInt32Bits(expected) != BitConverter.SingleToInt32Bits(actual))
        throw new InvalidDataException(
            $"Default portrait tuning changed {field}: {expected:R} != {actual:R}");
}

static void CheckArchiveOrdering()
{
    AssertArchiveOrder(
        ["base.MPQ", "patch.MPQ", "patch-2.MPQ", "patch-4.MPQ", "patch-10.MPQ"],
        ["patch-10.MPQ", "patch-4.MPQ", "patch-2.MPQ", "patch.MPQ", "base.MPQ"]);
    AssertArchiveOrder(
        ["patch-9.MPQ", "patch-10.MPQ"],
        ["patch-10.MPQ", "patch-9.MPQ"]);
    AssertArchiveOrder(
        ["base.MPQ", "patch.MPQ", "patch-enUS.MPQ", "patch-2.MPQ",
            "patch-enUS-2.MPQ", "patch-10.MPQ", "patch-enUS-10.MPQ"],
        ["patch-enUS-10.MPQ", "patch-10.MPQ", "patch-enUS-2.MPQ",
            "patch-2.MPQ", "patch-enUS.MPQ", "patch.MPQ", "base.MPQ"]);
    Console.WriteLine("[camera-check] MPQ archive ordering assertions passed");
}

static void AssertArchiveOrder(string[] input, string[] expected)
{
    IReadOnlyList<string> actual = MpqMount.OrderArchives(input);
    if (!actual.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase))
        throw new InvalidDataException(
            $"Archive order mismatch: expected {string.Join(" > ", expected)}, " +
            $"got {string.Join(" > ", actual)}");
}

static void CheckCreatureSpecimenEnumeration(MpqMount mpq)
{
    CreatureDisplayInfoTable? displays = mpq.ReadFile(CreatureDisplayInfoTable.MpqPath) is { } di
        ? CreatureDisplayInfoTable.Parse(di) : null;
    CreatureModelDataTable? models = mpq.ReadFile(CreatureModelDataTable.MpqPath) is { } md
        ? CreatureModelDataTable.Parse(md) : null;
    if (displays is null || models is null)
        throw new InvalidDataException("Creature specimen DBCs are unavailable");
    var resolver = new CreatureModelResolver(displays, models);
    var specimens = displays.All
        .Select(row => resolver.TryResolve((int)row.Id, out CreatureModelInfo info)
            ? (DisplayId: (int)row.Id, info.ModelPath) : default)
        .Where(specimen => specimen.DisplayId > 0)
        .OrderBy(specimen => specimen.DisplayId)
        .ToArray();
    int wolves = specimens.Count(specimen =>
        specimen.ModelPath.Contains("wolf", StringComparison.OrdinalIgnoreCase));
    if (specimens.Length == 0 || wolves == 0)
        throw new InvalidDataException(
            $"Creature specimen enumeration failed: total={specimens.Length}, wolfMatches={wolves}");
    Console.WriteLine(
        $"[camera-check] portrait specimens={specimens.Length}, wolfFilterMatches={wolves}");
}

static IEnumerable<string> DiagnosticLoadOrder(string clientDataPath)
{
    string[] all = Directory.GetFiles(clientDataPath, "*.MPQ", SearchOption.TopDirectoryOnly)
        .Concat(Directory.GetFiles(clientDataPath, "*.mpq", SearchOption.TopDirectoryOnly))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    return MpqMount.OrderArchives(all);
}

static IEnumerable<string> LegacyDiagnosticLoadOrder(string clientDataPath)
{
    string[] all = Directory.GetFiles(clientDataPath, "*.MPQ", SearchOption.TopDirectoryOnly)
        .Concat(Directory.GetFiles(clientDataPath, "*.mpq", SearchOption.TopDirectoryOnly))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    return all
        .Where(path => Path.GetFileName(path).StartsWith("patch", StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
        .Concat(all
            .Where(path => !Path.GetFileName(path).StartsWith("patch", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path =>
            {
                string name = Path.GetFileName(path);
                if (name.Equals("terrain.mpq", StringComparison.OrdinalIgnoreCase)) return 0;
                if (name.Equals("model.mpq", StringComparison.OrdinalIgnoreCase)) return 1;
                return 10;
            }));
}

static (string Archive, byte[] Bytes) ReadWithProvenance(IEnumerable<string> archivePaths,
    string internalPath)
{
    return TryReadWithProvenance(archivePaths, internalPath) ??
        throw new FileNotFoundException(internalPath);
}

static (string Archive, byte[] Bytes)? TryReadWithProvenance(
    IEnumerable<string> archivePaths, string internalPath)
{
    foreach (string archivePath in archivePaths)
    {
        using MpqArchive? archive = MpqArchive.Open(archivePath);
        if (archive?.ReadFile(internalPath) is { } bytes) return (archivePath, bytes);
    }

    return null;
}

static void PrintCameraHeader(byte[] bytes)
{
    uint version = U32(bytes, 0x004);
    uint cameraCount = U32(bytes, 0x124);
    uint cameraOffset = U32(bytes, 0x128);
    uint lookupCount = U32(bytes, 0x12C);
    uint lookupOffset = U32(bytes, 0x130);
    short lookup0 = lookupCount > 0 && lookupOffset <= bytes.Length - 2
        ? BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan((int)lookupOffset, 2))
        : (short)-1;
    long selectedCameraOffset = lookup0 >= 0 ? cameraOffset + lookup0 * 124L : -1L;

    Console.WriteLine($"[camera-check] header version={version} " +
        $"cameras=count@0x124={cameraCount},offset@0x128=0x{cameraOffset:X} " +
        $"cameraLookup=count@0x12C={lookupCount},offset@0x130=0x{lookupOffset:X} " +
        $"lookup0={lookup0} cameraStride=124 selectedCameraOffset=" +
        $"{(selectedCameraOffset >= 0 ? $"0x{selectedCameraOffset:X}" : "n/a")}");

    if (selectedCameraOffset < 0 || selectedCameraOffset > bytes.Length - 124) return;
    int camera = (int)selectedCameraOffset;
    Console.WriteLine($"[camera-check] camera type={U32(bytes, camera)} " +
        $"fov={F32(bytes, camera + 4):F6} far={F32(bytes, camera + 8):F4} " +
        $"near={F32(bytes, camera + 12):F4} " +
        $"positionBase=({F32(bytes, camera + 44):F4},{F32(bytes, camera + 48):F4},{F32(bytes, camera + 52):F4}) " +
        $"targetBase=({F32(bytes, camera + 84):F4},{F32(bytes, camera + 88):F4},{F32(bytes, camera + 92):F4})");
    PrintTrackHeader(bytes, camera + 16, "position");
    PrintTrackHeader(bytes, camera + 56, "target");
    PrintTrackHeader(bytes, camera + 96, "roll");
    uint positionKeys = U32(bytes, camera + 40);
    uint targetKeys = U32(bytes, camera + 80);
    uint rollKeys = U32(bytes, camera + 120);
    Console.WriteLine($"[camera-check] staticKeys " +
        $"position=({F32(bytes, (int)positionKeys):F4},{F32(bytes, (int)positionKeys + 4):F4},{F32(bytes, (int)positionKeys + 8):F4}) " +
        $"target=({F32(bytes, (int)targetKeys):F4},{F32(bytes, (int)targetKeys + 4):F4},{F32(bytes, (int)targetKeys + 8):F4}) " +
        $"roll={F32(bytes, (int)rollKeys):F6}");
}

static uint U32(byte[] bytes, int offset) => offset <= bytes.Length - 4
    ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4))
    : 0;

static float F32(byte[] bytes, int offset) => offset <= bytes.Length - 4
    ? BitConverter.ToSingle(bytes, offset)
    : 0f;

static void PrintTrackHeader(byte[] bytes, int track, string name)
{
    short globalSequence = track <= bytes.Length - 4
        ? BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(track + 2, 2))
        : (short)-1;
    Console.WriteLine($"[camera-check] {name}Track at=0x{track:X} " +
        $"interpolation={U16(bytes, track)} globalSequence={globalSequence} " +
        $"ranges={U32(bytes, track + 4)}@0x{U32(bytes, track + 8):X} " +
        $"times={U32(bytes, track + 12)}@0x{U32(bytes, track + 16):X} " +
        $"keys={U32(bytes, track + 20)}@0x{U32(bytes, track + 24):X}");
}

static ushort U16(byte[] bytes, int offset) => offset <= bytes.Length - 2
    ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2))
    : (ushort)0;
