using System.Numerics;

namespace MSUIClient.Net;

public readonly record struct GossipOption(uint ListId, byte Icon, bool Coded, string Text);
public readonly record struct GossipQuest(uint QuestId, uint Icon, int Level, string Title);

public sealed record GossipMenu(
    ulong SourceGuid,
    uint TextId,
    IReadOnlyList<GossipOption> Options,
    IReadOnlyList<GossipQuest> Quests);

public readonly record struct DialogueEmote(uint Id, uint DelayMs);
public readonly record struct NpcTextBlock(float Probability, string MaleText, string FemaleText,
    uint Language = 0, IReadOnlyList<DialogueEmote>? Emotes = null);
public sealed record NpcText(uint TextId, IReadOnlyList<NpcTextBlock> Blocks);
public readonly record struct GossipPoi(
    uint Flags, Vector2 Position, uint Icon, uint Data, string Name);

public static class GossipPackets
{
    public static GossipMenu ParseMenu(byte[] body)
    {
        var r = new PacketReader(body);
        ulong guid = r.ReadU64();
        uint textId = r.ReadU32();
        uint optionCount = r.ReadU32();
        if (optionCount > 15) throw new InvalidDataException($"SMSG_GOSSIP_MESSAGE option count {optionCount} exceeds 15");
        var options = new List<GossipOption>((int)optionCount);
        for (uint i = 0; i < optionCount; i++)
            options.Add(new GossipOption(r.ReadU32(), r.ReadU8(), r.ReadU8() != 0, r.ReadCString()));

        uint questCount = r.ReadU32();
        if (questCount > 32) throw new InvalidDataException($"SMSG_GOSSIP_MESSAGE quest count {questCount} exceeds 32");
        var quests = new List<GossipQuest>((int)questCount);
        for (uint i = 0; i < questCount; i++)
            quests.Add(new GossipQuest(r.ReadU32(), r.ReadU32(), r.ReadI32(), r.ReadCString()));
        if (r.Remaining != 0)
            throw new InvalidDataException($"SMSG_GOSSIP_MESSAGE has {r.Remaining} trailing byte(s)");
        return new GossipMenu(guid, textId, options, quests);
    }

    public static NpcText ParseText(byte[] body)
    {
        var r = new PacketReader(body);
        uint textId = r.ReadU32();
        var blocks = new List<NpcTextBlock>(8);
        for (int i = 0; i < 8; i++)
        {
            float probability = r.ReadF32();
            string m = r.ReadCString();
            string f = r.ReadCString();
            uint language = r.ReadU32();
            var emotes = new DialogueEmote[3];
            for (int e = 0; e < emotes.Length; e++)
            {
                uint delay = r.ReadU32(), id = r.ReadU32();
                emotes[e] = new(id, delay);
            }
            blocks.Add(new NpcTextBlock(probability, m, f, language, emotes));
        }
        if (r.Remaining != 0)
            throw new InvalidDataException($"SMSG_NPC_TEXT_UPDATE has {r.Remaining} trailing byte(s)");
        return new NpcText(textId, blocks);
    }

    public static GossipPoi ParsePoi(byte[] body)
    {
        var r = new PacketReader(body);
        uint flags = r.ReadU32();
        var position = new Vector2(r.ReadF32(), r.ReadF32());
        uint icon = r.ReadU32();
        uint data = r.ReadU32();
        string name = r.ReadCString();
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y))
            throw new InvalidDataException("SMSG_GOSSIP_POI contains a non-finite position");
        if (r.Remaining != 0)
            throw new InvalidDataException($"SMSG_GOSSIP_POI has {r.Remaining} trailing byte(s)");
        return new GossipPoi(flags, position, icon, data, name);
    }
}
