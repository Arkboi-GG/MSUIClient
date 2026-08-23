using System.Numerics;
using MSUIClient;
using MSUIClient.World.Units;

internal static class FishingLineClinicalChecks
{
    public static void Run()
    {
        Vector3 near = new(0f, 0f, 5f);
        Vector3 far = new(20f, 0f, 4f);
        Vector3[] points = FishingLineLaw.Build(near, far);
        Vector3 midpoint = Vector3.Lerp(near, far, .5f);
        Vector3[] longSpan = FishingLineLaw.Build(near, new Vector3(200f, 0f, 5f));
        Check(FishingLineLaw.Segments == 64 &&
              FishingLineLaw.VertexCount == 65 &&
              FishingLineLaw.Sag == .5f &&
              points.Length == 65 &&
              Vector3.Distance(points[0], near) < .00001f &&
              Vector3.Distance(points[^1], far) < .00001f &&
              MathF.Abs(points[32].Z - (midpoint.Z - .5f)) < .0001f &&
              MathF.Abs(longSpan[32].Z - 4.5f) < .0001f,
            "fishing line 64-segment fixed half-sine geometry drift");
        Check(FishingLineLaw.Eligible(7620, 0x1234, true, 17) &&
              !FishingLineLaw.Eligible(0, 0x1234, true, 17) &&
              !FishingLineLaw.Eligible(7620, null, true, 17) &&
              !FishingLineLaw.Eligible(7620, 0x1234, false, 17) &&
              !FishingLineLaw.Eligible(7620, 0x1234, true, 19),
            "fishing line channel/FISHINGNODE eligibility drift");

        string root = ClientConfig.FindRepoRoot();
        string attachments = SourceText.Read(Path.Combine(root, "MSUIClient", "World",
            "Units", "AttachedItemRenderer.cs"));
        string doodads = SourceText.Read(Path.Combine(root, "MSUIClient", "World",
            "Doodads", "DoodadRenderer.cs"));
        string targeting = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Combat", "GameLoop.Targeting.cs"));
        string renderer = SourceText.Read(Path.Combine(root, "MSUIClient", "World",
            "Units", "FishingLineRenderer.cs"));
        Check(attachments.Contains("mount.HeldSlot == 0", StringComparison.Ordinal) &&
              attachments.Contains("attachmentId == AttachHandRight", StringComparison.Ordinal) &&
              attachments.Contains("\"$CCH\"", StringComparison.Ordinal) &&
              attachments.Contains("FishingPoleTipPlacement", StringComparison.Ordinal),
            "fishing pole mainhand/held/$CCH endpoint publication drift");
        Check(doodads.Contains("TryGetDynamicFishingLineEnd", StringComparison.Ordinal) &&
              doodads.Contains("entry.Model.LocalMax.Z - entry.Model.LocalMin.Z", StringComparison.Ordinal) &&
              doodads.Contains("float height = localHeight * scale", StringComparison.Ordinal) &&
              doodads.Contains("instance.Transform.M43 + height * .5f", StringComparison.Ordinal),
            "bobber placement-base plus half-bounds-height endpoint drift");
        Check(targeting.Contains("owner.Fields.ChannelSpell", StringComparison.Ordinal) &&
              targeting.Contains("owner.Fields.ChannelObject", StringComparison.Ordinal) &&
              targeting.Contains("FishingLineLaw.Eligible", StringComparison.Ordinal) &&
              targeting.Contains("_doodads.TryGetDynamicFishingLineEnd", StringComparison.Ordinal),
            "per-visible-unit live fishing-line watcher drift");
        Check(renderer.Contains("PrimitiveType.LineStrip", StringComparison.Ordinal) &&
              renderer.Contains("FishingLineLaw.Build", StringComparison.Ordinal) &&
              renderer.Contains("_gl.Enable(EnableCap.DepthTest)", StringComparison.Ordinal) &&
              renderer.Contains("_gl.Disable(EnableCap.Blend)", StringComparison.Ordinal),
            "opaque unlit scene line-strip renderer drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
