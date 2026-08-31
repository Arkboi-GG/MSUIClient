using MSUIClient.Formats;
using MSUIClient.Engine;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using MSUIClient.World.Units;

string dataPath = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.GetFullPath(Path.Combine("GameData", "Data"));

using var mpq = new MpqMount(dataPath);
ItemDisplayTable displays = ItemDisplayTable.Parse(
    mpq.ReadFile(ItemDisplayTable.MpqPath) ??
    throw new InvalidOperationException("ItemDisplayInfo.dbc is missing")) ??
    throw new InvalidOperationException("ItemDisplayInfo.dbc could not be parsed");

ItemVisualCatalog visuals = ItemVisualCatalog.Load(dataPath) ??
    throw new InvalidOperationException("ItemVisual catalogs could not be parsed");
string?[] visual25 = visuals.Effects(25) ??
    throw new InvalidOperationException("ItemVisual 25 is missing");
for (int slot = 0; slot < visual25.Length; slot++)
{
    string? effectPath = visual25[slot];
    if (string.IsNullOrWhiteSpace(effectPath)) continue;
    byte[] effectBytes = mpq.ReadFile(effectPath) ??
        mpq.ReadFile(Path.ChangeExtension(effectPath, ".m2")) ??
        throw new InvalidOperationException($"ItemVisual 25 slot {slot} '{effectPath}' is missing");
    M2Model effect = M2Reader.Parse(effectBytes) ??
        throw new InvalidOperationException($"ItemVisual 25 slot {slot} '{effectPath}' did not parse");
    Console.WriteLine($"[warglaive] visual=25 slot={slot} effect='{effectPath}' " +
        $"vertices={effect.Vertices.Count} batches={effect.Batches.Count} " +
        $"bones={effect.Bones.Count} particles={effect.ParticleEmitters.Count} " +
        $"ribbons={effect.RibbonEmitters.Count} sequences={effect.Sequences.Count} " +
        $"globalSequences={effect.GlobalSequenceDurations.Count}");
}

foreach (uint displayId in new uint[] { 30934, 30935, 30936 })
{
    ItemDisplayRow display = displays.Find(displayId) ??
        throw new InvalidOperationException($"Item display {displayId} is missing");
    Console.WriteLine($"[warglaive] display={displayId} model1='{display.ModelName1}' " +
        $"model2='{display.ModelName2}' texture1='{display.ModelTexture1}' " +
        $"texture2='{display.ModelTexture2}' visual={display.ItemVisualId}");
    string modelName = display.ModelName1;
    string path = $@"Item\ObjectComponents\Weapon\{Path.GetFileNameWithoutExtension(modelName)}.m2";
    byte[] bytes = mpq.ReadFile(path) ?? mpq.ReadFile(Path.ChangeExtension(path, ".mdx")) ??
        throw new InvalidOperationException($"{path} is missing");
    M2Model model = M2Reader.Parse(bytes) ??
        throw new InvalidOperationException($"{path} could not be parsed");

    Console.WriteLine($"[warglaive] display={displayId} model={path} version={model.Version} " +
        $"vertices={model.Vertices.Count} batches={model.Batches.Count}");

    var environmentPasses = new List<M2Batch>();

    for (int batchIndex = 0; batchIndex < model.Batches.Count; batchIndex++)
    {
        M2Batch batch = model.Batches[batchIndex];
        int blend = batch.MaterialIndex < model.RenderFlags.Count
            ? model.RenderFlags[batch.MaterialIndex].BlendingMode : -1;
        int flags = batch.MaterialIndex < model.RenderFlags.Count
            ? model.RenderFlags[batch.MaterialIndex].Flags : -1;
        Console.WriteLine($"  batch[{batchIndex}] sub={batch.SubmeshIndex} blend={blend} " +
            $"flags=0x{flags:X} " +
            $"units={batch.TextureCount} texStart={batch.TextureIndex} " +
            $"coordStart={batch.TextureCoordIndex} transformStart={batch.TextureTransformIndex}");
        for (int unit = 0; unit < batch.TextureCount; unit++)
        {
            int texCombo = batch.TextureIndex + unit;
            int coordCombo = batch.TextureCoordIndex + unit;
            int transformCombo = batch.TextureTransformIndex + unit;
            int texture = texCombo < model.TextureLookup.Count ? model.TextureLookup[texCombo] : -1;
            int coordinate = batch.TextureCoordIndex != ushort.MaxValue &&
                coordCombo < model.TextureUnitLookup.Count ? model.TextureUnitLookup[coordCombo] : 0;
            int transform = batch.TextureTransformIndex != ushort.MaxValue &&
                transformCombo < model.TextureTransformLookup.Count
                ? model.TextureTransformLookup[transformCombo] : -1;
            int keys = transform >= 0 && transform < model.TextureTransforms.Count
                ? model.TextureTransforms[transform].Translation.Keys.Count : 0;
            short global = transform >= 0 && transform < model.TextureTransforms.Count
                ? model.TextureTransforms[transform].Translation.GlobalSequence : (short)-1;
            string textureName = texture >= 0 && texture < model.Textures.Count
                ? model.Textures[texture].Filename : "<unresolved>";
            Console.WriteLine($"    unit[{unit}] texture={texture} '{textureName}' " +
                $"coord={coordinate} transform={transform} translationKeys={keys} global={global}");
        }

        if (model.UsesEnvironmentMapForBatch(batch)) environmentPasses.Add(batch);
    }

    Require(environmentPasses.Count == 1,
        $"display {displayId} should have exactly one environment-mapped pass");
    M2Batch environment = environmentPasses[0];
    Require(environment.TextureCount == 1,
        $"display {displayId} environment pass unexpectedly changed texture topology");
    Require(environment.MaterialIndex < model.RenderFlags.Count &&
            model.RenderFlags[environment.MaterialIndex].BlendingMode == 4,
        $"display {displayId} environment pass is no longer additive blend 4");
    Require(environment.TextureIndex < model.TextureLookup.Count,
        $"display {displayId} environment texture lookup is out of range");
    int environmentTexture = model.TextureLookup[environment.TextureIndex];
    Require(environmentTexture < model.Textures.Count &&
            model.Textures[environmentTexture].Filename.EndsWith("ARMORREFLECT3.BLP",
                StringComparison.OrdinalIgnoreCase),
        $"display {displayId} environment pass no longer samples ArmorReflect3");
    int steadyBladePasses = model.Batches.Count(batch =>
        AttachedItemMaterialLaw.IsSteadyWarglaiveBladeBatch(path, model, batch));
    Require(steadyBladePasses == 2,
        $"display {displayId} should classify exactly its base+ArmorReflect blade passes, got {steadyBladePasses}");
}

byte[] reflectBlp = mpq.ReadFile(@"Item\ObjectComponents\Weapon\ArmorReflect3.blp") ??
    throw new InvalidOperationException("ArmorReflect3.blp is missing");
byte[] reflectPixels = BlpDecoder.GetPixels(reflectBlp, 0, out int reflectWidth, out int reflectHeight);
DescribeChannels(reflectPixels, reflectWidth, reflectHeight,
    BlpDecoder.HasAlphaChannel(reflectBlp));

// Exact build-5875 specimen: P=(0,0,-5) reflected around the 45-degree
// XZ normal becomes +X, which remaps to UV=(1,.5).
float diagonal = MathF.Sqrt(0.5f);
var uv = AttachedItemMaterialLaw.EnvironmentUv(
    new System.Numerics.Vector3(0f, 0f, -5f),
    new System.Numerics.Vector3(diagonal, 0f, diagonal));
Require(System.Numerics.Vector2.Distance(uv, new(1f, 0.5f)) < 0.00001f,
    $"build-5875 environment equation drifted: {uv}");

string vertexShader = File.ReadAllText(Path.Combine("MSUIClient", "Shaders", "attached.vert"));
string fragmentShader = File.ReadAllText(Path.Combine("MSUIClient", "Shaders", "character.frag"));
string renderer = File.ReadAllText(Path.Combine("MSUIClient", "World", "Units",
    "AttachedItemRenderer.cs"));
Require(AttachedItemMaterialLaw.UsesSteadyWarglaiveBlade(
            @"Item\ObjectComponents\Weapon\Glave_1H_DualBlade_D_01.m2") &&
        AttachedItemMaterialLaw.UsesSteadyWarglaiveBlade(
            @"Item\ObjectComponents\Weapon\Glave_1H_DualBlade_D_01Left.mdx") &&
        AttachedItemMaterialLaw.UsesSteadyWarglaiveBlade(
            @"Item\ObjectComponents\Weapon\Glave_1H_Short_B_01.m2") &&
        !AttachedItemMaterialLaw.UsesSteadyWarglaiveBlade(
            @"Item\ObjectComponents\Weapon\Sword_1H_Generic.m2"),
    "steady Warglaive lighting escaped its three-model family gate");
Require(vertexShader.Contains("vec3 reflected = viewPosition", StringComparison.Ordinal) &&
        vertexShader.Contains("2.0 * dot(viewPosition, viewNormal) * viewNormal",
            StringComparison.Ordinal) &&
        vertexShader.Contains("reflected.xy / reflectedLength * 0.5 + vec2(0.5)",
            StringComparison.Ordinal),
    "attached-item vertex shader is not using the build-5875 per-vertex reflection coordinate");
Require(vertexShader.Contains("uEnvironmentMap != 0 ? vec2(0.0) : uUvOffset",
            StringComparison.Ordinal),
    "generated-coordinate pass is incorrectly accepting an authored UV transform");
Require(!fragmentShader.Contains("vViewPosition", StringComparison.Ordinal) &&
        !fragmentShader.Contains("matcapUV", StringComparison.Ordinal),
    "obsolete per-fragment matcap path is still present");
Require(fragmentShader.Contains("if (uUnlit == 0)", StringComparison.Ordinal),
    "character shader no longer honors the authored UNLIT material flag");
Require(renderer.Contains("batch.Unlit || batch.SteadyWarglaiveBlade",
            StringComparison.Ordinal) &&
        !renderer.Contains("effectWeapon", StringComparison.Ordinal) &&
        !renderer.Contains("batch.BlendMode >= 2", StringComparison.Ordinal),
    "attached-item fullbright policy is not restricted to the Warglaive blade classifier");
Require(fragmentShader.Contains("light = clamp(light, vec3(0.0), vec3(1.0));",
            StringComparison.Ordinal),
    "Model2 lighting is not clamped before albedo multiplication");

Require(vertexShader.Contains("layout (location = 5) in vec2 aUV1;", StringComparison.Ordinal) &&
        vertexShader.Contains("out vec2 vUV2;", StringComparison.Ordinal) &&
        vertexShader.Contains("uUvSet2 != 0 ? aUV1 : aUV", StringComparison.Ordinal),
    "attached-item vertex shader does not carry authored UV1 into texture unit 1");
Require(fragmentShader.Contains("uniform sampler2D uTexture2;", StringComparison.Ordinal) &&
        fragmentShader.Contains("vec4 wave = albedo * stage2;", StringComparison.Ordinal) &&
        fragmentShader.Contains("albedo.rgb += wave.rgb;", StringComparison.Ordinal),
    "attached-item fragment shader does not reconstruct the two-unit MODULATE pass");
Require(renderer.Contains("batch.Texture2.Bind(1);", StringComparison.Ordinal) &&
        renderer.Contains("GetTextureCoordinateForBatchUnit(batch, 1)", StringComparison.Ordinal) &&
        renderer.Contains("AttachedItemMaterialLaw.UvOffsetAt(", StringComparison.Ordinal) &&
        renderer.Contains("SteadyModulatedGlow = batch.TextureCount == 2", StringComparison.Ordinal),
    "attached-item renderer does not bind or animate the second authored texture unit");

Console.WriteLine($"[warglaive] PASS build-5875 environment UV={uv}; displays " +
    "30934/30935/30936 use the single-unit ArmorReflect3 reflection pass and the attached " +
    "renderer uses per-vertex reflected-position sampling with a model-scoped steady blade pass");

if (args.Any(a => string.Equals(a, "--link-shaders", StringComparison.OrdinalIgnoreCase)))
    LinkShadersOnDriver();

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void LinkShadersOnDriver()
{
    var options = WindowOptions.Default with
    {
        Size = new Vector2D<int>(16, 16),
        IsVisible = false,
        API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default,
            new APIVersion(3, 3)),
    };
    using IWindow window = Window.Create(options);
    window.Initialize();
    using GL gl = window.CreateOpenGL();
    string shaderDir = Path.Combine("MSUIClient", "Shaders");
    using MSUIClient.Engine.Shader body = MSUIClient.Engine.Shader.FromFiles(gl,
        Path.Combine(shaderDir, "character.vert"),
        Path.Combine(shaderDir, "character.frag"));
    using MSUIClient.Engine.Shader attached = MSUIClient.Engine.Shader.FromFiles(gl,
        Path.Combine(shaderDir, "attached.vert"),
        Path.Combine(shaderDir, "character.frag"));
    Console.WriteLine("[warglaive] PASS character and attached shader programs compiled and linked on the active OpenGL driver");
}

static void DescribeChannels(byte[] bgra, int width, int height, bool hasAuthoredAlpha)
{
    byte minR = 255, minG = 255, minB = 255, minA = 255;
    byte maxR = 0, maxG = 0, maxB = 0, maxA = 0;
    double sumL = 0, sumA = 0, sumLL = 0, sumAA = 0, sumLA = 0;
    var alphas = new HashSet<byte>();
    int count = width * height;

    for (int i = 0; i < count; i++)
    {
        byte b = bgra[i * 4 + 0];
        byte g = bgra[i * 4 + 1];
        byte r = bgra[i * 4 + 2];
        byte a = bgra[i * 4 + 3];
        minR = Math.Min(minR, r); maxR = Math.Max(maxR, r);
        minG = Math.Min(minG, g); maxG = Math.Max(maxG, g);
        minB = Math.Min(minB, b); maxB = Math.Max(maxB, b);
        minA = Math.Min(minA, a); maxA = Math.Max(maxA, a);
        alphas.Add(a);

        double l = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        sumL += l; sumA += a; sumLL += l * l; sumAA += a * a; sumLA += l * a;
    }

    double meanL = sumL / count;
    double meanA = sumA / count;
    double covariance = sumLA / count - meanL * meanA;
    double varianceL = sumLL / count - meanL * meanL;
    double varianceA = sumAA / count - meanA * meanA;
    double correlation = varianceL > 0 && varianceA > 0
        ? covariance / Math.Sqrt(varianceL * varianceA)
        : 0;

    Console.WriteLine($"[warglaive] ArmorReflect3 {width}x{height} authoredAlpha={hasAuthoredAlpha} " +
        $"R={minR}..{maxR} G={minG}..{maxG} B={minB}..{maxB} A={minA}..{maxA} " +
        $"alphaValues={alphas.Count} meanLuma={meanL:F2} meanAlpha={meanA:F2} " +
        $"lumaAlphaCorrelation={correlation:F4}");
}
