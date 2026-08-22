using MSUIClient;
using MSUIClient.World.Units;

internal static class MountRenderingClinicalChecks
{
    public static void Run()
    {
        Check(CreatureRenderer.RiderAnimationId == 91,
            "mounted rider animation drifted from AnimationData 91");

        string root = ClientConfig.FindRepoRoot();
        string mount = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Units",
            "CreatureRenderer.Mounts.cs"));
        string creatures = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Units",
            "CreatureRenderer.cs"));
        string character = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Units",
            "CharacterRenderer.cs"));
        string program = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs"));

        Check(mount.Contains("private const int MountSeatAttachment = 0;",
                  StringComparison.Ordinal) &&
              mount.Contains("private bool TryDrawMount(", StringComparison.Ordinal) &&
              mount.Contains("SeatTransform(model, mountWorld", StringComparison.Ordinal) &&
              mount.Contains("SelectMountClip(", StringComparison.Ordinal) &&
              mount.Contains("BaseAnimationTrack, 94, true", StringComparison.Ordinal) &&
              mount.Contains("TryGetMountGroundRadius", StringComparison.Ordinal),
            "steed model, attachment-0 seat, gait/flourish or footprint wiring drifted");
        Check(creatures.Contains("if (e.MountDisplayId > 0)", StringComparison.Ordinal) &&
              creatures.Contains("mounted = TryDrawMount(camera, e.Guid, e.MountDisplayId",
                  StringComparison.Ordinal) &&
              creatures.Contains("BaseAnimationTrack, RiderAnimationId", StringComparison.Ordinal) &&
              creatures.Contains("mount.GroundRadius", StringComparison.Ordinal) &&
              creatures.Contains("mount.SeatHeight", StringComparison.Ordinal),
            "observed-rider mount draw, seated animation, shadow or height wiring drifted");
        Check(program.Contains("creatures.TryDrawSelfMount", StringComparison.Ordinal) &&
              program.Contains("_character.MountSeat = seat;", StringComparison.Ordinal) &&
              program.Contains("TryGetMountGroundRadius(RenderSelfGuid", StringComparison.Ordinal) &&
              character.Contains("public Matrix4x4? MountSeat", StringComparison.Ordinal) &&
              character.Contains("if (Mounted)", StringComparison.Ordinal) &&
              character.Contains("CreatureRenderer.RiderAnimationId", StringComparison.Ordinal),
            "local predicted rider is no longer seated on the rendered steed");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
