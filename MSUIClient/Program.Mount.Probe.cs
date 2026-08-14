namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Mount probe: boots the offline creator world, seats a spawned NPC and the
// player on the same steed, and screenshots both. The two mount paths are
// genuinely different code — a streamed unit rides inside CreatureRenderer's
// loop, the local player is drawn by CharacterRenderer from a seat handed over
// before that loop runs — so a probe that only checked one would sign off on
// half a system.
//
//   MSUI_MOUNT_PROBE="mount=10318;rider=1141"
//
// `mount` is a CreatureDisplayInfo id (10318 goblin rocket car, 2490 gnome car,
// 2404 riding horse, 15381 steam tonk — the one vanilla mount with no seat
// attachment, which is the interesting failure case). `rider` is the display id
// of the NPC spawned onto it. Output: dumps/gameplay-mount-probe-*.png.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private static readonly string? MountProbeSpec =
        Environment.GetEnvironmentVariable("MSUI_MOUNT_PROBE");

    private int _mountProbeStage = -1;      // -1 idle, 0 boot, 1 world-wait, 2 settle, 3 exit
    private double _mountProbeStageAt;
    private int _mountProbeMount = 10318;
    private int _mountProbeRider = 240;      // Orc Male Warrior — a humanoid you can see sitting
    private bool _mountProbeCancelOffset;    // exercise the toolkit's baked-offset correction
    private bool _mountProbeCancelled;
    private bool _mountProbeRecaptured;
    private bool _mountProbeKit;             // install and fire the cart kit
    private bool _mountProbeKitFired;

    private void UpdateMountProbe()
    {
        if (MountProbeSpec is null) return;
        double now = NowSeconds();

        if (_mountProbeStage < 0)
        {
            foreach (string part in MountProbeSpec.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string key = part[..eq].Trim().ToLowerInvariant();
                if (!int.TryParse(part[(eq + 1)..].Trim(), out int value)) continue;
                if (key == "mount") _mountProbeMount = value;
                else if (key == "rider") _mountProbeRider = value;
                else if (key == "cancel") _mountProbeCancelOffset = value != 0;
                else if (key == "kit") _mountProbeKit = value != 0;
            }
            Console.WriteLine($"[mount-probe] armed: mount={_mountProbeMount} rider={_mountProbeRider}");
            _mountProbeStage = 0;
            _mountProbeStageAt = now;
            return;
        }

        switch (_mountProbeStage)
        {
            case 0:   // boot into the offline world once GL is up
                if (_gl is null || _worldLoadStarted) return;
                if (now - _mountProbeStageAt < 1.0) return;
                EnterOfflineWorld();
                _mountProbeStage = 1;
                _mountProbeStageAt = now;
                return;

            case 1:   // world-wait, then mount the player and spawn a mounted NPC in front
                if (_worldLoading || !_creatorWorldRequested || _controller is null) return;
                if (now - _mountProbeStageAt < 2.0) return;
                _creatorMountDisplayId = _mountProbeMount;
                Settings.Mounts.RideDisplayId = _mountProbeMount;
                Settings.Mounts.Riding = true;
                SpawnCreatorCreature($"Rider {_mountProbeRider}", (uint)_mountProbeRider, 0f);
                Console.WriteLine($"[mount-probe] player + 1 NPC on display {_mountProbeMount}");
                _mountProbeStage = 2;
                _mountProbeStageAt = now;
                return;

            case 2:   // let the steed stream in and the seat settle, then capture
                if (now - _mountProbeStageAt < 4.0) return;
                if (_mountProbeKit && !_mountProbeKitFired)
                {
                    _mountProbeKitFired = true;
                    MountProbeFireKit(now);
                    // Rewind the settle clock so the capture lands about a second later,
                    // while the frost is still on screen rather than after it has expired.
                    _mountProbeStageAt = now - 3.0;
                    return;
                }
                MountProbeReport();
                _currentVantage = "mount-probe";
                ArmGameplayDump();
                _mountProbeStage = 3;
                _mountProbeStageAt = now;
                return;

            case 3:   // optionally cancel the model's baked origin offset and re-capture
                if (now - _mountProbeStageAt < 2.0) return;
                if (_mountProbeCancelOffset && !_mountProbeCancelled)
                {
                    _mountProbeCancelled = true;
                    if (_creatures?.TryMeasureMountOrigin(_mountProbeMount, out var drift) == true)
                    {
                        var tune = MountTuneFor(_mountProbeMount);
                        tune.MountForward = -drift.X;
                        tune.MountUp = -drift.Y;
                        tune.MountRight = -drift.Z;
                        Console.WriteLine($"[mount-probe] baked offset ({drift.X:F2}, {drift.Y:F2}, " +
                                          $"{drift.Z:F2}) cancelled");
                    }
                    else Console.WriteLine("[mount-probe] no baked offset measured");
                    _mountProbeStageAt = now;
                    return;
                }
                if (_mountProbeCancelled && !_mountProbeRecaptured)
                {
                    _mountProbeRecaptured = true;
                    MountProbeReport();
                    _currentVantage = "mount-probe-cancelled";
                    ArmGameplayDump();
                    _mountProbeStageAt = now;
                    return;
                }
                if (_mountProbeCancelled && now - _mountProbeStageAt < 2.0) return;
                Settings.Mounts.Riding = false;
                foreach (var spawn in _creatorSpawns)
                    if (_entities.TryGet(spawn.Guid, out var mountedUnit))
                        mountedUnit.Fields.SetU32(Net.ObjectFields.UNIT_MOUNTDISPLAYID, 0);
                _mountProbeStage = 4;
                _mountProbeStageAt = now;
                return;

            case 4:   // dismount verdict, then quit
                if (now - _mountProbeStageAt < 1.0) return;
                Console.WriteLine(_character?.MountSeat is null
                    ? "[mount-probe] dismount ok: player seat cleared"
                    : "[mount-probe] FAIL: player seat survived the dismount");
                foreach (var spawn in _creatorSpawns)
                    Console.WriteLine(_creatures?.TryGetMountSeat(spawn.Guid, out _) == true
                        ? $"[mount-probe] FAIL: npc {spawn.Name} seat survived the dismount"
                        : $"[mount-probe] dismount ok: npc {spawn.Name} seat cleared");
                Console.WriteLine("[mount-probe] done, quitting");
                Console.Out.Flush();
                _quitRequested = true;
                _mountProbeStage = 5;
                return;
        }
    }

    /// <summary>
    /// The verdict in text, so a failure is legible without opening the PNG: did each
    /// rider actually get a saddle, and where is it relative to the ground it stands on.
    /// </summary>
    private void MountProbeReport()
    {
        Console.WriteLine($"[mount-probe] mounts drawn this frame: {_creatures?.MountsDrawnLastFrame ?? 0}");
        Console.WriteLine($"[mount-probe] self guid=0x{RenderSelfGuid:X16} freeView={_freeView} " +
                          $"character={(_character is null ? "null" : _character.Enabled ? "enabled" : "disabled")}");

        if (_character is not null)
            Console.WriteLine(_character.MountSeat is { } seat
                ? $"[mount-probe] player seat ok: ({seat.M41:F2}, {seat.M42:F2}, {seat.M43:F2}) " +
                  $"vs feet ({_controller?.Position.X ?? 0:F2}, {_controller?.Position.Y ?? 0:F2}, " +
                  $"{_controller?.Position.Z ?? 0:F2}) {DescribeSeat(seat)}"
                : "[mount-probe] FAIL: player has no seat");

        foreach (var spawn in _creatorSpawns)
            Console.WriteLine(_creatures?.TryGetMountSeat(spawn.Guid, out var npcSeat) == true
                ? $"[mount-probe] npc {spawn.Name} seat ok: " +
                  $"({npcSeat.M41:F2}, {npcSeat.M42:F2}, {npcSeat.M43:F2}) {DescribeSeat(npcSeat)}"
                : $"[mount-probe] FAIL: npc {spawn.Name} has no seat");
    }

    /// <summary>
    /// Install the default kit on the ridden cart and fire every slot once: the slows should
    /// catch the spawned rider standing in front, and the dash should move the cart.
    /// </summary>
    private void MountProbeFireKit(double now)
    {
        int slots = InstallDefaultMountKit(_mountProbeMount);
        Console.WriteLine($"[mount-probe] kit installed: {slots} slot(s)");
        if (slots == 0)
        {
            Console.WriteLine("[mount-probe] FAIL: spell catalog produced no kit");
            return;
        }
        UpdateMountKit(now);   // materialise charges before anything is spent

        System.Numerics.Vector3 before = _controller?.Position ?? default;
        for (int i = 0; i < slots; i++)
            Console.WriteLine($"[mount-probe] slot {i + 1} fired: {FireMountKitSlot(i)}");

        float moved = System.Numerics.Vector3.Distance(before, _controller?.Position ?? default);
        Console.WriteLine(_mountKitSlows.Count > 0
            ? $"[mount-probe] slowed {_mountKitSlows.Count} unit(s)"
            : "[mount-probe] FAIL: nothing was slowed");
        Console.WriteLine(moved > 1f
            ? $"[mount-probe] dash moved the cart {moved:F1} yd"
            : $"[mount-probe] FAIL: dash moved {moved:F1} yd");
        Console.WriteLine($"[mount-probe] casts fired: {MountKitCastsFired}");
    }

    /// <summary>
    /// A seat matrix that misplaces a rider almost always carries the reason in its scale:
    /// the saddle bone's animated scale rides down the chain into the body parented to it.
    /// </summary>
    private static string DescribeSeat(System.Numerics.Matrix4x4 seat) =>
        System.Numerics.Matrix4x4.Decompose(seat, out var scale, out _, out _)
            ? $"scale=({scale.X:F3}, {scale.Y:F3}, {scale.Z:F3})"
            : "scale=indecomposable";
}
