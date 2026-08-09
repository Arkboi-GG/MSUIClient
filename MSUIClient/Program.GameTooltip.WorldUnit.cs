using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private const uint WorldUnitPvpFlag = 0x0000_1000u;
    private const uint WorldUnitSkinnableFlag = 0x0400_0000u;

    private readonly record struct WorldUnitTooltipStaticSignature(
        string Name,
        string? Subtitle,
        int Level,
        uint PlayerLevel,
        int Reaction,
        bool IsPlayer,
        string? Race,
        string? Class,
        string? CreatureTypeName,
        uint Rank,
        bool Dead,
        string? FactionName,
        bool Pvp,
        bool Skinnable,
        bool Civilian,
        bool RacialLeader);

    private sealed record WorldUnitTooltipRuntime(
        ulong Guid,
        GameTooltipOwnerToken Token,
        WorldUnitTooltipStaticSignature Signature,
        bool Hovering);

    private WorldUnitTooltipRuntime? _worldUnitTooltip;

    private static string WorldUnitGameTooltipLiveToken(ulong guid) =>
        $"world-unit:{guid:X16}";

    private static string? WorldUnitCreatureTypeWord(uint creatureType) => creatureType switch
    {
        1 => "Beast",
        2 => "Dragonkin",
        3 => "Demon",
        4 => "Elemental",
        5 => "Giant",
        6 => "Undead",
        7 => "Humanoid",
        8 => "Critter",
        9 => "Mechanical",
        _ => null,
    };

    private static int WorldUnitGameTooltipReaction(
        FactionReaction reaction,
        bool isPlayer)
    {
        // MSUI has no PvP/duel reaction feed yet. Frozen UnitReaction's safe player fallback is
        // friendly; creatures retain the current target-toward-player comparator.
        if (isPlayer) return 5;
        return reaction switch
        {
            FactionReaction.Hostile => 2,
            FactionReaction.Neutral => 4,
            FactionReaction.Friendly => 5,
            _ => 4,
        };
    }

    private GameTooltipUnitSnapshot BuildWorldUnitGameTooltipSnapshot(WorldEntity unit)
    {
        CreatureQueryInfo? query = null;
        string name;
        if (unit.IsPlayer)
        {
            name = unit.Guid == LocalPlayerGuid && _net is not null
                ? _net.PlayerName
                : _playerNames.GetValueOrDefault(unit.Guid, "");
        }
        else
        {
            _creatureQueryRecords.TryGetValue(unit.Entry, out query);
            // The available build-5875 response is template-scoped. Pets therefore retain the
            // current template name; a per-pet given-name feed is an explicit later ingress gap.
            name = query is { Name.Length: > 0 }
                ? query.Name
                : _creatureNames.GetValueOrDefault(unit.Entry, "");
        }

        string? race = null;
        string? @class = null;
        if (unit.IsPlayer)
        {
            var bytes = unit.Fields.Bytes0;
            if (bytes.Race != 0) race = RaceName(bytes.Race);
            if (bytes.Class != 0) @class = ClassName(bytes.Class);
        }

        uint playerLevel = _entities.TryGet(LocalPlayerGuid, out WorldEntity player)
            ? player.Level
            : 0;
        int reaction = WorldUnitGameTooltipReaction(
            ReactionTargetTowardPlayer(unit), unit.IsPlayer);
        uint rank = query is not null && !unit.Fields.IsPetOrCharm ? query.Rank : 0;

        return new GameTooltipUnitSnapshot(
            WorldUnitGameTooltipLiveToken(unit.Guid),
            Exists: true,
            name,
            query?.Subname,
            (int)Math.Min(unit.Level, (uint)int.MaxValue),
            playerLevel,
            reaction,
            unit.IsPlayer,
            race,
            @class,
            query is null ? null : WorldUnitCreatureTypeWord(query.CreatureType),
            rank,
            unit.IsDead,
            // Faction.dbc in MSUI does not carry the frozen per-slot hidden/by-id gating. A
            // plausible faction label would overclaim parity, so this remains deliberately null.
            FactionName: null,
            (unit.Fields.UnitFlags & WorldUnitPvpFlag) != 0,
            (unit.Fields.UnitFlags & WorldUnitSkinnableFlag) != 0,
            query?.Civilian ?? false,
            query?.RacialLeader ?? false,
            unit.Fields.Health,
            unit.Fields.MaxHealth);
    }

    private static WorldUnitTooltipStaticSignature WorldUnitGameTooltipStaticSignature(
        in GameTooltipUnitSnapshot unit) => new(
            unit.Name,
            unit.Subtitle,
            unit.Level,
            unit.PlayerLevel,
            unit.Reaction,
            unit.IsPlayer,
            unit.Race,
            unit.Class,
            unit.CreatureTypeName,
            unit.Rank,
            unit.Dead,
            unit.FactionName,
            unit.Pvp,
            unit.Skinnable,
            unit.Civilian,
            unit.RacialLeader);

    private static GameTooltipUnitSnapshot WorldUnitGameTooltipHealthPush(
        ulong guid,
        bool exists,
        uint health,
        uint maxHealth) => new(
            WorldUnitGameTooltipLiveToken(guid), exists, "", null, 0, 0, 0, false,
            null, null, null, 0, false, null, false, false, false, false,
            health, maxHealth);

    private bool UpdateAndQueueWorldUnitGameTooltip(double now)
    {
        if (!_sharedTooltipFrameOpen || _sharedTooltipFrameResolved) return false;

        // `_hoveredGuid` is the existing PickUnit/nameplate verdict. This adapter consumes it
        // without ray-casting or changing selection. World-gameobject hover remains an explicit
        // ingress gap until a stable GO picker/cursor verdict exists.
        WorldEntity? hovered = _hoveredGuid != 0 &&
            _entities.TryGet(_hoveredGuid, out WorldEntity candidate) && candidate.IsUnit
                ? candidate
                : null;

        if (hovered is not null)
        {
            EnsureUnitNameRequested(hovered);
            GameTooltipUnitSnapshot unit = BuildWorldUnitGameTooltipSnapshot(hovered);
            WorldUnitTooltipStaticSignature signature =
                WorldUnitGameTooltipStaticSignature(unit);
            GameTooltipRuntimeSnapshot shared = SharedGameTooltipSnapshot();
            bool exactOwner = _worldUnitTooltip is { } current &&
                current.Guid == hovered.Guid && SharedGameTooltipIsOwned(current.Token);
            bool fading = exactOwner && shared.Lifecycle.FadeStartedAt is not null;
            bool rebuild = !exactOwner || !_worldUnitTooltip!.Hovering ||
                _worldUnitTooltip.Signature != signature || fading;

            if (rebuild)
            {
                if (!TryShowWorldUnitGameTooltip(hovered.Guid, unit,
                        out GameTooltipOwnerToken token))
                {
                    _worldUnitTooltip = null;
                    return false;
                }
                _worldUnitTooltip = new(hovered.Guid, token, signature, Hovering: true);
            }
            else if (_worldUnitTooltip is { } retainedRuntime)
            {
                _worldUnitTooltip = retainedRuntime with { Hovering = true };
            }

            if (_worldUnitTooltip is not { } active ||
                !TryRefreshSharedGameTooltipUnit(active.Token,
                    WorldUnitGameTooltipHealthPush(hovered.Guid, exists: true,
                        hovered.Fields.Health, hovered.Fields.MaxHealth)))
                return false;
        }
        else
        {
            if (_worldUnitTooltip is not { } departing) return false;
            if (!SharedGameTooltipIsOwned(departing.Token))
            {
                _worldUnitTooltip = null;
                return false;
            }

            _worldUnitTooltip = departing with { Hovering = false };
            // Frozen OnLeave changes lifecycle only. The retained mouseover UnitState (including
            // its last health bar) remains immutable throughout the half-second fade, even when
            // the departed entity changes health or despawns before that fade completes.
            BeginSharedGameTooltipFade(departing.Token, now,
                GameTooltipUiLaw.WorldFadeSeconds);
        }

        if (_worldUnitTooltip is not { } runtime ||
            !SharedGameTooltipIsOwned(runtime.Token))
        {
            _worldUnitTooltip = null;
            return false;
        }

        GameTooltipRuntimeSnapshot rendererSnapshot = SharedGameTooltipSnapshot();
        PreparedSharedGameTooltipRenderer? prepared =
            PrepareSharedGameTooltipRenderer(rendererSnapshot);
        if (prepared is null) return false;
        return QueueSharedGameTooltipRenderer(runtime.Token,
            SharedGameTooltipLeavePolicy.Fade(GameTooltipUiLaw.WorldFadeSeconds),
            () => DrawPreparedSharedGameTooltip(prepared));
    }
}
