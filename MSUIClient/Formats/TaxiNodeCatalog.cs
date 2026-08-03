using System.Numerics;

namespace MSUIClient.Formats;

public readonly record struct TaxiNodeInfo(uint Id, uint MapId, Vector3 Position, string Name);

public sealed class TaxiNodeCatalog
{
    public const string Path = @"DBFilesClient\TaxiNodes.dbc";
    private readonly Dictionary<uint, TaxiNodeInfo> _nodes = [];
    public IEnumerable<TaxiNodeInfo> Nodes => _nodes.Values;
    public bool TryGet(uint id, out TaxiNodeInfo node) => _nodes.TryGetValue(id, out node);

    public static TaxiNodeCatalog? Load(MpqMount mpq)
    {
        byte[]? bytes = mpq.ReadFile(Path);
        if (bytes is null) return null;
        DbcFile? dbc = DbcFile.Parse(bytes);
        if (dbc is null || dbc.FieldCount < 16) return null;
        var result = new TaxiNodeCatalog();
        for (int row = 0; row < dbc.RecordCount; row++)
        {
            uint id = dbc.GetUInt(row, 0);
            if (id == 0) continue;
            result._nodes[id] = new TaxiNodeInfo(id, dbc.GetUInt(row, 1),
                new Vector3(dbc.GetFloat(row, 2), dbc.GetFloat(row, 3), dbc.GetFloat(row, 4)),
                dbc.GetString(row, 5));
        }
        return result;
    }
}
