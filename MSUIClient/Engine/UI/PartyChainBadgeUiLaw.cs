namespace MSUIClient.Engine.UI;

/// <summary>
/// Authored party-chain badge resources. The roster wire owns the state; this law only maps its
/// three values to original embedded art and preserves the logical footprint of the old glyph.
/// </summary>
public static class PartyChainBadgeUiLaw
{
    public const string LinkedResource =
        "MSUIClient.Assets.UI.PartyChain.party-chain-linked.png";
    public const string UnlinkedResource =
        "MSUIClient.Assets.UI.PartyChain.party-chain-unlinked.png";
    public const string WorldHoldResource =
        "MSUIClient.Assets.UI.PartyChain.party-chain-world-hold.png";

    // DrawChainGlyph's radius is shared by three established layouts. A 2.6x square keeps the
    // authored bezel at ~18 logical pixels on party frames and ~9 on command cards.
    public const float RadiusToSide = 2.6f;

    public static string ResourceForState(byte state) => state switch
    {
        0 => LinkedResource,
        2 => WorldHoldResource,
        _ => UnlinkedResource,
    };

    public static float SideForRadius(float radius) => MathF.Max(1f, radius * RadiusToSide);
}
