namespace MSUIClient.Formats;

public sealed record CreatureFamilyInfo(uint Id, string Name, uint FoodMask);

/// <summary>Build-5875 CreatureFamily/ItemPetFood display columns used by the pet paper doll.</summary>
public sealed class CreatureFamilyCatalog
{
    public const string FamilyPath = @"DBFilesClient\CreatureFamily.dbc";
    public const string FoodPath = @"DBFilesClient\ItemPetFood.dbc";
    private readonly Dictionary<uint, CreatureFamilyInfo> _families = [];
    private readonly Dictionary<uint, string> _foods = [];

    public bool TryGet(uint id, out CreatureFamilyInfo info) =>
        _families.TryGetValue(id, out info!);

    public string Diet(uint familyId)
    {
        if (!TryGet(familyId, out CreatureFamilyInfo family)) return "";
        return string.Join(", ", Enumerable.Range(0, 8)
            .Where(bit => (family.FoodMask & (1u << bit)) != 0)
            .Select(bit => _foods.GetValueOrDefault((uint)bit + 1, ""))
            .Where(name => name.Length > 0));
    }

    public static CreatureFamilyCatalog? Load(MpqMount mpq)
    {
        byte[]? familyBytes = mpq.ReadFile(FamilyPath);
        byte[]? foodBytes = mpq.ReadFile(FoodPath);
        DbcFile? family = familyBytes is null ? null : DbcFile.Parse(familyBytes);
        DbcFile? food = foodBytes is null ? null : DbcFile.Parse(foodBytes);
        if (family is null || family.FieldCount < 18 || family.RecordSize < 72 ||
            food is null || food.FieldCount < 10 || food.RecordSize < 40)
            return null;
        var result = new CreatureFamilyCatalog();
        for (int row = 0; row < family.RecordCount; row++)
        {
            uint id = family.GetUInt(row, 0);
            string name = family.GetString(row, 8);
            if (id != 0 && name.Length > 0)
                result._families[id] = new(id, name, family.GetUInt(row, 7));
        }
        for (int row = 0; row < food.RecordCount; row++)
        {
            uint id = food.GetUInt(row, 0);
            string name = food.GetString(row, 1);
            if (id != 0 && name.Length > 0) result._foods[id] = name;
        }
        return result;
    }
}
