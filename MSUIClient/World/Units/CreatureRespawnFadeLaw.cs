namespace MSUIClient.World.Units;

/// <summary>Build-5875 streamed-object appear fade, scoped by the renderer to creature respawns.</summary>
public static class CreatureRespawnFadeLaw
{
    public const float DurationSeconds = 2f;

    public static float Alpha(float elapsedSeconds)
    {
        float t = Math.Clamp(elapsedSeconds / DurationSeconds, 0f, 1f);
        return t * t * t;
    }
}
