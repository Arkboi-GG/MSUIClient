using System.Security.Cryptography;
using MSUIClient.Formats;

string dataPath = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.GetFullPath(Path.Combine("GameData", "Data"));
uint[] displayIds = args.Skip(1).Select(arg =>
    uint.TryParse(arg, out uint id) ? id : 0).Where(id => id != 0).ToArray();

using var mpq = new MpqMount(dataPath);
var displayFile = mpq.ReadFileWithSupplier(ItemDisplayTable.MpqPath) ??
    throw new InvalidOperationException($"Missing {ItemDisplayTable.MpqPath}");
Console.WriteLine($"[audit] {ItemDisplayTable.MpqPath} <- {displayFile.Supplier} " +
    $"bytes={displayFile.Data.Length} sha256={Sha(displayFile.Data)}");
ItemDisplayTable displays = ItemDisplayTable.Parse(displayFile.Data) ??
    throw new InvalidOperationException("ItemDisplayInfo.dbc did not parse");
if (displayIds.Length == 0)
    displayIds = displays.All
        .Where(row => row.ModelName1.Contains("Glave_1H_DualBlade_D_02",
        StringComparison.OrdinalIgnoreCase))
    .Select(row => row.Id)
    .Order()
    .ToArray();
Console.WriteLine($"[audit] D_02 display rows: {string.Join(", ", displayIds)}");
ItemDisplayRow[] customWeaponRows = displays.All
    .Where(row => row.ModelName1.StartsWith("SUI_W_",
        StringComparison.OrdinalIgnoreCase))
    .OrderBy(row => row.Id)
    .ToArray();
Console.WriteLine($"[audit] custom weapon rows={customWeaponRows.Length} " +
    $"range={customWeaponRows.FirstOrDefault()?.Id}..{customWeaponRows.LastOrDefault()?.Id}");
foreach (ItemDisplayRow custom in customWeaponRows.TakeLast(120))
    Console.WriteLine($"[custom] display={custom.Id} model='{custom.ModelName1}' " +
        $"texture='{custom.ModelTexture1}' visual={custom.ItemVisualId}");

foreach (uint displayId in displayIds)
{
    ItemDisplayRow row = displays.Find(displayId) ??
        throw new InvalidOperationException($"Display {displayId} is missing");
    Console.WriteLine();
    Console.WriteLine($"[display] id={displayId} model1='{row.ModelName1}' " +
        $"model2='{row.ModelName2}' texture1='{row.ModelTexture1}' " +
        $"texture2='{row.ModelTexture2}' visual={row.ItemVisualId}");

    string modelStem = Path.GetFileNameWithoutExtension(row.ModelName1);
    string modelPath = $@"Item\ObjectComponents\Weapon\{modelStem}.m2";
    var modelFile = ReadWithFallback(mpq, modelPath) ??
        throw new InvalidOperationException($"Missing model for display {displayId}: {modelPath}");
    M2Model model = M2Reader.Parse(modelFile.File.Data) ??
        throw new InvalidOperationException($"Could not parse {modelFile.Path}");

    Console.WriteLine($"[model] path='{modelFile.Path}' <- {modelFile.File.Supplier} " +
        $"bytes={modelFile.File.Data.Length} sha256={Sha(modelFile.File.Data)} " +
        $"version={model.Version} vertices={model.Vertices.Count} indices={model.Indices.Count} " +
        $"submeshes={model.Submeshes.Count} batches={model.Batches.Count} " +
        $"textures={model.Textures.Count} colors={model.Colors.Count} " +
        $"transparency={model.TransparencyTracks.Count} uvTransforms={model.TextureTransforms.Count} " +
        $"particles={model.ParticleEmitters.Count} ribbons={model.RibbonEmitters.Count}");

    Console.WriteLine($"[timing] globalSequences=[{string.Join(", ", model.GlobalSequenceDurations.Select((duration, index) => $"{index}:{duration}"))}]");
    for (int sequenceIndex = 0; sequenceIndex < model.Sequences.Count; sequenceIndex++)
    {
        M2Sequence sequence = model.Sequences[sequenceIndex];
        Console.WriteLine($"  sequence[{sequenceIndex}] anim={sequence.AnimationId} var={sequence.VariationId} " +
            $"time={sequence.StartTimestamp}..{sequence.EndTimestamp} duration={sequence.DurationMs} " +
            $"loop={sequence.IsLooping}");
    }
    for (int ribbonIndex = 0; ribbonIndex < model.RibbonEmitters.Count; ribbonIndex++)
    {
        M2RibbonEmitter ribbon = model.RibbonEmitters[ribbonIndex];
        string ribbonTexture = ribbon.Texture < model.Textures.Count
            ? model.Textures[ribbon.Texture].Filename : "<unresolved>";
        Console.WriteLine($"  ribbon[{ribbonIndex}] bone={ribbon.Bone} texture={ribbon.Texture} " +
            $"'{ribbonTexture}' material={ribbon.Material} eps={ribbon.EdgesPerSecond} " +
            $"life={ribbon.EdgeLifetime} gravity={ribbon.Gravity}");
        DescribeTrack("    ribbon.color", ribbon.Color);
        DescribeTrack("    ribbon.alpha", ribbon.Alpha);
        DescribeTrack("    ribbon.above", ribbon.HeightAbove);
        DescribeTrack("    ribbon.below", ribbon.HeightBelow);
        DescribeTrack("    ribbon.visibility", ribbon.Visibility);
    }
    DescribeReplacementTexture(mpq, row.ModelTexture1);
    for (int textureIndex = 0; textureIndex < model.Textures.Count; textureIndex++)
    {
        M2TextureRef texture = model.Textures[textureIndex];
        Console.WriteLine($"  texture[{textureIndex}] type={texture.Type} flags=0x{texture.Flags:X} " +
            $"filename='{texture.Filename}'");
        if (texture.Filename.Length > 0) DescribeFile(mpq, texture.Filename, "    explicit");
    }

    var environmentSubmeshes = new HashSet<ushort>();
    for (int batchIndex = 0; batchIndex < model.Batches.Count; batchIndex++)
    {
        M2Batch batch = model.Batches[batchIndex];
        M2RenderFlag? render = batch.MaterialIndex < model.RenderFlags.Count
            ? model.RenderFlags[batch.MaterialIndex]
            : null;
        bool environment = model.UsesEnvironmentMapForBatch(batch);
        if (environment) environmentSubmeshes.Add(batch.SubmeshIndex);
        int transform = model.GetTextureTransformForBatch(batch);
        Console.WriteLine($"  batch[{batchIndex}] sub={batch.SubmeshIndex} shader={batch.ShaderId} " +
            $"blend={render?.BlendingMode ?? 0} flags=0x{render?.Flags ?? 0:X} " +
            $"unlit={render?.Unlit ?? false} noZWrite={render?.NoZWrite ?? false} " +
            $"units={batch.TextureCount} env={environment} color={batch.ColorIndex} " +
            $"weightCombo={batch.TextureWeightIndex} transform={transform}");

        for (int unit = 0; unit < Math.Max(1, (int)batch.TextureCount); unit++)
        {
            int textureCombo = batch.TextureIndex + unit;
            int textureIndex = textureCombo < model.TextureLookup.Count
                ? model.TextureLookup[textureCombo]
                : -1;
            int coordinateCombo = batch.TextureCoordIndex + unit;
            int coordinate = batch.TextureCoordIndex != ushort.MaxValue &&
                coordinateCombo < model.TextureUnitLookup.Count
                    ? unchecked((ushort)model.TextureUnitLookup[coordinateCombo])
                    : -1;
            string textureName = textureIndex >= 0 && textureIndex < model.Textures.Count
                ? model.Textures[textureIndex].Filename
                : "<unresolved>";
            Console.WriteLine($"    unit[{unit}] texture={textureIndex} '{textureName}' " +
                $"coordinate={coordinate} generated={coordinate > 2}");
        }

        if (batch.ColorIndex >= 0 && batch.ColorIndex < model.Colors.Count)
        {
            M2ColorAnimation color = model.Colors[batch.ColorIndex];
            DescribeTrack("    color.rgb", color.Color);
            DescribeTrack("    color.alpha", color.Alpha);
        }

        if (batch.TextureWeightIndex < model.TransparencyLookup.Count)
        {
            int trackIndex = model.TransparencyLookup[batch.TextureWeightIndex];
            if (trackIndex >= 0 && trackIndex < model.TransparencyTracks.Count)
                DescribeTrack($"    transparency[{trackIndex}]", model.TransparencyTracks[trackIndex]);
        }

        if (transform >= 0)
            DescribeTrack($"    uvTranslation[{transform}]",
                model.TextureTransforms[transform].Translation);
    }

    int environmentPasses = model.Batches.Count(model.UsesEnvironmentMapForBatch);
    int steadyTopologyPasses = model.Batches.Count(batch =>
    {
        if (model.UsesEnvironmentMapForBatch(batch)) return true;
        M2RenderFlag? render = batch.MaterialIndex < model.RenderFlags.Count
            ? model.RenderFlags[batch.MaterialIndex]
            : null;
        bool transparent = (render?.BlendingMode ?? 0) >= 2 || (render?.NoZWrite ?? false);
        return !transparent && environmentSubmeshes.Contains(batch.SubmeshIndex);
    });
    Console.WriteLine($"[verdict] display={displayId} environmentPasses={environmentPasses} " +
        $"steadyTopologyPasses={steadyTopologyPasses} " +
        $"hasAuthoredLineMachinery={environmentPasses > 0}");
}

static void DescribeReplacementTexture(MpqMount mpq, string textureName)
{
    if (string.IsNullOrWhiteSpace(textureName)) return;
    string stem = textureName.Replace('/', '\\').TrimStart('\\');
    if (stem.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];
    DescribeFile(mpq, $@"Item\ObjectComponents\Weapon\{stem}.blp", "  replacement");
}

static void DescribeFile(MpqMount mpq, string path, string prefix)
{
    var found = mpq.ReadFileWithSupplier(path);
    Console.WriteLine(found is { } file
        ? $"{prefix} '{path}' <- {file.Supplier} bytes={file.Data.Length} sha256={Sha(file.Data)}"
        : $"{prefix} '{path}' MISSING");
}

static (string Path, (byte[] Data, string Supplier) File)? ReadWithFallback(
    MpqMount mpq, string path)
{
    foreach (string candidate in new[]
    {
        path,
        Path.ChangeExtension(path, ".M2"),
        Path.ChangeExtension(path, ".mdx"),
    })
    {
        var found = mpq.ReadFileWithSupplier(candidate);
        if (found is not null) return (candidate, found.Value);
    }
    return null;
}

static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

static void DescribeTrack<T>(string name, M2AnimTrack<T> track) where T : struct
{
    string times = track.Timestamps.Count == 0
        ? "none"
        : string.Join(",", track.Timestamps);
    string values = track.Keys.Count == 0
        ? "none"
        : string.Join(" | ", track.Keys);
    string ranges = track.Ranges.Count == 0
        ? "none"
        : string.Join(",", track.Ranges.Select(range => $"{range.Start}-{range.End}"));
    Console.WriteLine($"{name}: interpolation={track.InterpolationType} " +
        $"global={track.GlobalSequence} ranges=[{ranges}] " +
        $"keys={track.Keys.Count} times=[{times}] values=[{values}]");
}
