using MSUIClient;
using MSUIClient.Net;

internal static class HonorCreditClinicalChecks
{
    public static void Run()
    {
        PvpCreditPacket packet = PvpCreditPackets.Parse(
        [
            0x7D, 0, 0, 0,
            0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88,
            6, 0, 0, 0,
        ]);
        Check(packet == new PvpCreditPacket(125, 0x8877665544332211, 6) &&
              PvpCreditPackets.FloatingText(packet) == "Honor: 125" &&
              (ushort)Op.SMSG_PVP_CREDIT == 0x028C,
            "SMSG_PVP_CREDIT fixed honor/GUID/rank layout drift");

        bool rejected = false;
        try { _ = PvpCreditPackets.Parse(new byte[12]); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "truncated PvP credit must fail closed");

        WorldCombatTextPresentation honor = CombatFeedbackLaw.Presentation(
            WorldCombatTextStyle.Honor, critical: false, "Honor: 125");
        Check(honor.LifetimeSeconds == 4.5f && honor.RiseYards == 0f &&
              honor.FadeInEndSeconds == .5f && honor.FadeOutStartSeconds == 2f &&
              honor.Color == 0xFFE0CA0A &&
              CombatFeedbackLaw.Category(WorldCombatTextStyle.Honor, false, "Honor: 125") ==
                  WorldCombatTextCategory.Honor,
            "honor must use frozen world-text category 5");
        (float fadeMain, float fadeShadow) = CombatFeedbackLaw.Alpha(honor, .25f);
        (float plateauMain, float plateauShadow) = CombatFeedbackLaw.Alpha(honor, .5f);
        (float tailMain, float tailShadow) = CombatFeedbackLaw.Alpha(honor, 3.25f);
        Check(MathF.Abs(fadeMain - 1f / 18f) < .0001f && fadeShadow < fadeMain &&
              plateauMain == 1f && MathF.Abs(plateauShadow - 127f / 255f) < .0001f &&
              MathF.Abs(tailMain - .5f) < .0001f && tailShadow == tailMain,
            "category-5 duration-based fade and shadow min-cap drift");

        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.CombatFeedback.cs"));
        string dispatch = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        Check(runtime.Contains("credit.Honor <= 0", StringComparison.Ordinal) &&
              runtime.Contains("WorldCombatTextStyle.Honor", StringComparison.Ordinal) &&
              dispatch.Contains("case Op.SMSG_PVP_CREDIT:", StringComparison.Ordinal),
            "positive-credit self world-text dispatch drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
