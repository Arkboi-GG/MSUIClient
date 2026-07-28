namespace MSUIClient.Net;

// A sparse SMSG_UPDATE_OBJECT descriptor set: a bitmask of u32 blocks followed
// by one u32 per set bit, kept as an index->value map with named accessors.
// Ported from benilla-protocol update_object/fields (indices verified there
// against vmangos UpdateFields_1_12_1.h, build 5875).
//
// Absent-field semantics: a CREATE block is a COMPLETE snapshot (the server masks
// in only non-zero fields), read into a zero-initialized descriptor — so on a
// create-seeded set an absent field reads 0. A bare VALUES delta carries only
// changed fields, so an absent field there means "untouched" (null), and a merge
// must not clobber it.
public sealed class ObjectFields
{
    // --- field indices (build 5875) ---
    public const ushort OBJECT_GUID = 0;             // 2 slots
    public const ushort OBJECT_TYPE = 2;
    public const ushort OBJECT_ENTRY = 3;            // creature/gameobject template entry
    public const ushort OBJECT_SCALE_X = 4;          // f32

    public const ushort UNIT_TARGET = 16;            // guid
    public const ushort UNIT_HEALTH = 22;
    public const ushort UNIT_MAXHEALTH = 28;
    public const ushort UNIT_LEVEL = 34;
    public const ushort UNIT_FACTIONTEMPLATE = 35;
    public const ushort UNIT_BYTES_0 = 36;           // race/class/gender/powertype
    public const ushort UNIT_FLAGS = 46;
    public const ushort UNIT_DISPLAYID = 131;        // the rendered CreatureDisplayInfo id
    public const ushort UNIT_MOUNTDISPLAYID = 133;
    public const ushort UNIT_DYNAMIC_FLAGS = 143;
    public const ushort UNIT_NPC_FLAGS = 147;

    public const ushort GAMEOBJECT_DISPLAYID = 8;

    public const ushort PLAYER_BYTES = 193;          // skin/face/hairstyle/haircolor
    public const ushort PLAYER_BYTES_2 = 194;        // facial hair, etc.

    private readonly Dictionary<ushort, uint> _fields;
    private bool _created;

    public ObjectFields() => _fields = new Dictionary<ushort, uint>();
    private ObjectFields(Dictionary<ushort, uint> fields, bool created) { _fields = fields; _created = created; }

    public static ObjectFields Read(PacketReader r)
    {
        int blocks = r.ReadU8();
        var mask = new uint[blocks];
        for (int i = 0; i < blocks; i++) mask[i] = r.ReadU32();

        var fields = new Dictionary<ushort, uint>();
        for (int i = 0; i < blocks; i++)
        {
            uint word = mask[i];
            for (int bit = 0; bit < 32; bit++)
                if ((word & (1u << bit)) != 0)
                    fields[(ushort)(i * 32 + bit)] = r.ReadU32();
        }
        return new ObjectFields(fields, created: false);
    }

    /// <summary>Mark this as a CREATE snapshot (absent fields then read as 0).</summary>
    public ObjectFields AsCreated() { _created = true; return this; }

    /// <summary>Overlay a VALUES delta's present fields; keeps this set's created flag.</summary>
    public void Merge(ObjectFields delta)
    {
        foreach (var kv in delta._fields) _fields[kv.Key] = kv.Value;
    }

    // --- typed reads ---
    public uint? GetU32(ushort index)
    {
        if (_fields.TryGetValue(index, out uint v)) return v;
        return _created ? 0u : (uint?)null;
    }
    public int? GetI32(ushort index) => GetU32(index) is { } v ? unchecked((int)v) : null;
    public float? GetF32(ushort index) => GetU32(index) is { } v ? BitConverter.UInt32BitsToSingle(v) : null;

    public ulong? GetGuid(ushort index)
    {
        // Raw low-half read (a guid slot is "present iff the low half is", even on a created set).
        if (!_fields.TryGetValue(index, out uint lo)) return null;
        uint hi = GetU32((ushort)(index + 1)) ?? 0;
        return (ulong)lo | ((ulong)hi << 32);
    }

    // --- named accessors ---
    public uint? Entry => GetU32(OBJECT_ENTRY);
    public float Scale => GetF32(OBJECT_SCALE_X) ?? 1f;
    public int DisplayId => GetI32(UNIT_DISPLAYID) ?? 0;
    public uint MountDisplayId => GetU32(UNIT_MOUNTDISPLAYID) ?? 0;
    public uint Health => GetU32(UNIT_HEALTH) ?? 0;
    public uint MaxHealth => GetU32(UNIT_MAXHEALTH) ?? 0;
    public uint Level => GetU32(UNIT_LEVEL) ?? 0;
    public uint FactionTemplate => GetU32(UNIT_FACTIONTEMPLATE) ?? 0;
    public uint UnitFlags => GetU32(UNIT_FLAGS) ?? 0;
    public uint NpcFlags => GetU32(UNIT_NPC_FLAGS) ?? 0;
    public ulong? Target => GetGuid(UNIT_TARGET) is { } g && g != 0 ? g : null;

    /// <summary>race, class, gender, powerType from UNIT_FIELD_BYTES_0 (players + humanoid NPCs).</summary>
    public (byte Race, byte Class, byte Gender, byte PowerType) Bytes0
    {
        get { uint v = GetU32(UNIT_BYTES_0) ?? 0; return ((byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)); }
    }

    /// <summary>Health fraction 0..1 (1 when maxhealth unknown).</summary>
    public float HealthFraction => MaxHealth > 0 ? Math.Clamp((float)Health / MaxHealth, 0f, 1f) : 1f;

    public bool IsDead => MaxHealth > 0 && Health == 0;
}
