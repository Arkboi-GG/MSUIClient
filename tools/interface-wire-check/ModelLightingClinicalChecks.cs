using MSUIClient;

internal static class ModelLightingClinicalChecks
{
    public static void Run()
    {
        Check(Near(Model2SunResponse(1f), 1f),
            "Model2 light no longer preserves the fully lit face");
        Check(Near(Model2SunResponse(0f), 1.5f / 17f),
            "Model2 light lost its 1.12 terminator fill");
        Check(Near(Model2SunResponse(-1f), 1f / 17f),
            "Model2 light lost its 1.12 rear-face fill");

        string root = ClientConfig.FindRepoRoot();
        string character = SourceText.Read(Path.Combine(root, "MSUIClient", "Shaders",
            "character.frag"));
        string creature = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Units",
            "CreatureRenderer.cs"));
        Check(character.Contains("model2SunResponse(mu)", StringComparison.Ordinal) &&
              character.Contains("WorldModelSelfFill = 0.25", StringComparison.Ordinal) &&
              creature.Contains("model2SunResponse(dot(normal, normalize(uSunDir)))",
                  StringComparison.Ordinal) &&
              creature.Contains("WorldModelSelfFill = 0.25", StringComparison.Ordinal),
            "player/attached-item or creature Model2/self-fill lighting wiring drift");
    }

    private static float Model2SunResponse(float mu) =>
        (4f / 17f) * (.375f + 2f * mu + 1.875f * mu * mu);

    private static bool Near(float left, float right) => MathF.Abs(left - right) < .0001f;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
