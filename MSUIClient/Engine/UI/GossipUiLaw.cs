using MSUIClient.Net;

namespace MSUIClient.Engine.UI;

public readonly record struct GossipLogicalRect(float X, float Y, float Width, float Height)
{
    public System.Numerics.Vector2 Min => new(X, Y);
    public System.Numerics.Vector2 Size => new(Width, Height);
}

/// <summary>Current Benilla gossip option/quest row icon selection.</summary>
public static class GossipUiLaw
{
    public const float Width = 384f;
    public const float Height = 512f;
    public const float WindowY = 104f;
    public const float ScrollStep = 20f;
    public const float ScrollPad = 20f;
    public const int MaximumRows = 32;
    public static readonly GossipLogicalRect Portrait = new(7, 6, 60, 60);
    public static readonly GossipLogicalRect Scroll = new(23, 81, 300, 334);
    public static readonly GossipLogicalRect Greeting = new(33, 91, 270, 0);
    public static readonly GossipLogicalRect ScrollUp = new(329, 81, 16, 16);
    public static readonly GossipLogicalRect ScrollTrack = new(329, 97, 16, 302);
    public static readonly GossipLogicalRect ScrollDown = new(329, 399, 16, 16);
    public static readonly GossipLogicalRect Goodbye = new(267, 417, 78, 22);
    public static readonly GossipLogicalRect Close = new(326, 15, 32, 32);

    public static float RowTop(float greetingHeight) => 111f + Math.Max(0, greetingHeight);
    public static float RowHeight(float measuredTextHeight) =>
        Math.Max(16f, measuredTextHeight) + 2f;
    public static float ContentHeight(float greetingHeight,
        IReadOnlyList<float> rowHeights)
    {
        float bottom = 10f + Math.Max(0, greetingHeight);
        if (rowHeights.Count > 0)
            bottom = 30f + Math.Max(0, greetingHeight) + rowHeights.Sum();
        return Math.Max(Scroll.Height, bottom + ScrollPad);
    }
    public static float MaximumScroll(float contentHeight) =>
        Math.Max(0, contentHeight - Scroll.Height);
    public static float ClampScroll(float value, float contentHeight) =>
        Math.Clamp(value, 0, MaximumScroll(contentHeight));
    public static float WheelScroll(float value, float contentHeight, float wheel) =>
        ClampScroll(value - Math.Sign(wheel) * ScrollStep, contentHeight);
    public static float ThumbY(float value, float contentHeight)
    {
        float maximum = MaximumScroll(contentHeight);
        float fraction = maximum <= 0 ? 0 : Math.Clamp(value / maximum, 0, 1);
        return ScrollTrack.Y + fraction * (ScrollTrack.Height - 16f);
    }

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
