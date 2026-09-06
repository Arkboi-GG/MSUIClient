namespace MSUIClient.Formats;

/// <summary>Build-5875 classification fields; creature-target mask 14 is not a dispel type.</summary>
public static class SpellClassificationColumns
{
    public static (uint Category, uint DispelType, uint RequiredFocus) Read(DbcFile spells, int row)
    {
        if (spells.FieldCount < 16) throw new InvalidDataException("Spell classification needs columns through required focus");
        return (spells.GetUInt(row, 2), spells.GetUInt(row, 4), spells.GetUInt(row, 15));
    }
}
