using System.IO.Compression;
using System.Numerics;

namespace MSUIClient.Net;

// SMSG_UPDATE_OBJECT / SMSG_COMPRESSED_UPDATE_OBJECT decode, ported from
// benilla-protocol update_object/{mod,movement}.rs. The object list: a count +
// has-transport byte, then each entry's update-type, packed guid, optional
// movement block and sparse descriptor fields.

public enum ObjectTypeId : byte
{
    Object = 0, Item = 1, Container = 2, Unit = 3,
    Player = 4, GameObject = 5, DynamicObject = 6, Corpse = 7,
}

public enum UpdateKind : byte
{
    Values = 0, Movement = 1, CreateObject = 2, CreateObject2 = 3, OutOfRange = 4, Near = 5,
}

/// <summary>The pose (and, for a LIVING block, movement speeds) an update entry carries.</summary>
public sealed class MovementBlock
{
    public Vector3? Position;
    public float Orientation;
    public float[]? Speeds;   // [walk, run, run_back, swim, swim_back, turn_rate]; null for non-living

    // MovementBlock update_flag bits.
    private const byte TRANSPORT = 0x02, MELEE_ATTACKING = 0x04, HIGH_GUID = 0x08, ALL = 0x10, LIVING = 0x20, HAS_POSITION = 0x40;
    // Movement flags (in a LIVING block's inner flags word).
    private const uint MF_ON_TRANSPORT = 0x02000000, MF_SWIMMING = 0x200000, MF_JUMPING = 0x2000,
                       MF_SPLINE_ENABLED = 0x400000, MF_SPLINE_ELEVATION = 0x04000000;
    private const uint SPLINE_FINAL_POINT = 0x10000, SPLINE_FINAL_TARGET = 0x20000, SPLINE_FINAL_ANGLE = 0x40000;

    public static MovementBlock Read(PacketReader r)
    {
        byte updateFlag = r.ReadU8();
        var mb = new MovementBlock();

        if ((updateFlag & LIVING) != 0)
        {
            uint flags = r.ReadU32();
            r.ReadU32();                       // timestamp
            mb.Position = r.ReadVector3();
            mb.Orientation = r.ReadF32();

            if ((flags & MF_ON_TRANSPORT) != 0) { r.ReadU64(); r.ReadVector3(); r.ReadF32(); }
            if ((flags & MF_SWIMMING) != 0) r.ReadF32();     // pitch
            r.ReadF32();                                     // fall_time (an f32 in this block)
            if ((flags & MF_JUMPING) != 0) r.Skip(16);       // z_speed, cos, sin, xy_speed
            if ((flags & MF_SPLINE_ELEVATION) != 0) r.ReadF32();

            var s = new float[6];
            for (int i = 0; i < 6; i++) s[i] = r.ReadF32();
            mb.Speeds = s;

            if ((flags & MF_SPLINE_ENABLED) != 0)
            {
                uint sf = r.ReadU32();
                if ((sf & SPLINE_FINAL_ANGLE) != 0) r.ReadF32();
                else if ((sf & SPLINE_FINAL_TARGET) != 0) r.ReadU64();
                else if ((sf & SPLINE_FINAL_POINT) != 0) r.ReadVector3();
                r.ReadU32(); r.ReadU32(); r.ReadU32();       // time_passed, duration, id
                uint nodes = r.ReadU32();
                for (uint n = 0; n < nodes; n++) r.ReadVector3();
                r.ReadVector3();                             // final node
            }
        }
        else if ((updateFlag & HAS_POSITION) != 0)
        {
            mb.Position = r.ReadVector3();
            mb.Orientation = r.ReadF32();
        }

        if ((updateFlag & HIGH_GUID) != 0) r.ReadU32();
        if ((updateFlag & ALL) != 0) r.ReadU32();
        if ((updateFlag & MELEE_ATTACKING) != 0) r.ReadPackedGuid();
        if ((updateFlag & TRANSPORT) != 0) r.ReadU32();      // transport path progress

        return mb;
    }
}

public sealed class ObjectUpdate
{
    public UpdateKind Kind;
    public ulong Guid;
    public ObjectTypeId Type;         // create only
    public MovementBlock? Movement;   // movement / create
    public ObjectFields? Fields;      // values / create
    public List<ulong>? Guids;        // out-of-range / near
}

public static class UpdateObjectParser
{
    public static List<ObjectUpdate> Parse(byte[] body)
    {
        var r = new PacketReader(body);
        uint count = r.ReadU32();
        r.ReadU8();                    // has_transport (ignored)
        var list = new List<ObjectUpdate>((int)Math.Min(count, 0xFFFF));
        for (uint i = 0; i < count; i++)
        {
            var kind = (UpdateKind)r.ReadU8();
            switch (kind)
            {
                case UpdateKind.Values:
                    list.Add(new ObjectUpdate { Kind = kind, Guid = r.ReadPackedGuid(), Fields = ObjectFields.Read(r) });
                    break;
                case UpdateKind.Movement:
                    list.Add(new ObjectUpdate { Kind = kind, Guid = r.ReadPackedGuid(), Movement = MovementBlock.Read(r) });
                    break;
                case UpdateKind.CreateObject:
                case UpdateKind.CreateObject2:
                {
                    ulong guid = r.ReadPackedGuid();
                    var type = (ObjectTypeId)r.ReadU8();
                    var mb = MovementBlock.Read(r);
                    var fields = ObjectFields.Read(r).AsCreated();
                    list.Add(new ObjectUpdate { Kind = kind, Guid = guid, Type = type, Movement = mb, Fields = fields });
                    break;
                }
                case UpdateKind.OutOfRange:
                case UpdateKind.Near:
                    list.Add(new ObjectUpdate { Kind = kind, Guids = ReadGuidList(r) });
                    break;
                default:
                    // Unknown update type — the rest of the packet is no longer aligned, so stop here.
                    return list;
            }
        }
        return list;
    }

    public static List<ObjectUpdate> ParseCompressed(byte[] body)
    {
        var r = new PacketReader(body);
        int decompressedSize = (int)r.ReadU32();
        byte[] compressed = r.ReadBytes(r.Remaining);
        return Parse(Inflate(compressed, decompressedSize));
    }

    private static List<ulong> ReadGuidList(PacketReader r)
    {
        uint count = r.ReadU32();
        var guids = new List<ulong>((int)Math.Min(count, 0xFFFF));
        for (uint i = 0; i < count; i++) guids.Add(r.ReadPackedGuid());
        return guids;
    }

    private static byte[] Inflate(byte[] compressed, int expectedSize)
    {
        using var input = new MemoryStream(compressed);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(expectedSize > 0 ? expectedSize : compressed.Length * 4);
        z.CopyTo(output);
        return output.ToArray();
    }
}
