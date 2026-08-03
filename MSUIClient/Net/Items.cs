using MSUIClient.Formats;

namespace MSUIClient.Net;

public readonly record struct ItemStat(uint Type, int Value);
public readonly record struct ItemDamage(float Min, float Max, uint School);

public sealed class ItemTemplate
{
    public uint Entry;
    public uint Class;
    public uint Subclass;
    public string Name = "";
    public uint DisplayInfoId;
    public string IconPath = @"Interface\Icons\INV_Misc_QuestionMark.blp";
    public uint Quality;
    public uint Flags;
    public uint BuyPrice;
    public uint SellPrice;
    public uint InventoryType;
    public int AllowableClass;
    public int AllowableRace;
    public uint ItemLevel;
    public uint RequiredLevel;
    public uint RequiredSkill;
    public uint RequiredSkillRank;
    public uint RequiredSpell;
    public uint MaxCount;
    public uint Stackable;
    public uint ContainerSlots;
    public List<ItemStat> Stats = [];
    public List<ItemDamage> Damages = [];
    public uint Armor;
    public uint[] Resistances = new uint[6];
    public uint DelayMs;
    public uint Bonding;
    public string Description = "";
    public uint Material;
    public uint Sheath;
    public byte UseSpellIndex;

    public static ItemTemplate? Parse(byte[] body)
    {
        var r = new PacketReader(body);
        uint rawEntry = r.ReadU32();
        if ((rawEntry & 0x8000_0000u) != 0) return null;
        var item = new ItemTemplate
        {
            Entry = rawEntry,
            Class = r.ReadU32(),
            Subclass = r.ReadU32(),
            Name = r.ReadCString(),
        };
        r.ReadCString(); r.ReadCString(); r.ReadCString();
        item.DisplayInfoId = r.ReadU32();
        item.Quality = r.ReadU32();
        item.Flags = r.ReadU32();
        item.BuyPrice = r.ReadU32();
        item.SellPrice = r.ReadU32();
        item.InventoryType = r.ReadU32();
        item.AllowableClass = r.ReadI32();
        item.AllowableRace = r.ReadI32();
        item.ItemLevel = r.ReadU32();
        item.RequiredLevel = r.ReadU32();
        item.RequiredSkill = r.ReadU32();
        item.RequiredSkillRank = r.ReadU32();
        item.RequiredSpell = r.ReadU32();
        r.ReadU32(); r.ReadU32(); r.ReadU32(); r.ReadU32(); // honor/city/reputation
        item.MaxCount = r.ReadU32();
        item.Stackable = r.ReadU32();
        item.ContainerSlots = r.ReadU32();
        for (int i = 0; i < 10; i++)
        {
            uint type = r.ReadU32(); int value = r.ReadI32();
            if (value != 0) item.Stats.Add(new ItemStat(type, value));
        }
        for (int i = 0; i < 5; i++)
        {
            float min = r.ReadF32(), max = r.ReadF32(); uint school = r.ReadU32();
            if (min != 0 || max != 0) item.Damages.Add(new ItemDamage(min, max, school));
        }
        item.Armor = r.ReadU32();
        for (int i = 0; i < item.Resistances.Length; i++) item.Resistances[i] = r.ReadU32();
        item.DelayMs = r.ReadU32();
        r.ReadU32(); r.ReadF32();   // ammo type, ranged range modifier
        bool foundUseSpell = false;
        for (byte block = 0; block < 5; block++)
        {
            uint spell = r.ReadU32();
            uint trigger = r.ReadU32();
            r.ReadI32(); r.ReadI32(); r.ReadU32(); r.ReadI32();
            if (spell != 0 && trigger == 0 && !foundUseSpell)
            {
                item.UseSpellIndex = block;
                foundUseSpell = true;
            }
        }
        item.Bonding = r.ReadU32();
        item.Description = r.ReadCString();
        r.Skip(5 * 4);              // page text/language/page material/start quest/lock
        item.Material = r.ReadU32();
        item.Sheath = r.ReadU32();
        return item;
    }
}

/// <summary>Ask-once item-template cache; negative answers are cached too.</summary>
public sealed class ItemTemplateCache
{
    private readonly Dictionary<uint, ItemTemplate?> _templates = new();
    private readonly HashSet<uint> _pending = new();
    private readonly ItemDisplayTable? _displays;

    public ItemTemplateCache(ItemDisplayTable? displays) => _displays = displays;
    public int Count => _templates.Count;
    public int PendingCount => _pending.Count;
    public bool TryGet(uint entry, out ItemTemplate? item) => _templates.TryGetValue(entry, out item);
    public ItemTemplate? FindByName(string name) => _templates.Values.FirstOrDefault(x =>
        x is not null && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public void Require(uint entry, ulong guid, NetworkClient net)
    {
        if (entry == 0 || _templates.ContainsKey(entry) || !_pending.Add(entry)) return;
        net.ItemQuery(entry, guid);
    }

    /// <summary>
    /// Resolve an icon straight from a wire displayInfoId (SMSG_LOOT_RESPONSE carries one per
    /// row), so loot icons never wait on the item-template round trip. Null when unknown.
    /// </summary>
    public string? IconForDisplay(uint displayInfoId)
    {
        if (displayInfoId == 0 || _displays?.Find(displayInfoId) is not { } display ||
            display.InventoryIcon.Length == 0) return null;
        string icon = display.InventoryIcon;
        if (!icon.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) icon += ".blp";
        return icon.StartsWith("Interface", StringComparison.OrdinalIgnoreCase)
            ? icon
            : @"Interface\Icons\" + icon;
    }

    public void Apply(byte[] body)
    {
        if (body.Length < 4) return;
        uint raw = BitConverter.ToUInt32(body, 0);
        uint entry = raw & 0x7fff_ffffu;
        _pending.Remove(entry);
        ItemTemplate? item = ItemTemplate.Parse(body);
        if (item is not null && _displays?.Find(item.DisplayInfoId) is { } display && display.InventoryIcon.Length > 0)
            item.IconPath = display.InventoryIcon.StartsWith("Interface", StringComparison.OrdinalIgnoreCase)
                ? display.InventoryIcon + (display.InventoryIcon.EndsWith(".blp", StringComparison.OrdinalIgnoreCase) ? "" : ".blp")
                : @"Interface\Icons\" + display.InventoryIcon + (display.InventoryIcon.EndsWith(".blp", StringComparison.OrdinalIgnoreCase) ? "" : ".blp");
        _templates[entry] = item;
    }
}
