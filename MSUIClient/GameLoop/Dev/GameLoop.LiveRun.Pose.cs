using MSUIClient.Net;
using System.Globalization;
using System.Numerics;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool InspectLiveSupport(string line)
    {
        string[] args = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (args.Length != 4) return false;
        float[] xyz = args.Skip(1).Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        if (xyz.Any(v => !float.IsFinite(v))) return false;
        Vector3 point = new(xyz[0], xyz[1], xyz[2]);
        var hit = _collision?.Raycast(point + Vector3.UnitZ * 3, -Vector3.UnitZ, 80);
        float? terrain = _terrain?.SampleHeight(point.X, point.Y);
        Console.WriteLine($"[live-support] point={point};collisionFloor={hit?.Point.Z};" +
            $"normal={hit?.Normal};terrainSample={terrain};actor={_controller?.Position};grounded={_controller?.Grounded}");
        bool hasLiquid = TryGetEyeLiquidSurface(point, out float surfaceZ, out byte liquidType);
        Console.WriteLine($"[live-liquid] point={point};hasSurface={hasLiquid};z={surfaceZ};type={liquidType};" +
            $"enabled={_liquid?.Enabled};tiles={_liquid?.TileCount};drawn={_liquid?.TilesDrawnLastFrame};" +
            $"triangles={_liquid?.TrianglesLastFrame};wmoDrawn={_liquid?.WmoSurfacesDrawnLastFrame};" +
            $"riverAlpha={_liquid?.RiverAlphaShallow}|{_liquid?.RiverAlphaDeep};waterFrames={_liquid?.WaterFrames}");
        return true; // Observation only: a printed floor is not a placement or walkability pass.
    }

    private bool RunLivePose(string line)
    {
        string[] p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!_entities.TryGet(ControlledGuid, out WorldEntity actor) || _character is null) return false;
        int actualAnimation = _character.CurrentBaseAnimationId;
        string model = "CHARACTER";
        if (ControlledBodyIsStreamed)
        {
            if (_creatures?.TryGetSpellPose(ControlledGuid, out var pose) != true) return false;
            actualAnimation = pose.AnimationId;
            model = pose.ModelPath;
        }
        Console.WriteLine($"[live-pose] actor=0x{ControlledGuid:X};health={actor.Fields.Health};readsDead={actor.Fields.ReadsDead};ghost={actor.Fields.PlayerIsGhost};stand={actor.Fields.UnitStandState};dynamic=0x{actor.Fields.DynamicFlags:X};animation={actualAnimation};model={model};display={actor.DisplayId};transformed={actor.Fields.HasDisplayTransform}");
        Console.WriteLine($"[live-held-pose] sheath={actor.Fields.SheathState};visualSheath={_character.SheathState};" +
            $"channel={actor.Fields.ChannelSpell};channelObject=0x{actor.Fields.ChannelObject:X};" +
            $"poleTips={_character.FishingPoleTips.Count}");
        Console.WriteLine($"[live-motion-pose] position={_controller?.Position};swimming={_controller?.Swimming};" +
            $"grounded={_controller?.Grounded};speed={_controller?.PlanarSpeed};flags=0x{_movementSender.LastFlags:X8};" +
            $"stealthed={actor.Fields.UnitIsStealthed}");
        if (p.Length == 2 && p[1] == "inspect") return true;
        if (p.Length == 3 && p[1] == "assert-animation" && int.TryParse(p[2], out int animation))
            return actualAnimation == animation;
        if (p.Length == 3 && p[1] == "assert-model")
            return model.Contains(p[2], StringComparison.OrdinalIgnoreCase);
        if (p.Length == 3 && p[1] == "assert-transformed" && bool.TryParse(p[2], out bool transformed))
            return actor.Fields.HasDisplayTransform == transformed;
        if (p.Length == 3 && p[1] == "assert-dead" && bool.TryParse(p[2], out bool dead))
            return actor.Fields.ReadsDead == dead;
        return false;
    }
}
