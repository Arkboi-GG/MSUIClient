namespace MSUIClient.Formats;

/// <summary>Build-5875 Spell.dbc columns: stack=39, tools=40..41, reagent IDs=42..49, counts=50..57.</summary>
public static class SpellRequirementColumns
{
    public static (uint[] Tools, SpellReagent[] Reagents) Read(DbcFile spells, int row)
    {
        if (spells.FieldCount < 58) throw new InvalidDataException("Spell requirements need all eight reagent/count fields");
        uint[] tools = Enumerable.Range(0, 2).Select(i => spells.GetUInt(row, 40 + i)).Where(x => x != 0).ToArray();
        var reagents = new List<SpellReagent>(8);
        for (int i = 0; i < 8; i++)
        {
            int item = spells.GetInt(row, 42 + i);
            uint count = spells.GetUInt(row, 50 + i);
            if (item > 0 && count > 0) reagents.Add(new((uint)item, count));
        }
        return (tools, reagents.ToArray());
    }
}
