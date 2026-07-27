using System.Diagnostics;
using System.Numerics;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.Net;
using Shader = MSUIClient.Engine.Shader;
using Texture = MSUIClient.Engine.Texture;

namespace MSUIClient.World.Units;

// Draws the networked entity stream: every CREATURE/NPC as its M2 model at the
// server-given position / orientation / scale, SKINNED, ANIMATED, TEXTURED, and (for
// humanoid NPCs) GEOSET-FILTERED so only the right hairstyle/beard/armour variants draw.
//
// TRANSFORM (camera-relative, matches CharacterRenderer):
//   Scale * RotationY(heading) * Basis * Translate(pos), eye subtracted from the row.
//
// TEXTURES: resolved BY M2 TEXTURE TYPE — 0 embedded, 11/12/13 monster-skin variations,
//   type-1 CHAR_SKIN via CreatureDisplayInfoExtra (baked atlas or default body skin).
//
// GEOSETS (new): a character-model NPC's M2 holds EVERY variant (all hairstyles, beards,
//   sleeves...). CharacterGeosets.Visible() (benilla visible_geosets) computes the set of
//   skinSectionIds to draw from the NPC's CreatureDisplayInfoExtra hair/facial/equipment;
//   any submesh not in the set is skipped. Beasts are unfiltered (they have no variants).
//
// ANIMATION: one M2Animator per model, per-instance clock; idle/walk/run from spline speed.

public sealed class CreatureRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly MpqMount _mpq;
    private Shader? _shader;
    private CreatureModelResolver? _resolver;
    private ItemDisplayTable? _itemDisplay;
    private CharacterGeosets? _geosets;
    private readonly Dictionary<string, LoadedModel?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public bool Enabled { get; set; } = true;
    public bool Ok { get; private set; }

    public float HeadingOffsetDegrees { get; set; } = 90f;
    public float ScaleMultiplier { get; set; } = 1f;
    public int DrawnLastFrame { get; private set; }
    public int AnimatedLastFrame { get; private set; }

    /// <summary>Master animation switch (off = static bind pose).</summary>
    public bool Animate { get; set; } = true;

    /// <summary>Beyond this range a creature draws its static bind pose (skinning you couldn't see anyway).</summary>
    public float AnimateDistance { get; set; } = 130f;

    /// <summary>Filter humanoid-NPC geosets to the correct variants (off = draw every geoset, the old blob).</summary>
    public bool GeosetFilter { get; set; } = true;

    private static readonly Vector3 SunDir = Vector3.Normalize(new Vector3(0.45f, 0.35f, 0.82f));
    private static readonly Vector3 FogColor = new(0.56f, 0.71f, 0.85f);
    private const float FogStart = 350f, FogEnd = 900f;

    private static readonly int[] CreatureAnims = { 0, 4, 5, 13, 41, 42 };
    private const float DefaultWalkSpeed = 2.5f;
    private const float MovingEpsilon = 0.1f;

    private static readonly Matrix4x4 Basis = new(
        0f, -1f, 0f, 0f,
        0f, 0f, 1f, 0f,
        -1f, 0f, 0f, 0f,
        0f, 0f, 0f, 1f);

    private const int FloatsPerVertex = 16;   // pos3 + norm3 + uv2 + weight4 + index4
    private const int LoadsPerFrame = 4;
    private int _diagLogged;

    private readonly Matrix4x4[] _skin = new Matrix4x4[M2Animator.MaxBones];
    private readonly float[] _packed = new float[M2Animator.MaxBones * 12];

    private readonly Dictionary<ulong, float> _animTime = new();
    private readonly HashSet<ulong> _seen = new();
    private readonly List<ulong> _stale = new();
    private float _globalTime;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastSeconds;

    private sealed class LoadedModel
    {
        public uint Vao, Vbo, Ebo;
        public readonly List<DrawBatch> Batches = new();
        public float DbcScale = 1f;
        public M2Animator? Animator;
        public int BoneCount;
        public HashSet<int>? VisibleGeosets;   // null = draw all (beasts, or filter disabled/failed)
    }
    private struct DrawBatch { public int Start, Count; public Texture? Tex; public int Blend; public int GeosetId; }

    public CreatureRenderer(GL gl, MpqMount mpq)
    {
        _gl = gl;
        _mpq = mpq;
        try
        {
            var diBytes = mpq.ReadFile(CreatureDisplayInfoTable.MpqPath);
            var mdBytes = mpq.ReadFile(CreatureModelDataTable.MpqPath);
            var exBytes = mpq.ReadFile(CreatureDisplayExtraTable.MpqPath);
            var di = diBytes is null ? null : CreatureDisplayInfoTable.Parse(diBytes);
            var md = mdBytes is null ? null : CreatureModelDataTable.Parse(mdBytes);
            var ex = exBytes is null ? null : CreatureDisplayExtraTable.Parse(exBytes);
            if (di is not null && md is not null)
            {
                _resolver = new CreatureModelResolver(di, md, ex);
                _shader = Shader.FromSource(_gl, "creature", VertSrc, FragSrc);

                // Geoset visibility for humanoid NPCs (best-effort — filter degrades to naked defaults).
                var idBytes = mpq.ReadFile(ItemDisplayTable.MpqPath);
                _itemDisplay = idBytes is null ? null : ItemDisplayTable.Parse(idBytes);
                var hairBytes = mpq.ReadFile(CharHairGeosetsTable.MpqPath);
                var facialBytes = mpq.ReadFile(CharacterFacialHairTable.MpqPath);
                var helmBytes = mpq.ReadFile(HelmetGeosetVisTable.MpqPath);
                _geosets = new CharacterGeosets(
                    hairBytes is null ? null : CharHairGeosetsTable.Parse(hairBytes),
                    facialBytes is null ? null : CharacterFacialHairTable.Parse(facialBytes),
                    helmBytes is null ? null : HelmetGeosetVisTable.Parse(helmBytes));

                Ok = true;
                Console.WriteLine($"[creature] renderer ready ({di.Count} display rows, {md.Count} models, " +
                                  $"{(ex?.Count ?? 0)} extended-npc rows, geosets={(_geosets.Ok ? "on" : "no-dbc")})");
            }
            else Console.WriteLine("[creature] CreatureDisplayInfo/CreatureModelData DBCs missing — unit rendering off");
        }
        catch (Exception e) { Console.WriteLine($"[creature] init failed: {e.Message}"); Ok = false; }
    }

    public void Render(Camera camera, EntityStore entities)
    {
        DrawnLastFrame = 0;
        AnimatedLastFrame = 0;
        if (!Ok || !Enabled || _shader is null || _resolver is null) return;

        double nowS = _clock.Elapsed.TotalSeconds;
        float dt = (float)Math.Clamp(nowS - _lastSeconds, 0.0, 0.1);
        _lastSeconds = nowS;
        _globalTime += dt;

        Vector3 camPos = camera.Position;
        Matrix4x4 viewProj = camera.RelativeViewProjection;
        float heading0 = HeadingOffsetDegrees * MathF.PI / 180f;
        int loadsThisFrame = 0;

        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.Blend);
        _shader.Use();
        _shader.Set("uViewProj", viewProj);
        _shader.Set("uSunDir", SunDir);
        _shader.Set("uFogColor", FogColor);
        _shader.Set("uFogStart", FogStart);
        _shader.Set("uFogEnd", FogEnd);
        _shader.Set("uTex", 0);
        _seen.Clear();

        foreach (var e in entities.Units)
        {
            if (!e.IsCreature) continue;
            if (e.DisplayId <= 0) continue;
            if (!_resolver.TryResolve(e.DisplayId, out CreatureModelInfo info)) continue;

            string key = CacheKey(info);
            if (!_cache.TryGetValue(key, out var model))
            {
                if (loadsThisFrame >= LoadsPerFrame) continue;
                loadsThisFrame++;
                model = LoadModel(info);
                _cache[key] = model;
            }
            if (model is null) continue;

            _seen.Add(e.Guid);

            float scale = MathF.Max(0.01f, e.Scale) * model.DbcScale * ScaleMultiplier;
            float heading = e.Orientation + heading0;
            Matrix4x4 m = Matrix4x4.CreateScale(scale)
                * Matrix4x4.CreateRotationY(heading)
                * Basis
                * Matrix4x4.CreateTranslation(e.Position);
            m.M41 -= camPos.X; m.M42 -= camPos.Y; m.M43 -= camPos.Z;
            _shader.Set("uModel", m);

            int boneCount = 0;
            if (Animate && model.Animator is not null && model.BoneCount > 0 &&
                Vector3.Distance(e.Position, camPos) <= AnimateDistance)
            {
                if (!_animTime.TryGetValue(e.Guid, out float at)) at = InitialPhase(e.Guid);
                M2Animator.Clip? clip = SelectClip(e, model.Animator, out float rate);
                at += dt * rate;
                if (float.IsNaN(at) || float.IsInfinity(at)) at = 0f;
                _animTime[e.Guid] = at;

                if (clip is not null)
                {
                    boneCount = Math.Min(model.BoneCount, M2Animator.MaxBones);
                    model.Animator.Evaluate(clip, at, _globalTime, _skin);
                    M2Animator.Pack(_skin, boneCount, _packed);
                    _shader.SetVec4Array("uBones", _packed, boneCount * 3);
                    AnimatedLastFrame++;
                }
            }
            _shader.Set("uBoneCount", boneCount);

            bool filter = GeosetFilter && model.VisibleGeosets is not null;
            _gl.BindVertexArray(model.Vao);
            foreach (var b in model.Batches)
            {
                if (filter && !model.VisibleGeosets!.Contains(b.GeosetId)) continue;

                bool additive = b.Blend is 3 or 4;
                bool alphaKey = b.Blend == 1;
                if (additive) { _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One); _gl.DepthMask(false); }
                else if (b.Blend >= 2) { _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha); _gl.DepthMask(false); }
                else { _gl.BlendFunc(BlendingFactor.One, BlendingFactor.Zero); _gl.DepthMask(true); }
                _shader.Set("uAlphaCut", alphaKey ? 0.5f : 0.02f);
                b.Tex?.Bind(0);
                DrawElements(b.Start, b.Count);
            }
            DrawnLastFrame++;
        }
        _gl.BindVertexArray(0);
        _gl.DepthMask(true);

        PruneAnimState();
    }

    private static M2Animator.Clip? SelectClip(WorldEntity e, M2Animator animator, out float rate)
    {
        rate = 1f;
        float speed = e.Spline?.AverageSpeed ?? 0f;
        if (e.Spline is null || speed <= MovingEpsilon)
            return animator.Find(0);

        float walk = e.Speeds is { Length: > 0 } sp && sp[0] > 0f ? sp[0] : DefaultWalkSpeed;
        M2Animator.Clip? clip = speed > 2f * walk
            ? animator.FindFirst(5, 4, 0)
            : animator.FindFirst(4, 5, 0);

        if (clip is not null && clip.MoveSpeed > 0.01f)
            rate = Math.Clamp(speed / clip.MoveSpeed, 0.25f, 3f);
        return clip;
    }

    private static float InitialPhase(ulong guid) => (guid % 977) / 977f * 5f;

    private void PruneAnimState()
    {
        if (_animTime.Count == 0) return;
        _stale.Clear();
        foreach (var k in _animTime.Keys) if (!_seen.Contains(k)) _stale.Add(k);
        foreach (var k in _stale) _animTime.Remove(k);
    }

    private static string CacheKey(in CreatureModelInfo info) =>
        info.HasExtended
            ? $"{info.ModelPath}|npc:{info.ExtRace}/{info.ExtSex}/{info.ExtSkin}/{info.ExtHairStyle}/{info.ExtFacialHair}/{info.BakeName}/{string.Join('.', info.ExtEquipment)}"
            : $"{info.ModelPath}|{string.Join(",", info.Textures)}";

    // Build the NPC's EquipGeosets from its 10 CreatureDisplayInfoExtra equipment display ids.
    private EquipGeosets? BuildNpcEquip(in CreatureModelInfo info)
    {
        if (_itemDisplay is null || info.ExtEquipment.Length < 10) return null;
        var eq = info.ExtEquipment;   // [head, shoulder, shirt, chest, belt, pants, boots, wrist, gloves, tabard]
        var e = new EquipGeosets();
        for (int i = 0; i < 8; i++)   // bodyslots = shirt..tabard = eq[2..9]
        {
            uint disp = eq[2 + i];
            e.Bodyslots[i] = disp != 0 ? _itemDisplay.Find(disp) : null;
        }
        if (eq[0] != 0 && _itemDisplay.Find(eq[0]) is { } head)   // helm hides hair
            e.HelmVis = (head.HelmetGeosetVis1, head.HelmetGeosetVis2);
        return e;   // NPCs carry no cloak column
    }

    private unsafe LoadedModel? LoadModel(in CreatureModelInfo info)
    {
        string path = info.ModelPath;
        try
        {
            byte[]? bytes = _mpq.ReadFile(path);
            if (bytes is null) { Console.WriteLine($"[creature] model '{path}' not in MPQ"); return null; }
            M2Model? m2 = M2Reader.Parse(bytes);
            if (m2 is null || !m2.IsValid) return null;

            var lm = new LoadedModel { DbcScale = info.Scale };

            var animator = M2Animator.Build(m2, CreatureAnims);
            if (animator is not null && animator.BoneCount <= M2Animator.MaxBones)
            {
                lm.Animator = animator;
                lm.BoneCount = animator.BoneCount;
            }

            // Geoset visibility for humanoid NPCs (character models). Beasts stay unfiltered.
            if (info.HasExtended && _geosets is not null)
            {
                var eq = BuildNpcEquip(info);
                var vis = _geosets.Visible(info.ExtRace, info.ExtSex, info.ExtHairStyle, info.ExtFacialHair, eq);
                // Fail-safe: if the computed set matches no submesh, don't hide the whole NPC.
                int match = 0;
                foreach (var sm in m2.Submeshes) if (vis.Contains(sm.Id)) match++;
                lm.VisibleGeosets = match > 0 ? vis : null;
                if (match == 0)
                    Console.WriteLine($"[creature] {path}: geoset set matched 0 submeshes — drawing all (check DBC layout)");
            }

            var verts = new float[m2.Vertices.Count * FloatsPerVertex];
            for (int i = 0; i < m2.Vertices.Count; i++)
            {
                var v = m2.Vertices[i]; int o = i * FloatsPerVertex;
                verts[o + 0] = v.PosX; verts[o + 1] = v.PosY; verts[o + 2] = v.PosZ;
                verts[o + 3] = v.NormX; verts[o + 4] = v.NormY; verts[o + 5] = v.NormZ;
                verts[o + 6] = v.TexU; verts[o + 7] = v.TexV;

                float total = v.BoneWeight0 + v.BoneWeight1 + v.BoneWeight2 + v.BoneWeight3;
                if (total <= 0f)
                {
                    verts[o + 8] = 1f; verts[o + 9] = 0f; verts[o + 10] = 0f; verts[o + 11] = 0f;
                    verts[o + 12] = 0f; verts[o + 13] = 0f; verts[o + 14] = 0f; verts[o + 15] = 0f;
                }
                else
                {
                    verts[o + 8] = v.BoneWeight0 / total; verts[o + 9] = v.BoneWeight1 / total;
                    verts[o + 10] = v.BoneWeight2 / total; verts[o + 11] = v.BoneWeight3 / total;
                    verts[o + 12] = ClampBone(v.BoneIndex0); verts[o + 13] = ClampBone(v.BoneIndex1);
                    verts[o + 14] = ClampBone(v.BoneIndex2); verts[o + 15] = ClampBone(v.BoneIndex3);
                }
            }
            ushort[] idx = m2.Indices.ToArray();

            lm.Vao = _gl.GenVertexArray(); _gl.BindVertexArray(lm.Vao);
            lm.Vbo = _gl.GenBuffer(); _gl.BindBuffer(BufferTargetARB.ArrayBuffer, lm.Vbo);
            fixed (float* p = verts) _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(verts.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
            lm.Ebo = _gl.GenBuffer(); _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, lm.Ebo);
            fixed (ushort* p = idx) _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(idx.Length * sizeof(ushort)), p, BufferUsageARB.StaticDraw);
            int stride = FloatsPerVertex * sizeof(float);
            _gl.EnableVertexAttribArray(0); _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, (uint)stride, (void*)0);
            _gl.EnableVertexAttribArray(1); _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, (uint)stride, (void*)(3 * sizeof(float)));
            _gl.EnableVertexAttribArray(2); _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, (uint)stride, (void*)(6 * sizeof(float)));
            _gl.EnableVertexAttribArray(3); _gl.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, (uint)stride, (void*)(8 * sizeof(float)));
            _gl.EnableVertexAttribArray(4); _gl.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, (uint)stride, (void*)(12 * sizeof(float)));
            _gl.BindVertexArray(0);

            string modelDir = path.Contains('\\') ? path[..path.LastIndexOf('\\')] : "";
            int textured = 0;
            string firstTex = "NONE";
            foreach (var b in m2.Batches)
            {
                if (b.SubmeshIndex >= m2.Submeshes.Count) continue;
                var sm = m2.Submeshes[b.SubmeshIndex];

                Texture? tex = null;
                if (b.TextureIndex < m2.TextureLookup.Count)
                {
                    int t = m2.TextureLookup[b.TextureIndex];
                    if (t >= 0 && t < m2.Textures.Count)
                    {
                        var candidates = ResolveBatchTexture(m2.Textures[t].Type, m2.Textures[t].Filename, modelDir, info);
                        tex = LoadTexture(candidates, out string hit);
                        if (tex is not null) { textured++; if (firstTex == "NONE") firstTex = hit; }
                    }
                }

                int blend = b.MaterialIndex < m2.RenderFlags.Count ? m2.RenderFlags[b.MaterialIndex].BlendingMode : 0;
                lm.Batches.Add(new DrawBatch { Start = sm.IndexStart, Count = sm.IndexCount, Tex = tex, Blend = blend, GeosetId = sm.Id });
            }

            if (_diagLogged < 30)
            {
                _diagLogged++;
                int vis = lm.VisibleGeosets?.Count ?? -1;
                Console.WriteLine($"[creature] {path} ext={info.HasExtended} bones={lm.BoneCount} " +
                                  $"clips={lm.Animator?.Clips.Count ?? 0} batches={lm.Batches.Count} " +
                                  $"textured={textured}/{lm.Batches.Count} visgeosets={vis} first=[{firstTex}]");
            }
            return lm;
        }
        catch (Exception e) { Console.WriteLine($"[creature] model '{path}' failed: {e.Message}"); return null; }
    }

    private static float ClampBone(byte index) => index < M2Animator.MaxBones ? index : 0f;

    // ── texture resolution ────────────────────────────────────────────────────────

    private static IReadOnlyList<string> ResolveBatchTexture(uint type, string embedded, string modelDir, in CreatureModelInfo info)
    {
        if (!string.IsNullOrEmpty(embedded)) return new[] { embedded };

        switch (type)
        {
            case 11: case 12: case 13:
            {
                int slot = (int)type - 11;
                string name = slot < info.Textures.Length && !string.IsNullOrEmpty(info.Textures[slot])
                    ? info.Textures[slot]
                    : (info.Textures.Length > 0 ? info.Textures[0] : "");
                if (string.IsNullOrEmpty(name)) return Array.Empty<string>();
                return new[] { UnderDir(modelDir, name) };
            }
            case 1:
                return NpcBodySkinCandidates(info);
            default:
                if (info.Textures.Length > 0 && !string.IsNullOrEmpty(info.Textures[0]))
                    return new[] { UnderDir(modelDir, info.Textures[0]) };
                return Array.Empty<string>();
        }
    }

    private static string UnderDir(string dir, string stem) =>
        dir.Length > 0 ? dir + "\\" + stem + ".blp" : stem + ".blp";

    private static IReadOnlyList<string> NpcBodySkinCandidates(in CreatureModelInfo info)
    {
        if (!info.HasExtended) return Array.Empty<string>();
        var list = new List<string>(3);

        if (!string.IsNullOrEmpty(info.BakeName))
        {
            string bake = info.BakeName.EndsWith(".blp", StringComparison.OrdinalIgnoreCase) ? info.BakeName : info.BakeName + ".blp";
            list.Add(bake.Contains('\\') ? bake : "Textures\\BakedNpcTextures\\" + bake);
        }

        string race = RaceFolder(info.ExtRace);
        string gender = info.ExtSex == 1 ? "Female" : "Male";
        list.Add($"Character\\{race}\\{gender}\\{race}{gender}Skin{(int)info.ExtSkin:00}_00.blp");
        list.Add($"Character\\{race}\\{gender}\\{race}{gender}Skin00_00.blp");
        return list;
    }

    private static string RaceFolder(byte race) => race switch
    {
        1 => "Human", 2 => "Orc", 3 => "Dwarf", 4 => "NightElf",
        5 => "Scourge", 6 => "Tauren", 7 => "Gnome", 8 => "Troll", _ => "Human"
    };

    private readonly Dictionary<string, Texture?> _texCache = new(StringComparer.OrdinalIgnoreCase);
    private Texture? LoadTexture(IReadOnlyList<string> candidates, out string hitPath)
    {
        hitPath = "NONE";
        foreach (var path in candidates)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (_texCache.TryGetValue(path, out var cached))
            {
                if (cached is not null) { hitPath = path; return cached; }
                continue;
            }
            Texture? tex = null;
            try
            {
                byte[]? blp = _mpq.ReadFile(path);
                if (blp is not null) { byte[] bgra = BlpDecoder.GetPixels(blp, 0, out int w, out int h); tex = Texture.From2D(_gl, bgra, w, h, mipmaps: true, repeat: true); }
            }
            catch { /* leave null */ }
            _texCache[path] = tex;
            if (tex is not null) { hitPath = path; return tex; }
        }
        return null;
    }

    private unsafe void DrawElements(int start, int count)
        => _gl.DrawElements(PrimitiveType.Triangles, (uint)count, DrawElementsType.UnsignedShort, (void*)(start * sizeof(ushort)));

    public void Dispose()
    {
        foreach (var m in _cache.Values)
        {
            if (m is null) continue;
            if (m.Vbo != 0) _gl.DeleteBuffer(m.Vbo);
            if (m.Ebo != 0) _gl.DeleteBuffer(m.Ebo);
            if (m.Vao != 0) _gl.DeleteVertexArray(m.Vao);
        }
        _cache.Clear();
        foreach (var t in _texCache.Values) t?.Dispose();
        _texCache.Clear();
        _shader?.Dispose();
    }

    private const string VertSrc = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNorm;
layout(location=2) in vec2 aUv;
layout(location=3) in vec4 aBoneWeights;
layout(location=4) in vec4 aBoneIndices;
uniform mat4 uModel;
uniform mat4 uViewProj;
const int MAX_BONES = 160;
uniform vec4 uBones[MAX_BONES * 3];
uniform int uBoneCount;
out vec3 vNorm;
out vec2 vUv;
out float vDist;
vec3 skinPoint(vec3 p, int b){
    vec4 h = vec4(p, 1.0);
    return vec3(dot(uBones[b*3+0], h), dot(uBones[b*3+1], h), dot(uBones[b*3+2], h));
}
vec3 skinVec(vec3 v, int b){
    return vec3(dot(uBones[b*3+0].xyz, v), dot(uBones[b*3+1].xyz, v), dot(uBones[b*3+2].xyz, v));
}
void main(){
    vec3 position = aPos;
    vec3 normal = aNorm;
    if (uBoneCount > 0){
        vec3 sp = vec3(0.0); vec3 sn = vec3(0.0); float total = 0.0;
        for (int i = 0; i < 4; i++){
            float w = aBoneWeights[i];
            if (w <= 0.0) continue;
            int b = int(aBoneIndices[i] + 0.5);
            if (b < 0 || b >= uBoneCount) continue;
            sp += skinPoint(aPos, b) * w;
            sn += skinVec(aNorm, b) * w;
            total += w;
        }
        if (total > 0.0001){ position = sp / total; normal = sn / total; }
    }
    vec4 rel = uModel * vec4(position, 1.0);
    gl_Position = uViewProj * rel;
    vNorm = normalize(mat3(uModel) * normal);
    vUv = aUv;
    vDist = length(rel.xyz);
}";

    private const string FragSrc = @"#version 330 core
in vec3 vNorm;
in vec2 vUv;
in float vDist;
uniform sampler2D uTex;
uniform vec3 uSunDir;
uniform vec3 uFogColor;
uniform float uFogStart;
uniform float uFogEnd;
uniform float uAlphaCut;
out vec4 frag;
void main(){
    vec4 t = texture(uTex, vUv);
    if (t.a < uAlphaCut) discard;
    float ndl = max(dot(normalize(vNorm), normalize(uSunDir)), 0.0);
    float light = 0.45 + 0.55 * ndl;
    float fog = clamp((vDist - uFogStart) / (uFogEnd - uFogStart), 0.0, 1.0);
    frag = vec4(mix(t.rgb * light, uFogColor, fog), t.a);
}";
}
