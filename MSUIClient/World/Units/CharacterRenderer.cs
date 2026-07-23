using System.Numerics;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Formats;
using Shader = MSUIClient.Engine.Shader;
using Texture = MSUIClient.Engine.Texture;

namespace MSUIClient.World.Units;

/// <summary>
/// Draws one skinned character. This is the unit renderer, not a player
/// renderer - every NPC, mob and other player in Phase 2 goes through the same
/// path, which is why the state it consumes is a plain <see cref="UnitState"/>
/// rather than a CharacterController.
///
/// SAME SHAPE AS DoodadRenderer, DELIBERATELY
///   Model out of the MPQs, one VAO, one draw per visible submesh, textures via
///   batch.TextureIndex -> TextureLookup -> Textures. It reuses wmo.frag
///   unchanged so a character lights and fogs exactly like the ground it stands
///   on. Only the vertex shader is new, because only skinning is new.
///
/// WHAT MAKES A CHARACTER DIFFERENT FROM A DOODAD
///   1. It has a skeleton, so vertices are transformed by bone matrices before
///      the instance matrix. See M2Animator.
///   2. Its submeshes are GEOSETS, and they are not all meant to be visible at
///      once. Draw them all and the character wears every hairstyle in the file
///      simultaneously. The naked-default rules below come from the SuperUI
///      character viewer's geoset-rules.js, verified against HumanMale.
///   3. Most of its texture slots carry NO FILENAME. A character M2 expects the
///      application to supply the body skin, hair and cape images; only Type 0
///      slots name a BLP. That is the whole reason CharacterSkinCompositor
///      exists in SuperUI, and it is the part this class approximates for now.
///
/// THE MODEL-TO-WORLD BASIS IS NOT OPTIONAL, EVEN THOUGH DOODADS APPEAR NOT TO
/// HAVE ONE
///   The handbook says an M2's render vertices need no basis. That is true for
///   a doodad only because ADT placement space is itself Y-up, so the
///   placement-to-world conversion carries the flip. A character has no ADT
///   placement, so this class applies that conversion's LINEAR PART directly:
///
///       (x, y, z) -> (-z, -x, y)
///
///   which is exactly PlacementToWorld with the map-corner translation removed.
///
/// AND THE HEADING OFFSET IS A KNOB, NOT A CONSTANT
///   Which model-space axis a character faces along is the one thing here that
///   cannot be settled by arithmetic - a bounding box is invariant under a half
///   turn, so no scorer can catch a backwards character. The derivation says
///   Yaw + 90 degrees. It is exposed as a live slider so one run and one number
///   settles it, instead of a rebuild per guess. The debug capsule already
///   draws a facing spike; line the model up with that.
/// </summary>
public sealed class CharacterRenderer : IDisposable
{
    /// <summary>Position(3) Normal(3) UV(2) BoneWeights(4) BoneIndices(4).</summary>
    private const int FloatsPerVertex = 16;

    /// <summary>
    /// AnimationData.dbc IDs baked at load. Locomotion plus the airborne set.
    /// Missing entries are normal and handled by fallback chains in
    /// <see cref="ChooseClip"/> - not every model has every animation.
    /// </summary>
    private static readonly int[] BakedAnimations =
        [0, 4, 5, 11, 12, 13, 37, 38, 39, 40, 92, 93];

    /// <summary>
    /// Geoset variant shown per category when nothing is equipped. Ported from
    /// the character viewer's geoset-rules.js NAKED_DEFAULTS, which was verified
    /// against a HumanMale reference capture. Categories absent from this table
    /// are hidden; category 0 is special-cased (base body plus one hairstyle).
    /// </summary>
    private static readonly Dictionary<int, int> NakedDefaults = new()
    {
        [1] = 1,    // face chin
        [2] = 1,    // face jaw
        [3] = 1,    // face mouth
        [4] = 1,    // bare hands
        [5] = 1,    // bare shins / no boots
        [7] = 2,    // ears  (variant 2 is the normal ear, variant 1 is minimal)
        [13] = 1,   // bare pants
        [15] = 1,   // no cape
        [32] = 1,   // face geometry
    };

    /// <summary>ADT placement space to world, linear part only: (x,y,z) -> (-z,-x,y).</summary>
    private static readonly Matrix4x4 ModelToWorld = new(
         0f, -1f, 0f, 0f,
         0f, 0f, 1f, 0f,
        -1f, 0f, 0f, 0f,
         0f, 0f, 0f, 1f);

    /// <summary>State the renderer needs about a unit. Player today, packets tomorrow.</summary>
    public struct UnitState
    {
        public Vector3 Position;
        public float Yaw;
        public bool Grounded;
        public float VerticalVelocity;
        public bool Walking;
        public bool Flying;
    }

    private enum SlotFill { Bound, BodySkin, Unbound }

    private sealed class Slot
    {
        public Texture? Texture;
        public float AlphaCutoff = 0.35f;
        public SlotFill Fill = SlotFill.Unbound;
        public uint Type;
        public string Source = "";
    }

    private sealed class Piece
    {
        public uint IndexStart;
        public uint IndexCount;
        public int SlotIndex = -1;
        public bool TwoSided;
        public int GeosetId;
        public int Category;
        public int Variant;
        public bool Visible;
        public int SubmeshIndex;
        public int BatchIndex;
        public sbyte PriorityPlane;
        public ushort MaterialLayer;

        /// <summary>
        /// M2 blend mode. 0 opaque, 1 alpha-key, 2 alpha, 3+ additive and the
        /// modulate variants. TWO AND ABOVE ARE TRANSPARENT and must be drawn
        /// in a second pass with depth writes off.
        /// </summary>
        public int BlendMode;

        public bool NoZWrite;
        public bool Transparent => BlendMode >= 2 || NoZWrite;
    }

    private readonly GL _gl;
    private readonly ClientConfig _config;

    private Shader _shader = null!;
    private uint _vao, _vbo, _ebo;

    private M2Model? _m2;
    private M2Animator? _animator;
    private readonly List<Piece> _pieces = [];
    private readonly List<Slot> _slots = [];
    private Texture? _magenta;

    /// <summary>
    /// The base skin BGRA is kept because equipment is composited ONTO it, and
    /// re-equipping has to start from bare skin rather than from whatever was
    /// painted last time.
    /// </summary>
    private byte[]? _baseSkin;
    private int _skinWidth, _skinHeight;
    private float _skinCutoff = 0.35f;
    private int _bodySlotIndex = -1;
    private Texture? _bareSkin;
    private Texture? _dressedSkin;

    private ItemDisplayTable? _itemDisplay;
    private CharSectionsTable? _charSections;
    private CharHairGeosetsTable? _charHairGeosets;

    /// <summary>
    /// Character-creation appearance choices. In a real login these arrive in
    /// the four appearance bytes of the character record; until then they are
    /// knobs, because flipping through them is the fastest way to prove the
    /// CharSections lookup is finding real rows rather than falling through.
    /// </summary>
    public int SkinId { get; set; }
    public int FaceId { get; set; }
    // Until login supplies the real appearance byte, keep the Human male style
    // used by the original test character. CharHairGeosets maps style 9 to
    // geoset 10; style 0 is the valid but bald/default scalp.
    public int HairStyleId { get; set; } = 9;
    public int HairColorId { get; set; }
    public int FacialHairId { get; set; }
    private AttachedItemRenderer? _attached;

    /// <summary>Helms, shoulders, weapons and shields. Null until shaders load.</summary>
    public AttachedItemRenderer? Attached => _attached;

    /// <summary>What this character is wearing. Populate it, then call ApplyEquipment.</summary>
    public CharacterEquipment Equipment { get; set; } = new();

    private Matrix4x4[] _skin = [];
    private float[] _packed = [];

    private M2Animator.Clip? _clip;
    private float _clipTime;
    private float _globalTime;
    private float _clipRate = 1f;

    private Vector3 _lastPosition;
    private bool _hasLastPosition;
    private float _groundSpeed;
    private float _forwardness;
    private float _sideness;
    private float _moveYaw;

    /// <summary>Below this the character counts as standing still, in yards per second.</summary>
    private const float MoveThreshold = 0.3f;

    /// <summary>
    /// Categories forced off, for finding which geoset is doubling up. Drag
    /// through them in the HUD: whatever disappears is what was fighting.
    /// </summary>
    public HashSet<int> HiddenCategories { get; } = [];

    /// <summary>Category and variant of every geoset currently drawn.</summary>
    public List<(int Category, int Variant)> ActiveGeosets { get; private set; } = [];

    /// <summary>
    /// Hide the hairstyle without hiding the body.
    ///
    /// The per-category checkboxes cannot do this, and that is a flaw in them:
    /// category 0 holds the BASE BODY at variant 0 and every hairstyle at the
    /// others, so unticking category 0 to test whether hair is fighting the
    /// helm removes the entire character. Hair needs its own switch.
    /// </summary>
    public bool HideHair { get; set; }

    /// <summary>
    /// Draw ONE geoset and nothing else. -1 draws them all normally.
    ///
    /// Hiding categories was not decisive because z-fighting needs both halves
    /// present to show, so switching one off tells you a pair stopped fighting
    /// but not which pair. Soloing inverts that: step through the eleven drawn
    /// geosets one at a time, and the one that flickers ON ITS OWN is either
    /// self-overlapping or fighting something outside the geoset list entirely.
    /// If NONE of them flickers alone, the fight is between two of them and the
    /// index where it starts is the second half of the answer.
    /// </summary>
    public int SoloGeoset { get; set; } = -1;

    // ── knobs ────────────────────────────────────────────────────────────────

    public bool Enabled { get; set; } = true;

    /// <summary>Bind pose, no animation. First thing to try if the model looks folded.</summary>
    public bool BindPose { get; set; }

    /// <summary>
    /// Set when the model has more bones than the shader can hold. Animation is
    /// then refused outright rather than run on a truncated skeleton.
    ///
    /// This exists because the truncated version is WORSE than no animation and
    /// much harder to read: clamping the missing bones onto the last valid one
    /// looks perfect in bind pose and like a folded paper alien in motion, with
    /// nothing on screen pointing at the bone table. A parse or capacity failure
    /// must never present later as something that looks like a maths bug.
    /// </summary>
    public bool BoneOverflow { get; private set; }

    /// <summary>Draw every geoset. Produces the "all hairstyles at once" blob, on purpose.</summary>
    public bool ShowAllGeosets { get; set; }

    /// <summary>Paint texture slots we could not resolve magenta instead of falling back to skin.</summary>
    public bool MagentaUnbound { get; set; }

    /// <summary>Degrees added to the unit's yaw before the model-to-world basis. See class doc.</summary>
    public float HeadingOffsetDegrees { get; set; } = 90f;

    public float ModelScale { get; set; } = 1f;

    /// <summary>Vertical nudge, in yards. The M2 origin should already be at the feet.</summary>
    public float ZOffset { get; set; }

    /// <summary>
    /// How a character that is travelling sideways is made to look right.
    ///
    /// WholeBody turns the entire model to face the direction of travel and
    /// plays the ordinary run cycle. Simple, reads cleanly, and correct for the
    /// common case: the camera still sits behind where you are FACING, so
    /// strafing right shows you the character's side while he runs.
    ///
    /// LowerBody turns only the hips and legs and leaves the torso facing
    /// forward. Closer to what the real client does and more work: it needs the
    /// right bone, which is what TwistBone and the diagnostics around it exist
    /// for.
    ///
    /// Clips picks a separate sideways animation and rotates nothing. This is
    /// the version that looks like a dance step at running speed, kept only so
    /// the three are one click apart.
    /// </summary>
    public enum StrafeStyle { Split, WholeBody, LowerBody, Clips }

    /// <summary>
    /// Split is the real one and the default. The other three are the earlier
    /// attempts, kept because they are the ends of the same slider: TorsoFollow
    /// at 1.0 IS WholeBody and at 0.0 IS LowerBody.
    /// </summary>
    public StrafeStyle Strafe { get; set; } = StrafeStyle.Split;

    /// <summary>
    /// How much of the strafe angle the TORSO keeps, 0 to 1. The legs always
    /// take the full angle.
    ///
    /// Strafing is not "turn the body" and it is not "turn the legs" - it is
    /// both, at different angles. Nico measured the real client by eye at about
    /// ninety degrees on the legs against sixty on the torso, so roughly two
    /// thirds. WoWee's renderer carries the matching hook,
    /// setInstanceTorsoYaw(id, deltaYawRad) with a per-instance
    /// torsoYawOverrideRad - a DELTA on the torso over whatever the body is
    /// already doing.
    ///
    /// The exact constant lives in their character_renderer.cpp, which I have
    /// not seen, so this is a slider set to his measurement rather than a
    /// number I am pretending to know.
    /// </summary>
    public float TorsoFollow { get; set; } = 0.66f;

    /// <summary>Where the torso half of the strafe applies. Forwarded to the animator.</summary>
    public int TorsoBone
    {
        get => _animator?.TorsoBone ?? -1;
        set { if (_animator is not null) _animator.TorsoBone = value; }
    }

    /// <summary>
    /// Ceiling on the twist, in degrees. A hundred is already past what a hip
    /// can do; beyond it the legs visibly detach from the body.
    /// </summary>
    public float MaxTwistDegrees { get; set; } = 100f;

    /// <summary>
    /// Hold the strafe angle fixed regardless of movement. Zero means off and
    /// the animation drives it as normal.
    ///
    /// THIS IS THE TEST, not a feature, and it is what turned "did nothing"
    /// into an answer. Stand still and drag it: in WholeBody the model should
    /// swing to face the angle, in LowerBody the legs should and the torso
    /// should not. Whichever half moves tells you which half the code is
    /// actually acting on, with nothing depending on the trigger firing.
    /// </summary>
    public float ForceAngleDegrees { get; set; }

    /// <summary>Where the twist stops. Forwarded to the animator so the HUD can adjust it live.</summary>
    public int TwistBone
    {
        get => _animator?.TwistBone ?? -1;
        set { if (_animator is not null) _animator.TwistBone = value; }
    }

    /// <summary>
    /// True twists the hip bone's subtree, which is the lower body. False
    /// restores the original scheme - twist everything, cancel at that bone -
    /// which rotates the UPPER body instead. Kept so the two are one click
    /// apart if a rig turns out to be the other shape.
    /// </summary>
    public bool TwistSubtree
    {
        get => _animator?.TwistSubtree ?? true;
        set { if (_animator is not null) _animator.TwistSubtree = value; }
    }

    public float MoveYawDegrees => _moveYaw * 180f / MathF.PI;

    public string Race { get; private set; } = "Human";
    public string Gender { get; private set; } = "Male";

    // ── diagnostics ──────────────────────────────────────────────────────────

    public bool Loaded => _m2 is not null;
    public string ModelPath { get; private set; } = "";
    public int BoneCount => _animator?.BoneCount ?? 0;
    public int ClipCount => _animator?.Clips.Count ?? 0;
    public int PieceCount => _pieces.Count;
    public int VisiblePieces { get; private set; }
    public int UnboundSlots { get; private set; }
    public string ClipName => _clip?.Name ?? "(bind pose)";
    public bool ClipLooping => _clip?.Looping ?? true;
    public float ClipTime => _clipTime;
    public float ClipDuration => _clip?.DurationSeconds ?? 0f;
    public float ClipRate => _clipRate;
    public float GroundSpeed => _groundSpeed;
    public string SkinTexturePath { get; private set; } = "";

    public CharacterRenderer(GL gl, ClientConfig config)
    {
        _gl = gl;
        _config = config;
    }

    /// <summary>
    /// character.vert is new because skinning is new. The fragment stage is
    /// wmo.frag UNCHANGED - sharing the file is what guarantees a character
    /// cannot light differently from the world around it.
    ///
    /// Note it is still a SEPARATE GL PROGRAM from the WMO and doodad ones, so
    /// its uniforms must be set independently. Forgetting uAlphaCutoff on the
    /// doodad program once turned every tree into a black rectangle.
    /// </summary>
    public void LoadShaders(string shaderDir)
    {
        _shader = Shader.FromFiles(_gl,
            Path.Combine(shaderDir, "character.vert"),
            Path.Combine(shaderDir, "character.frag"));

        _attached = new AttachedItemRenderer(_gl, _config);
        _attached.LoadShaders(shaderDir);
    }

    // ── loading ──────────────────────────────────────────────────────────────

    public bool Load(string race, string gender)
    {
        Race = race;
        Gender = gender;

        byte[]? bytes = null;
        foreach (string candidate in ModelPathCandidates(race, gender))
        {
            bytes = AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, candidate);
            if (bytes is not null)
            {
                ModelPath = candidate;
                break;
            }
        }

        if (bytes is null)
        {
            Console.WriteLine($"[character] no model found for {race} {gender} - tried " +
                              string.Join(", ", ModelPathCandidates(race, gender)));
            return false;
        }

        var m2 = M2Reader.Parse(bytes);
        if (m2 is null || !m2.IsValid)
        {
            Console.WriteLine($"[character] {ModelPath} parsed to nothing usable");
            return false;
        }

        _m2 = m2;

        Console.WriteLine(
            $"[character] {ModelPath}: {m2.Vertices.Count:N0} verts, {m2.Indices.Count / 3:N0} tris, " +
            $"{m2.Submeshes.Count} geoset(s), {m2.Bones.Count} bone(s), {m2.Sequences.Count} sequence(s)");

        if (m2.Bones.Count > M2Animator.MaxBones)
        {
            BoneOverflow = true;
            Console.WriteLine(
                $"[character] ERROR {m2.Bones.Count} bones exceeds the shader's {M2Animator.MaxBones}.");
            Console.WriteLine(
                "[character] ANIMATION DISABLED - a truncated skeleton renders correctly in bind " +
                "pose and grotesquely in motion, which is a far worse failure than standing still. " +
                $"Raise MaxBones in M2Animator.cs AND MAX_BONES in character.vert to at least " +
                $"{m2.Bones.Count}, together.");
        }

        _animator = M2Animator.Build(m2, BakedAnimations);
        if (_animator is null)
        {
            Console.WriteLine("[character] model has no skeleton - it will draw in bind pose only");
        }
        else
        {
            _skin = new Matrix4x4[_animator.BoneCount];
            _packed = new float[M2Animator.MaxBones * 12];

            var baked = _animator.Clips.Values
                .OrderBy(c => c.AnimationId)
                .Select(c => $"{c.Name} {c.DurationSeconds:F2}s/{c.AnimatedBones}b");
            Console.WriteLine($"[character] clips: {string.Join(", ", baked)}");

            foreach (int wanted in BakedAnimations)
                if (_animator.Find(wanted) is null)
                    Console.WriteLine($"[character] no usable {M2Animator.AnimationName(wanted)} " +
                                      $"(id {wanted}) in this model");
        }

        BuildTextureSlots(m2);
        BuildGpuBuffers(m2);
        BuildPieces(m2);

        LoadItemDisplay();
        AttachedItemRenderer.ReportAttachments(m2);
        ApplyEquipment();

        return true;
    }

    /// <summary>
    /// Rebuild textures and geosets after an appearance change. Cheap enough to
    /// call from a slider - the M2 and its GPU buffers are untouched.
    /// </summary>
    public void Reload()
    {
        if (_m2 is null) return;

        foreach (var texture in _slots.Select(x => x.Texture)
                     .Where(t => t is not null &&
                                 !ReferenceEquals(t, _bareSkin) &&
                                 !ReferenceEquals(t, _dressedSkin))
                     .Distinct())
            texture!.Dispose();

        _bareSkin?.Dispose();
        _bareSkin = null;
        _dressedSkin?.Dispose();
        _dressedSkin = null;
        _magenta?.Dispose();
        _magenta = null;

        _slots.Clear();
        _bodySlotIndex = -1;
        _baseSkin = null;

        BuildTextureSlots(_m2);
        ApplyEquipment();
    }

    /// <summary>
    /// Read ItemDisplayInfo.dbc out of the MPQs. Non-fatal: without it the
    /// character is simply undressed, which is what it was already.
    /// </summary>
    private void LoadItemDisplay()
    {
        if (_itemDisplay is not null) return;

        var bytes = AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, ItemDisplayTable.MpqPath);
        if (bytes is null)
        {
            Console.WriteLine($"[dbc] {ItemDisplayTable.MpqPath} not found in the MPQs - no gear");
            return;
        }

        _itemDisplay = ItemDisplayTable.Parse(bytes);
    }

    /// <summary>
    /// Resolve the equipped pieces, repaint the body atlas and redo geoset
    /// visibility. Safe to call again after changing <see cref="Equipment"/>.
    /// </summary>
    public void ApplyEquipment()
    {
        Equipment.GenderSuffix = Gender.Equals("Female", StringComparison.OrdinalIgnoreCase) ? "F" : "M";
        Equipment.Resolve(_itemDisplay);
        BindCapeTexture();

        if (_baseSkin is not null && Equipment.Pieces.Count == 0)
        {
            foreach (var slot in _slots)
                if (slot.Fill == SlotFill.BodySkin) slot.Texture = _bareSkin;

            _dressedSkin?.Dispose();
            _dressedSkin = null;
        }
        else if (_baseSkin is not null && Equipment.Pieces.Count > 0)
        {
            var composited = Equipment.Composite(
                _baseSkin, _skinWidth, _skinHeight,
                path => AdtTerrainReader.ReadBlpPixels(_config.ClientDataPath, path));

            var texture = Texture.From2D(_gl, composited, _skinWidth, _skinHeight);

            // Type-1 slots follow the dressed atlas. Hair/scalp exceptions are
            // selected per draw below, because category 0 contains both the
            // actual body and hairstyle meshes that share this texture slot.
            foreach (var slot in _slots)
                if (slot.Fill == SlotFill.BodySkin) slot.Texture = texture;

            _dressedSkin?.Dispose();
            _dressedSkin = texture;
            _ = _bodySlotIndex;
        }

        ApplyGeosetVisibility();
        if (_attached is not null)
        {
            _attached.RaceGenderCode = RaceGenderCode(Race, Gender);
            _attached.Rebuild(Equipment);
        }
    }

    /// <summary>
    /// Cloaks are not attached item models. The character M2 already contains
    /// the cloth as geoset 1502; ItemDisplayInfo.ModelTexture supplies the BLP
    /// for its replaceable type-2 (OBJECT_SKIN) texture slot.
    /// </summary>
    private void BindCapeTexture()
    {
        var capeSlots = _slots.Where(slot => slot.Type == 2).ToList();
        if (capeSlots.Count == 0) return;

        // ApplyEquipment may be called repeatedly from the equipment UI.
        foreach (var texture in capeSlots
                     .Where(slot => slot.Fill == SlotFill.Bound && slot.Texture is not null)
                     .Select(slot => slot.Texture!)
                     .Distinct())
            texture.Dispose();

        foreach (var slot in capeSlots)
        {
            slot.Texture = null;
            slot.Fill = SlotFill.Unbound;
            slot.Source = "";
            slot.AlphaCutoff = 0.35f;
        }

        var cloak = Equipment.Pieces.LastOrDefault(piece =>
            piece.InventoryType == CharacterEquipment.Slot.Cloak && piece.Row is not null);
        if (cloak?.Row is null) return;

        var names = new[] { cloak.Row.ModelTexture1, cloak.Row.ModelTexture2 }
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        Texture? capeTexture = null;
        float capeCutoff = 0.35f;
        string source = "";

        foreach (string name in names)
        {
            foreach (string candidate in CapeTextureCandidates(name))
            {
                capeTexture = MakeTexture(candidate, out capeCutoff);
                if (capeTexture is null) continue;
                source = candidate;
                break;
            }
            if (capeTexture is not null) break;
        }

        if (capeTexture is null)
        {
            Console.WriteLine($"[character] cloak '{cloak.Name}' has no resolvable cape texture " +
                              $"('{cloak.Row.ModelTexture1}', '{cloak.Row.ModelTexture2}')");
            return;
        }

        foreach (var slot in capeSlots)
        {
            slot.Texture = capeTexture;
            slot.Fill = SlotFill.Bound;
            slot.Source = source;
            slot.AlphaCutoff = capeCutoff;
        }

        Console.WriteLine($"[character] cloak '{cloak.Name}' -> type-2 slot(s): {source}");
    }

    private IEnumerable<string> CapeTextureCandidates(string partial)
    {
        string stem = partial.Replace('/', '\\').TrimStart('\\');
        bool hasDirectory = stem.Contains('\\');
        if (stem.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];

        if (hasDirectory)
        {
            yield return stem + ".blp";
            yield break;
        }

        string suffix = Gender.Equals("Female", StringComparison.OrdinalIgnoreCase) ? "F" : "M";
        yield return $@"Item\ObjectComponents\Cape\{stem}.blp";
        yield return $@"Item\TextureComponents\Cape\{stem}.blp";
        yield return $@"Item\ObjectComponents\Cape\{stem}_{suffix}.blp";
        yield return $@"Item\ObjectComponents\Cape\{stem}_U.blp";
        yield return $@"Item\TextureComponents\Cape\{stem}_{suffix}.blp";
        yield return $@"Item\TextureComponents\Cape\{stem}_U.blp";
    }

    /// <summary>
    /// Two-letter race code plus M or F. Helm models are per race and gender -
    /// a helm has to fit the head it sits on - so the file name carries this
    /// suffix where shoulders and weapons do not.
    /// </summary>
    private static string RaceGenderCode(string race, string gender)
    {
        string r = race.ToLowerInvariant() switch
        {
            "human" => "Hu",
            "orc" => "Or",
            "dwarf" => "Dw",
            "nightelf" => "Ni",
            "scourge" or "undead" => "Sc",
            "tauren" => "Ta",
            "gnome" => "Gn",
            "troll" => "Tr",
            _ => "Hu",
        };

        return r + (gender.Equals("Female", StringComparison.OrdinalIgnoreCase) ? "F" : "M");
    }

    /// <summary>
    /// Vanilla character models live at Character\Race\Gender\RaceGender.m2.
    /// The .mdx variants are tried for the same reason DoodadRenderer tries
    /// them: vanilla tooling is inconsistent about which extension it records.
    /// </summary>
    private static IEnumerable<string> ModelPathCandidates(string race, string gender)
    {
        string stem = $"Character\\{race}\\{gender}\\{race}{gender}";
        yield return stem + ".m2";
        yield return stem + ".M2";
        yield return stem + ".mdx";
    }

    /// <summary>
    /// Resolve one image per texture SLOT, not per geoset, so slots are shared.
    ///
    /// M2 texture types, from the vanilla table:
    ///     0  the slot names a BLP and we just read it
    ///     1  CHAR_SKIN        the body atlas - supplied by the application
    ///     2  OBJECT_SKIN      cape or item texture - nothing equipped yet
    ///     6  CHAR_HAIR        supplied by the application
    ///     7  CHAR_FACIAL_HAIR supplied by the application
    ///     8  SKIN_EXTRA       supplied by the application
    ///
    /// Everything except type 0 is normally driven by CharSections.dbc, which
    /// this client does not read yet. Until it does, this resolves the base
    /// skin by filename convention and logs every slot it could not bind. That
    /// logging is not decoration: SuperUI's writer spent a long time silently
    /// falling back to the body atlas for every unresolved slot, which rendered
    /// plausibly - hair textured like skin - and hid the real error underneath.
    /// </summary>
    /// <summary>
    /// Resolve every texture slot the model asks for.
    ///
    /// SLOTS ARE FILLED BY TYPE, AND THE TYPES DO NOT SHARE A SOURCE. This is
    /// the whole thing, and getting it wrong is the "hair textures like skin"
    /// bug: type 6 is hair and must get the CharSections HAIR texture, not the
    /// body atlas. Pointing every empty slot at the skin renders plausibly and
    /// is wrong everywhere it matters.
    ///
    ///   type 0  the slot names a BLP - just read it
    ///   type 1  CHAR_SKIN        the body atlas, composited below
    ///   type 2  OBJECT_SKIN      a cape or item texture; nothing until one is worn
    ///   type 6  CHAR_HAIR        CharSections section 3, by hair style and colour
    ///   type 7  CHAR_FACIAL_HAIR CharSections section 2
    ///   type 8  SKIN_EXTRA       CharSections section 4, the underwear
    ///
    /// AND THE FACE IS NOT A SLOT AT ALL. Most races' body skin BLP has no eye
    /// detail whatsoever - the eyes live in a CharSections Face row that gets
    /// composited onto the atlas. Miss that and the character renders
    /// blank-faced, which looks exactly like "eyes closed" and sends you
    /// hunting through geosets for something that was never there.
    /// </summary>
    private void BuildTextureSlots(M2Model m2)
    {
        LoadCharSections();
        LoadCharHairGeosets();

        uint raceId = CharSectionsTable.RaceId(Race);
        uint sexId = Gender.Equals("Female", StringComparison.OrdinalIgnoreCase) ? 1u : 0u;

        string skinPath = "";
        string hairPath = "";
        string facialHairPath = "";
        // (path, region). CharSections TELLS US which is which - Texture1 is the
        // lower face and Texture2 the upper - and the first version threw that
        // away into a flat list and then tried to infer the region back from
        // the image height. Guessing at something you were handed is how the
        // face ended up painted across the eyes.
        var overlays = new List<(string Path, FaceRegion Region)>();

        if (_charSections is not null)
        {
            var skinRow = _charSections.Find(raceId, sexId, CharSectionsTable.SectionSkin, -1, SkinId);
            if (skinRow is not null) skinPath = skinRow.Texture1;

            // Face: matched on face shape AND skin tone. Texture1 is the lower
            // face, Texture2 the upper - and the upper is where the eyes are.
            var faceRow = _charSections.Find(raceId, sexId, CharSectionsTable.SectionFace, FaceId, SkinId);
            if (faceRow is not null)
            {
                if (faceRow.Texture1.Length > 0) overlays.Add((faceRow.Texture1, FaceRegion.Lower));
                if (faceRow.Texture2.Length > 0) overlays.Add((faceRow.Texture2, FaceRegion.Upper));
                Console.WriteLine($"[character] face lower '{faceRow.Texture1}' upper '{faceRow.Texture2}'");
            }
            else
            {
                Console.WriteLine($"[character] no CharSections Face row for race {raceId} sex {sexId} " +
                                  $"face {FaceId} skin {SkinId} - the character will be blank-faced");
            }

            var hairRow = _charSections.Find(raceId, sexId, CharSectionsTable.SectionHair, HairStyleId, HairColorId);
            if (hairRow is not null) hairPath = hairRow.Texture1;

            var facialRow = _charSections.Find(raceId, sexId, CharSectionsTable.SectionFacialHair, FacialHairId, HairColorId);
            if (facialRow is not null) facialHairPath = facialRow.Texture1;

        }

        // Fall back to the filename convention when the table is missing or the
        // ids do not match a row.
        Texture? skin = null;
        float skinCutoff = 0.35f;
        string usedSkinPath = "";

        foreach (string candidate in SkinCandidates(skinPath, Race, Gender))
        {
            var made = MakeTexture(candidate, out float cutoff, out var pixels, out int w, out int h);
            if (made is null) continue;

            skin = made;
            usedSkinPath = candidate;
            skinCutoff = cutoff;
            _baseSkin = pixels;
            _skinWidth = w;
            _skinHeight = h;
            break;
        }

        SkinTexturePath = usedSkinPath.Length > 0 ? usedSkinPath : "(none)";
        _skinCutoff = skinCutoff;

        if (skin is null)
        {
            Console.WriteLine($"[character] no body skin BLP found for {Race} {Gender}");
        }
        else
        {
            Console.WriteLine($"[character] body skin {usedSkinPath}");

            // Paint the face and underwear into the base BEFORE any gear, so
            // re-equipping composites onto a face rather than erasing it.
            if (overlays.Count > 0 && _baseSkin is not null)
            {
                int painted = ApplyAppearanceOverlays(overlays);
                skin.Dispose();
                skin = Texture.From2D(_gl, _baseSkin, _skinWidth, _skinHeight);
                Console.WriteLine($"[character] composited {painted}/{overlays.Count} appearance overlay(s) onto the skin");
            }
        }

        // Keep the correctly composed bare atlas alive. Dressed body pieces
        // use a second texture, while hairstyle scalp and ear geosets must keep
        // sampling this one or armor regions bleed across the head.
        _bareSkin = skin;

        UnboundSlots = 0;

        for (int i = 0; i < m2.Textures.Count; i++)
        {
            var reference = m2.Textures[i];
            var slot = new Slot { Type = reference.Type };

            string external = reference.Type switch
            {
                6 => hairPath,
                7 => facialHairPath,
                _ => "",
            };

            if (!string.IsNullOrWhiteSpace(reference.Filename))
            {
                slot.Texture = MakeTexture(reference.Filename, out float cutoff);
                slot.AlphaCutoff = cutoff;
                if (slot.Texture is not null) { slot.Fill = SlotFill.Bound; slot.Source = reference.Filename; }
            }
            else if (external.Length > 0)
            {
                foreach (string candidate in CharacterTextureCandidates(external))
                {
                    slot.Texture = MakeTexture(candidate, out float cutoff);
                    if (slot.Texture is null) continue;
                    slot.AlphaCutoff = cutoff;
                    slot.Fill = SlotFill.Bound;
                    slot.Source = candidate;
                    break;
                }
            }
            else if (reference.Type == 1 && skin is not null)
            {
                if (reference.Type == 1 && _bodySlotIndex < 0) _bodySlotIndex = i;
                slot.Texture = skin;
                slot.AlphaCutoff = skinCutoff;
                slot.Fill = SlotFill.BodySkin;
                slot.Source = usedSkinPath;
            }

            if (slot.Fill == SlotFill.Unbound) UnboundSlots++;

            _slots.Add(slot);

            Console.WriteLine(
                $"[character] texslot {i}: type={reference.Type} file='{reference.Filename}' " +
                $"-> {slot.Fill}" + (slot.Source.Length > 0 ? $" {slot.Source}" : ""));
        }

        if (UnboundSlots > 0)
            Console.WriteLine($"[character] {UnboundSlots} texture slot(s) unbound - " +
                              "tick 'Magenta unbound' to see which geosets they are");

        // 1x1 BGRA magenta. Deliberately impossible to overlook.
        _magenta = Texture.From2D(_gl, [255, 0, 255, 255], 1, 1);
    }

    /// <summary>
    /// The vanilla blend modes that matter, mapped to GL. Anything unrecognised
    /// falls back to straight alpha, which is the safe wrong answer - visible
    /// and roughly right rather than invisible or blindingly additive.
    /// </summary>
    private void ApplyBlendMode(int mode)
    {
        switch (mode)
        {
            case 3:     // Add
            case 4:
                _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
                break;
            case 5:     // Mod
                _gl.BlendFunc(BlendingFactor.DstColor, BlendingFactor.Zero);
                break;
            case 6:     // Mod2x
                _gl.BlendFunc(BlendingFactor.DstColor, BlendingFactor.SrcColor);
                break;
            default:    // 2 Alpha, and anything unknown
                _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                break;
        }
    }

    /// <summary>
    /// Report any two VISIBLE pieces whose index ranges overlap.
    ///
    /// This is the definitive test for the flicker, not another theory about
    /// which categories might be fighting. Two draws that share triangles are
    /// literally the same surface submitted twice, and no depth function can
    /// order a surface against itself - it is z-fighting by construction.
    ///
    /// Silence means the geometry is disjoint and the flicker is coming from
    /// somewhere else: coplanar-but-distinct meshes, or something outside the
    /// character entirely.
    /// </summary>
    private void ReportOverlaps()
    {
        var visible = _pieces.Where(p => p.Visible).ToList();
        int reported = 0;

        for (int a = 0; a < visible.Count; a++)
        {
            for (int b = a + 1; b < visible.Count; b++)
            {
                // Several batches over one submesh are intentional material
                // layers, not duplicate geosets. Their authored priority/layer
                // order is exactly why the renderer preserves every batch.
                if (visible[a].SubmeshIndex == visible[b].SubmeshIndex) continue;

                uint aStart = visible[a].IndexStart, aEnd = aStart + visible[a].IndexCount;
                uint bStart = visible[b].IndexStart, bEnd = bStart + visible[b].IndexCount;

                if (aStart >= bEnd || bStart >= aEnd) continue;

                if (reported++ == 0)
                    Console.WriteLine("[character] OVERLAPPING DRAWS - the same triangles are being " +
                                      "submitted more than once, which IS the flicker:");

                Console.WriteLine(
                    $"[character]   geoset {visible[a].GeosetId} [{aStart}..{aEnd}) overlaps " +
                    $"geoset {visible[b].GeosetId} [{bStart}..{bEnd})");

                if (reported >= 12) { Console.WriteLine("[character]   ... more"); return; }
            }
        }

        if (reported == 0)
            Console.WriteLine($"[character] {visible.Count} visible batch(es), no unintended overlapping index ranges");

        var blended = visible.Where(p => p.Transparent).ToList();
        Console.WriteLine(
            $"[character] draw split: {visible.Count - blended.Count} opaque, {blended.Count} blended" +
            (blended.Count > 0
                ? " -> " + string.Join(" ", blended.Select(p => $"{p.GeosetId}(mode {p.BlendMode}{(p.NoZWrite ? ",noZ" : "")})"))
                : ""));
    }

    /// <summary>
    /// Paint the two CharSections face layers into their canonical atlas
    /// rectangles. Underwear is a separate 128x64 pelvis component and must
    /// never be stretched across the complete body texture.
    /// </summary>
    private enum FaceRegion { Lower, Upper }

    private int ApplyAppearanceOverlays(List<(string Path, FaceRegion Region)> paths)
    {
        if (_baseSkin is null) return 0;

        int painted = 0;

        foreach (var (path, region) in paths)
        {
            (byte[] bgra, int w, int h)? decoded = null;
            string used = "";

            foreach (string candidate in CharacterTextureCandidates(path))
            {
                decoded = AdtTerrainReader.ReadBlpPixels(_config.ClientDataPath, candidate);
                if (decoded is not null) { used = candidate; break; }
            }

            if (decoded is null)
            {
                Console.WriteLine($"[character] appearance overlay '{path}' not found");
                continue;
            }

            var (bgra, w, h) = decoded.Value;

            // CharSections tells us which face strip this is. Match the working
            // SuperUI compositor and always paint into that canonical region;
            // never infer a full-atlas replacement from image dimensions.
            var (x, y, rw, rh) = region == FaceRegion.Upper
                ? (0, 160, 128, 32)
                : (0, 192, 128, 64);

            float sx = _skinWidth / 256f, sy = _skinHeight / 256f;

            CharacterEquipment.BlitOver(_baseSkin, _skinWidth, _skinHeight, bgra, w, h,
                                        (int)(x * sx), (int)(y * sy), (int)(rw * sx), (int)(rh * sy));
            Console.WriteLine($"[character] overlay {used} -> face {region} ({x},{y},{rw},{rh}) from {w}x{h}");

            painted++;
        }

        return painted;
    }

    /// <summary>
    /// CharSections stores partial paths, sometimes with the extension and
    /// sometimes without. Same candidate treatment SuperUI's compositor uses.
    /// </summary>
    private IEnumerable<string> CharacterTextureCandidates(string partial)
    {
        string stem = partial.Replace('/', '\\').TrimStart('\\');
        if (stem.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];

        yield return stem + ".blp";
        yield return $@"Character\{stem}.blp";
        yield return $@"Character\{Race}\{Gender}\{stem}.blp";
    }

    private static IEnumerable<string> SkinCandidates(string fromDbc, string race, string gender)
    {
        if (fromDbc.Length > 0)
        {
            string stem = fromDbc;
            if (stem.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];
            yield return stem + ".blp";
        }

        foreach (string candidate in SkinPathCandidates(race, gender)) yield return candidate;
    }

    private void LoadCharSections()
    {
        if (_charSections is not null) return;

        var bytes = AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, CharSectionsTable.MpqPath);
        if (bytes is null)
        {
            Console.WriteLine($"[dbc] {CharSectionsTable.MpqPath} not found - no face, no hair colour");
            return;
        }

        _charSections = CharSectionsTable.Parse(bytes);
    }

    private void LoadCharHairGeosets()
    {
        if (_charHairGeosets is not null) return;

        var bytes = AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, CharHairGeosetsTable.MpqPath);
        if (bytes is null)
        {
            Console.WriteLine($"[dbc] {CharHairGeosetsTable.MpqPath} not found - hairstyle mesh will use a fallback");
            return;
        }

        _charHairGeosets = CharHairGeosetsTable.Parse(bytes);
    }

    private static IEnumerable<string> SkinPathCandidates(string race, string gender)
    {
        string dir = $"Character\\{race}\\{gender}\\{race}{gender}";
        for (int variant = 0; variant < 4; variant++)
            yield return $"{dir}Skin00_{variant:00}.blp";

        yield return $"{dir}Skin00.blp";
    }

    /// <summary>
    /// Decode a BLP and pick its alpha cutoff, guarding the 1-bit case.
    ///
    /// BlpDecoder returns 1-bit alpha as 0 or 1 rather than 0 or 255. In the
    /// shader that is 0.004, which fails any sensible cut on every texel, so the
    /// surface loads, textures correctly, and renders as nothing at all. That is
    /// what made Goldshire's walls disappear and it survived two wrong
    /// diagnoses. Character models lean on alpha for hair, eyelashes and cloth
    /// edges, so it would land here too.
    ///
    /// THE PROPER FIX BELONGS IN BlpDecoder. This is the same point-of-use guard
    /// WmoRenderer carries, and both should be deleted the day that lands.
    /// </summary>
    private Texture? MakeTexture(string blpPath, out float alphaCutoff)
        => MakeTexture(blpPath, out alphaCutoff, out _, out _, out _);

    private Texture? MakeTexture(string blpPath, out float alphaCutoff,
                                 out byte[]? pixels, out int width, out int height)
    {
        alphaCutoff = 0.35f;
        pixels = null;
        width = 0;
        height = 0;

        var decoded = AdtTerrainReader.ReadBlpPixels(_config.ClientDataPath, blpPath);
        if (decoded is null) return null;

        var (bgra, w, h) = decoded.Value;
        if (bgra.Length < 4 || w <= 0 || h <= 0) return null;

        byte maxAlpha = 0;
        for (int i = 3; i < bgra.Length; i += 4)
            if (bgra[i] > maxAlpha) maxAlpha = bgra[i];

        if (maxAlpha == 0)
        {
            // No alpha channel at all. Cutting anything would erase the model.
            alphaCutoff = 0f;
        }
        else if (maxAlpha == 1)
        {
            for (int i = 3; i < bgra.Length; i += 4)
                if (bgra[i] != 0) bgra[i] = 255;

            Console.WriteLine($"[character] {blpPath}: 1-bit alpha decoded as 0/1, rescaled to 0/255");
        }

        pixels = bgra;
        width = w;
        height = h;

        return Texture.From2D(_gl, bgra, w, h);
    }

    private unsafe void BuildGpuBuffers(M2Model m2)
    {
        int vertexCount = m2.Vertices.Count;
        var vertices = new float[vertexCount * FloatsPerVertex];

        int clampedIndices = 0;

        for (int i = 0; i < vertexCount; i++)
        {
            var v = m2.Vertices[i];
            int o = i * FloatsPerVertex;

            vertices[o + 0] = v.PosX;
            vertices[o + 1] = v.PosY;
            vertices[o + 2] = v.PosZ;
            vertices[o + 3] = v.NormX;
            vertices[o + 4] = v.NormY;
            vertices[o + 5] = v.NormZ;
            vertices[o + 6] = v.TexU;
            vertices[o + 7] = v.TexV;

            // Weights are bytes summing to 255. Normalise here rather than in
            // the shader so the shader stays a straight weighted sum.
            float total = v.BoneWeight0 + v.BoneWeight1 + v.BoneWeight2 + v.BoneWeight3;
            if (total <= 0f)
            {
                // No influence at all. Pin to bone 0 rather than collapsing the
                // vertex to the origin, which is the visible failure mode.
                vertices[o + 8] = 1f;
                vertices[o + 9] = 0f;
                vertices[o + 10] = 0f;
                vertices[o + 11] = 0f;
                vertices[o + 12] = 0f;
                vertices[o + 13] = 0f;
                vertices[o + 14] = 0f;
                vertices[o + 15] = 0f;
                continue;
            }

            vertices[o + 8] = v.BoneWeight0 / total;
            vertices[o + 9] = v.BoneWeight1 / total;
            vertices[o + 10] = v.BoneWeight2 / total;
            vertices[o + 11] = v.BoneWeight3 / total;

            vertices[o + 12] = ClampBone(v.BoneIndex0, ref clampedIndices);
            vertices[o + 13] = ClampBone(v.BoneIndex1, ref clampedIndices);
            vertices[o + 14] = ClampBone(v.BoneIndex2, ref clampedIndices);
            vertices[o + 15] = ClampBone(v.BoneIndex3, ref clampedIndices);
        }

        if (clampedIndices > 0)
            Console.WriteLine($"[character] {clampedIndices} bone reference(s) past " +
                              $"{M2Animator.MaxBones} were clamped");

        var indices = m2.Indices.ToArray();

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = vertices)
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer,
                (nuint)(vertices.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        }

        _ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (ushort* p = indices)
        {
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer,
                (nuint)(indices.Length * sizeof(ushort)), p, BufferUsageARB.StaticDraw);
        }

        const uint stride = FloatsPerVertex * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        _gl.EnableVertexAttribArray(3);
        _gl.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, stride, (void*)(8 * sizeof(float)));
        _gl.EnableVertexAttribArray(4);
        _gl.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, stride, (void*)(12 * sizeof(float)));

        _gl.BindVertexArray(0);
    }

    private static float ClampBone(byte index, ref int clamped)
    {
        if (index < M2Animator.MaxBones) return index;
        clamped++;
        return M2Animator.MaxBones - 1;
    }

    /// <summary>
    /// One drawable piece per M2 batch. A submesh may have several authored
    /// material passes; collapsing those to "first batch wins" loses textures,
    /// transparency and the layer order used by faces, hair and effects.
    /// </summary>
    private void BuildPieces(M2Model m2)
    {
        var representedSubmeshes = new HashSet<int>();
        int fallbackPieces = 0;

        foreach (var entry in m2.Batches
                     .Select((batch, index) => (batch, index))
                     .OrderBy(x => x.batch.PriorityPlane)
                     .ThenBy(x => x.batch.MaterialLayer))
        {
            var batch = entry.batch;
            int sub = batch.SubmeshIndex;
            if (sub < 0 || sub >= m2.Submeshes.Count) continue;

            var submesh = m2.Submeshes[sub];
            if (submesh.IndexCount == 0) continue;
            if (submesh.IndexStart + submesh.IndexCount > m2.Indices.Count) continue;

            int slot = -1;
            if (batch.TextureIndex < m2.TextureLookup.Count)
                slot = m2.TextureLookup[batch.TextureIndex];

            bool twoSided = false;
            int blendMode = 0;
            bool noZWrite = false;

            if (batch.MaterialIndex < m2.RenderFlags.Count)
            {
                var flags = m2.RenderFlags[batch.MaterialIndex];
                twoSided = flags.TwoSided;
                blendMode = flags.BlendingMode;
                noZWrite = flags.NoZWrite;
            }

            _pieces.Add(new Piece
            {
                IndexStart = submesh.IndexStart,
                IndexCount = submesh.IndexCount,
                SlotIndex = slot >= 0 && slot < _slots.Count ? slot : -1,
                TwoSided = twoSided,
                BlendMode = blendMode,
                NoZWrite = noZWrite,
                GeosetId = submesh.Id,
                Category = submesh.Id / 100,
                Variant = submesh.Id % 100,
                SubmeshIndex = sub,
                BatchIndex = entry.index,
                PriorityPlane = batch.PriorityPlane,
                MaterialLayer = batch.MaterialLayer,
            });

            representedSubmeshes.Add(sub);
        }

        // Malformed/simple assets occasionally carry geometry without a batch.
        // Keep that geometry visible with a conservative opaque fallback.
        for (int sub = 0; sub < m2.Submeshes.Count; sub++)
        {
            if (representedSubmeshes.Contains(sub)) continue;
            var submesh = m2.Submeshes[sub];
            if (submesh.IndexCount == 0) continue;
            if (submesh.IndexStart + submesh.IndexCount > m2.Indices.Count) continue;

            _pieces.Add(new Piece
            {
                IndexStart = submesh.IndexStart,
                IndexCount = submesh.IndexCount,
                GeosetId = submesh.Id,
                Category = submesh.Id / 100,
                Variant = submesh.Id % 100,
                SubmeshIndex = sub,
                BatchIndex = -1,
            });
            fallbackPieces++;
        }

        int layeredSubmeshes = _pieces
            .Where(p => p.BatchIndex >= 0)
            .GroupBy(p => p.SubmeshIndex)
            .Count(g => g.Count() > 1);
        Console.WriteLine($"[character] render list: {m2.Batches.Count} batch(es) -> {_pieces.Count} draw(s), " +
                          $"{layeredSubmeshes} layered submesh(es), {fallbackPieces} fallback draw(s)");
    }

    /// <summary>
    /// Decide which geosets are drawn.
    ///
    /// Category 0 is base body (variant 0) plus exactly ONE hairstyle. Every
    /// other category shows the single variant named in NakedDefaults, or
    /// nothing. Skipping this step is not a subtle bug: the character wears all
    /// thirteen hairstyles at once.
    ///
    /// Equipment then overrides the map, which is why this builds a
    /// category-to-variant table first rather than testing each piece directly.
    /// A geosetGroup of zero means "leave the default", not "hide", so the two
    /// passes have to stay separate.
    /// </summary>
    private void ApplyGeosetVisibility()
    {
        var hairVariants = _pieces
            .Where(p => p.Category == 0 && p.Variant > 0)
            .Select(p => p.Variant)
            .Distinct()
            .ToList();

        uint raceId = CharSectionsTable.RaceId(Race);
        uint sexId = Gender.Equals("Female", StringComparison.OrdinalIgnoreCase) ? 1u : 0u;
        int mappedHair = _charHairGeosets?.Find(raceId, sexId, HairStyleId) ?? -1;

        // The DBC is authoritative. style+1 is only a last-resort convention
        // for incomplete custom data sets; choosing an arbitrary high variant
        // pairs one hairstyle's texture with another hairstyle's geometry.
        int fallbackHair = Math.Max(HairStyleId + 1, 1);
        int hair = hairVariants.Contains(mappedHair)
            ? mappedHair
            : hairVariants.Contains(fallbackHair)
                ? fallbackHair
                : hairVariants.Contains(1) ? 1 : -1;

        var selected = new Dictionary<int, int>(NakedDefaults);
        Equipment.ApplyGeosets(selected);

        // Closed helms suppress hair. Open helms such as Helm of Might keep the
        // style-specific scalp; forcing every helmet bald is visibly wrong and
        // does not affect the material flicker investigated below.
        if (Equipment.HidesHair()) hair = -1;

        foreach (var piece in _pieces)
        {
            bool show;

            if (piece.Category == 0)
                show = piece.Variant == 0 || (hair >= 0 && !HideHair && piece.Variant == hair);
            else
                show = selected.TryGetValue(piece.Category, out int want) && want > 0 && piece.Variant == want;

            if (HiddenCategories.Contains(piece.Category)) show = false;

            piece.Visible = show;
        }

        if (SoloGeoset >= 0)
        {
            var geosets = _pieces
                .Where(p => p.Visible)
                .Select(p => (p.Category, p.Variant))
                .Distinct()
                .OrderBy(x => x.Category)
                .ThenBy(x => x.Variant)
                .ToList();

            if (SoloGeoset < geosets.Count)
            {
                var selectedGeoset = geosets[SoloGeoset];
                foreach (var piece in _pieces)
                {
                    if (!piece.Visible) continue;
                    if ((piece.Category, piece.Variant) != selectedGeoset)
                        piece.Visible = false;
                }
            }
        }

        ReportOverlaps();

        VisiblePieces = _pieces.Count(p => p.Visible);

        // Published for the HUD. Overlapping geometry is what z-fighting IS,
        // so being able to switch one category off and watch the flicker stop
        // names the culprit faster than any amount of reasoning about it.
        ActiveGeosets = _pieces
            .Where(p => p.Visible)
            .Select(p => (p.Category, p.Variant))
            .Distinct()
            .OrderBy(x => x.Category)
            .ToList();

        var byCategory = _pieces
            .Where(p => p.Visible)
            .GroupBy(p => p.Category)
            .OrderBy(g => g.Key)
            .Select(g => $"c{g.Key}=[{string.Join(",", g.Select(p => p.GeosetId))}]");

        Console.WriteLine(
            $"[character] geosets {VisiblePieces}/{_pieces.Count} visible" +
            (hair >= 0 ? $" (hair style {HairStyleId} -> variant {hair}" +
                         (hair == mappedHair ? ", DBC)" : ", fallback)")
                       : " (no hair geoset found)") +
            $": {string.Join(" ", byCategory)}");
    }

    // ── animation ────────────────────────────────────────────────────────────

    public void Update(float dt, in UnitState state)
    {
        if (_m2 is null || dt <= 0f) return;

        MeasureMotion(dt, state);

        var next = ChooseClip(state, out float rate, out float targetTwist);
        _clipRate = rate;

        if (!ReferenceEquals(next, _clip))
        {
            _clip = next;
            _clipTime = 0f;
        }

        // Ease the twist rather than snapping it, so the legs unwind when you
        // stop instead of the character flicking straight.
        if (ForceAngleDegrees != 0f)
        {
            _moveYaw = ForceAngleDegrees * MathF.PI / 180f;
        }
        else if (targetTwist == 0f && _groundSpeed < MoveThreshold)
        {
            // Stopped. Snap square rather than easing round, for the same
            // reason the speed snaps: an eased return reads as the character
            // still settling after you have let go.
            _moveYaw = 0f;
        }
        else
        {
            float blend = 1f - MathF.Exp(-dt * 14f);
            _moveYaw += (targetTwist - _moveYaw) * blend;
            if (MathF.Abs(_moveYaw) < 0.002f) _moveYaw = 0f;
        }

        _clipTime += dt * _clipRate;
        _globalTime += dt;

        // Cheap insurance. A NaN here would freeze the pose and look exactly
        // like a state-machine bug, which is a diagnosis I would rather not
        // have to make twice.
        if (float.IsNaN(_clipTime) || float.IsInfinity(_clipTime)) _clipTime = 0f;
        if (float.IsNaN(_globalTime) || float.IsInfinity(_globalTime)) _globalTime = 0f;
    }

    /// <summary>
    /// Ground speed and direction from ACTUAL displacement, not from input.
    ///
    /// CharacterController only stores a vertical Velocity - horizontal motion
    /// is applied straight to Position - so there is no velocity vector to read.
    /// Measuring displacement is also simply more honest: walk into a wall and
    /// the slide slows you down, and the animation slows with it instead of
    /// running on the spot.
    /// </summary>
    private void MeasureMotion(float dt, in UnitState state)
    {
        if (!_hasLastPosition)
        {
            _lastPosition = state.Position;
            _hasLastPosition = true;
            return;
        }

        var delta = state.Position - _lastPosition;
        _lastPosition = state.Position;

        var flat = new Vector3(delta.X, delta.Y, 0f);
        float raw = flat.Length() / dt;

        // ASYMMETRIC ON PURPOSE. Smoothing exists so a single frame against a
        // doorframe does not flick the clip, but it also means releasing W
        // leaves the speed decaying for a tenth of a second and the run cycle
        // keeps going after the character has stopped. That is the lag he can
        // feel. Speeding UP is smoothed; a genuine stop is taken immediately.
        float blend = 1f - MathF.Exp(-dt * 12f);

        if (raw < MoveThreshold) _groundSpeed = raw;
        else _groundSpeed += (raw - _groundSpeed) * blend;

        if (flat.LengthSquared() > 1e-8f)
        {
            var direction = Vector3.Normalize(flat);
            var facing = new Vector3(MathF.Cos(state.Yaw), MathF.Sin(state.Yaw), 0f);
            var right = new Vector3(MathF.Sin(state.Yaw), -MathF.Cos(state.Yaw), 0f);

            _forwardness += (Vector3.Dot(direction, facing) - _forwardness) * blend;
            _sideness += (Vector3.Dot(direction, right) - _sideness) * blend;
        }

    }

    private M2Animator.Clip? ChooseClip(in UnitState state, out float rate, out float twist)
    {
        rate = 1f;
        twist = 0f;
        if (_animator is null || BindPose || BoneOverflow) return null;

        if (state.Flying)
            return _animator.FindFirst(40, 38, 0);

        if (!state.Grounded)
        {
            return state.VerticalVelocity > 0.5f
                ? _animator.FindFirst(38, 37, 40, 0)
                : _animator.FindFirst(40, 38, 0);
        }

        // NO GRACE WINDOW. WoWee's FSM holds the moving state open past the
        // last motion and I copied that; Nico's verdict was that it feels
        // awful and the sharp stop is better. He is the one looking at it.
        if (_groundSpeed < MoveThreshold) return _animator.FindFirst(0);

        float nominal = state.Walking ? _config.Movement.WalkSpeed : _config.Movement.RunSpeed;
        if (nominal < 0.5f) nominal = 5f;

        // Play the cycle at the speed the character is actually travelling, so
        // feet do not skate. Clamped because a wall slide can drop the measured
        // speed near zero while the clip should still read as walking.
        rate = Math.Clamp(_groundSpeed / nominal, 0.35f, 2.5f);

        // Angle between where the character is FACING and where he is actually
        // GOING. Zero is straight ahead, positive is toward his left.
        //
        // Negated side term because "right" sits at world yaw (Yaw - 90): with
        // facing = (cos Y, sin Y) and right = (sin Y, -cos Y), a direction at
        // (Yaw + phi) gives forwardness = cos(phi) and sideness = -sin(phi).
        float phi = MathF.Atan2(-_sideness, _forwardness);

        float maxTwist = MaxTwistDegrees * MathF.PI / 180f;

        bool rotating = Strafe is StrafeStyle.Split or StrafeStyle.WholeBody
                     || (Strafe == StrafeStyle.LowerBody && _animator.TwistBone >= 0);

        if (rotating)
        {
            // Past this the character is going backwards, and turning to face
            // that way would read as him deciding to run off rather than
            // backing up. Swap to the backwards cycle and take the angle off
            // what is LEFT after the half turn, so straight back is unrotated
            // and back-and-left leans naturally.
            const float backwards = 1.92f;   // about 110 degrees

            float angle = MathF.Abs(phi) > backwards
                ? phi - MathF.Sign(phi) * MathF.PI
                : phi;

            // The clamp is a hip limit, so it only applies when the hips are
            // doing the work. A whole body can face any direction it likes.
            // Only the pure lower-body mode is hip-limited. In Split the legs
            // come from the model heading, which has no such limit.
            twist = Strafe == StrafeStyle.LowerBody
                ? Math.Clamp(angle, -maxTwist, maxTwist)
                : angle;

            if (MathF.Abs(phi) > backwards) return _animator.FindFirst(13, 4, 5, 0);

            return state.Walking ? _animator.FindFirst(4, 5, 0) : _animator.FindFirst(5, 4, 0);
        }

        // Fallback: discrete sideways clips, no twist. Kept so the two can be
        // compared in one click, but this is the version that looks like a
        // dance step at running speed.
        if (MathF.Abs(_forwardness) >= MathF.Abs(_sideness) * 1.2f)
        {
            if (_forwardness >= 0f)
                return state.Walking ? _animator.FindFirst(4, 5, 0) : _animator.FindFirst(5, 4, 0);

            return _animator.FindFirst(13, 4, 5, 0);
        }

        return _sideness > 0f
            ? (state.Walking ? _animator.FindFirst(12, 92, 5, 4, 0)
                             : _animator.FindFirst(92, 12, 5, 4, 0))
            : (state.Walking ? _animator.FindFirst(11, 93, 5, 4, 0)
                             : _animator.FindFirst(93, 11, 5, 4, 0));
    }

    // ── drawing ──────────────────────────────────────────────────────────────

    public Matrix4x4 BuildTransform(in UnitState state)
    {
        // The strafe angle goes into the model's own heading in WholeBody mode,
        // which is the entire mechanism: the character turns to face where he
        // is travelling and the ordinary run cycle plays.
        //
        // Note it does NOT touch state.Yaw. That is the character's facing, the
        // camera sits behind it, and a movement packet will want it in Phase 2.
        // Only the drawn model turns, so strafing right shows you his side
        // while the view stays where you are pointed.
        // Split turns the whole model too - the torso is then pulled back part
        // of the way by its own yaw, which is where the 90-against-60 comes from.
        bool bodyTurns = Strafe is StrafeStyle.Split or StrafeStyle.WholeBody;
        float bodyYaw = bodyTurns && !BindPose ? _moveYaw : 0f;

        float heading = state.Yaw + HeadingOffsetDegrees * MathF.PI / 180f + bodyYaw;
        var position = state.Position + new Vector3(0f, 0f, ZOffset);

        return Matrix4x4.CreateScale(ModelScale)
             * Matrix4x4.CreateRotationY(heading)
             * ModelToWorld
             * Matrix4x4.CreateTranslation(position);
    }

    public unsafe void Render(Camera camera, in UnitState state)
    {
        if (!Enabled || _m2 is null || _shader is null || _pieces.Count == 0) return;

        int bones = _animator?.BoneCount ?? 0;
        if (_animator is not null)
        {
            _animator.LowerBodyYaw =
                BindPose || Strafe != StrafeStyle.LowerBody ? 0f : _moveYaw;

            // The torso keeps only part of what the body just turned, so its
            // delta is the REMAINDER, negative. TorsoFollow 1 leaves it with the
            // body (WholeBody); 0 holds it facing forward (LowerBody).
            _animator.TorsoYaw =
                BindPose || Strafe != StrafeStyle.Split
                    ? 0f
                    : (Math.Clamp(TorsoFollow, 0f, 1f) - 1f) * _moveYaw;
            _animator.Evaluate(BindPose ? null : _clip, _clipTime, _globalTime, _skin);
            M2Animator.Pack(_skin, Math.Min(bones, M2Animator.MaxBones), _packed);
        }

        var modelTransform = BuildTransform(state);
        modelTransform.M41 -= camera.Position.X;
        modelTransform.M42 -= camera.Position.Y;
        modelTransform.M43 -= camera.Position.Z;

        _shader.Use();
        _shader.Set("uModel", modelTransform);
        _shader.Set("uModelViewProjection", modelTransform * camera.RelativeViewProjection);
        _shader.Set("uCameraPos", Vector3.Zero);
        _shader.Set("uSunDirection", SunDirection);
        _shader.Set("uFogStart", FogStart);
        _shader.Set("uFogEnd", FogEnd);
        _shader.Set("uFogColor", FogColor);
        _shader.Set("uTexture", 0);

        if (bones > 0)
            _shader.SetVec4Array("uBones", _packed, Math.Min(bones, M2Animator.MaxBones) * 3);
        _shader.Set("uBoneCount", Math.Min(bones, M2Animator.MaxBones));

        _gl.BindVertexArray(_vao);

        bool cullingOn = true;
        VisiblePieces = 0;

        // Opaque/alpha-test first, then transparent/additive with depth writes
        // disabled. This preserves the M2 material distinction without making
        // translucent cards reject each other through the depth buffer.
        for (int pass = 0; pass < 2; pass++)
        {
            bool transparentPass = pass == 1;

            if (transparentPass)
            {
                // Depth TEST stays on - blended geometry still hides behind
                // walls. Depth WRITE goes off, so two blended surfaces cannot
                // reject each other.
                _gl.DepthMask(false);
                _gl.Enable(EnableCap.Blend);
            }

            DrawPieces(transparentPass, ref cullingOn);

            if (transparentPass)
            {
                _gl.Disable(EnableCap.Blend);
                _gl.DepthMask(true);
            }
        }

        if (!cullingOn) _gl.Enable(EnableCap.CullFace);
        _gl.BindVertexArray(0);

        // Attached items ride the SAME skin matrices that were just evaluated,
        // which is what makes a pauldron follow the shoulder.
        _attached?.Render(camera, modelTransform, _m2, _skin);
    }

    private unsafe void DrawPieces(bool transparentPass, ref bool cullingOn)
    {
        foreach (var piece in _pieces)
        {
            if (!ShowAllGeosets && !piece.Visible) continue;
            if (piece.Transparent != transparentPass) continue;

            var slot = piece.SlotIndex >= 0 ? _slots[piece.SlotIndex] : null;

            Texture? drawTexture = slot?.Texture;
            if (slot?.Fill == SlotFill.BodySkin &&
                ((piece.Category == 0 && piece.Variant > 0) || piece.Category == 7))
            {
                drawTexture = _bareSkin;
            }

            bool unbound = slot is null || slot.Fill == SlotFill.Unbound || drawTexture is null;

            // SuperUI emits transparent materials for unresolved client-filled
            // slots. Rendering them as solid grey creates phantom cape/facial-
            // hair/skin-extra surfaces. Magenta mode intentionally overrides
            // this so those assignments can still be diagnosed.
            if (unbound && !MagentaUnbound && slot?.Type is 2 or 7 or 8)
                continue;

            if (piece.TwoSided && cullingOn)
            {
                _gl.Disable(EnableCap.CullFace);
                cullingOn = false;
            }
            else if (!piece.TwoSided && !cullingOn)
            {
                _gl.Enable(EnableCap.CullFace);
                cullingOn = true;
            }

            if (unbound)
            {
                if (MagentaUnbound && _magenta is not null)
                {
                    _magenta.Bind(0);
                    _shader.Set("uHasTexture", 1);
                    _shader.Set("uAlphaCutoff", 0f);
                }
                else
                {
                    _shader.Set("uHasTexture", 0);
                    _shader.Set("uAlphaCutoff", 0f);
                }
            }
            else
            {
                drawTexture!.Bind(0);
                _shader.Set("uHasTexture", 1);

                // Blend mode decides whether alpha CUTS or COMPOSITES, and
                // doing both is how a soft hair edge turns into a hard one.
                //   0  opaque      no cut at all
                //   1  alpha key   cut, no blend
                //   2+ blended     blend, no cut
                float cutoff = piece.BlendMode switch
                {
                    0 => 0f,
                    1 => MathF.Min(slot!.AlphaCutoff, AlphaCutoff),
                    _ => 0f,
                };
                _shader.Set("uAlphaCutoff", cutoff);

                if (transparentPass) ApplyBlendMode(piece.BlendMode);
            }

            _gl.DrawElements(PrimitiveType.Triangles, piece.IndexCount,
                DrawElementsType.UnsignedShort, (void*)(piece.IndexStart * sizeof(ushort)));

            VisiblePieces++;
        }
    }

    /// <summary>
    /// Copied value for value from DoodadRenderer. These are not defaults to be
    /// tuned here - a character lit even slightly differently from the trees
    /// beside it reads as a bug you cannot name. If they change there, change
    /// them here.
    /// </summary>
    public Vector3 SunDirection { get; set; } = Vector3.Normalize(new Vector3(0.45f, 0.35f, 0.82f));
    public Vector3 FogColor { get; set; } = new(0.56f, 0.71f, 0.85f);
    public float FogStart { get; set; } = 350f;
    public float FogEnd { get; set; } = 900f;

    /// <summary>Global ceiling on the per-slot cutoff. Drag to zero to prove alpha is the culprit.</summary>
    public float AlphaCutoff { get; set; } = 0.35f;

    public void Dispose()
    {
        if (_vao != 0) _gl.DeleteVertexArray(_vao);
        if (_vbo != 0) _gl.DeleteBuffer(_vbo);
        if (_ebo != 0) _gl.DeleteBuffer(_ebo);

        // Slots share textures, so dispose distinct instances only - and skip
        // the composited atlas, which is disposed on its own below.
        foreach (var texture in _slots.Select(s => s.Texture)
                     .Where(t => t is not null &&
                                 !ReferenceEquals(t, _bareSkin) &&
                                 !ReferenceEquals(t, _dressedSkin))
                     .Distinct())
            texture!.Dispose();

        _attached?.Dispose();
        _bareSkin?.Dispose();
        _dressedSkin?.Dispose();
        _magenta?.Dispose();
        _shader?.Dispose();

        _slots.Clear();
        _pieces.Clear();
    }
}
