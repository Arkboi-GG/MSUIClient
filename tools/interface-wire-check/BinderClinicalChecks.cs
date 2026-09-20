using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

internal static class BinderClinicalChecks
{
    public static void Run()
    {
        Check((ushort)Op.CMSG_BINDER_ACTIVATE == 0x01B5 &&
              (ushort)Op.SMSG_PLAYERBINDERROR == 0x01B6 &&
              (ushort)Op.SMSG_BINDER_CONFIRM == 0x02EB &&
              (ushort)Op.SMSG_BINDPOINTUPDATE == 0x0155 &&
              (ushort)Op.SMSG_PLAYERBOUND == 0x0158,
            "binder opcode drift");

        var confirmWriter = new PacketWriter();
        confirmWriter.WriteU64(0xF130000127000001);
        Check(BinderPackets.ParseConfirm(confirmWriter.ToArray()).BinderGuid ==
              0xF130000127000001, "binder confirm body drift");

        var boundWriter = new PacketWriter();
        boundWriter.WriteU64(0xF130000127000001);
        boundWriter.WriteU32(1519);
        PlayerBoundPacket bound = BinderPackets.ParsePlayerBound(boundWriter.ToArray());
        Check(bound.BinderGuid == 0xF130000127000001 && bound.AreaId == 1519,
            "player-bound body drift");

        var pointWriter = new PacketWriter();
        pointWriter.WriteF32(-9464.5f);
        pointWriter.WriteF32(62.1f);
        pointWriter.WriteF32(56f);
        pointWriter.WriteU32(0);
        pointWriter.WriteU32(87); // trailing AreaTable id in SMSG_BINDPOINTUPDATE
        BindPointPacket point = BinderPackets.ParseBindPoint(pointWriter.ToArray());
        Check(point.Position == new Vector3(-9464.5f, 62.1f, 56f) && point.MapId == 0 && point.AreaId == 87,
            "bind-point body drift");

        var spells = new SpellCatalog();
        SpellInfo hearth = default(SpellInfo) with
        {
            Id = 8690, Name = "Hearthstone", Rank = "",
            Description = "Returns you to $z. Speak to an Innkeeper in a different place to change your home location.",
        };
        Check(SpellTooltipLaw.Substitute(hearth.Description, hearth, spells,
                  homeAreaName: "Goldshire") == hearth.Description.Replace("$z", "Goldshire") &&
              SpellTooltipLaw.Build(hearth, spells, homeAreaName: "Northshire Valley").Description ==
                  hearth.Description.Replace("$z", "Northshire Valley"),
            "hearth item and spell tooltips must resolve the saved home area");
        foreach (string? unavailable in new string?[] { null, "", "  " })
            Check(SpellTooltipLaw.Substitute(hearth.Description, hearth, spells,
                      homeAreaName: unavailable) == hearth.Description.Replace("$z", "your home"),
                "an unreported home must use readable text without inventing a zone");
        Check(SpellTooltipLaw.Substitute("$z / $z / $s1 / $k", hearth, spells,
                  homeAreaName: "Goldshire") == "Goldshire / Goldshire / 0 / $k",
            "home substitution must coexist with numeric and unsupported tokens");

        Check(BinderConfirmUiLaw.Prompt("Stormwind City") ==
              "Do you want to make Stormwind City your new home?" &&
              BinderConfirmUiLaw.Prompt("") ==
              "Do you want to make your inn your new home?" &&
              BinderConfirmUiLaw.PlayerBoundText("Goldshire") ==
              "Goldshire is now your home." &&
              BinderConfirmUiLaw.PlayerBoundText("") is null,
            "binder text drift");

        BinderConfirmUiLaw.ScreenRect rect = BinderConfirmUiLaw.PopupRect(
            new Vector2(1920, 1080), 1.5f, 14f);
        Check(rect.Min == new Vector2(720, 192) && rect.Size.X == 480 &&
              BinderConfirmUiLaw.ButtonMin(1, 14f) == new Vector2(26, 38) &&
              BinderConfirmUiLaw.ButtonMin(2, 14f) == new Vector2(167, 38) &&
              BinderConfirmUiLaw.ButtonSize(1.5f) == new Vector2(192, 30) &&
              BinderConfirmUiLaw.ButtonUvMax == new Vector2(1f, .625f),
            "binder StaticPopup geometry drift");

        Check(BinderConfirmUiLaw.ShouldRemainOpen(true, true, true, false,
                  BinderConfirmUiLaw.ServiceRange) &&
              !BinderConfirmUiLaw.ShouldRemainOpen(true, true, true, false,
                  MathF.BitIncrement(BinderConfirmUiLaw.ServiceRange)) &&
              !BinderConfirmUiLaw.ShouldRemainOpen(true, false, true, false, 0) &&
              !BinderConfirmUiLaw.ShouldRemainOpen(true, true, true, true, 0),
            "binder service-range lifetime drift");

        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Hearth.cs"));
        string net = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene", "GameLoop.Net.cs"));
        string logout = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels", "GameLoop.Logout.cs"));
        Check(net.Contains("ResetHearth(clearBindPoint: false)", StringComparison.Ordinal) &&
              logout.Contains("ResetHearth()", StringComparison.Ordinal) &&
              runtime.Contains("if (clearBindPoint)", StringComparison.Ordinal),
            "a map transfer must retain the home; session teardown must clear it");
        Check(runtime.Contains("BinderConfirmUiLaw.PopupRect", StringComparison.Ordinal) &&
              runtime.Contains("BinderConfirmUiLaw.ButtonMin", StringComparison.Ordinal) &&
              !runtime.Contains("DrawHearthFrame", StringComparison.Ordinal) &&
              !runtime.Contains("Inn & Hearthstone", StringComparison.Ordinal) &&
              !runtime.Contains("ImGuiCond.FirstUseEver", StringComparison.Ordinal) &&
              !runtime.Contains("logicalDisplay", StringComparison.Ordinal) &&
              !runtime.Contains("new Vector2(", StringComparison.Ordinal) &&
              runtime.Contains("BinderConfirmUiLaw.ButtonSize", StringComparison.Ordinal) &&
              runtime.Contains("BinderConfirmUiLaw.ButtonUvMax", StringComparison.Ordinal),
            "binder renderer bypasses its modal law");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
