using System.Numerics;

namespace MSUIClient.Creator;

// ═══════════════════════════════════════════════════════════════════════════
// M2 BONE reader/patcher for vanilla v256 M2s (shared_docs/SPELL_CREATOR_IDE.md §2.6).
//
// Every emitter, ribbon and mesh vertex of an effect rides a bone, and the bone's
// pose is what points it: an emitter has no rotation of its own. Verified layout
// (M2Reader.ParseBones, 108 bytes per bone at header 0x34):
//
//   +0   keyBoneId s32      +4  flags u32      +8  parent s16     +10 submeshId u16
//   +12  translation track (28 B, vec3 keys)   +40 rotation track (28 B, raw float
//        quaternion x,y,z,w keys)              +68 scale track (28 B, vec3 keys)
//   +96  pivot vec3, raw WoW Z-up
//
// PATCH LAW: the pivot writes in place; rotation and translation write the FIRST
// key (or every key with AllKeys - a static pose for an animated bone), in the
// file's raw axes. A bone with no rotation keys cannot be rotated (there is no
// array to write into); the UI says so.
// ═══════════════════════════════════════════════════════════════════════════

public sealed class BoneSnapshot
{
    public int Index { get; set; }
    public int KeyBoneId { get; set; }
    public uint Flags { get; set; }
    public short Parent { get; set; }
    public ushort SubmeshId { get; set; }
    public float PivotX { get; set; }
    public float PivotY { get; set; }
    public float PivotZ { get; set; }
    public Vector4? RotationFirst { get; set; }
    public int RotationKeys { get; set; }
    public Vector3? TranslationFirst { get; set; }
    public int TranslationKeys { get; set; }
    public int ScaleKeys { get; set; }
    public int Children { get; set; }
    public bool Animated => RotationKeys > 1 || TranslationKeys > 1 || ScaleKeys > 1;
}

/// <summary>Absolute edits for one bone; null = leave authored.</summary>
public sealed class BonePatch
{
    public int BoneIndex { get; set; }
    public float? PivotX { get; set; }
    public float? PivotY { get; set; }
    public float? PivotZ { get; set; }
    /// <summary>Raw-frame Euler degrees (roll about X, pitch about Y, yaw about Z), written as
    /// the rotation track's quaternion key(s).</summary>
    public Vector3? RotationEulerDegrees { get; set; }
    /// <summary>The rotation key as a raw file quaternion (x, y, z, w), exact - what a ring drag
    /// writes (SPELL_CREATOR_IDE §2.10). Wins over the Euler field when both are set.</summary>
    public Vector4? Rotation { get; set; }
    public float? TranslationX { get; set; }
    public float? TranslationY { get; set; }
    public float? TranslationZ { get; set; }
    /// <summary>Write every key of the rotation/translation track, not just the first: a
    /// static pose that overrides the bone's animation.</summary>
    public bool AllKeys { get; set; }

    public bool IsEmpty => PivotX is null && PivotY is null && PivotZ is null &&
                           RotationEulerDegrees is null && Rotation is null &&
                           TranslationX is null && TranslationY is null && TranslationZ is null;
}

public static class M2BoneParser
{
    public const int HEADER_BONES = 0x34;
    public const int BONE_SIZE = 108;
    private const int REL_KEY_BONE = 0, REL_FLAGS = 4, REL_PARENT = 8, REL_SUBMESH = 10;
    private const int REL_TRANSLATION = 12, REL_ROTATION = 40, REL_SCALE = 68, REL_PIVOT = 96;

    private static bool TryBase(byte[] d, int index, out long o)
    {
        o = 0;
        if (!M2MeshParser.IsVanilla(d) || index < 0) return false;
        var (n, ofs) = M2MeshParser.Array(d, HEADER_BONES);
        if (index >= n || !M2MeshParser.Fits(d, ofs, n, BONE_SIZE)) return false;
        o = ofs + (long)index * BONE_SIZE;
        return true;
    }

    public static List<BoneSnapshot> ReadBones(byte[] d)
    {
        var result = new List<BoneSnapshot>();
        if (!M2MeshParser.IsVanilla(d)) return result;
        var (n, ofs) = M2MeshParser.Array(d, HEADER_BONES);
        if (n == 0 || !M2MeshParser.Fits(d, ofs, n, BONE_SIZE)) return result;
        for (int i = 0; i < n; i++)
        {
            long o = ofs + (long)i * BONE_SIZE;
            var bone = new BoneSnapshot
            {
                Index = i,
                KeyBoneId = unchecked((int)M2MeshParser.U32(d, o + REL_KEY_BONE)),
                Flags = M2MeshParser.U32(d, o + REL_FLAGS),
                Parent = unchecked((short)M2MeshParser.U16(d, o + REL_PARENT)),
                SubmeshId = M2MeshParser.U16(d, o + REL_SUBMESH),
                PivotX = M2MeshParser.F32(d, o + REL_PIVOT),
                PivotY = M2MeshParser.F32(d, o + REL_PIVOT + 4),
                PivotZ = M2MeshParser.F32(d, o + REL_PIVOT + 8),
                RotationKeys = M2MeshParser.TrackKeyCount(d, o + REL_ROTATION),
                TranslationFirst = M2MeshParser.TrackFirstVec3(d, o + REL_TRANSLATION),
                TranslationKeys = M2MeshParser.TrackKeyCount(d, o + REL_TRANSLATION),
                ScaleKeys = M2MeshParser.TrackKeyCount(d, o + REL_SCALE),
            };
            var (rn, rofs) = M2MeshParser.TrackKeys(d, o + REL_ROTATION);
            if (rn > 0 && rofs > 0 && rofs + 16 <= d.Length)
                bone.RotationFirst = new Vector4(M2MeshParser.F32(d, rofs), M2MeshParser.F32(d, rofs + 4),
                    M2MeshParser.F32(d, rofs + 8), M2MeshParser.F32(d, rofs + 12));
            result.Add(bone);
        }
        foreach (BoneSnapshot bone in result)
            if (bone.Parent >= 0 && bone.Parent < result.Count) result[bone.Parent].Children++;
        return result;
    }

    /// <summary>Raw-frame Euler (degrees; roll X, pitch Y, yaw Z) to the file's quaternion.</summary>
    public static Vector4 EulerToQuaternion(Vector3 degrees)
    {
        const float toRad = MathF.PI / 180f;
        Quaternion q = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees.Z * toRad) *
                       Quaternion.CreateFromAxisAngle(Vector3.UnitY, degrees.Y * toRad) *
                       Quaternion.CreateFromAxisAngle(Vector3.UnitX, degrees.X * toRad);
        q = Quaternion.Normalize(q);
        return new Vector4(q.X, q.Y, q.Z, q.W);
    }

    /// <summary>The inverse, for showing an authored key as editable degrees (ZYX order).</summary>
    public static Vector3 QuaternionToEuler(Vector4 raw)
    {
        var q = Quaternion.Normalize(new Quaternion(raw.X, raw.Y, raw.Z, raw.W));
        const float toDeg = 180f / MathF.PI;
        float sinRoll = 2f * (q.W * q.X + q.Y * q.Z);
        float cosRoll = 1f - 2f * (q.X * q.X + q.Y * q.Y);
        float roll = MathF.Atan2(sinRoll, cosRoll);
        float sinPitch = Math.Clamp(2f * (q.W * q.Y - q.Z * q.X), -1f, 1f);
        float pitch = MathF.Asin(sinPitch);
        float sinYaw = 2f * (q.W * q.Z + q.X * q.Y);
        float cosYaw = 1f - 2f * (q.Y * q.Y + q.Z * q.Z);
        float yaw = MathF.Atan2(sinYaw, cosYaw);
        return new Vector3(roll * toDeg, pitch * toDeg, yaw * toDeg);
    }

    public static bool ApplyBonePatch(byte[] d, BonePatch p)
    {
        if (p.IsEmpty || !TryBase(d, p.BoneIndex, out long o)) return false;
        if (p.PivotX is { } x) M2MeshParser.WF(d, o + REL_PIVOT, x);
        if (p.PivotY is { } y) M2MeshParser.WF(d, o + REL_PIVOT + 4, y);
        if (p.PivotZ is { } z) M2MeshParser.WF(d, o + REL_PIVOT + 8, z);
        Vector4? pose = p.Rotation ?? (p.RotationEulerDegrees is { } euler ? EulerToQuaternion(euler) : null);
        if (pose is { } q)
        {
            var (n, ofs) = M2MeshParser.TrackKeys(d, o + REL_ROTATION);
            if (n > 0 && ofs > 0 && ofs + (long)n * 16 <= d.Length)
            {
                uint keys = p.AllKeys ? n : 1;
                for (uint k = 0; k < keys; k++)
                {
                    long ko = ofs + k * 16;
                    M2MeshParser.WF(d, ko, q.X); M2MeshParser.WF(d, ko + 4, q.Y);
                    M2MeshParser.WF(d, ko + 8, q.Z); M2MeshParser.WF(d, ko + 12, q.W);
                }
            }
        }
        if (p.TranslationX is not null || p.TranslationY is not null || p.TranslationZ is not null)
        {
            var (n, ofs) = M2MeshParser.TrackKeys(d, o + REL_TRANSLATION);
            if (n > 0 && ofs > 0 && ofs + (long)n * 12 <= d.Length)
            {
                uint keys = p.AllKeys ? n : 1;
                for (uint k = 0; k < keys; k++)
                {
                    long ko = ofs + k * 12;
                    if (p.TranslationX is { } tx) M2MeshParser.WF(d, ko, tx);
                    if (p.TranslationY is { } ty) M2MeshParser.WF(d, ko + 4, ty);
                    if (p.TranslationZ is { } tz) M2MeshParser.WF(d, ko + 8, tz);
                }
            }
        }
        return true;
    }
}
