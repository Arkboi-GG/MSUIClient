using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

internal static class DuelFrameClinicalChecks
{
    public static void Run()
    {
        Check((ushort)Op.SMSG_DUEL_REQUESTED == 0x0167 &&
              (ushort)Op.SMSG_DUEL_OUTOFBOUNDS == 0x0168 &&
              (ushort)Op.SMSG_DUEL_INBOUNDS == 0x0169 &&
              (ushort)Op.SMSG_DUEL_COMPLETE == 0x016a &&
              (ushort)Op.SMSG_DUEL_WINNER == 0x016b &&
              (ushort)Op.CMSG_DUEL_ACCEPTED == 0x016c &&
              (ushort)Op.CMSG_DUEL_CANCELLED == 0x016d &&
              (ushort)Op.SMSG_DUEL_COUNTDOWN == 0x02b7,
            "build-5875 duel opcode family drift");

        const ulong arbiter = 0xf100_0000_dead_beef;
        const ulong challenger = 7;
        byte[] request = [.. BitConverter.GetBytes(arbiter), .. BitConverter.GetBytes(challenger)];
        Check(DuelPackets.ParseRequested(request) == new DuelRequestedWire(arbiter, challenger) &&
              DuelPackets.BuildReplyBody(arbiter).SequenceEqual(BitConverter.GetBytes(arbiter)) &&
              !DuelPackets.ParseComplete([0]) && DuelPackets.ParseComplete([1]) &&
              DuelPackets.ParseCountdownSeconds(BitConverter.GetBytes(3999u)) == 3,
            "duel request/reply/complete/countdown wire drift");

        DuelWinnerWire winner = DuelPackets.ParseWinner(
            [1, .. "Onerogue\0Twomage\0"u8.ToArray()]);
        Check(winner == new DuelWinnerWire(true, "Onerogue", "Twomage") &&
              DuelFrameUiLaw.WinnerLine(false, "Onerogue", "Twomage") ==
                  "Onerogue has defeated Twomage in a duel" &&
              DuelFrameUiLaw.WinnerLine(true, "Onerogue", "Twomage") ==
                  "Twomage has fled from Onerogue in a duel" &&
              DuelFrameUiLaw.CountdownLine(3) == "Duel starting: 3",
            "duel outcome/countdown text drift");
        Reject(() => DuelPackets.ParseWinner([0, (byte)'A']),
            "unterminated duel winner name accepted");
        Reject(() => DuelPackets.ParseRequested(request[..^1]),
            "truncated duel request accepted");
        Reject(() => DuelPackets.ParseCountdownSeconds([0, 0, 0, 0, 1]),
            "duel countdown trailing byte accepted");

        SpellInfo duelSpell = Spell(7266, [83u, 0, 0]);
        SpellInfo ordinary = Spell(1, [0u, 83, 0]);
        Check(DuelFrameUiLaw.IsDuelSpell(duelSpell) &&
              !DuelFrameUiLaw.IsDuelSpell(ordinary) &&
              DuelFrameUiLaw.DuelRowEnabled(false, false, true, 99.9f) &&
              !DuelFrameUiLaw.DuelRowEnabled(false, false, true, 100f) &&
              !DuelFrameUiLaw.DuelRowEnabled(true, false, true, 1) &&
              DuelFrameUiLaw.RequestedText("Onerogue") ==
                  "Onerogue has challenged you to a duel." &&
              DuelFrameUiLaw.OutOfBoundsText(9.2) ==
                  "Exiting duel area, you will forfeit in 10 seconds." &&
              DuelFrameUiLaw.OutOfBoundsText(.2) ==
                  "Exiting duel area, you will forfeit in 1 second." &&
              DuelFrameUiLaw.PopupSize(12, buttons: true) == new System.Numerics.Vector2(320, 72) &&
              DuelFrameUiLaw.TextLineCenter(0) == new System.Numerics.Vector2(160, 22),
            "duel spell/UnitPopup/dialog text law drift");

        StaticPopupCoordinatorLaw.Plan shown = StaticPopupCoordinatorLaw.Show(
            StaticPopupCoordinatorLaw.Slots.Empty, DuelFrameUiLaw.RequestedDefinition,
            playerDeadOrGhost: false, dataToken: "Onerogue");
        Check(shown.Outcome == StaticPopupCoordinatorLaw.Outcome.Shown &&
              shown.Slots.First is { TimeLeft: 60 } &&
              shown.Effects.Any(effect => effect.Kind ==
                  StaticPopupCoordinatorLaw.EffectKind.EntrySound &&
                  effect.Value == DuelFrameUiLaw.InviteSound) &&
              DuelFrameUiLaw.Visible(shown.Slots,
                  DuelFrameUiLaw.RequestedPopupType) is { Slot: 1 },
            "duel challenge StaticPopup definition drift");

        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.Duel.cs"));
        string unitPopup = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.UnitPopup.cs"));
        string net = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        Check(runtime.Contains("StaticPopupCoordinatorLaw.Show", StringComparison.Ordinal) &&
              runtime.Contains("DuelPackets.ParseRequested", StringComparison.Ordinal) &&
              runtime.Contains("_ignored.Contains", StringComparison.Ordinal) &&
              runtime.Contains("DuelFrameUiLaw.IsDuelSpell", StringComparison.Ordinal) &&
              runtime.Contains("TryCast(spellId)", StringComparison.Ordinal) &&
              unitPopup.Contains("case UnitPopupRow.Duel", StringComparison.Ordinal) &&
              net.Contains("case Op.SMSG_DUEL_COUNTDOWN", StringComparison.Ordinal) &&
              runtime.Contains("DuelFrameUiLaw.PopupSize", StringComparison.Ordinal) &&
              runtime.Contains("DuelFrameUiLaw.TextLineCenter", StringComparison.Ordinal) &&
              !MethodBody(runtime, "private void DrawDuelPopup").Contains(
                  "new Vector2", StringComparison.Ordinal),
            "duel production request/UnitPopup/net wiring drift");
    }

    private static SpellInfo Spell(uint id, uint[] effects) => new(
        Id: id, Name: "Duel", Rank: "", IconPath: "icon", Attributes: 0,
        AttributesEx2: 0, AttributesEx3: 0, InterruptFlags: 0,
        ChannelInterruptFlags: 0, Targets: 0, ImplicitTarget: 0, RecoveryMs: 0,
        CategoryRecoveryMs: 0, PowerType: 0, ManaCost: 0, ManaCostPercent: 0,
        StartRecoveryCategory: 0, StartRecoveryMs: 0, VisualId: 0, Speed: 0,
        Description: "", RangeIndex: 0, EffectIds: effects);

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
        {
            return;
        }
        throw new InvalidDataException(message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) return "";
        int next = source.IndexOf("\n    private ", start + signature.Length,
            StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
    }
}
