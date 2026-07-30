using System.Numerics;
using MSUIClient.Engine;
using MSUIClient.Formats;

string data = args.Length > 0 ? args[0] : Path.GetFullPath(Path.Combine("GameData", "Data"));
string[] models = args.Length > 1 ? args.Skip(1).ToArray() :
[
    @"Character\Dwarf\Male\DwarfMale.m2",
    @"Character\Human\Male\HumanMale.m2",
    @"Creature\Wolf\Wolf.m2",
];

using var mpq = new MpqMount(data);
if (mpq.ArchiveCount == 0) throw new InvalidOperationException($"No MPQs mounted from {data}");

foreach (string path in models)
{
    byte[] bytes = mpq.ReadFile(path) ?? throw new FileNotFoundException(path);
    M2Model model = M2Reader.Parse(bytes) ?? throw new InvalidDataException($"Could not parse {path}");
    M2PortraitCamera camera = model.PortraitCamera ?? throw new InvalidDataException($"No portrait camera: {path}");
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

Console.WriteLine("portrait camera check passed");
