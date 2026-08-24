namespace MSUIClient.Engine.UI;

public enum WorldCursorKind
{
    Point,
    Cast,
    Attack,
    Speak,
    Pickup,
    LootAll,
    Interact,
    Buy,
    Inspect,
    Trainer,
    Taxi,
    Skin,
    Mail,
    Mine,
    GatherHerbs,
    PickLock,
}

public readonly record struct WorldCursorState(WorldCursorKind Kind, bool Unable)
{
    public string Stem => Unable && Kind != WorldCursorKind.Point
        ? $"Unable{Kind}" : Kind.ToString();
}

/// <summary>The build-5875 world-unit cursor classifier and service-bit priority ladder.</summary>
public static class WorldCursorUiLaw
{
    public const uint GameObjectInUse = 0x1;
    public const uint GameObjectLocked = 0x2;
    public const uint GameObjectInteractCondition = 0x4;
    public const uint GameObjectNoInteract = 0x10;
    public const uint GameObjectActivate = 0x1;
    public const uint Gossip = 0x0000_0001;
    public const uint Questgiver = 0x0000_0002;
    public const uint Vendor = 0x0000_0004;
    public const uint FlightMaster = 0x0000_0008;
    public const uint Trainer = 0x0000_0010;
    public const uint SpiritHealer = 0x0000_0020;
    public const uint SpiritGuide = 0x0000_0040;
    public const uint Innkeeper = 0x0000_0080;
    public const uint Banker = 0x0000_0100;
    public const uint Petitioner = 0x0000_0200;
    public const uint TabardDesigner = 0x0000_0400;
    public const uint Battlemaster = 0x0000_0800;
    public const uint Auctioneer = 0x0000_1000;
    public const uint StableMaster = 0x0000_2000;
    public const uint Skinnable = 0x0400_0000;
    public const float ServiceRangeSquared = NpcSessionUiLaw.ServiceRangeSquared;
    public const float AttackRangeSquared = 109.2025f;
    public const float MeleeReachOffset = 1.33333f;
    public const float MeleeReachFloor = 5f;
    public const float GameObjectInteractRangeSquared = ServiceRangeSquared;

    /// <summary>
    /// Build-5875 unit interaction reach shared by the cursor verdict and the actual commit
    /// tail. Keeping this calculation here prevents an UnableLoot cursor from disagreeing with
    /// whether CMSG_LOOT is allowed to leave the client.
    /// </summary>
    public static float UnitMeleeReachSquared(float playerCombatReach, float unitCombatReach)
    {
        float reach = MathF.Max(playerCombatReach + unitCombatReach + MeleeReachOffset,
            MeleeReachFloor);
        return reach * reach;
    }

    public static WorldCursorState ItemTargeting(bool pointerOverUi) =>
        new(WorldCursorKind.Cast, Unable: !pointerOverUi);

    public static bool QuestgiverHasQuest(uint? status) =>
        status is not (null or 0u or 1u);

    /// <summary>Lowest consulted UNIT_NPC_FLAGS bit wins; repair is intentionally absent.</summary>
    public static WorldCursorKind? ServiceKind(uint flags, uint? questStatus)
    {
        if ((flags & Gossip) != 0 ||
            (flags & Questgiver) != 0 && QuestgiverHasQuest(questStatus))
            return WorldCursorKind.Speak;
        if ((flags & Vendor) != 0) return WorldCursorKind.Pickup;
        if ((flags & FlightMaster) != 0) return WorldCursorKind.Taxi;
        if ((flags & Trainer) != 0) return WorldCursorKind.Trainer;
        if ((flags & (SpiritHealer | SpiritGuide)) != 0) return WorldCursorKind.Speak;
        if ((flags & Innkeeper) != 0) return WorldCursorKind.Interact;
        if ((flags & Banker) != 0) return WorldCursorKind.Buy;
        if ((flags & (Petitioner | TabardDesigner | Battlemaster)) != 0)
            return WorldCursorKind.Speak;
        if ((flags & Auctioneer) != 0) return WorldCursorKind.Buy;
        if ((flags & StableMaster) != 0) return WorldCursorKind.Speak;
        return null;
    }

    public static WorldCursorState? Unit(bool isPlayer, bool dead, bool lootable,
        bool skinnable, bool knowsSkinning, bool attackable, bool serviceEligible,
        uint npcFlags, uint? questStatus, float distanceSquared,
        float playerCombatReach, float unitCombatReach, bool autoLoot, bool shiftHeld)
    {
        if (dead)
        {
            bool unable = distanceSquared >
                UnitMeleeReachSquared(playerCombatReach, unitCombatReach);
            if (lootable)
                return new(autoLoot != shiftHeld ? WorldCursorKind.LootAll :
                    WorldCursorKind.Pickup, unable);
            if (skinnable && knowsSkinning) return new(WorldCursorKind.Skin, unable);
            return null;
        }

        if (!isPlayer && serviceEligible && ServiceKind(npcFlags, questStatus) is { } service)
            return new(service, distanceSquared > ServiceRangeSquared);
        if (attackable)
            return new(WorldCursorKind.Attack, distanceSquared > AttackRangeSquared);
        return null;
    }

    /// <summary>Current Benilla's GameObject cursor law. The lock type is the first Lock.dbc
    /// requirement's LockType id: 1 Pick Lock, 2 Herbalism, 3 Mining, or zero/other.</summary>
    public static WorldCursorState? GameObject(int type, uint flags, uint dynamicFlags,
        uint firstLockType, bool fishingChannelOwned, bool meetingStoneQueued,
        bool? hostileTowardPlayer, bool lockRequirementMet, float distanceSquared)
    {
        if (!HighlightableGameObject(type, flags, dynamicFlags, hostileTowardPlayer,
                fishingChannelOwned, meetingStoneQueued)) return null;

        WorldCursorKind kind = type switch
        {
            9 => WorldCursorKind.Inspect,
            18 or 19 or 28 => WorldCursorKind.Mail,
            _ => firstLockType switch
            {
                1 => WorldCursorKind.PickLock,
                2 => WorldCursorKind.GatherHerbs,
                3 => WorldCursorKind.Mine,
                _ => WorldCursorKind.Interact,
            },
        };
        float rangeSquared = type switch
        {
            7 => 9f,
            17 => 10_000f,
            _ => GameObjectInteractRangeSquared,
        };
        bool lockUnmet = (flags & GameObjectLocked) != 0 && !lockRequirementMet;
        bool unable = kind != WorldCursorKind.PickLock &&
            (lockUnmet || distanceSquared > rangeSquared);
        return new(kind, unable);
    }

    public static bool StrategyIsDefault(int type) => type == 21 || type is < 0 or > 30;

    public static bool StrategyNeverHighlightable(int type) => StrategyIsDefault(type) ||
        type is 5 or 8 or 11 or 14 or 15 or 16 or 20 or 25 or 29 or 30;

    /// <summary>The per-type +0x14 gate shared by cursor, brighten and right-click USE.</summary>
    public static bool HighlightableGameObject(int type, uint flags, uint dynamicFlags,
        bool? hostileTowardPlayer, bool fishingChannelOwned, bool meetingStoneQueued)
    {
        if (StrategyNeverHighlightable(type)) return false;
        if (type == 17 && !fishingChannelOwned) return false;
        // MEETINGSTONE replaces the shared faction/flag gate outright.
        if (type == 23) return !meetingStoneQueued;
        // TRAP alone inverts the faction term. Unknown reaction is permissive.
        if (type == 6)
        {
            if (hostileTowardPlayer == false) return false;
        }
        else if (hostileTowardPlayer == true) return false;
        if ((flags & (GameObjectInUse | GameObjectNoInteract)) != 0) return false;
        return (flags & GameObjectInteractCondition) == 0 ||
            (dynamicFlags & GameObjectActivate) != 0;
    }

    /// <summary>The sibling +0x0c mouseover gate; deliberately not cursor highlightability.</summary>
    public static bool MouseoverEligibleGameObject(int type, uint flags, uint dynamicFlags,
        int? highlightColumn, bool? hostileTowardPlayer, bool fishingChannelOwned,
        bool meetingStoneQueued) => type switch
    {
        11 or 14 or 15 => false,
        8 or 16 or 25 or 30 => true,
        5 or 29 => highlightColumn is null || highlightColumn != 0,
        _ => HighlightableGameObject(type, flags, dynamicFlags, hostileTowardPlayer,
            fishingChannelOwned, meetingStoneQueued),
    };

    /// <summary>Marker strategies hover without +64 brighten; GENERIC/CAPTURE use their column.</summary>
    public static bool BrightensGameObject(int type, uint flags, uint dynamicFlags,
        int? highlightColumn, bool? hostileTowardPlayer, bool fishingChannelOwned,
        bool meetingStoneQueued) => type switch
    {
        8 or 16 or 25 or 30 => false,
        5 or 29 => highlightColumn is null || highlightColumn != 0,
        _ => HighlightableGameObject(type, flags, dynamicFlags, hostileTowardPlayer,
            fishingChannelOwned, meetingStoneQueued),
    };
}
