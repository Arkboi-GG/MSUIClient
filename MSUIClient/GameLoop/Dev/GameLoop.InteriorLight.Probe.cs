using System.Globalization;
using System.Numerics;
using MSUIClient.Engine;
using MSUIClient.Net;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Interior unit-light probe: a scripted offline proof that the dynamic M2s - the
// local body, creatures, gameobjects - take the room's baked light instead of
// the sky. Activated by MSUI_INTERIORLIGHT_PROBE=<map>,<x>,<y>,<z> (default: the
// Ironforge Commons by the bank, where the owner's 2026-09-04 capture showed a
// moonlit-grey toon beside warm props). The client boots the creator world at
// that spot at NIGHT (so the sky light is the blue that produced the report),
// drops a dwarf, a human and a mailbox beside the player, and dumps two
// screenshots: room light ON, then OFF, so the difference is the feature.
// Console lines carry PASS/FAIL per claim. No-ops unless the env var is set.
// ─────────────────────────────────────────────────────────────────────────────

public sealed partial class GameLoop
{
    private static readonly string? InteriorLightProbeSpec =
        Environment.GetEnvironmentVariable("MSUI_INTERIORLIGHT_PROBE");

    private int _interiorLightProbeStage;
    private double _interiorLightProbeAt;
    private int _interiorLightProbeFailures;
    private Vector3 _interiorLightProbeSpot;
    private bool _interiorUnitLightProbeOff;
    private readonly List<ulong> _interiorLightProbeGuids = [];

    private void InteriorLightProbeCheck(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"[interiorlight-probe] {(ok ? "PASS" : "FAIL")}  {name}" +
                          (detail.Length > 0 ? $"  [{detail}]" : ""));
        if (!ok) _interiorLightProbeFailures++;
    }

    private void InteriorLightProbeSpawn(string name, ObjectTypeId type, ObjectFields fields,
        Vector3 spot, float facing)
    {
        ulong guid = 0xF00D000000000000UL + (ulong)(_interiorLightProbeGuids.Count + 1);
        _entities.AddSynthetic(new WorldEntity
        {
            Guid = guid,
            Type = type,
            Fields = fields,
            Position = spot,
            Orientation = facing,
        });
        _interiorLightProbeGuids.Add(guid);
        Console.WriteLine($"[interiorlight-probe] spawned {name} at ({spot.X:0.0}, {spot.Y:0.0}, {spot.Z:0.0})");
    }

    private void UpdateInteriorLightProbe()
    {
        if (InteriorLightProbeSpec is null || _interiorLightProbeStage >= 99) return;
        double now = NowSeconds();

        switch (_interiorLightProbeStage)
        {
            case 0:
            {
                if (_gl is null || _worldLoadStarted) return;
                if (_interiorLightProbeAt == 0) { _interiorLightProbeAt = now; return; }
                if (now - _interiorLightProbeAt < 1.0) return;
                // Ironforge Commons, between Lieutenant Rotimer and the bank's mailbox
                // (world DB: rotimer -4903,-968; mailbox -4910,-976; dinita -4887,-978 on the
                // raised bank platform at 504 - a spot inside that slab probes nothing).
                int map = 0; float x = -4904f, y = -966f, z = 501.6f;
                string[] parts = InteriorLightProbeSpec.Split(',', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4 &&
                    int.TryParse(parts[0], out int m) &&
                    float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float px) &&
                    float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float py) &&
                    float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float pz))
                { map = m; x = px; y = py; z = pz; }
                _interiorLightProbeSpot = new Vector3(x, y, z);
                _config.DevTools = true;   // screenshots ride the gameplay-dump machinery
                Settings.Creator.LocMap = map;
                Settings.Creator.LocMapName = map switch { 0 => "Azeroth", 1 => "Kalimdor", _ => Settings.Creator.LocMapName };
                Settings.Creator.LocX = x; Settings.Creator.LocY = y; Settings.Creator.LocZ = z;
                Settings.Creator.LocYaw = 2.2f;   // face the bank hall, back to the camera
                // Night, pinned: the sky light is then the cold blue of the report, so a warm
                // body is the feature and not the hour.
                Settings.Lighting.TimeSource = TimeSource.Fixed;
                Settings.Lighting.TimeOfDay = 23f;
                Console.WriteLine($"[interiorlight-probe] entering creator world at map {map} ({x:0.0}, {y:0.0}, {z:0.0}) at 23:00");
                EnterOfflineWorld();
                _interiorLightProbeStage = 1; _interiorLightProbeAt = now;
                return;
            }

            case 1:   // world streamed in: spawn the cast around the player
            {
                if (_worldLoading || _controller is null || now - _interiorLightProbeAt < 8.0) return;
                // The building's collision streams in after the body is placed, and the first
                // run fell to the terrain 280 yd below the Commons floor before it did. Re-seat
                // the body on the spot now that the WMO is resident (a hand over the floor,
                // the controller settles), and place the cast relative to the spot.
                // The creator body falls through the Commons floor here (a creator collision
                // matter, not this feature's), so the plain F-fly ghost holds it in place: no
                // gravity, position kept, and the floor ray under it still finds the room.
                _controller.Teleport(_interiorLightProbeSpot.X, _interiorLightProbeSpot.Y,
                    _interiorLightProbeSpot.Z + 0.3f);
                _controller.Flying = true;
                Vector3 feet = _interiorLightProbeSpot + new Vector3(0f, 0f, 0.3f);
                float yaw = _controller.Yaw;
                var fwd = new Vector3(MathF.Cos(yaw), MathF.Sin(yaw), 0f);
                var left = new Vector3(-fwd.Y, fwd.X, 0f);
                // A dwarf (Lieutenant Rotimer's display) and a human male, facing the camera.
                InteriorLightProbeSpawn("dwarf", ObjectTypeId.Unit,
                    ObjectFields.ForSyntheticUnit(13850), feet + fwd * 5f + left * 3f, yaw + MathF.PI);
                InteriorLightProbeSpawn("human", ObjectTypeId.Unit,
                    ObjectFields.ForSyntheticUnit(49), feet + fwd * 5f - left * 3f, yaw + MathF.PI);
                // The Ironforge mailbox (gameobject display 1947, type 19).
                var mailbox = ObjectFields.ForSyntheticUnit(0);
                mailbox.SetU32(ObjectFields.GAMEOBJECT_DISPLAYID, 1947);
                mailbox.SetU32(ObjectFields.GAMEOBJECT_TYPE_ID, 19);
                InteriorLightProbeSpawn("mailbox", ObjectTypeId.GameObject, mailbox,
                    feet + fwd * 7f, yaw);
                _interiorLightProbeStage = 2; _interiorLightProbeAt = now;
                return;
            }

            case 2:   // settle, verify the resolved light, screenshot ON
            {
                if (now - _interiorLightProbeAt < 6.0 || _controller is null) return;
                Vector3 feet = _controller.Position;
                Vector3? floor = _wmo?.ResolveInteriorLight(feet, _terrain?.SampleHeight(feet.X, feet.Y));
                InteriorLightProbeCheck("player stands in an interior cell", floor is not null,
                    $"feet=({feet.X:0.0},{feet.Y:0.0},{feet.Z:0.0}) camera cell={_wmo?.CameraGroup?.GroupName ?? "outdoors"}");
                Console.WriteLine($"[interiorlight-probe] player cell:{_wmo?.DescribeInteriorLight(feet, _terrain?.SampleHeight(feet.X, feet.Y))}");
                foreach (ulong guid in _interiorLightProbeGuids)
                    if (_entities.TryGet(guid, out WorldEntity ge))
                        Console.WriteLine($"[interiorlight-probe] {guid & 0xFF} cell:{_wmo?.DescribeInteriorLight(ge.Position, _terrain?.SampleHeight(ge.Position.X, ge.Position.Y))}");
                if (floor is Vector3 f)
                {
                    InteriorLightProbeCheck("floor light is warm (R > B)", f.X > f.Z,
                        $"floor MOCV=({f.X * 255:0},{f.Y * 255:0},{f.Z * 255:0})");
                    InteriorLightProbeCheck("floor light is not black", f.X + f.Y + f.Z > 0.15f);
                }
                Vector4 body = _interiorUnitLight.For(RenderSelfGuid, feet);
                InteriorLightProbeCheck("local body receives the room light", body.W > 0.9f,
                    $"uInteriorLight=({body.X:0.00},{body.Y:0.00},{body.Z:0.00},{body.W:0.00}) " +
                    $"tracked={_interiorUnitLight.Tracked} indoors={_interiorUnitLight.InteriorCount}");
                foreach (ulong guid in _interiorLightProbeGuids)
                {
                    if (!_entities.TryGet(guid, out WorldEntity e)) continue;
                    if (e.IsGameObject)
                    {
                        Vector3? goFloor = _wmo?.ResolveInteriorLight(e.Position,
                            _terrain?.SampleHeight(e.Position.X, e.Position.Y));
                        InteriorLightProbeCheck("mailbox receives the room light", goFloor is not null,
                            $"lit gameobjects={_doodads?.InteriorLitCount}");
                    }
                    else
                    {
                        Vector4 unit = _interiorUnitLight.For(guid, e.Position);
                        InteriorLightProbeCheck($"creature display {e.DisplayId} receives the room light", unit.W > 0.9f,
                            $"uInteriorLight=({unit.X:0.00},{unit.Y:0.00},{unit.Z:0.00},{unit.W:0.00})");
                    }
                }
                Vector3 sky = _atmosphere.AmbientColor;
                Console.WriteLine($"[interiorlight-probe] sky ambient=({sky.X:0.00},{sky.Y:0.00},{sky.Z:0.00}) " +
                                  $"sun=({_atmosphere.SunColor.X:0.00},{_atmosphere.SunColor.Y:0.00},{_atmosphere.SunColor.Z:0.00}) " +
                                  $"hour={Settings.Lighting.TimeOfDay:0.0}");
                _currentVantage = "interiorlight-on";
                ArmGameplayDump();
                _interiorLightProbeStage = 3; _interiorLightProbeAt = now;
                return;
            }

            case 3:   // the comparison: room light off for units and gameobjects
                if (now - _interiorLightProbeAt < 2.0) return;
                _interiorUnitLightProbeOff = true;
                _interiorLightProbeStage = 4; _interiorLightProbeAt = now;
                return;

            case 4:
                if (now - _interiorLightProbeAt < 2.0) return;
                _currentVantage = "interiorlight-off";
                ArmGameplayDump();
                _interiorLightProbeStage = 5; _interiorLightProbeAt = now;
                return;

            case 5:
                if (now - _interiorLightProbeAt < 2.5) return;
                Console.WriteLine(_interiorLightProbeFailures == 0
                    ? "[interiorlight-probe] VERDICT: ALL CHECKS PASSED"
                    : $"[interiorlight-probe] VERDICT: {_interiorLightProbeFailures} CHECK(S) FAILED");
                Console.Out.Flush();
                _quitRequested = true;
                _interiorLightProbeStage = 99;
                return;
        }
    }
}
