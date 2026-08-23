using System.Numerics;
using MSUIClient;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.World.Wmo;

internal static class WmoLiquidPointClinicalChecks
{
    public static void Run()
    {
        WmoLiquid liquid = new()
        {
            XVerts = 3,
            YVerts = 2,
            XTiles = 2,
            YTiles = 1,
            TileFlags = [0x0f, 0x02],
            VertexHeights = new float[6],
        };
        Vector3 origin = new(10f, 20f, 0f);
        Vector2 u = new(2f, 1f);
        Vector2 v = new(-1f, 2f);
        Vector3[] vertices = new Vector3[6];
        for (int j = 0; j < 2; j++)
        for (int i = 0; i < 3; i++)
            vertices[j * 3 + i] = new Vector3(
                origin.X + i * u.X + j * v.X,
                origin.Y + i * u.Y + j * v.Y,
                4f + 2f * i + 10f * j);
        WmoLiquidSurface surface = new(
            17, "fixture.wmo", 3, "pool", -20f, 0x0fu, liquid, vertices);

        Vector2 sample = new(origin.X + 1.25f * u.X + .5f * v.X,
            origin.Y + 1.25f * u.Y + .5f * v.Y);
        Check(WmoLiquidPointLaw.TrySample(surface, sample.X, sample.Y,
                  out float height, out byte type) &&
              MathF.Abs(height - 11.5f) < .0001f && type == 6,
            "rotated WMO liquid cell inversion, bilinear height, or type translation drifted");

        Vector2 hidden = new(origin.X + .5f * u.X + .5f * v.X,
            origin.Y + .5f * u.Y + .5f * v.Y);
        Vector2 farEdge = new(origin.X + 2f * u.X + .5f * v.X,
            origin.Y + 2f * u.Y + .5f * v.Y);
        Check(!WmoLiquidPointLaw.TrySample(surface, hidden.X, hidden.Y, out _, out _) &&
              WmoLiquidPointLaw.TrySample(surface, farEdge.X, farEdge.Y,
                  out float edgeHeight, out _) &&
              MathF.Abs(edgeHeight - 13f) < .0001f &&
              WmoLiquidPointLaw.IsWater(1) && WmoLiquidPointLaw.IsWater(4) &&
              !WmoLiquidPointLaw.IsWater(3) && !WmoLiquidPointLaw.IsWater(6),
            "WMO liquid dry-cell, far-edge, or water-kind filtering drifted");
        Check(WmoLiquidPointLaw.TryMapGroupOverride(0, out byte still) && still == 4 &&
              WmoLiquidPointLaw.TryMapGroupOverride(1, out byte ocean) && ocean == 1 &&
              WmoLiquidPointLaw.TryMapGroupOverride(2, out byte magma) && magma == 6 &&
              WmoLiquidPointLaw.TryMapGroupOverride(3, out byte slime) && slime == 3 &&
              WmoLiquidPointLaw.TryMapGroupOverride(8, out byte rapids) && rapids == 4 &&
              !WmoLiquidPointLaw.TryMapGroupOverride(0x0f, out _) &&
              !WmoLiquidPointLaw.TryMapGroupOverride(5, out _),
            "MOGP whole-group liquid override mapping drifted");

        string root = ClientConfig.FindRepoRoot();
        string renderer = SourceText.Read(Path.Combine(root, "MSUIClient", "World",
            "LiquidRenderer.cs"));
        string wmo = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Wmo",
            "WmoRenderer.cs"));
        string splash = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.WaterSounds.cs"));
        string footsteps = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Footsteps.cs"));
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs"));
        string sound = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Sound.cs"));
        string portal = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Portals",
            "PortalDestinationScene.cs"));
        Check(renderer.Contains("surface.InstanceId != instanceId", StringComparison.Ordinal) &&
              renderer.Contains("worldPoint.Z < surface.GroupFloor", StringComparison.Ordinal) &&
              renderer.Contains("candidate >= lowest", StringComparison.Ordinal) &&
              wmo.Contains("out int liquidInstanceId", StringComparison.Ordinal) &&
              wmo.Contains("int InstanceId = 0", StringComparison.Ordinal) &&
              wmo.Contains("TryGetGroupLiquidOverride(", StringComparison.Ordinal) &&
              splash.Contains("TryGetBodyLiquidSurface(", StringComparison.Ordinal) &&
              splash.Contains("TryGetEyeLiquidSurface(", StringComparison.Ordinal) &&
              splash.Contains("RefreshRetainedWmoLiquid();", StringComparison.Ordinal) &&
              splash.Contains("height = float.MaxValue;", StringComparison.Ordinal) &&
              footsteps.Contains("TryGetWmoSurface(feet, wmoInstanceId", StringComparison.Ordinal) &&
              runtime.Contains("TryGetBodyLiquidSurface(_controller.Position", StringComparison.Ordinal) &&
              runtime.Contains("TryGetEyeLiquidSurface(eye", StringComparison.Ordinal) &&
              sound.Contains("TryGetEyeLiquidSurface(eye", StringComparison.Ordinal) &&
              portal.Contains("room.InstanceId", StringComparison.Ordinal),
            "placed-owner/floor/lowest-winner WMO liquid production wiring drifted");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
