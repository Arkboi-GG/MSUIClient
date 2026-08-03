using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Units;
using Silk.NET.OpenGL;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private readonly record struct GameplayLayoutRow(
        string Id, float[] Authored, float[] Screen);

    private bool _gameplayDumpRequested;
    private bool _gameplayDumpArmed;
    private string? _gameplayDumpDirectoryOverride;
    private readonly List<GameplayLayoutRow> _gameplayDumpLayout = [];
    private readonly List<ActionButtonVerdict> _gameplayDumpVisibleActions = [];

    private void ArmGameplayDump()
    {
        if (_config.DevTools || _liveRunOptions is not null) _gameplayDumpRequested = true;
    }

    private void BeginGameplayDumpFrame()
    {
        if (!_gameplayDumpRequested || (!_config.DevTools && _liveRunOptions is null)) return;
        _gameplayDumpRequested = false;
        _gameplayDumpArmed = true;
        _gameplayDumpLayout.Clear();
        _gameplayDumpVisibleActions.Clear();
    }

    private void CollectGameplayLayout(string id, float x, float y, float width, float height,
        Vector2 screenMin, Vector2 screenSize)
    {
        if (!_gameplayDumpArmed) return;
        _gameplayDumpLayout.Add(new GameplayLayoutRow(id,
            [x, y, width, height],
            [screenMin.X, screenMin.Y, screenSize.X, screenSize.Y]));
    }

    private void CollectGameplayAction(in ActionButtonVerdict verdict)
    {
        if (_gameplayDumpArmed) _gameplayDumpVisibleActions.Add(verdict);
    }

    private void FinishGameplayDump()
    {
        if (!_gameplayDumpArmed) return;
        _gameplayDumpArmed = false;

        string name = _currentVantage ?? DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string fileName = $"gameplay-{name}";
        string relativeJson = Path.Combine("dumps", fileName + ".json").Replace('\\', '/');
        string relativePng = Path.Combine("dumps", fileName + ".png").Replace('\\', '/');
        string jsonPath = _gameplayDumpDirectoryOverride is null ? Path.Combine(_config.RepoRoot, relativeJson) :
            Path.Combine(_gameplayDumpDirectoryOverride, fileName + ".json");
        string pngPath = _gameplayDumpDirectoryOverride is null ? Path.Combine(_config.RepoRoot, relativePng) :
            Path.Combine(_gameplayDumpDirectoryOverride, fileName + ".png");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
            object dump = BuildGameplayDump(name);
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(dump, DumpJson));

            bool png = false;
            try
            {
                png = TrySaveGameplayScreenshot(pngPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[gdump] screenshot unavailable - {ex.Message}");
            }
            Console.WriteLine($"[gdump] wrote {relativeJson}{(png ? " (+ .png)" : "")}");
            ImGui.SetClipboardText(relativeJson);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[gdump] failed - {ex.Message}");
        }
        finally
        {
            _gameplayDumpDirectoryOverride = null;
            _gameplayDumpLayout.Clear();
            _gameplayDumpVisibleActions.Clear();
        }
    }

    private object BuildGameplayDump(string name)
    {
        double now = NowSeconds();
        WorldEntity? player = _net is not null &&
            _entities.TryGet(_net.PlayerGuid, out WorldEntity playerEntity)
                ? playerEntity : null;
        WorldEntity? selection = _selectionGuid != 0 &&
            _entities.TryGet(_selectionGuid, out WorldEntity selectionEntity)
                ? selectionEntity : null;

        object? selectionFraming = selection is { IsCreature: true } creature &&
            _creatures?.TryGetPortraitFraming(creature, out CreatureRenderer.PortraitFraming framing) == true
                ? framing : null;
        string? playerOverrideKey = _character is null ? null : PlayerPortraitKey(_character);
        if (playerOverrideKey is not null && _portraitOverrides?.Find(playerOverrideKey) is null)
            playerOverrideKey = null;
        string? targetOverrideKey = selection is { IsCreature: true }
            ? CreaturePortraitKey(selection.DisplayId) : null;
        if (targetOverrideKey is not null && _portraitOverrides?.Find(targetOverrideKey) is null)
            targetOverrideKey = null;

        IReadOnlyList<IVerdict> verdictSnapshot = _verdicts.SnapshotAll();
        PortraitVerdict[] playerPortraits = verdictSnapshot.OfType<PortraitVerdict>()
            .Where(verdict => verdict.Subject == PortraitSubject.Player).ToArray();
        PortraitVerdict? playerPortrait = playerPortraits.Length == 0 ? null : playerPortraits[^1];
        PortraitVerdict[] targetPortraits = verdictSnapshot.OfType<PortraitVerdict>()
            .Where(verdict => verdict.Subject == PortraitSubject.Target).ToArray();
        PortraitVerdict? targetPortrait = targetPortraits.Length == 0 ? null : targetPortraits[^1];

        object[] AnimatorTracks(string unit) => Enumerable.Range(0, 3).Select(track =>
        {
            bool found = _lastAnimChoices.TryGetValue((unit, track), out var state);
            AnimChoice[] choices = verdictSnapshot.OfType<AnimChoice>()
                .Where(choice => choice.Unit.Equals(unit, StringComparison.OrdinalIgnoreCase) &&
                                 choice.Track == track).ToArray();
            AnimChoice? last = choices.Length == 0 ? null : choices[^1];
            return (object)new
            {
                track,
                requestedId = found ? state.Requested : -1,
                playedId = found ? state.Played : -1,
                last,
            };
        }).ToArray();

        string selectionUnit = selection is { IsCreature: true }
            ? $"creature:{selection.DisplayId}" : "selection";
        Vector2 framebuffer = _window.FramebufferSize;
        Vector2 display = ImGui.GetIO().DisplaySize;
        object[] equipment = _character?.Equipment.Pieces.Select(piece => (object)new
        {
            slot = piece.EquipmentSlot,
            displayId = piece.DisplayId,
            inventoryType = piece.InventoryType,
            name = piece.Name,
        }).ToArray() ?? Array.Empty<object>();

        return new
        {
            name,
            takenLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            map = _config.Start.MapName,
            scenario = new
            {
                player = new
                {
                    race = _character?.Race ?? "",
                    gender = _character?.Gender ?? "",
                    level = player?.Level ?? _net?.Player?.Level ?? 0,
                    guid = $"0x{_net?.PlayerGuid ?? 0:X16}",
                    position = player is null ? Array.Empty<float>() : V(player.Position),
                    health = player?.Fields.Health ?? 0,
                    maxHealth = player?.Fields.MaxHealth ?? 0,
                    powerType = player?.Fields.PowerType ?? 0,
                    power = player?.Fields.ActivePower ?? 0,
                    maxPower = player?.Fields.ActiveMaxPower ?? 0,
                    mounted = (player?.Fields.MountDisplayId ?? 0) != 0,
                    dead = player?.IsDead ?? false,
                },
                equipment,
                selection = new
                {
                    guid = $"0x{selection?.Guid ?? 0:X16}",
                    displayId = selection?.DisplayId ?? 0,
                    scale = selection?.Scale ?? 0f,
                    reaction = selection is null ? FactionReaction.Neutral : ReactionTargetTowardPlayer(selection),
                    dead = selection?.IsDead ?? false,
                    lootable = selection?.Fields.Lootable ?? false,
                    distanceToPlayer = selection is null || player is null
                        ? -1f : Vector3.Distance(player.Position, selection.Position),
                    portraitFraming = selectionFraming,
                },
                pendingCast = new
                {
                    spellId = _pendingCastSpell,
                    autoRepeatSpellId = _autoRepeatSpell,
                    queuedMeleeSpellId = _queuedMeleeSpell,
                    castBarSpellId = _castBarSpell,
                    stage = _castBarPhase,
                    remainingSeconds = Math.Max(0.0, _castBarEnds - now),
                },
                panelsOpen = new
                {
                    character = _characterOpen,
                    spellbook = _spellbookOpen,
                    backpack = _backpackOpen,
                    equippedBags = (bool[])_equippedBagOpen.Clone(),
                    loot = _loot.IsOpen,
                    settings = _settingsOpen,
                    portraitLab = _labPanelOpen,
                },
                uiScale = new
                {
                    effective = GameplayUiScale(),
                    configuredPreference = _skin?.Scale ?? _config.Window.UiScale,
                    framebuffer = new[] { framebuffer.X, framebuffer.Y },
                    displaySize = new[] { display.X, display.Y },
                },
            },
            portraits = new
            {
                player = new
                {
                    latest = playerPortrait,
                    usable = _playerPortraitUsable,
                    dirty = _playerPortraitDirty,
                    retryAt = _playerPortraitRetryAt,
                    activeOverrideKey = playerOverrideKey,
                },
                target = new
                {
                    latest = targetPortrait,
                    usable = _targetPortraitUsable,
                    retryAt = _targetPortraitRetryAt,
                    activeOverrideKey = targetOverrideKey,
                },
            },
            actionBar = new
            {
                page = _actionPage,
                packedSlots = Enumerable.Range(0, 120)
                    .Select(slot => _actions[slot]?.Packed ?? 0).ToArray(),
                visible = _gameplayDumpVisibleActions.ToArray(),
                recent = verdictSnapshot.OfType<ActionButtonVerdict>().TakeLast(20).ToArray(),
            },
            animator = new
            {
                player = AnimatorTracks("player"),
                selection = AnimatorTracks(selectionUnit),
                recent = verdictSnapshot.OfType<AnimChoice>().TakeLast(20).ToArray(),
            },
            combat = new
            {
                intentOn = _attackTargetGuid != 0,
                targetGuid = $"0x{_attackTargetGuid:X16}",
                serverEngaged = _net is not null && _combat.IsEngaged(_net.PlayerGuid),
                swingTimerOwner = "server",
                clientRangeEligibility = "unchecked",
                clientArcEligibility = "unchecked",
                traceActive = _combatTraceWriter is not null,
                tracePath = _combatTracePath,
                recent = verdictSnapshot.OfType<CombatVerdict>().TakeLast(50).ToArray(),
            },
            verdicts = verdictSnapshot.Select(verdict => new
            {
                channel = verdict.Channel,
                time = verdict.Time,
                line = verdict.ToLine(),
                data = (object)verdict,
            }).ToArray(),
            wire = _wire.Snapshot().TakeLast(100).ToArray(),
            layout = _gameplayDumpLayout.ToArray(),
        };
    }

    private unsafe bool TrySaveGameplayScreenshot(string path)
    {
        if (_gl is null) return false;
        Vector2 size = _window.FramebufferSize;
        int width = Math.Max(1, (int)size.X);
        int height = Math.Max(1, (int)size.Y);
        byte[] bottomUp = new byte[checked(width * height * 4)];
        fixed (byte* pixels = bottomUp)
            _gl.ReadPixels(0, 0, (uint)width, (uint)height,
                PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        int stride = width * 4;
        byte[] topDown = new byte[bottomUp.Length];
        for (int y = 0; y < height; y++)
            System.Buffer.BlockCopy(bottomUp, y * stride, topDown, (height - 1 - y) * stride, stride);
        PortraitRenderTarget.SaveRgbaPng(path, width, height, topDown);
        return true;
    }

    /// <summary>Evidence-only framebuffer capture for high-volume spell sequences.
    /// The acting renderer is sampled unchanged; only the stored evidence image is
    /// reduced to a bounded 640px width so a full class matrix remains reviewable.</summary>
    private unsafe bool TrySaveAnimationSequenceFrame(string path)
    {
        if (_gl is null) return false;
        Vector2 size = _window.FramebufferSize;
        int sourceWidth = Math.Max(1, (int)size.X);
        int sourceHeight = Math.Max(1, (int)size.Y);
        byte[] bottomUp = new byte[checked(sourceWidth * sourceHeight * 4)];
        fixed (byte* pixels = bottomUp)
            _gl.ReadPixels(0, 0, (uint)sourceWidth, (uint)sourceHeight,
                PixelFormat.Rgba, PixelType.UnsignedByte, pixels);

        int width = Math.Min(480, sourceWidth);
        int height = Math.Max(1, sourceHeight * width / sourceWidth);
        byte[] reduced = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        {
            int sourceY = sourceHeight - 1 - y * sourceHeight / height;
            for (int x = 0; x < width; x++)
            {
                int sourceX = x * sourceWidth / width;
                int source = (sourceY * sourceWidth + sourceX) * 4;
                int target = (y * width + x) * 4;
                System.Buffer.BlockCopy(bottomUp, source, reduced, target, 4);
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PortraitRenderTarget.SaveRgbaPng(path, width, height, reduced);
        return true;
    }
}
