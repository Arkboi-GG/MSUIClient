using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

internal static class DeathFrameClinicalChecks
{
    public static void Run()
    {
        Check((ushort)Op.CMSG_REPOP_REQUEST == 0x015A &&
              (ushort)Op.SMSG_RESURRECT_REQUEST == 0x015B &&
              (ushort)Op.CMSG_RESURRECT_RESPONSE == 0x015C &&
              (ushort)Op.CMSG_RECLAIM_CORPSE == 0x01D2 &&
              (ushort)Op.CMSG_SPIRIT_HEALER_ACTIVATE == 0x021C &&
              (ushort)Op.MSG_CORPSE_QUERY == 0x0216 &&
              (ushort)Op.SMSG_SPIRIT_HEALER_CONFIRM == 0x0222 &&
              (ushort)Op.SMSG_CORPSE_RECLAIM_DELAY == 0x0269 &&
              (ushort)Op.SMSG_DURABILITY_DAMAGE_DEATH == 0x02BD,
            "death/corpse opcode family drift");

        CorpseLocation absent = DeathPackets.ParseCorpseQuery([0]);
        var corpseBody = new PacketWriter();
        corpseBody.WriteU8(1); corpseBody.WriteI32(0);
        corpseBody.WriteF32(-8949.95f); corpseBody.WriteF32(-132.49f);
        corpseBody.WriteF32(83.53f); corpseBody.WriteU32(36);
        CorpseLocation found = DeathPackets.ParseCorpseQuery(corpseBody.ToArray());
        Check(!absent.Found && found == new CorpseLocation(true, 0,
                  new Vector3(-8949.95f, -132.49f, 83.53f), 36),
            "MSG_CORPSE_QUERY's 1/21-byte response shapes drift");
        CheckThrows(() => DeathPackets.ParseCorpseQuery([0, 0]),
            "MSG_CORPSE_QUERY accepted a not-found tail");

        Check(DeathPackets.ParseReclaimDelay(Convert.FromHexString("30750000")) == 30_000,
            "SMSG_CORPSE_RECLAIM_DELAY millisecond body drift");
        CheckThrows(() => DeathPackets.ParseReclaimDelay([1, 2, 3, 4, 5]),
            "SMSG_CORPSE_RECLAIM_DELAY accepted trailing bytes");

        var offerBody = new PacketWriter();
        offerBody.WriteU64(0x000000010000002A);
        offerBody.WriteU32(1); offerBody.WriteU8(0);
        offerBody.WriteU8(0); offerBody.WriteU8(1);
        ResurrectRequestPacket offer = DeathPackets.ParseResurrectRequest(offerBody.ToArray());
        Check(offer == new ResurrectRequestPacket(0x000000010000002A, "", false, true),
            "SMSG_RESURRECT_REQUEST length-prefixed cstring/flag shape drift");
        CheckThrows(() => DeathPackets.ParseResurrectRequest(
                offerBody.ToArray().Concat(new byte[] { 0xCC }).ToArray()),
            "SMSG_RESURRECT_REQUEST accepted trailing bytes");

        Check(DeathPackets.ParseSpiritHealerConfirm(
                  Convert.FromHexString("B32A0000BE0F30F1")) == 0xF1300FBE00002AB3 &&
              Convert.ToHexString(WorldSession.BuildSpiritHealerBody(
                  0xF1300FBE00002AB3)) == "B32A0000BE0F30F1" &&
              Convert.ToHexString(WorldSession.BuildReclaimCorpseBody(
                  0xF500000000000001)) == "01000000000000F5" &&
              Convert.ToHexString(WorldSession.BuildResurrectResponseBody(0x2A, true)) ==
                  "2A0000000000000001",
            "death client/server full-guid golden bodies drift");

        Check(DeathFrameUiLaw.CorpseRange == 40 &&
              DeathFrameUiLaw.SpiritHealerRange == 5.5556f &&
              DeathFrameUiLaw.ReleaseWindowSeconds == 360 &&
              DeathFrameUiLaw.ResurrectOfferSeconds == 60 &&
              DeathFrameUiLaw.ReleaseText(true, 61) == "2 minutes until release" &&
              DeathFrameUiLaw.RecoverText(2) == "2 seconds until resurrection" &&
              DeathFrameUiLaw.RecoverText(0) == "Resurrect now?" &&
              DeathFrameUiLaw.SicknessDuration(10) is null &&
              DeathFrameUiLaw.SicknessDuration(11) == "1 minute" &&
              DeathFrameUiLaw.SicknessDuration(19) == "9 minutes" &&
              DeathFrameUiLaw.SicknessDuration(20) == "10 minutes",
            "DeathFrame countdown/range/sickness law drift");
        DeathFrameUiLaw.ScreenRect ordinary = DeathFrameUiLaw.PopupRect(
            new Vector2(1920, 1080), 1, 28, false);
        DeathFrameUiLaw.ScreenRect alert = DeathFrameUiLaw.PopupRect(
            new Vector2(1920, 1080), 1, 28, true);
        Check(ordinary.Min.X == 800 && ordinary.Min.Y == DeathFrameUiLaw.PopupTop &&
              ordinary.Size.X == 320 && alert.Min.X == 750 && alert.Size.X == 420 &&
              DeathFrameUiLaw.ButtonMin(1, 2, 320, 28).X == 26 &&
              DeathFrameUiLaw.ButtonMin(2, 2, 320, 28).X == 167 &&
              DeathFrameUiLaw.AlertIconDimensions == new Vector2(64, 64) &&
              DeathFrameUiLaw.ButtonSize(1.5f) == new Vector2(192, 30) &&
              DeathFrameUiLaw.DialogButtonUvMax == new Vector2(1, .625f) &&
              !DeathFrameUiLaw.HideOnEscape(DeathDialogKind.Release) &&
              !DeathFrameUiLaw.HideOnEscape(DeathDialogKind.RecoverCorpse) &&
              DeathFrameUiLaw.HideOnEscape(DeathDialogKind.Resurrect) &&
              DeathFrameUiLaw.HideOnEscape(DeathDialogKind.XpLoss),
            "DeathFrame StaticPopup seat/width/button/Escape law drift");
        Check(DeathFrameUiLaw.TryWorldMapFraction(0, 0, new Vector3(50, 25, 0),
                  0, 100, 0, 100, out Vector2 corpseFraction) &&
              corpseFraction == new Vector2(.25f, .5f) &&
              !DeathFrameUiLaw.TryWorldMapFraction(1, 0, Vector3.Zero,
                  0, 100, 0, 100, out _) &&
              DeathFrameUiLaw.TryMinimapCorpseRect(0, 0, Vector3.Zero,
                  new Vector3(10, 0, 0), Vector2.Zero, new Vector2(140), 100,
                  out DeathFrameUiLaw.ScreenRect corpseRect) &&
              corpseRect.Size == new Vector2(15.4f) &&
              DeathFrameUiLaw.CorpseUvMin == new Vector2(.875f, 0) &&
              DeathFrameUiLaw.CorpseUvMax == new Vector2(1, .125f),
            "corpse world-map/minimap projection or POIIcons skull-cell law drift");

        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.DeathRez.cs"));
        string dispatch = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        string draw = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.CombatFeedback.cs"));
        string settings = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Settings.cs"));
        string worldMap = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.WorldMap.cs"));
        string minimap = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.Minimap.cs"));
        Check(runtime.Contains("DeathPackets.ParseCorpseQuery", StringComparison.Ordinal) &&
              runtime.Contains("DeathPackets.ParseResurrectRequest", StringComparison.Ordinal) &&
              runtime.Contains("DeathFrameUiLaw.PopupRect", StringComparison.Ordinal) &&
              runtime.Contains("DeathFrameUiLaw.ButtonMin", StringComparison.Ordinal) &&
              runtime.Contains("DeathFrameUiLaw.ButtonSize", StringComparison.Ordinal) &&
              !runtime.Contains("new Vector2", StringComparison.Ordinal) &&
              runtime.Contains("_xpLossStage = 2", StringComparison.Ordinal) &&
              runtime.Contains("TryDismissDeathConfirmationOnEscape", StringComparison.Ordinal) &&
              !runtime.Contains("BeginVanillaWindow", StringComparison.Ordinal) &&
              !runtime.Contains("ImGui.SetNextWindowPos(new", StringComparison.Ordinal) &&
              !runtime.Contains("new Vector2(384", StringComparison.Ordinal) &&
              dispatch.Contains("case Op.MSG_CORPSE_QUERY", StringComparison.Ordinal) &&
              dispatch.Contains("case Op.SMSG_SPIRIT_HEALER_CONFIRM", StringComparison.Ordinal) &&
              dispatch.Contains("case Op.SMSG_DURABILITY_DAMAGE_DEATH", StringComparison.Ordinal) &&
              draw.IndexOf("ResolveAndDrawSharedGameTooltip();", StringComparison.Ordinal) <
                  draw.IndexOf("DrawDeathRezFrame();", StringComparison.Ordinal) &&
              settings.Contains("_deathRezOpen ||", StringComparison.Ordinal) &&
              settings.Contains("TryDismissDeathConfirmationOnEscape()", StringComparison.Ordinal) &&
              worldMap.Contains("DrawWorldMapCorpseMarker", StringComparison.Ordinal) &&
              worldMap.Contains("DeathFrameUiLaw.TryWorldMapFraction", StringComparison.Ordinal) &&
              minimap.Contains("DrawMinimapCorpseMarker", StringComparison.Ordinal) &&
              minimap.Contains("DeathFrameUiLaw.TryMinimapCorpseRect", StringComparison.Ordinal),
            "DeathFrame production wiring bypasses strict wire, dialog strata, or UI law");
    }

    private static void CheckThrows(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidDataException(message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
