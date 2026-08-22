using MSUIClient.Net;

namespace MSUIClient.Engine.UI;

/// <summary>Current Benilla gossip option/quest row icon selection.</summary>
public static class GossipUiLaw
{
    /// <summary>
    /// Select one of an NPC text record's eight greeting blocks. The gender column
    /// is fixed for the whole draw and empty strings in that column are excluded;
    /// the other column is never a fallback. Roll has the reference's [1,2) shape.
    /// </summary>
    public static string? SelectGreeting(
        IReadOnlyList<NpcTextBlock> blocks, byte npcGender, float roll)
    {
        bool female = npcGender == 1;
        double sum = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            NpcTextBlock block = blocks[i];
            string column = female ? block.FemaleText : block.MaleText;
            if (column.Length > 0) sum += block.Probability;
        }

        double threshold = (2.0 - roll) * sum;
        double accumulated = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            NpcTextBlock block = blocks[i];
            string column = female ? block.FemaleText : block.MaleText;
            if (column.Length == 0) continue;
            accumulated += block.Probability;
            if (threshold <= accumulated) return column;
        }
        return null;
    }

    /// <summary>The reference constructs a uniform [1,2) float from 23 PRNG mantissa bits.</summary>
    public static float GreetingRoll(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        int bits = random.Next(0x80_0000) | 0x3f80_0000;
        return BitConverter.Int32BitsToSingle(bits);
    }

    public static string OptionIcon(byte icon) => icon switch
    {
        1 => @"Interface\GossipFrame\VendorGossipIcon",
        2 => @"Interface\GossipFrame\TaxiGossipIcon",
        3 => @"Interface\GossipFrame\TrainerGossipIcon",
        4 => @"Interface\GossipFrame\HealerGossipIcon",
        5 => @"Interface\GossipFrame\BinderGossipIcon",
        6 => @"Interface\GossipFrame\BankerGossipIcon",
        7 => @"Interface\GossipFrame\PetitionGossipIcon",
        8 => @"Interface\GossipFrame\TabardGossipIcon",
        9 => @"Interface\GossipFrame\BattleMasterGossipIcon",
        // Auctioneer is a real table name but has no build-5875 texture; Benilla's XML
        // deliberately falls back to the shipped gossip bubble for this missing art.
        _ => @"Interface\GossipFrame\GossipGossipIcon",
    };

    public static string QuestIcon(uint dialogStatus) =>
        QuestFrameUiLaw.GreetingPool(dialogStatus) == QuestGreetingPool.Active
            ? @"Interface\GossipFrame\ActiveQuestIcon"
            : @"Interface\GossipFrame\AvailableQuestIcon";
}
