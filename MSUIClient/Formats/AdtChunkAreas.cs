namespace MSUIClient.Formats;

/// <summary>
/// The AreaTable id of every MCNK in an ADT, read from the chunk headers alone (index 0x04/0x08, AreaId 0x34) - no
/// heights, layers or alpha decoded, so a whole continent can be scanned in seconds. Used by the Quest Tabletop to
/// find a zone's board when WorldMapArea.dbc has no rectangle for it (pack zones such as Gilneas; QUEST_TABLETOP.md
/// §8, owner decision 2026-10-14: the bounding box of the chunks tagged with the zone or its subzones).
/// </summary>
public static class AdtChunkAreas
{
    private static readonly uint Mcnk = BitConverter.ToUInt32(new[] { (byte)'K', (byte)'N', (byte)'C', (byte)'M' }, 0);

    public static List<(int IndexX, int IndexY, uint AreaId)> Read(byte[] adt)
    {
        var list = new List<(int, int, uint)>(256);
        int pos = 0;
        while (pos + 8 <= adt.Length)
        {
            uint magic = BitConverter.ToUInt32(adt, pos);
            int size = BitConverter.ToInt32(adt, pos + 4);
            if (size < 0 || pos + 8L + size > adt.Length) break;
            int data = pos + 8;
            if (magic == Mcnk && size >= 0x38)
                list.Add((BitConverter.ToInt32(adt, data + 0x04), BitConverter.ToInt32(adt, data + 0x08), BitConverter.ToUInt32(adt, data + 0x34)));
            pos = data + size;
        }
        return list;
    }
}
