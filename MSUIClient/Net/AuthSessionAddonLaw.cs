using System.IO.Compression;
using System.Text;

namespace MSUIClient.Net;

/// <summary>The stock secure-addon tail carried by every 1.12.1 CMSG_AUTH_SESSION.</summary>
public static class AuthSessionAddonLaw
{
    public readonly record struct SecureAddon(string Name, byte Flags, uint ModulusCrc, uint UrlCrc);

    public const uint StandardModulusCrc = 0x4C1C_776D;

    public static readonly SecureAddon[] StockSecureAddons =
    [
        Stock("Blizzard_AuctionUI"),
        Stock("Blizzard_BattlefieldMinimap"),
        Stock("Blizzard_BindingUI"),
        Stock("Blizzard_CombatText"),
        Stock("Blizzard_CraftUI"),
        Stock("Blizzard_GMSurveyUI"),
        Stock("Blizzard_InspectUI"),
        Stock("Blizzard_MacroUI"),
        Stock("Blizzard_RaidUI"),
        Stock("Blizzard_TalentUI"),
        Stock("Blizzard_TradeSkillUI"),
        Stock("Blizzard_TrainerUI"),
    ];

    public static byte[] Block(IReadOnlyList<SecureAddon> addons)
    {
        var writer = new PacketWriter(addons.Sum(addon => addon.Name.Length + 10));
        foreach (SecureAddon addon in addons)
        {
            writer.WriteBytes(Encoding.ASCII.GetBytes(addon.Name));
            writer.WriteU8(0);
            writer.WriteU8(addon.Flags);
            writer.WriteU32(addon.ModulusCrc);
            writer.WriteU32(addon.UrlCrc);
        }
        return writer.ToArray();
    }

    /// <summary>u32 uncompressed size plus RFC1950 zlib, or no bytes for no secure addons.</summary>
    public static byte[] Tail(IReadOnlyList<SecureAddon> addons)
    {
        if (addons.Count == 0) return [];

        byte[] plain = Block(addons);
        using var output = new MemoryStream();
        output.Write(BitConverter.GetBytes((uint)plain.Length));
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(plain);
        return output.ToArray();
    }

    public static byte[] StockTail() => Tail(StockSecureAddons);

    private static SecureAddon Stock(string name) =>
        new(name, 1, StandardModulusCrc, 0);
}
