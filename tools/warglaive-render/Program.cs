// Offline render of the Warglaive blade using MSUIClient's OWN attached-item shaders.
//
// Purpose: reproduce, deterministically and with no game server, exactly what the client's
// attached.vert + character.frag produce for the ArmorReflect3 environment pass, so the
// "sweeping magical lines vs uniform glow" question is answered with pixels, not theory.
//
// It loads the real blade M2 and the real ArmorReflect3.blp, draws base + reflect passes with
// the client's shaders across N yaw angles, and writes BMP strips:
//   warglaive_current_full.bmp    base + additive reflect (what the user sees)
//   warglaive_current_reflect.bmp reflect pass only, on black (the effect isolated)
// A base-only strip (warglaive_current_base.bmp) is the sanity check that camera/mesh are right.

using System.Numerics;
using MSUIClient.Engine;
using MSUIClient.Formats;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using SkiaSharp;
using Shader = MSUIClient.Engine.Shader;
using Texture = MSUIClient.Engine.Texture;

string dataPath = args.Length > 0 ? Path.GetFullPath(args[0])
    : Path.GetFullPath(Path.Combine("GameData", "Data"));
string outDir = args.Length > 1 ? Path.GetFullPath(args[1]) : Directory.GetCurrentDirectory();
Directory.CreateDirectory(outDir);
string shaderDir = Path.Combine("MSUIClient", "Shaders");

static string Sha256File(string path) => Convert.ToHexString(
    System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
string productionVertexPath = Path.GetFullPath(Path.Combine(shaderDir, "attached.vert"));
string productionFragmentPath = Path.GetFullPath(Path.Combine(shaderDir, "character.frag"));
Console.WriteLine($"[production-shader] attached.vert sha256={Sha256File(productionVertexPath)}");
Console.WriteLine($"[production-shader] character.frag sha256={Sha256File(productionFragmentPath)}");

const int FrameW = 320, FrameH = 320, Frames = 9;
int fullW = FrameW * Frames, fullH = FrameH;

using var mpq = new MpqMount(dataPath);

// Optional: dump DBCs the web app needs into a directory (arg[2]) so its viewer can render.
if (args.Length > 2 && !args[2].Equals("none", StringComparison.OrdinalIgnoreCase))
{
    Directory.CreateDirectory(args[2]);
    foreach (var name in new[] { "ItemDisplayInfo", "ItemModelInfo", "ItemVisuals", "ItemVisualEffects", "SpellVisualKit", "SpellVisual" })
    {
        var b = mpq.ReadFile($@"DBFilesClient\{name}.dbc");
        if (b is not null) { File.WriteAllBytes(Path.Combine(args[2], name + ".dbc"), b); Console.WriteLine($"[dbc] dumped {name} ({b.Length}b)"); }
    }
}

// Optional noisy inventory scan; omitted for the repeatable visual oracle.
bool verboseInventory = args.Contains("--verbose-inventory", StringComparer.OrdinalIgnoreCase);
if (verboseInventory)
{
    Console.WriteLine("[list] mounted files matching glave/warglaive/reflect/blade/energy:");
    foreach (var f in mpq.ListedFiles())
    {
        string fl = f.ToLowerInvariant();
        if (fl.Contains("glave") || fl.Contains("glaive") || fl.Contains("warglaive") ||
            fl.Contains("reflect") || (fl.Contains("blade") && fl.EndsWith(".blp")))
            Console.WriteLine("   " + f);
    }
}
// Who supplies the pieces I render? (base.MPQ = stock, patch-4.MPQ = custom override)
foreach (var probe in new[] {
    @"Item\ObjectComponents\Weapon\Glave_1H_DualBlade_D_01.m2",
    @"Item\ObjectComponents\Weapon\Glave_1H_DualBlade_D_01Black.blp",
    @"Item\ObjectComponents\Weapon\ArmorReflect3.blp",
    @"DBFilesClient\ItemDisplayInfo.dbc" })
{
    var ws = mpq.ReadFileWithSupplier(probe);
    Console.WriteLine($"[supplier] {probe} <- {(ws is { } v ? v.Supplier : "MISSING")}");
}

// Display 30935 = the mainhand Warglaive of Azzinoth (Glave_1H_DualBlade_D_01).
ItemDisplayTable displays = ItemDisplayTable.Parse(mpq.ReadFile(ItemDisplayTable.MpqPath)!)!;

// Scan for every FORGED (custom-textured) display, to find the user's green warglaive.
if (verboseInventory)
{
    Console.WriteLine("[scan] forged displays (Custom_ texture):");
    foreach (var d in displays.All)
    {
        string tx = d.ModelTexture1 ?? "";
        if (tx.IndexOf("Custom", StringComparison.OrdinalIgnoreCase) < 0) continue;
        Console.WriteLine($"  model1='{d.ModelName1}' tex1='{tx}' visual={d.ItemVisualId}");
    }
}

uint displayId = args.Length > 3 && uint.TryParse(args[3], out var _d) ? _d : 30935u;
ItemDisplayRow row = displays.Find(displayId) ?? throw new InvalidOperationException($"display {displayId} missing");
Console.WriteLine($"[render] display={displayId} model1='{row.ModelName1}' tex1='{row.ModelTexture1}' visual={row.ItemVisualId}");
string modelStem = Path.GetFileNameWithoutExtension(row.ModelName1);
string modelPath = $@"Item\ObjectComponents\Weapon\{modelStem}.m2";
byte[] m2Bytes = mpq.ReadFile(modelPath) ?? mpq.ReadFile(Path.ChangeExtension(modelPath, ".mdx"))!;
M2Model m2 = M2Reader.Parse(m2Bytes)!;
Console.WriteLine($"[render] model={modelPath} verts={m2.Vertices.Count} batches={m2.Batches.Count} " +
    $"baseTexture={row.ModelTexture1}");

// ── window / GL ──────────────────────────────────────────────────────────────
var options = WindowOptions.Default with
{
    Size = new Vector2D<int>(fullW, fullH),
    IsVisible = false,
    API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default,
        new APIVersion(3, 3)),
};
using IWindow window = Window.Create(options);
window.Initialize();
using GL gl = window.CreateOpenGL();
using Shader shader = Shader.FromFiles(gl,
    Path.Combine(shaderDir, "attached.vert"), Path.Combine(shaderDir, "character.frag"));

// ── geometry (16-float layout, rigid: zero weights, uBoneCount = 0) ───────────
const int F = 16;
var verts = new float[m2.Vertices.Count * F];
var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
for (int i = 0; i < m2.Vertices.Count; i++)
{
    var v = m2.Vertices[i]; int o = i * F;
    verts[o + 0] = v.PosX; verts[o + 1] = v.PosY; verts[o + 2] = v.PosZ;
    verts[o + 3] = v.NormX; verts[o + 4] = v.NormY; verts[o + 5] = v.NormZ;
    verts[o + 6] = v.TexU; verts[o + 7] = v.TexV;
    min = Vector3.Min(min, new Vector3(v.PosX, v.PosY, v.PosZ));
    max = Vector3.Max(max, new Vector3(v.PosX, v.PosY, v.PosZ));
}
Vector3 center = (min + max) * 0.5f;
float radius = (max - min).Length() * 0.5f;
Console.WriteLine($"[render] aabb min={min} max={max} center={center} radius={radius}");
ushort[] indices = m2.Indices.ToArray();

uint vao = gl.GenVertexArray(); gl.BindVertexArray(vao);
uint vbo = gl.GenBuffer(); gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
unsafe { fixed (float* p = verts) gl.BufferData(BufferTargetARB.ArrayBuffer,
    (nuint)(verts.Length * sizeof(float)), p, BufferUsageARB.StaticDraw); }
uint ebo = gl.GenBuffer(); gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
unsafe { fixed (ushort* p = indices) gl.BufferData(BufferTargetARB.ElementArrayBuffer,
    (nuint)(indices.Length * sizeof(ushort)), p, BufferUsageARB.StaticDraw); }
const uint stride = F * sizeof(float);
unsafe
{
    gl.EnableVertexAttribArray(0); gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
    gl.EnableVertexAttribArray(1); gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
    gl.EnableVertexAttribArray(2); gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
    gl.EnableVertexAttribArray(3); gl.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, stride, (void*)(8 * sizeof(float)));
    gl.EnableVertexAttribArray(4); gl.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, stride, (void*)(12 * sizeof(float)));
}
gl.BindVertexArray(0);

// ── textures ─────────────────────────────────────────────────────────────────
Texture? baseTex = LoadTex($@"Item\ObjectComponents\Weapon\{row.ModelTexture1}.blp");
Texture? reflectTex = LoadTex(@"Item\ObjectComponents\Weapon\ArmorReflect3.blp");

Texture? LoadTex(string path)
{
    var dec = AdtTerrainReader.ReadBlpPixels(dataPath, path);
    if (dec is null) { Console.WriteLine($"[render] MISSING tex {path}"); return null; }
    var (bgra, w, h) = dec.Value;
    // Dump the source texture (magnified, top-down flipped to match viewers) for inspection.
    var flip = new byte[bgra.Length];
    for (int y = 0; y < h; y++) Array.Copy(bgra, y * w * 4, flip, (h - 1 - y) * w * 4, w * 4);
    WriteBmp(Path.Combine(outDir, "tex_" + Path.GetFileNameWithoutExtension(path) + ".bmp"), flip, w, h);
    return Texture.From2D(gl, bgra, w, h);
}

// ── batches (mirror AttachedItemRenderer.BuildModel) ─────────────────────────
var built = new List<(uint start, uint count, Texture? tex, int blend, bool twoSided,
    bool noZWrite, bool noZTest, bool unlit, bool env, bool steadyBlade)>();
foreach (var (batch, idx) in m2.Batches.Select((b, i) => (b, i)))
{
    if (m2.IsBatchConstantInvisible(batch)) continue;
    if (batch.SubmeshIndex >= m2.Submeshes.Count) continue;
    var sm = m2.Submeshes[batch.SubmeshIndex];
    if (sm.IndexCount == 0) continue;

    Texture? tex = null; bool hasRef = false;
    if (batch.TextureIndex < m2.TextureLookup.Count)
    {
        int ti = m2.TextureLookup[batch.TextureIndex];
        if (ti >= 0 && ti < m2.Textures.Count)
        {
            hasRef = true; var tr = m2.Textures[ti];
            if (tr.Filename.Length > 0) tex = tr.Filename.ToUpperInvariant().Contains("ARMORREFLECT3") ? reflectTex : LoadTex(tr.Filename);
            else if (tr.Type == 2) tex = baseTex;
        }
    }
    if (!hasRef) tex = baseTex;

    var rf = batch.MaterialIndex < m2.RenderFlags.Count ? m2.RenderFlags[batch.MaterialIndex] : null;
    built.Add((sm.IndexStart, sm.IndexCount, tex, rf?.BlendingMode ?? 0,
        rf?.TwoSided ?? false, rf?.NoZWrite ?? false, rf?.NoZTest ?? false, rf?.Unlit ?? false,
        m2.UsesEnvironmentMapForBatch(batch),
        MSUIClient.World.Units.AttachedItemMaterialLaw.IsSteadyWarglaiveBladeBatch(
            modelPath, m2, batch)));
    int gtx = m2.GetTextureTransformForBatch(batch); // what MSUIClient (unit 0) resolves
    Console.WriteLine($"[render] batch[{idx}] blend={rf?.BlendingMode} flags=0x{rf?.Flags:X} " +
        $"unlit={rf?.Unlit} env={m2.UsesEnvironmentMapForBatch(batch)} " +
        $"steadyBlade={built[^1].steadyBlade} " +
        $"texCount={batch.TextureCount} xformIdx={batch.TextureTransformIndex} " +
        $"MSUIresolvesXform={gtx} " +
        $"idx[{sm.IndexStart}..{sm.IndexStart + sm.IndexCount}) tex={(tex == reflectTex ? "REFLECT" : tex == baseTex ? "base" : "other")}");
}

// ── RAW animation-track dump: find the pulse source (global-sequence transparency/color) ──
Console.WriteLine($"[anim] globalSequences={m2.GlobalSequenceDurations.Count}: [{string.Join(",", m2.GlobalSequenceDurations)}]");
Console.WriteLine($"[anim] transparency tracks={m2.TransparencyTracks.Count}, TransparencyLookup=[{string.Join(",", m2.TransparencyLookup)}]");
for (int i = 0; i < m2.TransparencyTracks.Count; i++)
{
    var t = m2.TransparencyTracks[i];
    float mn = 2f, mx = -2f;
    foreach (var k in t.Keys) { float v = Math.Clamp(k / 32767f, 0f, 1f); mn = Math.Min(mn, v); mx = Math.Max(mx, v); }
    Console.WriteLine($"   transp[{i}] gseq={t.GlobalSequence} keys={t.Keys.Count} interp={t.InterpolationType} range={mn:F2}..{mx:F2}");
}
Console.WriteLine($"[anim] color anims={m2.Colors.Count}");
for (int i = 0; i < m2.Colors.Count; i++)
{
    var c = m2.Colors[i];
    Console.WriteLine($"   color[{i}] rgb.gseq={c.Color.GlobalSequence} rgbKeys={c.Color.Keys.Count} " +
        $"alpha.gseq={c.Alpha.GlobalSequence} alphaKeys={c.Alpha.Keys.Count}");
}
Console.WriteLine($"[anim] texture transforms={m2.TextureTransforms.Count}, TextureTransformLookup=[{string.Join(",", m2.TextureTransformLookup)}]");
for (int i = 0; i < m2.TextureTransforms.Count; i++)
{
    var tt = m2.TextureTransforms[i];
    Console.WriteLine($"   texXform[{i}] transKeys={tt.Translation.Keys.Count} gseq={tt.Translation.GlobalSequence}");
}

// ── bone animation probe: any bone driven by a GLOBAL SEQUENCE (continuous motion)? ──
Console.WriteLine($"[bones] count={m2.Bones.Count}");
for (int i = 0; i < m2.Bones.Count; i++)
{
    var bn = m2.Bones[i];
    bool anim = bn.Translation.Keys.Count > 1 || bn.Rotation.Keys.Count > 1 || bn.Scale.Keys.Count > 1;
    if (!anim) continue;
    Console.WriteLine($"   bone[{i}] flags=0x{bn.Flags:X} T(keys={bn.Translation.Keys.Count},gseq={bn.Translation.GlobalSequence}) " +
        $"R(keys={bn.Rotation.Keys.Count},gseq={bn.Rotation.GlobalSequence}) S(keys={bn.Scale.Keys.Count},gseq={bn.Scale.GlobalSequence})");
}

// ── material-track inspection: is there a green tint? constant or animated? ──
Console.WriteLine("[mat] per-batch AttachedItemMaterialLaw.At sampled over time (tint / alpha / uvOffset):");
foreach (var (batch, idx) in m2.Batches.Select((b, i) => (b, i)))
{
    if (m2.IsBatchConstantInvisible(batch)) continue;
    Console.Write($"  batch[{idx}] env={m2.UsesEnvironmentMapForBatch(batch)} colorIdx={batch.ColorIndex} " +
                  $"texWeightIdx={batch.TextureWeightIndex}: ");
    foreach (float t in new[] { 0f, 0.3f, 0.6f, 1.0f, 1.5f, 2.0f })
    {
        var s = MSUIClient.World.Units.AttachedItemMaterialLaw.At(m2, batch, t);
        Console.Write($"t={t}:tint=({s.Tint.X:F2},{s.Tint.Y:F2},{s.Tint.Z:F2})a={s.Alpha:F2} ");
    }
    Console.WriteLine();
}

// ── lighting uniforms (AttachedItemRenderer defaults) ────────────────────────
void SetLightingOn(Shader targetShader)
{
    // The production character-select booth values from BoothTune.  This is
    // the exact context of the reported bright/dim loop, including its
    // viewer-relative key (camera is +Z with +Y up in this harness).
    float keyAz = 29.320f * MathF.PI / 180f;
    float keyEl = 19.545f * MathF.PI / 180f;
    Vector3 boothSunDir = Vector3.Normalize(new Vector3(
        -MathF.Sin(keyAz) * MathF.Cos(keyEl),
         MathF.Sin(keyEl),
         MathF.Cos(keyAz) * MathF.Cos(keyEl)));
    targetShader.Set("uCameraPos", Vector3.Zero); // set per-frame below
    targetShader.Set("uSunDirection", boothSunDir);
    targetShader.Set("uSunColor", new Vector3(1.318f, 1.0f, 0.682f));
    targetShader.Set("uSunIntensity", 0.555f);
    targetShader.Set("uAmbientColor", new Vector3(0.810f, 1.0f, 1.190f));
    targetShader.Set("uAmbientIntensity", 0.456f);
    targetShader.Set("uShadowWrap", 0.226f);
    targetShader.Set("uFogStart", 5000f); targetShader.Set("uFogEnd", 9000f);
    targetShader.Set("uFogColor", new Vector3(0.56f, 0.71f, 0.85f));
    targetShader.Set("uBodyTint", Vector3.One); targetShader.Set("uBodyAlpha", 1f);
    targetShader.Set("uPointLightCount", 0);
    targetShader.Set("uTexture", 0);
}
void SetLighting() => SetLightingOn(shader);

Vector3 camPos = new(0, 0, radius * 2.1f);
Vector3 target = Vector3.Zero;
Vector3 up = Vector3.UnitY;
Matrix4x4 view = Matrix4x4.CreateLookAt(camPos, target, up);
Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(
    48f * MathF.PI / 180f, (float)FrameW / FrameH, 0.05f, 20000f);
Matrix4x4 viewProj = view * proj;
Vector3 sunDir = Vector3.Normalize(new Vector3(0.45f, 0.35f, 0.82f));

// The blade lies flat in XY (thin in Z), so the camera at +Z already faces its flat side.
// A gentle downward tilt lets us look at the face; each frame yaws it a little so the
// environment reflection sweeps across the blade without going edge-on.
Matrix4x4 preTilt = Matrix4x4.CreateRotationX(0.35f);

gl.Enable(EnableCap.DepthTest);
gl.DepthFunc(DepthFunction.Lequal);   // match ClientWindow.cs:804 so same-geometry overlay passes draw
gl.Enable(EnableCap.ScissorTest);

// mode: 0 = base only, 1 = full (base+reflect), 2 = reflect only on black
byte[] Render(int mode)
{
    gl.Viewport(0, 0, (uint)fullW, (uint)fullH);
    gl.Scissor(0, 0, (uint)fullW, (uint)fullH);
    gl.ClearColor(0.10f, 0.10f, 0.12f, 1f);
    if (mode == 2) gl.ClearColor(0f, 0f, 0f, 1f);
    gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

    shader.Use(); SetLighting();
    shader.Set("uSunDirection", sunDir);
    shader.Set("uBoneCount", 0);

    for (int f = 0; f < Frames; f++)
    {
        gl.Viewport(f * FrameW, 0, (uint)FrameW, (uint)FrameH);
        gl.Scissor(f * FrameW, 0, (uint)FrameW, (uint)FrameH);

        // Tilt the face through the camera so the env streak sweeps across it AND the
        // directional-lighting response swings (exposing the pulse).
        float ang = -0.7f + 1.4f * (f / (float)(Frames - 1));
        Matrix4x4 model = Matrix4x4.CreateTranslation(-center)
            * Matrix4x4.CreateRotationX(ang) * Matrix4x4.CreateRotationY(0.5f);
        shader.Set("uModel", model);
        shader.Set("uModelViewProjection", model * viewProj);
        shader.Set("uView", view);
        shader.Set("uCameraPos", camPos);

        gl.BindVertexArray(vao);
        // pass A: opaque bases; pass B: transparent/additive (incl. reflect)
        for (int phase = 0; phase < 2; phase++)
        {
            bool transparent = phase == 1;
            if (transparent) { gl.DepthMask(false); gl.Enable(EnableCap.Blend); }
            else { gl.DepthMask(true); gl.Disable(EnableCap.Blend); }

            foreach (var b in built)
            {
                bool isTransparent = b.blend >= 2 || b.noZWrite;
                if (isTransparent != transparent) continue;
                if (mode == 0 && b.env) continue;            // base-only strip
                if (mode == 2 && !b.env) continue;           // reflect-only strip

                if (transparent) ApplyBlend(gl, b.blend);
                if (b.twoSided) gl.Disable(EnableCap.CullFace); else gl.Enable(EnableCap.CullFace);
                if (b.noZTest) gl.Disable(EnableCap.DepthTest); else gl.Enable(EnableCap.DepthTest);

                if (b.tex is not null) { b.tex.Bind(0); shader.Set("uHasTexture", 1); }
                else shader.Set("uHasTexture", 0);
                shader.Set("uAlphaCutoff", b.blend == 1 ? 0.35f : 0f);
                shader.Set("uEnvironmentMap", b.env ? 1 : 0);
                // mode 3 = proposed fix: draw the energy/base UNLIT so it glows steadily
                // instead of pulsing with the blade's angle to the sun.
                shader.Set("uUnlit", (mode == 3 && !b.env) ? 1 : (b.unlit ? 1 : 0));
                shader.Set("uFogPolicy", b.env ? 1 : 0);
                unsafe
                {
                    gl.DrawElements(PrimitiveType.Triangles, b.count,
                        DrawElementsType.UnsignedShort, (void*)(b.start * sizeof(ushort)));
                }
            }
            if (transparent) { gl.Disable(EnableCap.Blend); gl.DepthMask(true); }
        }
    }
    gl.Enable(EnableCap.DepthTest);

    var pixels = new byte[fullW * fullH * 4];
    gl.Viewport(0, 0, (uint)fullW, (uint)fullH);
    gl.Scissor(0, 0, (uint)fullW, (uint)fullH);
    unsafe { fixed (byte* p = pixels)
        gl.ReadPixels(0, 0, (uint)fullW, (uint)fullH, PixelFormat.Bgra, PixelType.UnsignedByte, p); }
    return pixels;
}

static void ApplyBlend(GL gl, int mode)
{
    switch (mode)
    {
        case 3: case 4: gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One); break;
        case 5: gl.BlendFunc(BlendingFactor.DstColor, BlendingFactor.Zero); break;
        case 6: gl.BlendFunc(BlendingFactor.DstColor, BlendingFactor.SrcColor); break;
        default: gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha); break;
    }
}

WriteBmp(Path.Combine(outDir, "warglaive_current_base.bmp"), Render(0), fullW, fullH);
WriteBmp(Path.Combine(outDir, "warglaive_current_full.bmp"), Render(1), fullW, fullH);
WriteBmp(Path.Combine(outDir, "warglaive_current_reflect.bmp"), Render(2), fullW, fullH);
WriteBmp(Path.Combine(outDir, "warglaive_proposed_full.bmp"), Render(3), fullW, fullH);
Console.WriteLine($"[render] wrote strips to {outDir}");

// ── equation comparison: render ONLY the env submesh opaquely, raw reflect color ──
byte[] RenderDebug(Shader dbg)
{
    gl.Viewport(0, 0, (uint)fullW, (uint)fullH); gl.Scissor(0, 0, (uint)fullW, (uint)fullH);
    gl.ClearColor(0f, 0f, 0f, 1f);
    gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));
    dbg.Use(); dbg.Set("uTexture", 0);
    for (int f = 0; f < Frames; f++)
    {
        gl.Viewport(f * FrameW, 0, (uint)FrameW, (uint)FrameH);
        gl.Scissor(f * FrameW, 0, (uint)FrameW, (uint)FrameH);
        float yaw = -0.8f + 1.6f * (f / (float)(Frames - 1));
        Matrix4x4 model = Matrix4x4.CreateTranslation(-center) * preTilt * Matrix4x4.CreateRotationY(yaw);
        dbg.Set("uModel", model); dbg.Set("uModelViewProjection", model * viewProj);
        dbg.Set("uView", view); dbg.Set("uBoneCount", 0);
        gl.BindVertexArray(vao); gl.Disable(EnableCap.Blend); gl.DepthMask(true);
        gl.Disable(EnableCap.CullFace); gl.Enable(EnableCap.DepthTest);
        foreach (var b in built)
        {
            if (!b.env || b.tex is null) continue;
            b.tex.Bind(0);
            unsafe { gl.DrawElements(PrimitiveType.Triangles, b.count,
                DrawElementsType.UnsignedShort, (void*)(b.start * sizeof(ushort))); }
        }
    }
    var px = new byte[fullW * fullH * 4];
    gl.Viewport(0, 0, (uint)fullW, (uint)fullH); gl.Scissor(0, 0, (uint)fullW, (uint)fullH);
    unsafe { fixed (byte* p = px) gl.ReadPixels(0, 0, (uint)fullW, (uint)fullH, PixelFormat.Bgra, PixelType.UnsignedByte, p); }
    return px;
}
// Full blade with the env pass LIT (pre-matcap behavior) — the "neon pulse" look.
byte[] RenderFullLit(Shader sh)
{
    gl.Viewport(0, 0, (uint)fullW, (uint)fullH); gl.Scissor(0, 0, (uint)fullW, (uint)fullH);
    gl.ClearColor(0.10f, 0.10f, 0.12f, 1f);
    gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));
    sh.Use();
    sh.Set("uCameraPos", Vector3.Zero); sh.Set("uSunColor", new Vector3(1.00f, 0.95f, 0.85f));
    sh.Set("uSunIntensity", 1.15f); sh.Set("uAmbientColor", new Vector3(0.42f, 0.50f, 0.60f));
    sh.Set("uAmbientIntensity", 0.85f); sh.Set("uShadowWrap", 0f);
    sh.Set("uFogStart", 5000f); sh.Set("uFogEnd", 9000f); sh.Set("uFogColor", new Vector3(0.56f, 0.71f, 0.85f));
    sh.Set("uBodyTint", Vector3.One); sh.Set("uBodyAlpha", 1f); sh.Set("uPointLightCount", 0);
    sh.Set("uTexture", 0); sh.Set("uSunDirection", sunDir); sh.Set("uBoneCount", 0);
    for (int f = 0; f < Frames; f++)
    {
        gl.Viewport(f * FrameW, 0, (uint)FrameW, (uint)FrameH);
        gl.Scissor(f * FrameW, 0, (uint)FrameW, (uint)FrameH);
        float yaw = -0.8f + 1.6f * (f / (float)(Frames - 1));
        Matrix4x4 model = Matrix4x4.CreateTranslation(-center) * preTilt * Matrix4x4.CreateRotationY(yaw);
        sh.Set("uModel", model); sh.Set("uModelViewProjection", model * viewProj); sh.Set("uView", view);
        gl.BindVertexArray(vao);
        for (int phase = 0; phase < 2; phase++)
        {
            bool transparent = phase == 1;
            if (transparent) { gl.DepthMask(false); gl.Enable(EnableCap.Blend); }
            else { gl.DepthMask(true); gl.Disable(EnableCap.Blend); }
            foreach (var b in built)
            {
                if ((b.blend >= 2 || b.noZWrite) != transparent) continue;
                if (transparent) ApplyBlend(gl, b.blend);
                if (b.twoSided) gl.Disable(EnableCap.CullFace); else gl.Enable(EnableCap.CullFace);
                if (b.noZTest) gl.Disable(EnableCap.DepthTest); else gl.Enable(EnableCap.DepthTest);
                if (b.tex is not null) { b.tex.Bind(0); sh.Set("uHasTexture", 1); } else sh.Set("uHasTexture", 0);
                sh.Set("uAlphaCutoff", b.blend == 1 ? 0.35f : 0f);
                sh.Set("uEnvironmentMap", b.env ? 1 : 0); sh.Set("uUnlit", b.unlit ? 1 : 0);
                sh.Set("uFogPolicy", b.env ? 1 : 0);
                unsafe { gl.DrawElements(PrimitiveType.Triangles, b.count, DrawElementsType.UnsignedShort, (void*)(b.start * sizeof(ushort))); }
            }
            if (transparent) { gl.Disable(EnableCap.Blend); gl.DepthMask(true); }
        }
    }
    gl.Enable(EnableCap.DepthTest);
    var px = new byte[fullW * fullH * 4];
    gl.Viewport(0, 0, (uint)fullW, (uint)fullH); gl.Scissor(0, 0, (uint)fullW, (uint)fullH);
    unsafe { fixed (byte* p = px) gl.ReadPixels(0, 0, (uint)fullW, (uint)fullH, PixelFormat.Bgra, PixelType.UnsignedByte, p); }
    return px;
}
using (var lit = Shader.FromFiles(gl, Path.Combine(shaderDir, "attached.vert"),
           Path.Combine("tools", "warglaive-render", "lit_env.frag")))
    WriteBmp(Path.Combine(outDir, "warglaive_lit_full.bmp"), RenderFullLit(lit), fullW, fullH);

using (var mc = Shader.FromFiles(gl, Path.Combine(shaderDir, "attached.vert"),
           Path.Combine("tools", "warglaive-render", "dbg_matcap.frag")))
    WriteBmp(Path.Combine(outDir, "dbg_matcap.bmp"), RenderDebug(mc), fullW, fullH);
using (var sp = Shader.FromFiles(gl, Path.Combine(shaderDir, "attached.vert"),
           Path.Combine("tools", "warglaive-render", "dbg_sphere.frag")))
    WriteBmp(Path.Combine(outDir, "dbg_sphere.bmp"), RenderDebug(sp), fullW, fullH);
Console.WriteLine("[render] wrote dbg_matcap / dbg_sphere equation strips");

// ── A/B GPU oracle: current Three matcap vs exact build-5875 generated UV ───
// Both use the real display-30936 env submesh, real ArmorReflect3 texture, the
// same model/view/projection matrices, and the authored SrcAlpha/One blend onto
// black.  The only changed variable is how the generated texture coordinate is
// produced.
byte[] RenderIsolatedEnvironment(Shader envShader, bool productionShader = false)
{
    gl.Viewport(0, 0, (uint)fullW, (uint)fullH);
    gl.Scissor(0, 0, (uint)fullW, (uint)fullH);
    gl.ClearColor(0f, 0f, 0f, 1f);
    gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

    envShader.Use();
    envShader.Set("uTexture", 0);
    envShader.Set("uBoneCount", 0);
    if (productionShader)
    {
        SetLighting();
        envShader.Set("uHasTexture", 1);
        envShader.Set("uEnvironmentMap", 1);
        envShader.Set("uUnlit", 0);
        envShader.Set("uFogPolicy", 1);
        envShader.Set("uAlphaCutoff", 0f);
        envShader.Set("uUvOffset", Vector2.Zero);
    }
    gl.BindVertexArray(vao);
    gl.Enable(EnableCap.DepthTest);
    gl.DepthFunc(DepthFunction.Lequal);
    gl.DepthMask(false);
    gl.Enable(EnableCap.Blend);
    gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
    gl.Disable(EnableCap.CullFace);

    for (int f = 0; f < Frames; f++)
    {
        gl.Viewport(f * FrameW, 0, (uint)FrameW, (uint)FrameH);
        gl.Scissor(f * FrameW, 0, (uint)FrameW, (uint)FrameH);
        float yaw = -1.05f + 2.10f * (f / (float)(Frames - 1));
        Matrix4x4 model = Matrix4x4.CreateTranslation(-center)
            * Matrix4x4.CreateRotationX(0.22f)
            * Matrix4x4.CreateRotationY(yaw);
        envShader.Set("uModel", model);
        envShader.Set("uModelViewProjection", model * viewProj);
        envShader.Set("uView", view);
        foreach (var b in built)
        {
            if (!b.env || b.tex is null) continue;
            b.tex.Bind(0);
            unsafe
            {
                gl.DrawElements(PrimitiveType.Triangles, b.count,
                    DrawElementsType.UnsignedShort,
                    (void*)(b.start * sizeof(ushort)));
            }
        }
    }

    gl.Disable(EnableCap.Blend);
    gl.DepthMask(true);
    var px = new byte[fullW * fullH * 4];
    gl.Viewport(0, 0, (uint)fullW, (uint)fullH);
    gl.Scissor(0, 0, (uint)fullW, (uint)fullH);
    unsafe
    {
        fixed (byte* p = px)
            gl.ReadPixels(0, 0, (uint)fullW, (uint)fullH,
                PixelFormat.Bgra, PixelType.UnsignedByte, p);
    }
    return px;
}

byte[] currentEnv = RenderIsolatedEnvironment(shader, productionShader: true);
byte[] vanillaEnv;
using (var vanilla = Shader.FromFiles(gl,
           Path.Combine("tools", "warglaive-render", "env_5875.vert"),
           Path.Combine("tools", "warglaive-render", "env_5875.frag")))
    vanillaEnv = RenderIsolatedEnvironment(vanilla);

string currentPng = Path.Combine(outDir, "env_current_matcap.png");
string vanillaPng = Path.Combine(outDir, "env_build5875_per_vertex.png");
WritePng(currentPng, currentEnv, fullW, fullH);
WritePng(vanillaPng, vanillaEnv, fullW, fullH);
WriteFrames(outDir, "current", currentEnv, fullW, fullH, FrameW, FrameH, Frames);
WriteFrames(outDir, "build5875", vanillaEnv, fullW, fullH, FrameW, FrameH, Frames);
WriteContactSheet(Path.Combine(outDir, "env_ab_contact_sheet.png"),
    currentEnv, vanillaEnv, fullW, fullH);
PrintPixelStats("current-matcap", currentEnv, fullW, fullH, FrameW, FrameH, Frames);
PrintPixelStats("build5875-per-vertex", vanillaEnv, fullW, fullH, FrameW, FrameH, Frames);
PrintComponentStats("current-matcap", currentEnv, fullW, FrameW, FrameH, Frames, 24);
PrintComponentStats("build5875-per-vertex", vanillaEnv, fullW, FrameW, FrameH, Frames, 24);
Console.WriteLine("[render] wrote env_current_matcap.png, env_build5875_per_vertex.png, " +
                  "and env_ab_contact_sheet.png");

// The same A/B with the real opaque base passes underneath.  This answers the
// actual symptom: whether the env draw adds a thin moving streak or turns a
// large part of the authored glowing blade brighter/dimmer as one unit.
byte[] RenderComposite(Shader? exactEnvShader, bool baseOnly,
    bool blanketUnlit = true, bool exactEnvUsesProductionFragment = false,
    bool targetedUnlit = false, bool onlySteadyBatches = false)
{
    gl.Viewport(0, 0, (uint)fullW, (uint)fullH);
    gl.Scissor(0, 0, (uint)fullW, (uint)fullH);
    gl.ClearColor(0.025f, 0.025f, 0.03f, 1f);
    gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));
    gl.Enable(EnableCap.DepthTest); gl.DepthFunc(DepthFunction.Lequal);
    gl.BindVertexArray(vao);

    for (int f = 0; f < Frames; f++)
    {
        gl.Viewport(f * FrameW, 0, (uint)FrameW, (uint)FrameH);
        gl.Scissor(f * FrameW, 0, (uint)FrameW, (uint)FrameH);
        float yaw = -1.05f + 2.10f * (f / (float)(Frames - 1));
        Matrix4x4 model = Matrix4x4.CreateTranslation(-center)
            * Matrix4x4.CreateRotationX(0.22f)
            * Matrix4x4.CreateRotationY(yaw);

        // Production base shader/state; the requested policy decides whether
        // lighting is blanket, authored, or targeted to classified blade batches.
        shader.Use(); SetLighting();
        shader.Set("uModel", model);
        shader.Set("uModelViewProjection", model * viewProj);
        shader.Set("uView", view); shader.Set("uBoneCount", 0);
        shader.Set("uUvOffset", Vector2.Zero); shader.Set("uBodyTint", Vector3.One);
        shader.Set("uBodyAlpha", 1f); shader.Set("uEnvironmentMap", 0);
        shader.Set("uFogPolicy", 0);
        gl.DepthMask(true); gl.Disable(EnableCap.Blend);
        foreach (var b in built)
        {
            if (b.env || b.tex is null) continue;
            if (onlySteadyBatches && !b.steadyBlade) continue;
            shader.Set("uUnlit", (blanketUnlit || b.unlit ||
                (targetedUnlit && b.steadyBlade)) ? 1 : 0);
            if (b.twoSided) gl.Disable(EnableCap.CullFace); else gl.Enable(EnableCap.CullFace);
            if (b.noZTest) gl.Disable(EnableCap.DepthTest); else gl.Enable(EnableCap.DepthTest);
            b.tex.Bind(0); shader.Set("uHasTexture", 1);
            shader.Set("uAlphaCutoff", b.blend == 1 ? 0.35f : 0f);
            unsafe { gl.DrawElements(PrimitiveType.Triangles, b.count,
                DrawElementsType.UnsignedShort, (void*)(b.start * sizeof(ushort))); }
        }
        if (baseOnly) continue;

        gl.DepthMask(false); gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
        foreach (var b in built)
        {
            if (!b.env || b.tex is null) continue;
            if (onlySteadyBatches && !b.steadyBlade) continue;
            if (b.twoSided) gl.Disable(EnableCap.CullFace); else gl.Enable(EnableCap.CullFace);
            if (b.noZTest) gl.Disable(EnableCap.DepthTest); else gl.Enable(EnableCap.DepthTest);
            b.tex.Bind(0);
            if (exactEnvShader is null)
            {
                shader.Use(); SetLighting();
                shader.Set("uModel", model);
                shader.Set("uModelViewProjection", model * viewProj);
                shader.Set("uView", view); shader.Set("uBoneCount", 0);
                shader.Set("uUvOffset", Vector2.Zero); shader.Set("uHasTexture", 1);
                shader.Set("uBodyTint", Vector3.One); shader.Set("uBodyAlpha", 1f);
                shader.Set("uEnvironmentMap", 1);
                shader.Set("uUnlit", (blanketUnlit || b.unlit ||
                    (targetedUnlit && b.steadyBlade)) ? 1 : 0);
                shader.Set("uFogPolicy", 1); shader.Set("uAlphaCutoff", 0f);
            }
            else
            {
                exactEnvShader.Use();
                if (exactEnvUsesProductionFragment) SetLightingOn(exactEnvShader);
                else exactEnvShader.Set("uTexture", 0);
                exactEnvShader.Set("uModel", model);
                exactEnvShader.Set("uModelViewProjection", model * viewProj);
                exactEnvShader.Set("uView", view); exactEnvShader.Set("uBoneCount", 0);
                if (exactEnvUsesProductionFragment)
                {
                    // uEnvironmentMap=0 is intentional: env_5875.vert has already
                    // generated vUV, so the production fragment must sample it
                    // rather than replacing it with the Three matcap coordinate.
                    exactEnvShader.Set("uCameraPos", camPos);
                    exactEnvShader.Set("uHasTexture", 1);
                    exactEnvShader.Set("uEnvironmentMap", 0);
                    exactEnvShader.Set("uUnlit", (blanketUnlit || b.unlit ||
                        (targetedUnlit && b.steadyBlade)) ? 1 : 0);
                    exactEnvShader.Set("uFogPolicy", 1);
                    exactEnvShader.Set("uAlphaCutoff", 0f);
                    exactEnvShader.Set("uBodyTint", Vector3.One);
                    exactEnvShader.Set("uBodyAlpha", 1f);
                }
            }
            unsafe { gl.DrawElements(PrimitiveType.Triangles, b.count,
                DrawElementsType.UnsignedShort, (void*)(b.start * sizeof(ushort))); }
        }
        gl.Disable(EnableCap.Blend); gl.DepthMask(true);
    }

    gl.Enable(EnableCap.DepthTest); gl.Disable(EnableCap.Blend); gl.DepthMask(true);
    var px = new byte[fullW * fullH * 4];
    gl.Viewport(0, 0, (uint)fullW, (uint)fullH);
    gl.Scissor(0, 0, (uint)fullW, (uint)fullH);
    unsafe { fixed (byte* p = px) gl.ReadPixels(0, 0, (uint)fullW, (uint)fullH,
        PixelFormat.Bgra, PixelType.UnsignedByte, p); }
    return px;
}

byte[] baseComposite = RenderComposite(null, baseOnly: true);
byte[] currentComposite = RenderComposite(null, baseOnly: false);
byte[] vanillaComposite;
using (var vanilla = Shader.FromFiles(gl,
           Path.Combine("tools", "warglaive-render", "env_5875.vert"),
           Path.Combine("tools", "warglaive-render", "env_5875.frag")))
    vanillaComposite = RenderComposite(vanilla, baseOnly: false);
WritePng(Path.Combine(outDir, "composite_base_only.png"), baseComposite, fullW, fullH);
WritePng(Path.Combine(outDir, "composite_current_matcap.png"), currentComposite, fullW, fullH);
WritePng(Path.Combine(outDir, "composite_build5875.png"), vanillaComposite, fullW, fullH);
WriteCompositeContactSheet(Path.Combine(outDir, "composite_abc_contact_sheet.png"),
    baseComposite, currentComposite, vanillaComposite, fullW, fullH);
Console.WriteLine("[render] wrote base/current/build5875 composite contact sheet");

// Lighting decision oracle: keep the exact build-5875 coordinate fixed and
// change only the material-lighting policy.
byte[] authoredLitComposite;
using (var vanillaLit = Shader.FromFiles(gl,
           Path.Combine("tools", "warglaive-render", "env_5875.vert"),
           Path.Combine(shaderDir, "character.frag")))
    authoredLitComposite = RenderComposite(vanillaLit, baseOnly: false,
        blanketUnlit: false, exactEnvUsesProductionFragment: true);
WritePng(Path.Combine(outDir, "composite_build5875_authored_lit.png"),
    authoredLitComposite, fullW, fullH);
WriteLightingContactSheet(Path.Combine(outDir, "lighting_ab_contact_sheet.png"),
    vanillaComposite, authoredLitComposite, fullW, fullH);
PrintBladeStats("A exact5875 + blanket-unlit", baseComposite, vanillaComposite,
    fullW, FrameW, FrameH, Frames);
PrintBladeStats("B exact5875 + authored-lit", baseComposite, authoredLitComposite,
    fullW, FrameW, FrameH, Frames);
Console.WriteLine("[render] wrote build5875 blanket-unlit/authored-lit oracle");

// Final production-equivalent oracle.  Row B uses the production shader pair
// loaded above and the exact same targeted classifier result carried by each
// built batch: env + opaque same-submesh base unlit, handle authored-lit.
byte[] bladeMaskComposite = RenderComposite(null, baseOnly: true,
    blanketUnlit: true, onlySteadyBatches: true);
byte[] productionTargetedComposite = RenderComposite(null, baseOnly: false,
    blanketUnlit: false, targetedUnlit: true);
byte[] preFixThreeBlanketComposite;
using (var preFixThree = Shader.FromFiles(gl,
           Path.Combine("tools", "warglaive-render", "env_5875.vert"),
           Path.Combine("tools", "warglaive-render", "env_current.frag")))
    preFixThreeBlanketComposite = RenderComposite(preFixThree, baseOnly: false,
        blanketUnlit: true);
WritePng(Path.Combine(outDir, "production_targeted_current.png"),
    productionTargetedComposite, fullW, fullH);
WritePng(Path.Combine(outDir, "prefix_three_blanket.png"),
    preFixThreeBlanketComposite, fullW, fullH);
WriteProductionOracleSheet(Path.Combine(outDir,
        "production_targeted_vs_prefix_contact_sheet.png"),
    preFixThreeBlanketComposite, productionTargetedComposite, fullW, fullH);
PrintBladeStats("pre-fix Three matcap + blanket-unlit", bladeMaskComposite,
    preFixThreeBlanketComposite, fullW, FrameW, FrameH, Frames);
PrintBladeStats("CURRENT production exact5875 + targeted blade-unlit", bladeMaskComposite,
    productionTargetedComposite, fullW, FrameW, FrameH, Frames);
Console.WriteLine("[render] wrote final production-targeted vs pre-fix oracle");

// 32-bit BGRA, bottom-up (GL readback is already bottom-up) BMP.
static void WriteBmp(string path, byte[] bgra, int w, int h)
{
    using var fs = new FileStream(path, FileMode.Create);
    using var bw = new BinaryWriter(fs);
    int imgSize = w * h * 4, fileSize = 54 + imgSize;
    bw.Write((byte)'B'); bw.Write((byte)'M'); bw.Write(fileSize);
    bw.Write(0); bw.Write(54);
    bw.Write(40); bw.Write(w); bw.Write(h); bw.Write((short)1); bw.Write((short)32);
    bw.Write(0); bw.Write(imgSize); bw.Write(2835); bw.Write(2835); bw.Write(0); bw.Write(0);
    bw.Write(bgra, 0, imgSize);
}

static SKBitmap BitmapFromGlBgra(byte[] bottomUpBgra, int w, int h)
{
    var topDown = new byte[bottomUpBgra.Length];
    for (int y = 0; y < h; y++)
        System.Buffer.BlockCopy(bottomUpBgra, y * w * 4,
            topDown, (h - 1 - y) * w * 4, w * 4);
    var bitmap = new SKBitmap(new SKImageInfo(w, h,
        SKColorType.Bgra8888, SKAlphaType.Unpremul));
    System.Runtime.InteropServices.Marshal.Copy(topDown, 0,
        bitmap.GetPixels(), topDown.Length);
    return bitmap;
}

static void WritePng(string path, byte[] bgra, int w, int h)
{
    using SKBitmap bitmap = BitmapFromGlBgra(bgra, w, h);
    using SKImage image = SKImage.FromBitmap(bitmap);
    using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);
    using FileStream stream = File.Create(path);
    encoded.SaveTo(stream);
}

static void WriteFrames(string outDir, string prefix, byte[] strip,
    int stripW, int stripH, int frameW, int frameH, int frames)
{
    using SKBitmap source = BitmapFromGlBgra(strip, stripW, stripH);
    for (int f = 0; f < frames; f++)
    {
        using var frame = new SKBitmap(new SKImageInfo(frameW, frameH,
            SKColorType.Bgra8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(frame))
        {
            canvas.Clear(SKColors.Black);
            canvas.DrawBitmap(source,
                new SKRect(f * frameW, 0, (f + 1) * frameW, frameH),
                new SKRect(0, 0, frameW, frameH));
        }
        using SKImage image = SKImage.FromBitmap(frame);
        using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream stream = File.Create(Path.Combine(outDir,
            $"env_{prefix}_{f:D2}.png"));
        encoded.SaveTo(stream);
    }
}

static void WriteContactSheet(string path, byte[] current, byte[] vanilla,
    int stripW, int stripH)
{
    const int labelH = 46;
    using SKBitmap a = BitmapFromGlBgra(current, stripW, stripH);
    using SKBitmap b = BitmapFromGlBgra(vanilla, stripW, stripH);
    using var sheet = new SKBitmap(new SKImageInfo(stripW, stripH * 2 + labelH * 2,
        SKColorType.Bgra8888, SKAlphaType.Unpremul));
    using var canvas = new SKCanvas(sheet);
    canvas.Clear(SKColors.Black);
    using var font = new SKFont(SKTypeface.Default, 25);
    using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
    canvas.DrawText("A — current MSUIClient / Three per-fragment matcap", 12, 31, font, paint);
    canvas.DrawBitmap(a, 0, labelH);
    canvas.DrawText("B — exact build 5875 / per-vertex reflected-position UV", 12,
        stripH + labelH + 31, font, paint);
    canvas.DrawBitmap(b, 0, stripH + labelH * 2);
    using SKImage image = SKImage.FromBitmap(sheet);
    using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);
    using FileStream stream = File.Create(path);
    encoded.SaveTo(stream);
}

static void WriteCompositeContactSheet(string path, byte[] baseOnly, byte[] current,
    byte[] vanilla, int stripW, int stripH)
{
    const int labelH = 42;
    using SKBitmap a = BitmapFromGlBgra(baseOnly, stripW, stripH);
    using SKBitmap b = BitmapFromGlBgra(current, stripW, stripH);
    using SKBitmap c = BitmapFromGlBgra(vanilla, stripW, stripH);
    using var sheet = new SKBitmap(new SKImageInfo(stripW, stripH * 3 + labelH * 3,
        SKColorType.Bgra8888, SKAlphaType.Unpremul));
    using var canvas = new SKCanvas(sheet); canvas.Clear(SKColors.Black);
    using var font = new SKFont(SKTypeface.Default, 24);
    using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
    string[] labels = {
        "A — authored base only (production effect-weapon state)",
        "B — base + current MSUIClient per-fragment matcap",
        "C — base + exact build-5875 per-vertex reflected-position UV"
    };
    SKBitmap[] bitmaps = { a, b, c };
    for (int row = 0; row < 3; row++)
    {
        int y = row * (stripH + labelH);
        canvas.DrawText(labels[row], 12, y + 29, font, paint);
        canvas.DrawBitmap(bitmaps[row], 0, y + labelH);
    }
    using SKImage image = SKImage.FromBitmap(sheet);
    using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);
    using FileStream stream = File.Create(path); encoded.SaveTo(stream);
}

static void WriteLightingContactSheet(string path, byte[] blanketUnlit,
    byte[] authoredLit, int stripW, int stripH)
{
    const int labelH = 42;
    using SKBitmap a = BitmapFromGlBgra(blanketUnlit, stripW, stripH);
    using SKBitmap b = BitmapFromGlBgra(authoredLit, stripW, stripH);
    using var sheet = new SKBitmap(new SKImageInfo(stripW, stripH * 2 + labelH * 2,
        SKColorType.Bgra8888, SKAlphaType.Unpremul));
    using var canvas = new SKCanvas(sheet); canvas.Clear(SKColors.Black);
    using var font = new SKFont(SKTypeface.Default, 24);
    using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
    canvas.DrawText("A — exact 5875 env + current blanket-unlit weapon", 12, 29, font, paint);
    canvas.DrawBitmap(a, 0, labelH);
    canvas.DrawText("B — exact 5875 env + authored uUnlit only (base and env lit)",
        12, stripH + labelH + 29, font, paint);
    canvas.DrawBitmap(b, 0, stripH + labelH * 2);
    using SKImage image = SKImage.FromBitmap(sheet);
    using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);
    using FileStream stream = File.Create(path); encoded.SaveTo(stream);
}

static void WriteProductionOracleSheet(string path, byte[] preFix,
    byte[] currentTargeted, int stripW, int stripH)
{
    const int labelH = 48;
    using SKBitmap a = BitmapFromGlBgra(preFix, stripW, stripH);
    using SKBitmap b = BitmapFromGlBgra(currentTargeted, stripW, stripH);
    using var sheet = new SKBitmap(new SKImageInfo(stripW, stripH * 2 + labelH * 2,
        SKColorType.Bgra8888, SKAlphaType.Unpremul));
    using var canvas = new SKCanvas(sheet); canvas.Clear(SKColors.Black);
    using var font = new SKFont(SKTypeface.Default, 23);
    using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
    canvas.DrawText("A — PRE-FIX: Three per-fragment matcap + blanket-unlit whole weapon",
        12, 31, font, paint);
    canvas.DrawBitmap(a, 0, labelH);
    canvas.DrawText("B — CURRENT PRODUCTION: exact-5875 UV + targeted blade-unlit; handle authored-lit",
        12, stripH + labelH + 31, font, paint);
    canvas.DrawBitmap(b, 0, stripH + labelH * 2);
    using SKImage image = SKImage.FromBitmap(sheet);
    using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);
    using FileStream stream = File.Create(path); encoded.SaveTo(stream);
}

static void PrintBladeStats(string name, byte[] maskSource, byte[] image,
    int stripW, int frameW, int frameH, int frames)
{
    var means = new double[frames];
    var brightFractions = new double[frames];
    Console.WriteLine($"[blade-lighting] {name}");
    for (int f = 0; f < frames; f++)
    {
        int count = 0, bright = 0;
        long lumSum = 0;
        for (int y = 0; y < frameH; y++)
        for (int x = 0; x < frameW; x++)
        {
            int i = (y * stripW + f * frameW + x) * 4;
            int maskLum = (maskSource[i + 2] * 54 + maskSource[i + 1] * 183 +
                maskSource[i] * 19) >> 8;
            if (maskLum <= 10) continue; // exclude the 0.025 background
            int lum = (image[i + 2] * 54 + image[i + 1] * 183 + image[i] * 19) >> 8;
            count++; lumSum += lum;
            if (lum >= 160) bright++;
        }
        means[f] = count > 0 ? lumSum / (double)count : 0;
        brightFractions[f] = count > 0 ? bright / (double)count : 0;
        Console.WriteLine($"  f{f:D2}: bladePx={count,5} meanLum={means[f],6:F1} " +
            $"bright160={brightFractions[f],6:P1}");
    }

    static double StdDev(double[] values)
    {
        double mean = values.Average();
        return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / values.Length);
    }
    Console.WriteLine($"  summary: meanLum avg={means.Average():F1} " +
        $"range={means.Max() - means.Min():F1} sd={StdDev(means):F1}; " +
        $"bright160 avg={brightFractions.Average():P1} " +
        $"range={brightFractions.Max() - brightFractions.Min():P1} " +
        $"sd={StdDev(brightFractions):P1}");
}

static void PrintPixelStats(string name, byte[] px, int stripW, int stripH,
    int frameW, int frameH, int frames)
{
    Console.WriteLine($"[pixels] {name}");
    for (int f = 0; f < frames; f++)
    {
        int lit = 0, bright = 0, minX = frameW, minY = frameH, maxX = -1, maxY = -1;
        long sum = 0; int peak = 0;
        for (int y = 0; y < frameH; y++)
        for (int x = 0; x < frameW; x++)
        {
            int i = (y * stripW + f * frameW + x) * 4;
            int lum = (px[i + 2] * 54 + px[i + 1] * 183 + px[i] * 19) >> 8;
            peak = Math.Max(peak, lum);
            if (lum > 2)
            {
                lit++; sum += lum;
                minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
            }
            if (lum >= 48) bright++;
        }
        Console.WriteLine($"  f{f:D2}: lit={lit,5} bright48={bright,5} peak={peak,3} " +
            $"mean={(lit > 0 ? sum / (double)lit : 0):F1} " +
            $"bbox={(maxX >= 0 ? $"{maxX - minX + 1}x{maxY - minY + 1}" : "none")}");
    }
}

static void PrintComponentStats(string name, byte[] px, int stripW,
    int frameW, int frameH, int frames, int threshold)
{
    Console.WriteLine($"[components] {name} threshold={threshold}");
    int[] queue = new int[frameW * frameH];
    for (int f = 0; f < frames; f++)
    {
        var active = new bool[frameW * frameH];
        var seen = new bool[active.Length];
        for (int y = 0; y < frameH; y++)
        for (int x = 0; x < frameW; x++)
        {
            int i = (y * stripW + f * frameW + x) * 4;
            int lum = (px[i + 2] * 54 + px[i + 1] * 183 + px[i] * 19) >> 8;
            active[y * frameW + x] = lum >= threshold;
        }

        int bestArea = 0, bestW = 0, bestH = 0;
        for (int seed = 0; seed < active.Length; seed++)
        {
            if (!active[seed] || seen[seed]) continue;
            int head = 0, tail = 0, area = 0;
            int minX = frameW, minY = frameH, maxX = -1, maxY = -1;
            queue[tail++] = seed; seen[seed] = true;
            while (head < tail)
            {
                int p = queue[head++]; int x = p % frameW, y = p / frameW;
                area++; minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx, ny = y + dy;
                    if ((uint)nx >= (uint)frameW || (uint)ny >= (uint)frameH) continue;
                    int n = ny * frameW + nx;
                    if (!active[n] || seen[n]) continue;
                    seen[n] = true; queue[tail++] = n;
                }
            }
            if (area > bestArea)
            {
                bestArea = area; bestW = maxX - minX + 1; bestH = maxY - minY + 1;
            }
        }
        int major = Math.Max(bestW, bestH), minor = Math.Max(1, Math.Min(bestW, bestH));
        double aspect = major / (double)minor;
        double fill = bestW * bestH > 0 ? bestArea / (double)(bestW * bestH) : 0;
        Console.WriteLine($"  f{f:D2}: largest={bestArea,5} bbox={bestW}x{bestH} " +
            $"aspect={aspect:F2} fill={fill:P1}");
    }
}
