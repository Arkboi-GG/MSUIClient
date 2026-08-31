using System.Numerics;
using MSUIClient;
using MSUIClient.World;
using MSUIClient.World.Units;

internal static class VisualLifecycleClinicalChecks
{
    public static void Run()
    {
        Near(CreatureRespawnFadeLaw.Alpha(0f), 0f, "respawn fade start");
        Near(CreatureRespawnFadeLaw.Alpha(0.5f), 0.015625f, "respawn fade cubic quarter");
        Near(CreatureRespawnFadeLaw.Alpha(1f), 0.125f, "respawn fade cubic midpoint");
        Near(CreatureRespawnFadeLaw.Alpha(2f), 1f, "respawn fade finish");

        float height = WaterFoamLaw.DefaultCollisionHeight;
        Check(WaterFoamLaw.Eligible(1.52f, height),
            "surface swimming must remain inside the two-height foam gate");
        Check(!WaterFoamLaw.Eligible(4.1f, height) &&
              !WaterFoamLaw.Eligible(null, height),
            "deep diving and dry ground must not emit water foam");
        Check(!WaterFoamLaw.BeyondWadeLine(0.8f, height) &&
              WaterFoamLaw.BeyondWadeLine(0.82f, height),
            "0.4 x collision-height wade crossing drifted");

        Near(WaterFoamLaw.RecordAlpha(0.8f, 1f, 0f, 0.2f), 0.4f,
            "foam 40-percent rise");
        Near(WaterFoamLaw.RecordAlpha(0.8f, 1f, 0f, 0.4f), 0.8f,
            "foam peak");
        Near(WaterFoamLaw.RecordAlpha(0.8f, 1f, 0f, 0.7f), 0.4f,
            "foam 60-percent fall");
        Near(WaterFoamLaw.RecordAlpha(0.8f, 1f, 0f, 1f), 0f,
            "foam retirement");
        Check(WaterFoamLaw.RecordAlive(1f, 10f, 10f),
            "a zero-alpha newborn foam record must survive its emission frame");
        Check(WaterFoamLaw.RecordAlive(1f, 10f, 10.01f) &&
              WaterFoamLaw.RecordAlpha(0.8f, 1f, 10f, 10.01f) > 0f,
            "a retained newborn foam record must become visible on the following frame");
        Check(!WaterFoamLaw.RecordAlive(1f, 10f, 11f),
            "foam record must retire at its authored lifetime");
        Near(WaterFoamLaw.RecordSize(0.5f, 1f, 0f, 0.5f), 1f,
            "foam texgen growth");
        Near(WaterFoamLaw.WakeCooldown(7f, 1f), 0.625f / 7f,
            "wake distance cadence");

        Vector2 ahead = WaterFoamLaw.TexGen(Vector2.Zero, 0f, 1f, Vector2.UnitX);
        Vector2 behind = WaterFoamLaw.TexGen(Vector2.Zero, 0f, 1f, -Vector2.UnitX);
        Vector2 across = WaterFoamLaw.TexGen(Vector2.Zero, 0f, 1f, Vector2.UnitY);
        Near(ahead.X, 0.5f, "wake ahead U");
        Near(ahead.Y, 0f, "wake apex ahead");
        Near(behind.Y, 1f, "wake arms behind");
        Near(across.X, 1f, "wake across-track U");

        string root = ClientConfig.FindRepoRoot();
        string liquid = SourceText.Read(Path.Combine(root,
            "MSUIClient", "World", "LiquidRenderer.cs"));
        string shader = SourceText.Read(Path.Combine(root,
            "MSUIClient", "Shaders", "water.frag"));
        string program = SourceText.Read(Path.Combine(root,
            "MSUIClient", "Program.cs"));
        string creatures = SourceText.Read(Path.Combine(root,
            "MSUIClient", "World", "Units", "CreatureRenderer.cs"));
        Check(liquid.Contains(@"XTextures\splash\wake.blp", StringComparison.Ordinal) &&
              liquid.Contains(@"XTextures\splash\splash.blp", StringComparison.Ordinal) &&
              liquid.Contains("SelfFoamSlots = 32", StringComparison.Ordinal) &&
              liquid.Contains("OtherFoamSlots = 96", StringComparison.Ordinal) &&
              liquid.Contains("MaxPackedFoamRecords = 64", StringComparison.Ordinal) &&
              liquid.Contains("Dictionary<ulong, FoamEmitterState>", StringComparison.Ordinal) &&
              liquid.Contains("public void UpdateOtherWake(ulong guid", StringComparison.Ordinal) &&
              liquid.Contains("slot = SelfFoamSlots + _otherFoamCursor", StringComparison.Ordinal) &&
              liquid.Contains("WaterFoamLaw.RecordAlive(record.Lifetime, record.Born, Time)",
                  StringComparison.Ordinal) &&
              liquid.Contains("if (alpha <= 0f) continue;", StringComparison.Ordinal) &&
              !liquid.Contains("if (alpha <= 0f)\n                _foamPool[i] = null;",
                  StringComparison.Ordinal) &&
              shader.Contains("uFoamA[MAX_FOAM_RECORDS]", StringComparison.Ordinal) &&
              shader.Contains("MAX_FOAM_RECORDS = 64", StringComparison.Ordinal) &&
              shader.Contains("uTexRing", StringComparison.Ordinal) &&
              shader.Contains("stencil.rgb * stencil.a * vertexAlpha", StringComparison.Ordinal) &&
              !shader.Contains("uFoamColor", StringComparison.Ordinal) &&
              !shader.Contains("uFoamOpacity", StringComparison.Ordinal) &&
              !shader.Contains("uWakeRepeat", StringComparison.Ordinal),
            "production water path lost its reference-composited following records, exposed " +
            "the broad alpha wedge as colour, or returned to the stretched/repeated V approximation");
        Check(program.Contains("_liquid.BeginWakeFrame();", StringComparison.Ordinal) &&
              program.Contains("foreach (WorldEntity foamUnit in _entities.Units)",
                  StringComparison.Ordinal) &&
              program.Contains("foamUnit.Guid == ControlledGuid", StringComparison.Ordinal) &&
              program.Contains("_liquid.UpdateOtherWake(foamUnit.Guid", StringComparison.Ordinal) &&
              program.Contains("_liquid.EndWakeFrame();", StringComparison.Ordinal),
            "streamed players/creatures lost their foam feed or the controlled body can be " +
            "submitted twice");
        Check(creatures.Contains("CreatureRespawnFadeLaw.Alpha(elapsed)",
                  StringComparison.Ordinal) &&
              creatures.Contains("_deadCreatureSeenAt", StringComparison.Ordinal) &&
              creatures.Contains("alphaMultiplier: respawnAlpha", StringComparison.Ordinal),
            "creature respawn fade lost its body/equipment or corpse-stream continuity wiring");
    }

    private static void Near(float actual, float expected, string label)
    {
        if (MathF.Abs(actual - expected) > 1e-5f)
            throw new InvalidOperationException($"{label} drifted: {actual} != {expected}");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
