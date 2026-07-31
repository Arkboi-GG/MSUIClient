using System.Numerics;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Formats;
using Shader = MSUIClient.Engine.Shader;
using Texture = MSUIClient.Engine.Texture;

namespace MSUIClient.World.Units;

/// <summary>
/// Draws the equipment that has geometry of its own - helms, shoulders,
/// weapons and shields. Cape cloth is part of the character M2 and is handled
/// by CharacterRenderer's type-2 replaceable texture slot.
///
/// WHY THESE ARE NOT LIKE THE REST OF THE GEAR
///   A breastplate is paint on the character's own skin. A helm is a SEPARATE
///   M2 FILE that gets parented to a bone. Nothing about the body atlas or the
///   geoset rules touches these, which is why a fully-working Tier set can
///   still be missing its pauldrons.
///
/// HOW A MODEL FINDS ITS PLACE
///   Every M2 carries an Attachments array: a semantic id, a bone index and a
///   position in model space. M2Reader already parses it. The skinning matrix
///   the animator computes for that bone is exactly the transform a rigid point
///   attached to it needs, so the whole placement is:
///
///       item vertex -> T(attachment.Position) -> Skin[attachment.BoneIndex]
///                   -> the character's own instance matrix
///
///   which means an attached model follows the animation for free. No separate
///   bone chain, no second skinning path. The item M2s themselves are drawn
///   unskinned - a sword does not bend.
///
/// ATTACHMENT IDS come from SuperUI's equip.js, where they were established by
/// eye rather than from a table:
///     0  LeftWrist   - shields mount HERE, not on the palm
///     1  HandRight
///     2  HandLeft
///     5  ShoulderRight  (ModelName2, the R file)
///     6  ShoulderLeft   (ModelName1, the L file)
///    11  Helm
/// </summary>
public sealed class AttachedItemRenderer : IDisposable
{
    public const int AttachLeftWrist = 0;
    public const int AttachHandRight = 1;
    public const int AttachHandLeft = 2;
    public const int AttachShoulderRight = 5;
    public const int AttachShoulderLeft = 6;
    public const int AttachHelm = 11;
    public const int AttachBackRight = 26;
    public const int AttachBackLeft = 27;
    public const int AttachShieldBack = 28;
    public const int AttachBackLowerMain = 30;
    public const int AttachBackLowerOff = 31;
    public const int AttachHipMain = 32;
    public const int AttachHipOff = 33;

    /// <summary>Position(3) Normal(3) UV(2) - the doodad layout, since nothing here is skinned.</summary>
    private const int FloatsPerVertex = 8;

    internal sealed class Batch
    {
        public uint IndexStart;
        public uint IndexCount;
        public Texture? Texture;
        public bool TwoSided;
        public int BlendMode;
        public bool NoZWrite;
        public bool Transparent => BlendMode >= 2 || NoZWrite;
    }

    internal sealed class ItemModel : IDisposable
    {
        public uint Vao, Vbo, Ebo;
        public List<Batch> Batches = [];
        public string Path = "";

        private GL? _gl;
        public void Attach(GL gl) => _gl = gl;

        public void Dispose()
        {
            if (_gl is null) return;
            if (Vao != 0) _gl.DeleteVertexArray(Vao);
            if (Vbo != 0) _gl.DeleteBuffer(Vbo);
            if (Ebo != 0) _gl.DeleteBuffer(Ebo);
        }
    }

    /// <summary>One mounted piece: a model, where it hangs, and what it is called.</summary>
    internal sealed class Mount
    {
        public ItemModel Model = null!;
        public int AttachmentId;
        public int HeldSlot = -1;
        public int InventoryType;
        public byte ItemSheath;
        public string Label = "";
        public bool Visible = true;
    }

    public sealed class MountSet
    {
        internal readonly List<Mount> Items = [];
        public int Count => Items.Count;
    }

    private sealed class SharedResources
    {
        public required GL Gl;
        public Shader? Shader;
        public readonly Dictionary<string, ItemModel?> Models =
            new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, Texture?> Textures =
            new(StringComparer.OrdinalIgnoreCase);
        public int Owners;
    }

    private static readonly object SharedGate = new();
    private static SharedResources? s_shared;

    private readonly GL _gl;
    private readonly ClientConfig _config;
    private readonly SharedResources _shared;
    private bool _disposed;

    private Shader? Shader => _shared.Shader;
    private Dictionary<string, ItemModel?> Models => _shared.Models;
    private Dictionary<string, Texture?> Textures => _shared.Textures;
    private readonly List<Mount> _mounts = [];

    public bool Enabled { get; set; } = true;
    public int MountCount => _mounts.Count;

    /// <summary>
    /// Label and visibility of each mounted piece, so the HUD can switch them
    /// off one at a time.
    ///
    /// The geoset checkboxes cannot do this: an attached item is a separate M2,
    /// not a geoset, so hiding every geoset category leaves the helm and the
    /// sword exactly where they were. Two different mechanisms need two
    /// different switches.
    /// </summary>
    public IEnumerable<(string Label, bool Visible)> Mounts
        => _mounts.Select(m => (m.Label, m.Visible));

    public void SetMountVisible(string label, bool visible)
    {
        foreach (var mount in _mounts)
            if (mount.Label == label) mount.Visible = visible;
    }
    public int DrawnLastFrame { get; private set; }
    public byte SheathState { get; set; }

    /// <summary>Matched to the character so a pauldron lights like the shoulder under it.</summary>
    public Vector3 SunDirection { get; set; } = Vector3.Normalize(new Vector3(0.45f, 0.35f, 0.82f));
    public Vector3 SunColor { get; set; } = new(1.00f, 0.95f, 0.85f);
    public float SunIntensity { get; set; } = 1.15f;
    public Vector3 AmbientColor { get; set; } = new(0.42f, 0.50f, 0.60f);
    public float AmbientIntensity { get; set; } = 0.85f;
    public float ShadowSoftness { get; set; } = 0f;   // matches CharacterRenderer wrap (uShadowWrap); shares character.frag
    public Vector3 FogColor { get; set; } = new(0.56f, 0.71f, 0.85f);
    public float FogStart { get; set; } = 350f;
    public float FogEnd { get; set; } = 900f;
    public float AlphaCutoff { get; set; } = 0.35f;

    /// <summary>
    /// Two-letter race code plus M or F, e.g. HuM.
    ///
    /// HELMS ARE PER RACE AND GENDER and nothing else is. A helm has to fit the
    /// head it sits on, so vanilla ships one file per head shape and the name
    /// carries this suffix - Helm_Plate_A_01_HuM. Shoulders and weapons have a
    /// single file each. Missing this is the most likely reason a helm resolves
    /// to nothing while the pauldrons mount fine.
    /// </summary>
    public string RaceGenderCode { get; set; } = "HuM";

    public AttachedItemRenderer(GL gl, ClientConfig config)
    {
        _gl = gl;
        _config = config;
        lock (SharedGate)
        {
            if (s_shared is null)
                s_shared = new SharedResources { Gl = gl };
            else if (!ReferenceEquals(s_shared.Gl, gl))
                throw new InvalidOperationException(
                    "attachment resources cannot span unrelated GL contexts");
            _shared = s_shared;
            _shared.Owners++;
        }
    }

    /// <summary>
    /// Unskinned attachment shader. It keeps WMO lighting, but uses its own
    /// vertex stage so clip positions retain character-scale precision at
    /// large world coordinates.
    /// </summary>
    public void LoadShaders(string shaderDir)
    {
        lock (SharedGate)
        {
            _shared.Shader ??= MSUIClient.Engine.Shader.FromFiles(_gl,
                Path.Combine(shaderDir, "attached.vert"),
                Path.Combine(shaderDir, "character.frag"));
        }
    }

    // ── building the mount list ──────────────────────────────────────────────

    /// <summary>
    /// Which folder under Item\ObjectComponents holds a slot's models. Vanilla
    /// splits them by body part rather than by item type.
    /// </summary>
    private static string FolderFor(int inventoryType) => inventoryType switch
    {
        CharacterEquipment.Slot.Head => "Head",
        CharacterEquipment.Slot.Shoulders => "Shoulder",
        CharacterEquipment.Slot.Shield => "Shield",
        _ => "Weapon",
    };

    /// <summary>
    /// Where a slot's model hangs. A shield goes on the LEFT WRIST and not the
    /// palm, which is the sort of thing that looks like a transform bug when it
    /// is really a table lookup.
    /// </summary>
    private static int AttachmentFor(int inventoryType) => inventoryType switch
    {
        CharacterEquipment.Slot.Head => AttachHelm,
        CharacterEquipment.Slot.Shield => AttachLeftWrist,
        CharacterEquipment.Slot.OffHand => AttachHandLeft,
        _ => AttachHandRight,
    };

    public void Rebuild(CharacterEquipment equipment)
    {
        _mounts.Clear();
        if (Shader is null) return;

        BuildMounts(equipment, _mounts);
        Console.WriteLine($"[attach] {_mounts.Count} model(s) mounted");
    }

    /// <summary>
    /// Drop unattended batch-only model/texture caches after the current specimen is no longer
    /// being drawn. Normal interactive rendering never calls this.
    /// </summary>
    public void ClearVariantCache()
    {
        _mounts.Clear();
        lock (SharedGate)
        {
            foreach (ItemModel model in Models.Values.Where(model => model is not null)
                         .Select(model => model!).Distinct())
                model.Dispose();
            foreach (Texture texture in Textures.Values.Where(texture => texture is not null)
                         .Select(texture => texture!).Distinct())
                texture.Dispose();
            Models.Clear();
            Textures.Clear();
        }
    }

    public MountSet BuildMountSet(CharacterEquipment equipment)
    {
        var result = new MountSet();
        if (Shader is not null) BuildMounts(equipment, result.Items);
        Console.WriteLine($"[attach] {result.Count} model(s) mounted");
        return result;
    }

    private void BuildMounts(CharacterEquipment equipment, List<Mount> mounts)
    {

        foreach (var piece in equipment.Pieces)
        {
            if (piece.Row is null || !piece.Row.HasModel) continue;

            string folder = FolderFor(piece.InventoryType);

            if (piece.InventoryType == CharacterEquipment.Slot.Shoulders)
            {
                // Shoulders are two files: ModelName1 is the left, ModelName2
                // the right, and both are needed.
                AddMount(mounts, piece.Row.ModelName1, piece.Row.ModelTexture1, folder,
                         AttachShoulderLeft, piece.Name + " (L)");
                AddMount(mounts, piece.Row.ModelName2, piece.Row.ModelTexture2, folder,
                         AttachShoulderRight, piece.Name + " (R)");
                continue;
            }

            int heldSlot = piece.EquipmentSlot switch { 15 => 0, 16 => 1, 17 => 2, _ => -1 };
            AddMount(mounts, piece.Row.ModelName1, piece.Row.ModelTexture1, folder,
                     AttachmentFor(piece.InventoryType), piece.Name, heldSlot,
                     piece.InventoryType, piece.Sheath);
        }
    }

    private void AddMount(List<Mount> mounts, string modelName, string textureName,
        string folder, int attachmentId, string label,
        int heldSlot = -1, int inventoryType = 0, byte itemSheath = 0)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return;

        var model = ResolveModel(modelName, textureName, folder);
        if (model is null)
        {
            Console.WriteLine($"[attach] {label}: model '{modelName}' not found under {folder}");
            return;
        }

        mounts.Add(new Mount
        {
            Model = model, AttachmentId = attachmentId, Label = label,
            HeldSlot = heldSlot, InventoryType = inventoryType, ItemSheath = itemSheath,
        });
        Console.WriteLine($"[attach] {label}: {model.Path} on attachment {attachmentId}");
    }

    // ── loading ──────────────────────────────────────────────────────────────

    /// <summary>
    /// ItemDisplayInfo stores bare names like "Helm_Plate_A_01". The real file
    /// lives under Item\ObjectComponents\&lt;Folder&gt;\ and vanilla records the
    /// .mdx extension while the MPQ holds .m2 - the same swap MDDF needs. The
    /// candidates cover both, and the first hit is logged so the convention is
    /// learned rather than assumed.
    /// </summary>
    private ItemModel? ResolveModel(string modelName, string textureName, string folder)
    {
        string key = $"{folder}|{modelName}|{textureName}|" +
            (folder == "Head" ? RaceGenderCode : "");
        if (Models.TryGetValue(key, out var cached)) return cached;

        string stem = modelName.Replace('/', '\\').TrimStart('\\');
        int dot = stem.LastIndexOf('.');
        if (dot > 0) stem = stem[..dot];

        var candidates = new List<string>();

        // Helms first try the race/gender file, because for the Head folder
        // that IS the file and the bare name will simply not exist.
        if (folder == "Head")
        {
            candidates.Add($@"Item\ObjectComponents\{folder}\{stem}_{RaceGenderCode}.m2");
            candidates.Add($@"Item\ObjectComponents\{folder}\{stem}_{RaceGenderCode}.M2");
            candidates.Add($@"Item\ObjectComponents\{folder}\{stem}_{RaceGenderCode}.mdx");
            candidates.Add($@"Item\ObjectComponents\{folder}\{stem}{RaceGenderCode}.m2");
        }

        candidates.Add($@"Item\ObjectComponents\{folder}\{stem}.m2");
        candidates.Add($@"Item\ObjectComponents\{folder}\{stem}.M2");
        candidates.Add($@"Item\ObjectComponents\{folder}\{stem}.mdx");
        candidates.Add($"{stem}.m2");

        byte[]? bytes = null;
        string found = "";
        foreach (string candidate in candidates)
        {
            bytes = AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, candidate);
            if (bytes is not null) { found = candidate; break; }
        }

        if (bytes is null)
        {
            Console.WriteLine($"[attach] not found, tried: {string.Join("  ", candidates)}");
            Models[key] = null;
            return null;
        }

        var m2 = M2Reader.Parse(bytes);
        if (m2 is null || !m2.IsValid)
        {
            Models[key] = null;
            return null;
        }

        var model = BuildModel(m2, textureName, folder);
        if (model is not null) model.Path = found;
        Models[key] = model;
        return model;
    }

    private unsafe ItemModel? BuildModel(M2Model m2, string textureName, string folder)
    {
        int vertexCount = m2.Vertices.Count;
        var vertices = new float[vertexCount * FloatsPerVertex];

        for (int i = 0; i < vertexCount; i++)
        {
            var v = m2.Vertices[i];
            int o = i * FloatsPerVertex;
            vertices[o + 0] = v.PosX; vertices[o + 1] = v.PosY; vertices[o + 2] = v.PosZ;
            vertices[o + 3] = v.NormX; vertices[o + 4] = v.NormY; vertices[o + 5] = v.NormZ;
            vertices[o + 6] = v.TexU; vertices[o + 7] = v.TexV;
        }

        var indices = m2.Indices.ToArray();
        if (indices.Length < 3) return null;

        uint vao = _gl.GenVertexArray();
        _gl.BindVertexArray(vao);

        uint vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        fixed (float* p = vertices)
            _gl.BufferData(BufferTargetARB.ArrayBuffer,
                (nuint)(vertices.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);

        uint ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        fixed (ushort* p = indices)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer,
                (nuint)(indices.Length * sizeof(ushort)), p, BufferUsageARB.StaticDraw);

        const uint stride = FloatsPerVertex * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));

        _gl.BindVertexArray(0);

        var model = new ItemModel { Vao = vao, Vbo = vbo, Ebo = ebo };
        model.Attach(_gl);

        // An item M2's own texture table is usually a single type-2 slot with
        // no filename - the name comes from ItemDisplayInfo instead.
        var texture = ResolveTexture(textureName, folder);

        // Item models commonly put an opaque base and a specular/environment
        // effect pass over the exact same submesh. This renderer does not yet
        // implement those M2 effect shaders; drawing every pass as ordinary
        // opaque textured geometry makes the identical triangles z-fight.
        // Keep one base pass per submesh, preferring the authored opaque pass.
        var baseBatches = m2.Batches
            .Select((batch, index) => (batch, index))
            .GroupBy(x => x.batch.SubmeshIndex)
            .Select(group => group
                .OrderBy(x => x.batch.MaterialIndex < m2.RenderFlags.Count &&
                              m2.RenderFlags[x.batch.MaterialIndex].BlendingMode == 0 ? 0 : 1)
                .ThenBy(x => x.index)
                .First())
            .OrderBy(x => x.index)
            .ToList();

        int suppressedEffectPasses = m2.Batches.Count - baseBatches.Count;
        if (suppressedEffectPasses > 0)
            Console.WriteLine($"[attach] suppressed {suppressedEffectPasses} overlapping effect pass(es)");

        foreach (var entry in baseBatches)
        {
            var batch = entry.batch;
            if (batch.SubmeshIndex >= m2.Submeshes.Count) continue;
            var submesh = m2.Submeshes[batch.SubmeshIndex];
            if (submesh.IndexCount == 0) continue;
            if (submesh.IndexStart + submesh.IndexCount > indices.Length) continue;

            Texture? slot = texture;
            if (slot is null && batch.TextureIndex < m2.TextureLookup.Count)
            {
                int texIdx = m2.TextureLookup[batch.TextureIndex];
                if (texIdx >= 0 && texIdx < m2.Textures.Count && m2.Textures[texIdx].Filename.Length > 0)
                    slot = ResolveTexturePath(m2.Textures[texIdx].Filename);
            }

            var renderFlags = batch.MaterialIndex < m2.RenderFlags.Count
                ? m2.RenderFlags[batch.MaterialIndex]
                : null;

            model.Batches.Add(new Batch
            {
                IndexStart = submesh.IndexStart,
                IndexCount = submesh.IndexCount,
                Texture = slot,
                TwoSided = renderFlags?.TwoSided ?? false,
                BlendMode = renderFlags?.BlendingMode ?? 0,
                NoZWrite = renderFlags?.NoZWrite ?? false,
            });
        }

        if (model.Batches.Count == 0)
            model.Batches.Add(new Batch { IndexStart = 0, IndexCount = (uint)indices.Length, Texture = texture, TwoSided = true });

        return model;
    }

    private Texture? ResolveTexture(string textureName, string folder)
    {
        if (string.IsNullOrWhiteSpace(textureName)) return null;

        string stem = textureName.Replace('/', '\\').TrimStart('\\');
        if (stem.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];

        string[] candidates =
        [
            $@"Item\ObjectComponents\{folder}\{stem}.blp",
            $"{stem}.blp",
        ];

        foreach (string candidate in candidates)
        {
            var made = ResolveTexturePath(candidate);
            if (made is not null) return made;
        }

        Console.WriteLine($"[attach] texture '{textureName}' not found, tried: {string.Join("  ", candidates)}");
        return null;
    }

    private Texture? ResolveTexturePath(string blpPath)
    {
        if (Textures.TryGetValue(blpPath, out var cached)) return cached;

        var decoded = AdtTerrainReader.ReadBlpPixels(_config.ClientDataPath, blpPath);
        if (decoded is null) { Textures[blpPath] = null; return null; }

        var (bgra, w, h) = decoded.Value;

        // Same 1-bit alpha guard as everywhere else. See CharacterRenderer.
        byte maxAlpha = 0;
        for (int i = 3; i < bgra.Length; i += 4) if (bgra[i] > maxAlpha) maxAlpha = bgra[i];
        if (maxAlpha == 1)
            for (int i = 3; i < bgra.Length; i += 4) if (bgra[i] != 0) bgra[i] = 255;

        var texture = Texture.From2D(_gl, bgra, w, h);
        Textures[blpPath] = texture;
        return texture;
    }

    // ── drawing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Draw every mounted item. <paramref name="skin"/> is the animator's
    /// per-bone skinning matrices, which is what makes a sword swing with the
    /// hand instead of hovering where the hand started.
    /// </summary>
    public unsafe void Render(Camera camera, Matrix4x4 instance,
                              M2Model? character, Matrix4x4[] skin)
        => RenderMounts(camera, instance, character, skin, _mounts, SheathState);

    public unsafe void Render(Camera camera, Matrix4x4 instance,
        M2Model? character, Matrix4x4[] skin, MountSet mounts, byte sheathState)
        => RenderMounts(camera, instance, character, skin, mounts.Items, sheathState);

    private unsafe void RenderMounts(Camera camera, Matrix4x4 instance,
        M2Model? character, Matrix4x4[] skin, IReadOnlyList<Mount> mounts,
        byte sheathState)
    {
        DrawnLastFrame = 0;
        Shader? shader = Shader;
        if (!Enabled || shader is null || mounts.Count == 0 || character is null) return;

        shader.Use();
        shader.Set("uCameraPos", Vector3.Zero);
        shader.Set("uSunDirection", SunDirection);
        shader.Set("uSunColor", SunColor);
        shader.Set("uSunIntensity", SunIntensity);
        shader.Set("uAmbientColor", AmbientColor);
        shader.Set("uAmbientIntensity", AmbientIntensity);
        shader.Set("uShadowWrap", ShadowSoftness);
        shader.Set("uFogStart", FogStart);
        shader.Set("uFogEnd", FogEnd);
        shader.Set("uFogColor", FogColor);
        shader.Set("uTexture", 0);

        bool cullingOn = true;

        for (int pass = 0; pass < 2; pass++)
        {
            bool transparentPass = pass == 1;
            if (transparentPass)
            {
                _gl.DepthMask(false);
                _gl.Enable(EnableCap.Blend);
            }

            foreach (var mount in mounts)
            {
                if (!mount.Visible) continue;

                int attachmentId = ResolveAttachment(mount, sheathState);
                if (attachmentId < 0) continue;
                var attachment = FindAttachment(character, attachmentId);
                if (attachment is null) continue;

                int bone = (int)attachment.BoneIndex;
                var boneMatrix = bone >= 0 && bone < skin.Length ? skin[bone] : Matrix4x4.Identity;

                var model = Matrix4x4.CreateTranslation(attachment.Position) * boneMatrix * instance;
                shader.Set("uModel", model);
                shader.Set("uModelViewProjection", model * camera.RelativeViewProjection);

                _gl.BindVertexArray(mount.Model.Vao);

                foreach (var batch in mount.Model.Batches)
                {
                    if (batch.Transparent != transparentPass) continue;

                    if (batch.TwoSided && cullingOn) { _gl.Disable(EnableCap.CullFace); cullingOn = false; }
                    else if (!batch.TwoSided && !cullingOn) { _gl.Enable(EnableCap.CullFace); cullingOn = true; }

                    if (batch.Texture is not null)
                    {
                        batch.Texture.Bind(0);
                        shader.Set("uHasTexture", 1);
                        shader.Set("uAlphaCutoff", batch.BlendMode == 1 ? AlphaCutoff : 0f);
                    }
                    else
                    {
                        shader.Set("uHasTexture", 0);
                        shader.Set("uAlphaCutoff", 0f);
                    }

                    if (transparentPass) ApplyBlendMode(batch.BlendMode);

                    _gl.DrawElements(PrimitiveType.Triangles, batch.IndexCount,
                        DrawElementsType.UnsignedShort, (void*)(batch.IndexStart * sizeof(ushort)));
                }

                if (!transparentPass) DrawnLastFrame++;
            }

            if (transparentPass)
            {
                _gl.Disable(EnableCap.Blend);
                _gl.DepthMask(true);
            }
        }

        if (!cullingOn) _gl.Enable(EnableCap.CullFace);
        _gl.BindVertexArray(0);
    }

    private static int ResolveAttachment(Mount mount, byte sheathState)
    {
        if (mount.HeldSlot < 0) return mount.AttachmentId;

        // Ranged slot: bows use the left hand, all other ranged families the
        // right, and vanilla detaches the model entirely while it is stowed.
        if (mount.HeldSlot == 2)
            return sheathState == 2
                ? mount.InventoryType == 15 ? AttachHandLeft : AttachHandRight
                : -1;

        if (sheathState == 1)
            return mount.HeldSlot == 0 ? AttachHandRight
                : mount.InventoryType == CharacterEquipment.Slot.Shield ? AttachLeftWrist
                : AttachHandLeft;

        return mount.ItemSheath switch
        {
            1 => mount.HeldSlot == 0 ? AttachBackRight : AttachBackLeft,
            2 => mount.HeldSlot == 0 ? AttachBackLowerMain : AttachBackLowerOff,
            3 => mount.HeldSlot == 0 ? AttachHipMain : AttachHipOff,
            4 => AttachShieldBack,
            _ => -1,
        };
    }

    private void ApplyBlendMode(int mode)
    {
        switch (mode)
        {
            case 3:
                _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
                break;
            case 4:
                _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
                break;
            case 5:
                _gl.BlendFunc(BlendingFactor.DstColor, BlendingFactor.Zero);
                break;
            case 6:
                _gl.BlendFunc(BlendingFactor.DstColor, BlendingFactor.SrcColor);
                break;
            default:
                _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                break;
        }
    }

    private static M2Attachment? FindAttachment(M2Model model, int id)
    {
        foreach (var attachment in model.Attachments)
            if (attachment.Id == (uint)id) return attachment;
        return null;
    }

    /// <summary>Print what the character model actually offers, so a missing mount is readable.</summary>
    public static void ReportAttachments(M2Model model)
    {
        var ids = model.Attachments.Select(a => $"{a.Id}(bone {a.BoneIndex})");
        Console.WriteLine($"[attach] character offers {model.Attachments.Count} point(s): {string.Join(" ", ids)}");
    }

    public void Dispose()
    {
        _mounts.Clear();
        lock (SharedGate)
        {
            if (_disposed) return;
            _disposed = true;
            _shared.Owners--;
            if (_shared.Owners != 0) return;
            foreach (var model in _shared.Models.Values) model?.Dispose();
            foreach (var texture in _shared.Textures.Values) texture?.Dispose();
            _shared.Models.Clear();
            _shared.Textures.Clear();
            _shared.Shader?.Dispose();
            _shared.Shader = null;
            if (ReferenceEquals(s_shared, _shared)) s_shared = null;
        }
    }
}
