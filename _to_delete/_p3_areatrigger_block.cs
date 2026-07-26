// (tail of Formats/DbcReader.cs, namespace MSUIClient.Formats, usings: System.Numerics, System.Text)

===
// AREA TRIGGERS — AreaTrigger.dbc.
//
// 432 rows in 1.12, 10 fields, 40-byte records. Field 0 id, 1 map, 2-4 the
// position, 5 a sphere radius, 6-9 an oriented box (length, width, height,
// yaw). A row uses ONE of the two shapes: 352 are spheres with a radius and a
// zeroed box, 80 are boxes with a zero radius. Measured, not assumed.
//
// WHAT THIS TABLE IS AND IS NOT
//   It is every trigger VOLUME in the game, on both sides of every portal: the
//   one in the Moonbrook mineshaft (id 78, map 0, radius 7) and the ones inside
//   the Deadmines (map 36) alike.
//
//   It is NOT the destinations. Nothing in the client says where a trigger
//   sends you, or even which direction it points - that lives in the server's
//   areatrigger_teleport table. Checked and ruled out on the way here:
//   AreaPOI.dbc is landmarks ("Echo Ridge Mine", "Sentinel Hill"), and
//   pfQuest's areatrigger.lua carries only map-pin percentages.
//
// WHAT IT IS STILL WORTH ON ITS OWN
//   Every instance map's triggers sit INSIDE that instance's playable space,
//   which makes them far better arrival points than any geometric guess.
//   Deadmines' tile-cluster centre is (-267, -267); its content, measured from
//   the built collision BVH, spans X -316..162 Y -1050..-342. The centre misses
//   the dungeon by 469 yards and you arrive on empty terrain with all 698
//   doodads distance-culled. Trigger 119 at (-14, -393, 65) is inside it.
// ============================================================================

/// <summary>One AreaTrigger.dbc row: a sphere or an oriented box on some map.</summary>
public sealed class AreaTriggerRow
{
    public int Id { get; init; }
    public int MapId { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }

    /// <summary>Sphere radius in yards. Zero when this row is a box.</summary>
    public float Radius { get; init; }

    public float BoxLength { get; init; }
    public float BoxWidth { get; init; }
    public float BoxHeight { get; init; }

    /// <summary>Box yaw in radians. Meaningless when <see cref="IsSphere"/>.</summary>
    public float BoxYaw { get; init; }

    public bool IsSphere => Radius > 0f;

    /// <summary>
    /// Is this point inside the volume? Sphere is a plain distance test; box is
    /// the point rotated into the box's frame and compared against half-extents.
    ///
    /// Z is treated as a half-height about the centre for boxes, and as part of
    /// the sphere for spheres. Vanilla's server does the same, and it is why a
    /// box trigger in a stairwell does not fire from the floor below.
    /// </summary>
    public bool Contains(Vector3 p)
    {
        float dx = p.X - X, dy = p.Y - Y, dz = p.Z - Z;

        if (IsSphere)
            return dx * dx + dy * dy + dz * dz <= Radius * Radius;

        if (MathF.Abs(dz) > BoxHeight * 0.5f) return false;

        float cos = MathF.Cos(-BoxYaw), sin = MathF.Sin(-BoxYaw);
        float rx = dx * cos - dy * sin;
        float ry = dx * sin + dy * cos;
        return MathF.Abs(rx) <= BoxLength * 0.5f
            && MathF.Abs(ry) <= BoxWidth * 0.5f;
    }

    public override string ToString()
        => IsSphere
            ? $"{Id} map {MapId} ({X:F1},{Y:F1},{Z:F1}) r{Radius:F1}"
            : $"{Id} map {MapId} ({X:F1},{Y:F1},{Z:F1}) box {BoxLength:F1}x{BoxWidth:F1}x{BoxHeight:F1} yaw {BoxYaw:F2}";
}

public sealed class AreaTriggerTable
{
    public const string MpqPath = @"DBFilesClient\AreaTrigger.dbc";

    private const int FieldId = 0;
    private const int FieldMap = 1;
    private const int FieldX = 2;
    private const int FieldRadius = 5;
    private const int FieldBoxLength = 6;

    private readonly List<AreaTriggerRow> _all = [];
    private readonly Dictionary<int, List<AreaTriggerRow>> _byMap = [];

    public IReadOnlyList<AreaTriggerRow> All => _all;
    public int Count => _all.Count;

    /// <summary>Every trigger on a map, in file order. Empty list, never null.</summary>
    public IReadOnlyList<AreaTriggerRow> ForMap(int mapId)
        => _byMap.TryGetValue(mapId, out var list) ? list : [];

    /// <summary>The trigger on this map whose centre is nearest a point, or null.</summary>
    public AreaTriggerRow? NearestOnMap(int mapId, Vector3 to)
    {
        AreaTriggerRow? best = null;
        float bestSq = float.MaxValue;
        foreach (var t in ForMap(mapId))
        {
            float dx = t.X - to.X, dy = t.Y - to.Y, dz = t.Z - to.Z;
            float d = dx * dx + dy * dy + dz * dz;
            if (d >= bestSq) continue;
            bestSq = d;
            best = t;
        }
        return best;
    }

    /// <summary>
    /// Nearest trigger measured in XY only. Use this when picking somewhere to
    /// arrive: Gnomeregan's triggers run from Z -149 to -241 and Deadmines' from
    /// 35 to 65, so folding Z into the distance ranks them by depth as much as
    /// by where they are, which is not the question being asked.
    /// </summary>
    public AreaTriggerRow? NearestOnMapXY(int mapId, Vector2 to)
    {
        AreaTriggerRow? best = null;
        float bestSq = float.MaxValue;
        foreach (var t in ForMap(mapId))
        {
            float dx = t.X - to.X, dy = t.Y - to.Y;
            float d = dx * dx + dy * dy;
            if (d >= bestSq) continue;
            bestSq = d;
            best = t;
        }
        return best;
    }

    /// <summary>The first trigger on this map containing the point, or null.</summary>
    public AreaTriggerRow? Containing(int mapId, Vector3 p)
    {
        foreach (var t in ForMap(mapId))
            if (t.Contains(p)) return t;
        return null;
    }

    public static AreaTriggerTable? Parse(byte[] data)
    {
        var dbc = DbcFile.Parse(data);
        if (dbc is null) return null;

        if (dbc.FieldCount < 10)
        {
            Console.WriteLine($"[dbc] AreaTrigger: {dbc.FieldCount} field(s), expected 10. NOT LOADED.");
            return null;
        }

        var table = new AreaTriggerTable();
        int spheres = 0, boxes = 0;

        for (int r = 0; r < dbc.RecordCount; r++)
        {
            int id = dbc.GetInt(r, FieldId);
            if (id == 0) continue;

            var row = new AreaTriggerRow
            {
                Id = id,
                MapId = dbc.GetInt(r, FieldMap),
                X = dbc.GetFloat(r, FieldX),
                Y = dbc.GetFloat(r, FieldX + 1),
                Z = dbc.GetFloat(r, FieldX + 2),
                Radius = dbc.GetFloat(r, FieldRadius),
                BoxLength = dbc.GetFloat(r, FieldBoxLength),
                BoxWidth = dbc.GetFloat(r, FieldBoxLength + 1),
                BoxHeight = dbc.GetFloat(r, FieldBoxLength + 2),
                BoxYaw = dbc.GetFloat(r, FieldBoxLength + 3),
            };

            if (row.IsSphere) spheres++; else boxes++;

            table._all.Add(row);
            if (!table._byMap.TryGetValue(row.MapId, out var list))
                table._byMap[row.MapId] = list = [];
            list.Add(row);
        }

        Console.WriteLine($"[dbc] AreaTrigger: {dbc.RecordCount} record(s), {dbc.FieldCount} field(s), " +
            $"{dbc.RecordSize} bytes; {spheres} sphere(s), {boxes} box(es) over {table._byMap.Count} map(s)");
        return table;
    }
}
