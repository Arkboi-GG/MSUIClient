using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

internal static class MirrorTimerClinicalChecks
{
    public static void Run()
    {
        Check((ushort)Op.SMSG_START_MIRROR_TIMER == 0x01D9 &&
              (ushort)Op.SMSG_PAUSE_MIRROR_TIMER == 0x01DA &&
              (ushort)Op.SMSG_STOP_MIRROR_TIMER == 0x01DB,
            "mirror-timer opcode family drift");

        var startBody = new PacketWriter();
        startBody.WriteU32(1); startBody.WriteU32(45_000); startBody.WriteU32(60_000);
        startBody.WriteI32(-1); startBody.WriteU8(0); startBody.WriteU32(0);
        MirrorTimerStart start = MirrorTimerPackets.ParseStart(startBody.ToArray());
        Check(start == new MirrorTimerStart(1, 45_000, 60_000, -1, false, 0) &&
              start.Kind == MirrorTimerKind.Breath,
            "SMSG_START_MIRROR_TIMER signed-scale/field order drift");
        CheckThrows(() => MirrorTimerPackets.ParseStart(startBody.ToArray()[..20]),
            "SMSG_START_MIRROR_TIMER accepted a truncated spell id");
        Check(MirrorTimerPackets.ParsePause(Convert.FromHexString("0000000001")) == (0u, true) &&
              MirrorTimerPackets.ParseStop(Convert.FromHexString("02000000")) == 2,
            "mirror pause/stop body drift");
        CheckThrows(() => MirrorTimerPackets.ParsePause([0, 0, 0, 0, 1, 0]),
            "SMSG_PAUSE_MIRROR_TIMER accepted trailing bytes");

        var state = new MirrorTimerState();
        MirrorTimerState.ActiveTimer breath = state.Start(start, 10) ??
            throw new InvalidDataException("breath did not claim the first frame");
        Check(state.Frames[0] == breath && MirrorTimerState.ValueAt(breath, 15) == 40 &&
              MathF.Abs(MirrorTimerState.FractionAt(breath, 15) - 2f / 3f) < .0001f,
            "mirror-timer signed client integration drift");
        Check(state.Pause(1, true, 15) && MirrorTimerState.ValueAt(breath, 30) == 40 &&
              state.Pause(1, false, 30) && MirrorTimerState.ValueAt(breath, 32) == 38,
            "mirror-timer pause/unpause settlement drift");
        MirrorTimerState.ActiveTimer restated = state.Start(
            new MirrorTimerStart(1, 30_000, 60_000, 10, false, 5697), 40) ??
            throw new InvalidDataException("START re-state was dropped");
        Check(state.Frames[0] == restated && restated.SpellId == 5697 &&
              restated.Scale == 10 && state.Frames.Skip(1).All(x => x is null),
            "START must fully re-state its existing frame rather than consume another");
        Check(state.Start(new MirrorTimerStart(99, 1, 1, -1, false, 0), 0) is null &&
              state.Stop(1) && state.Frames[0] is null,
            "unknown mirror kind or STOP release drift");
        MirrorTimerState.ActiveTimer predicted = state.Start(
            new MirrorTimerStart(1, 60_000, 60_000, -1, false, 0), 50,
            serverAuthoritative: false) ??
            throw new InvalidDataException("predicted breath did not claim a frame");
        Check(!predicted.ServerAuthoritative && state.Find(MirrorTimerKind.Breath) == predicted &&
              MirrorTimerState.ValueAt(predicted, 55) == 55,
            "predicted breath source or drain integration drift");
        MirrorTimerState.ActiveTimer authoritative = state.Start(
            new MirrorTimerStart(1, 42_000, 60_000, -1, false, 0), 55) ??
            throw new InvalidDataException("server breath did not replace prediction");
        Check(authoritative.ServerAuthoritative && state.Frames[0] == authoritative,
            "server breath did not take ownership of the predicted frame");

        MirrorTimerUiLaw.ScreenRect first = MirrorTimerUiLaw.FrameRect(
            new Vector2(1920, 1080), 1, 0);
        MirrorTimerUiLaw.ScreenRect third = MirrorTimerUiLaw.FrameRect(
            new Vector2(1920, 1080), 1, 2);
        Check(first.Min == new Vector2(857, 96) && first.Size == new Vector2(206, 26) &&
              third.Min == new Vector2(857, 148) &&
              MirrorTimerUiLaw.BarMin == new Vector2(5.5f, 2) &&
              MirrorTimerUiLaw.BorderMin == new Vector2(-25, -25) &&
              MirrorTimerUiLaw.BarRect(first, 1) ==
                  new MirrorTimerUiLaw.ScreenRect(new Vector2(862.5f, 98),
                      new Vector2(195, 13)) &&
              MirrorTimerUiLaw.BorderRect(first, 1) ==
                  new MirrorTimerUiLaw.ScreenRect(new Vector2(832, 71),
                      new Vector2(256, 64)) &&
              MirrorTimerUiLaw.FillUvMax(.75f) == new Vector2(.75f, 1) &&
              MirrorTimerUiLaw.ScriptName(MirrorTimerKind.Fatigue) == "EXHAUSTION" &&
              MirrorTimerUiLaw.FallbackCaption(MirrorTimerKind.Fatigue) == "Fatigue" &&
              MirrorTimerUiLaw.FallbackCaption(MirrorTimerKind.Breath) == "Breath" &&
              MirrorTimerUiLaw.FallbackCaption(MirrorTimerKind.FeignDeath) == "" &&
              MirrorTimerUiLaw.Color(MirrorTimerKind.Breath) == new Vector4(0, .5f, 1, 1),
            "MirrorTimer authored stack/name/caption/color law drift");

        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.MirrorTimer.cs"));
        string dispatch = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        string draw = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.CombatFeedback.cs"));
        Check(runtime.Contains("MirrorTimerPackets.ParseStart", StringComparison.Ordinal) &&
              runtime.Contains("MirrorTimerUiLaw.FrameRect", StringComparison.Ordinal) &&
              runtime.Contains("MirrorTimerUiLaw.BarRect", StringComparison.Ordinal) &&
              runtime.Contains("MirrorTimerUiLaw.BorderRect", StringComparison.Ordinal) &&
              !runtime.Contains("new Vector2", StringComparison.Ordinal) &&
              runtime.Contains("MirrorTimerState.FractionAt", StringComparison.Ordinal) &&
              runtime.Contains("UpdatePredictedBreath", StringComparison.Ordinal) &&
              runtime.Contains("ServerAuthoritative", StringComparison.Ordinal) &&
              runtime.Contains("spell.Name", StringComparison.Ordinal) &&
              !runtime.Contains("SetNextWindowPos", StringComparison.Ordinal) &&
              dispatch.Contains("case Op.SMSG_START_MIRROR_TIMER", StringComparison.Ordinal) &&
              dispatch.Contains("case Op.SMSG_PAUSE_MIRROR_TIMER", StringComparison.Ordinal) &&
              dispatch.Contains("case Op.SMSG_STOP_MIRROR_TIMER", StringComparison.Ordinal) &&
              dispatch.Contains("ResetMirrorTimers();", StringComparison.Ordinal) &&
              draw.Contains("DrawMirrorTimerFrames();", StringComparison.Ordinal),
            "MirrorTimer production wiring bypasses strict packets, reset, or UI law");
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
