namespace MSUIClient.Net;

public readonly record struct QuestRewardItem(uint ItemId, uint Count, uint DisplayId);
public readonly record struct QuestStatus(ulong GiverGuid, uint Status);
public sealed record QuestList(ulong GiverGuid, string Greeting, uint EmoteDelay, uint Emote,
    IReadOnlyList<GossipQuest> Quests);
public sealed record QuestDetails(ulong GiverGuid, uint QuestId, string Title, string Details,
    string Objectives, bool AutoFinish, IReadOnlyList<QuestRewardItem> ChoiceRewards,
    IReadOnlyList<QuestRewardItem> FixedRewards, int Money, uint RewardSpell);
public sealed record QuestOffer(ulong GiverGuid, uint QuestId, string Title, string Text,
    bool EnableNext, IReadOnlyList<QuestRewardItem> ChoiceRewards,
    IReadOnlyList<QuestRewardItem> FixedRewards, int Money, uint RewardSpell);
public sealed record QuestRequestItems(ulong GiverGuid, uint QuestId, string Title, string Text,
    uint RequiredMoney, IReadOnlyList<QuestRewardItem> RequiredItems, bool Completable);
public readonly record struct QuestComplete(uint QuestId, uint Experience, int Money,
    IReadOnlyList<(uint ItemId, uint Count)> Rewards);
public readonly record struct QuestKillUpdate(uint QuestId, uint Entry, uint Current, uint Required, ulong Guid);
public readonly record struct QuestGiverFailure(uint QuestId, uint Reason);
public readonly record struct QuestLogObjective(uint CreatureOrGo, uint RequiredCount,
    uint ItemId, uint ItemCount, string Text);
public sealed record QuestTemplate(uint QuestId, uint Level, int ZoneOrSort, string Title,
    string ObjectivesText, string Details, int Money, uint RewardSpell,
    IReadOnlyList<QuestRewardItem> FixedRewards, IReadOnlyList<QuestRewardItem> ChoiceRewards,
    IReadOnlyList<QuestLogObjective> Objectives, uint Flags = 0)
{
    /// <summary>QUEST_FLAGS_SHARABLE. The server does NOT gate the push on this —
    /// it happily forwards an unsharable quest and then refuses every accept — so
    /// the client is what must keep the Share Quest button honest.</summary>
    public bool Sharable => (Flags & 0x08) != 0;
}

public static class QuestPackets
{
    public static QuestStatus ParseStatus(byte[] body)
    { var r = Exact(body, 12); return new(r.ReadU64(), r.ReadU32()); }

    public static QuestList ParseList(byte[] body)
    {
        var r = new PacketReader(body); ulong guid = r.ReadU64(); string greeting = r.ReadCString();
        uint delay = r.ReadU32(), emote = r.ReadU32(); byte count = r.ReadU8();
        if (count > 32) throw new InvalidDataException($"quest list count {count} exceeds 32");
        var quests = new List<GossipQuest>(count);
        for (int i = 0; i < count; i++) quests.Add(new(r.ReadU32(), r.ReadU32(), r.ReadI32(), r.ReadCString()));
        RequireEnd(r, "quest list"); return new(guid, greeting, delay, emote, quests);
    }

    public static QuestDetails ParseDetails(byte[] body)
    {
        var r = new PacketReader(body); ulong guid = r.ReadU64(); uint id = r.ReadU32();
        string title = r.ReadCString(), details = r.ReadCString(), objectives = r.ReadCString();
        bool auto = r.ReadU32() != 0; var choice = ReadItems(r, 6, "choice reward");
        var fixedItems = ReadItems(r, 5, "fixed reward"); int money = r.ReadI32(); uint spell = r.ReadU32();
        uint emotes = r.ReadU32(); if (emotes > 4) throw new InvalidDataException($"detail emote count {emotes} exceeds 4");
        r.Skip(checked((int)emotes * 8)); RequireEnd(r, "quest details");
        return new(guid, id, title, details, objectives, auto, choice, fixedItems, money, spell);
    }

    public static QuestOffer ParseOffer(byte[] body)
    {
        var r = new PacketReader(body); ulong guid = r.ReadU64(); uint id = r.ReadU32();
        string title = r.ReadCString(), bodyText = r.ReadCString(); bool next = r.ReadU32() != 0;
        uint emotes = r.ReadU32(); if (emotes > 4) throw new InvalidDataException($"offer emote count {emotes} exceeds 4");
        r.Skip(checked((int)emotes * 8)); var choice = ReadItems(r, 6, "choice reward");
        var fixedItems = ReadItems(r, 5, "fixed reward"); int money = r.ReadI32(); r.ReadU32(); uint spell = r.ReadU32();
        RequireEnd(r, "quest offer"); return new(guid, id, title, bodyText, next, choice, fixedItems, money, spell);
    }

    public static QuestRequestItems ParseRequestItems(byte[] body)
    {
        var r = new PacketReader(body); ulong guid = r.ReadU64(); uint id = r.ReadU32();
        string title = r.ReadCString(), bodyText = r.ReadCString(); r.Skip(12); uint money = r.ReadU32();
        var items = ReadItems(r, 4, "required item"); r.ReadU32(); uint flags = r.ReadU32(); r.Skip(8);
        RequireEnd(r, "quest request items"); return new(guid, id, title, bodyText, money, items, flags != 0);
    }

    public static QuestComplete ParseComplete(byte[] body)
    {
        var r = new PacketReader(body); uint id = r.ReadU32(); r.ReadU32(); uint xp = r.ReadU32(); int money = r.ReadI32();
        uint count = r.ReadU32(); if (count > 5) throw new InvalidDataException($"complete reward count {count} exceeds 5");
        var items = new List<(uint, uint)>((int)count);
        for (uint i = 0; i < count; i++) items.Add((r.ReadU32(), r.ReadU32()));
        RequireEnd(r, "quest complete"); return new(id, xp, money, items);
    }

    public static QuestKillUpdate ParseKill(byte[] body)
    { var r = Exact(body, 24); return new(r.ReadU32(), r.ReadU32(), r.ReadU32(), r.ReadU32(), r.ReadU64()); }
    public static (uint ItemId, uint Count) ParseItem(byte[] body)
    { var r = Exact(body, 8); return (r.ReadU32(), r.ReadU32()); }
    public static uint ParseQuestId(byte[] body)
    { var r = Exact(body, 4); return r.ReadU32(); }
    public static uint ParseInvalidReason(byte[] body)
    { var r = Exact(body, 4); return r.ReadU32(); }
    public static QuestGiverFailure ParseGiverFailure(byte[] body)
    { var r = Exact(body, 8); return new(r.ReadU32(), r.ReadU32()); }

    /// <summary>
    /// SMSG_QUEST_QUERY_RESPONSE: 15 fixed header dwords, four fixed rewards, six fixed choices,
    /// the map-point quad, four C strings, four objective quads, then four objective C strings.
    /// </summary>
    public static QuestTemplate ParseQueryResponse(byte[] body)
    {
        var r = new PacketReader(body);
        uint id = r.ReadU32();
        r.ReadU32(); // method
        uint level = r.ReadU32();
        int zoneOrSort = r.ReadI32();
        r.Skip(6 * 4); // quest type through next quest in chain
        int money = r.ReadI32();
        r.ReadU32(); // reward money at max level
        uint rewardSpell = r.ReadU32();
        r.ReadU32(); // source item
        uint questFlags = r.ReadU32();
        QuestRewardItem[] fixedRewards = ReadFixedTemplateItems(r, 4);
        QuestRewardItem[] choiceRewards = ReadFixedTemplateItems(r, 6);
        r.Skip(4 * 4); // map id, x, y, point option
        string title = r.ReadCString();
        string objectivesText = r.ReadCString();
        string details = r.ReadCString();
        r.ReadCString(); // end text

        var raw = new (uint CreatureOrGo, uint Required, uint Item, uint ItemCount)[4];
        for (int i = 0; i < raw.Length; i++)
            raw[i] = (r.ReadU32(), r.ReadU32(), r.ReadU32(), r.ReadU32());
        var result = new QuestLogObjective[4];
        for (int i = 0; i < result.Length; i++)
        {
            string text = r.ReadCString();
            result[i] = new(raw[i].CreatureOrGo, raw[i].Required,
                raw[i].Item, raw[i].ItemCount, text);
        }
        RequireEnd(r, "quest query response");
        return new(id, level, zoneOrSort, title, objectivesText, details, money, rewardSpell,
            fixedRewards, choiceRewards, result, questFlags);
    }

    private static QuestRewardItem[] ReadFixedTemplateItems(PacketReader r, int slots)
    {
        var result = new List<QuestRewardItem>(slots);
        for (int i = 0; i < slots; i++)
        {
            uint item = r.ReadU32(), count = r.ReadU32();
            if (item != 0) result.Add(new(item, count, 0));
        }
        return result.ToArray();
    }

    private static IReadOnlyList<QuestRewardItem> ReadItems(PacketReader r, uint maximum, string label)
    {
        uint count = r.ReadU32(); if (count > maximum) throw new InvalidDataException($"{label} count {count} exceeds {maximum}");
        var items = new List<QuestRewardItem>((int)count);
        for (uint i = 0; i < count; i++) items.Add(new(r.ReadU32(), r.ReadU32(), r.ReadU32()));
        return items;
    }
    private static PacketReader Exact(byte[] body, int bytes)
    { if (body.Length != bytes) throw new InvalidDataException($"expected {bytes} bytes, got {body.Length}"); return new(body); }
    private static void RequireEnd(PacketReader r, string label)
    { if (r.Remaining != 0) throw new InvalidDataException($"{label} has {r.Remaining} trailing bytes"); }
}
