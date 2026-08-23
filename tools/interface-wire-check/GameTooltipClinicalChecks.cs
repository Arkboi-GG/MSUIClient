using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

internal static class GameTooltipClinicalChecks
{
    public static void RunItemSnapshotOnly() => CheckB3PreparedItemSnapshot();

    public static void Run()
    {
        CheckOwnerGenerationAndClear();
        CheckSameOwnerTypedPublishReplacesEveryChannel();
        CheckFrameRendererCoordinator();
        CheckPreservedRendererAdapter();
        CheckB2FixedOwnerIdentity();
        CheckB3PreparedItemSnapshot();
        CheckB3FixedOwnerIdentity();
        CheckB4PreparedAuraSnapshot();
        CheckB4B5FixedOwnerIdentity();
        CheckB5MinimapResourceLifecycle();
        CheckB6CreatureQueryWireAndGate();
        CheckB6WorldUnitSemantics();
        CheckPartyProducerLifecycle();
        CheckFrameCoordinatorSourceFence();
        CheckB2ProducerSourceFence();
        CheckB3ItemProducerSourceFence();
        CheckB4B5ProducerSourceFence();
        CheckB6WorldUnitSourceFence();
        CheckB7B8RuntimePresentationSourceFence();
        CheckFadeLifecycle();
        CheckMoneyAndNewbieContent();
        CheckUnitContentAndLiveToken();
        CheckGameObjectResponderContent();
        CheckManagedOffsets();
        CheckBindingText();
    }

    private static void CheckSameOwnerTypedPublishReplacesEveryChannel()
    {
        object game = RuntimeHelpers.GetUninitializedObject(typeof(GameLoop));
        var owner = new GameTooltipOwnerKey("inventory-slot", 7);
        GameTooltipOwnerToken token = Invoke<GameTooltipOwnerToken>(game,
            "ClaimSharedGameTooltip", owner);
        var firstContent = new GameTooltipContent(GameTooltipAnchorKind.Cursor,
            [new("Old item", GameTooltipTextTone.White)], "old-live-token",
            new GameTooltipHealthState(true, 80, 40), UnitReaction: 2);
        Check(Invoke<bool>(game, "PublishSharedGameTooltip", token, firstContent,
                  (Vector2?)new Vector2(25, 30)) &&
              Invoke<bool>(game, "SetSharedGameTooltipMoney", token, 12_345u) &&
              Invoke<bool>(game, "SetSharedGameTooltipComparisonCount", token, 2),
            "GameTooltip same-owner replacement fixture could not arm every content channel");

        GameTooltipOwnerToken reclaimed = Invoke<GameTooltipOwnerToken>(game,
            "ClaimSharedGameTooltip", owner);
        object retained = Invoke<object>(game, "SharedGameTooltipSnapshot");
        Check(reclaimed == token &&
              Property<GameTooltipMoneyParts?>(retained, "Money") is not null &&
              Property<int>(retained, "ComparisonCount") == 2 &&
              Property<string?>(retained, "LiveUnitToken") == "old-live-token" &&
              Property<GameTooltipHealthState>(retained, "Health") ==
                  new GameTooltipHealthState(true, 80, 40),
            "GameTooltip ownership-only reclaim cleared retained push/additive state");

        var replacement = new GameTooltipContent(GameTooltipAnchorKind.OwnerRight,
            [new("New item", GameTooltipTextTone.Gold)]);
        Check(Invoke<bool>(game, "PublishSharedGameTooltip", reclaimed, replacement,
                  (Vector2?)null),
            "GameTooltip exact same owner rejected its typed replacement publication");
        object replaced = Invoke<object>(game, "SharedGameTooltipSnapshot");
        GameTooltipLifecycleState lifecycle =
            Property<GameTooltipLifecycleState>(replaced, "Lifecycle");
        Check(lifecycle.Owner == owner && lifecycle.Generation == token.Generation &&
              lifecycle.Visible && lifecycle.FadeStartedAt is null && lifecycle.Alpha == 1f &&
              Property<GameTooltipLine[]>(replaced, "Lines").SequenceEqual(replacement.Lines) &&
              Property<GameTooltipMoneyParts?>(replaced, "Money") is null &&
              Property<int>(replaced, "ComparisonCount") == 0 &&
              Property<string?>(replaced, "LiveUnitToken") is null &&
              Property<GameTooltipHealthState>(replaced, "Health") ==
                  GameTooltipHealthState.Hidden &&
              Property<int?>(replaced, "UnitReaction") is null &&
              Property<Vector2?>(replaced, "Cursor") is null,
            "GameTooltip typed same-owner publish retained stale money/comparison/live state");
    }

    private static void CheckPreservedRendererAdapter()
    {
        object game = RuntimeHelpers.GetUninitializedObject(typeof(GameLoop));
        var owner = new GameTooltipOwnerKey("micro-button", 1);
        GameTooltipOwnerToken token = Invoke<GameTooltipOwnerToken>(game,
            "ClaimSharedGameTooltip", owner);
        var content = new GameTooltipContent(GameTooltipAnchorKind.Cursor,
            [new("Old opaque owner", GameTooltipTextTone.White)], "party1",
            new GameTooltipHealthState(true, 100, 75), UnitReaction: 4);
        Check(Invoke<bool>(game, "PublishSharedGameTooltip", token, content,
                  (Vector2?)new Vector2(15, 20)) &&
              Invoke<bool>(game, "SetSharedGameTooltipMoney", token, 12_345u) &&
              Invoke<bool>(game, "SetSharedGameTooltipComparisonCount", token, 2),
            "preserved GameTooltip fixture could not seed every owner-scoped channel");

        int calls = 0;
        object beforeRejectedOffer = Invoke<object>(game, "SharedGameTooltipSnapshot");
        Check(!Invoke<bool>(game, "OfferPreservedSharedGameTooltipRenderer", owner,
                  (Action)(() => calls++)) &&
              SameSnapshot(beforeRejectedOffer,
                  Invoke<object>(game, "SharedGameTooltipSnapshot")) && calls == 0,
            "out-of-frame preserved GameTooltip offer mutated ownership or rendered");

        InvokeVoid(game, "BeginSharedGameTooltipFrame", 10d);
        Check(Invoke<bool>(game, "OfferPreservedSharedGameTooltipRenderer", owner,
                  (Action)(() => calls++)),
            "exact-owner preserved GameTooltip offer was rejected inside its frame");
        object cleared = Invoke<object>(game, "SharedGameTooltipSnapshot");
        GameTooltipLifecycleState clearedLifecycle =
            Property<GameTooltipLifecycleState>(cleared, "Lifecycle");
        Check(clearedLifecycle.Owner == owner && clearedLifecycle.Generation == token.Generation &&
              clearedLifecycle.Visible && clearedLifecycle.FadeStartedAt is null &&
              clearedLifecycle.Alpha == 1f &&
              Property<GameTooltipLine[]>(cleared, "Lines").Length == 0 &&
              Property<GameTooltipMoneyParts?>(cleared, "Money") is null &&
              Property<int>(cleared, "ComparisonCount") == 0 &&
              Property<string?>(cleared, "LiveUnitToken") is null &&
              Property<GameTooltipHealthState>(cleared, "Health") ==
                  GameTooltipHealthState.Hidden &&
              Property<int?>(cleared, "UnitReaction") is null &&
              Property<Vector2?>(cleared, "Cursor") is null,
            "same-owner preserved GameTooltip offer retained stale semantic channels");
        Check(Invoke<bool>(game, "ResolveAndDrawSharedGameTooltip") &&
              !Invoke<bool>(game, "ResolveAndDrawSharedGameTooltip") && calls == 1,
            "preserved GameTooltip renderer did not resolve exactly once");
        InvokeVoid(game, "EndSharedGameTooltipFrame");
        InvokeVoid(game, "BeginSharedGameTooltipFrame", 11d);
        InvokeVoid(game, "EndSharedGameTooltipFrame");
        Check(Property<GameTooltipLifecycleState>(
                  Invoke<object>(game, "SharedGameTooltipSnapshot"), "Lifecycle").Owner is null &&
              calls == 1,
            "unseen preserved GameTooltip owner did not immediately hide without repainting");

        object arbitration = RuntimeHelpers.GetUninitializedObject(typeof(GameLoop));
        var ownerA = new GameTooltipOwnerKey("spellbook-tab", 1);
        var ownerB = new GameTooltipOwnerKey("pet-action", 2);
        int firstCalls = 0, retainedCalls = 0, replacementCalls = 0;
        InvokeVoid(arbitration, "BeginSharedGameTooltipFrame", 20d);
        Check(Invoke<bool>(arbitration, "OfferPreservedSharedGameTooltipRenderer", ownerA,
                  (Action)(() => firstCalls++)),
            "first preserved GameTooltip arbitration offer was rejected");
        GameTooltipLifecycleState firstLifecycle = Property<GameTooltipLifecycleState>(
            Invoke<object>(arbitration, "SharedGameTooltipSnapshot"), "Lifecycle");
        var tokenA = new GameTooltipOwnerToken(ownerA, firstLifecycle.Generation);
        Check(Invoke<bool>(arbitration, "OfferPreservedSharedGameTooltipRenderer", ownerA,
                  (Action)(() => retainedCalls++)),
            "same-owner replacement renderer was rejected");
        GameTooltipLifecycleState retainedLifecycle = Property<GameTooltipLifecycleState>(
            Invoke<object>(arbitration, "SharedGameTooltipSnapshot"), "Lifecycle");
        Check(retainedLifecycle.Generation == firstLifecycle.Generation &&
              Invoke<bool>(arbitration, "OfferPreservedSharedGameTooltipRenderer", ownerB,
                  (Action)(() => replacementCalls++)),
            "preserved GameTooltip same-owner generation or replacement offer drift");
        GameTooltipLifecycleState replacementLifecycle = Property<GameTooltipLifecycleState>(
            Invoke<object>(arbitration, "SharedGameTooltipSnapshot"), "Lifecycle");
        Check(replacementLifecycle.Owner == ownerB &&
              replacementLifecycle.Generation == firstLifecycle.Generation + 1 &&
              !Invoke<bool>(arbitration, "QueueSharedGameTooltipRenderer", tokenA,
                  ImmediateLeavePolicy(), (Action)(() => firstCalls++)) &&
              Invoke<bool>(arbitration, "ResolveAndDrawSharedGameTooltip") &&
              firstCalls == 0 && retainedCalls == 0 && replacementCalls == 1,
            "later preserved GameTooltip owner did not suppress stale-generation renderers");
        InvokeVoid(arbitration, "EndSharedGameTooltipFrame");
    }

    private static void CheckB2FixedOwnerIdentity()
    {
        string[] multiSurfaces =
        [
            "MultiBarBottomLeft",
            "MultiBarBottomRight",
            "MultiBarRight",
            "MultiBarLeft",
        ];
        GameTooltipOwnerKey[] multiOwners = multiSurfaces
            .SelectMany(surface => Enumerable.Range(0, 12)
                .Select(index => InvokeStatic<GameTooltipOwnerKey>(
                    "ActionBarGameTooltipOwner", surface, index)))
            .ToArray();
        GameTooltipOwnerKey[] mainOwners = Enumerable.Range(1, 12)
            .Select(index => new GameTooltipOwnerKey("action-main", (ulong)index))
            .ToArray();
        Check(multiOwners.Distinct().Count() == 48 &&
              mainOwners.Concat(multiOwners).Distinct().Count() == 60 &&
              multiOwners.All(owner => owner.Identity is >= 1 and <= 12) &&
              mainOwners[0] != InvokeStatic<GameTooltipOwnerKey>(
                  "ActionBarGameTooltipOwner", "MultiBarRight", 0) &&
              mainOwners[0] != InvokeStatic<GameTooltipOwnerKey>(
                  "ActionBarGameTooltipOwner", "MultiBarLeft", 0) &&
              mainOwners[0] != InvokeStatic<GameTooltipOwnerKey>(
                  "ActionBarGameTooltipOwner", "MultiBarBottomRight", 0) &&
              mainOwners[0] != InvokeStatic<GameTooltipOwnerKey>(
                  "ActionBarGameTooltipOwner", "MultiBarBottomLeft", 0),
            "GameTooltip fixed main/multi button identity collided across mirrored wire slots");

        var petSlot = new GameTooltipOwnerKey("pet-action", 3);
        var samePetSlotAfterRebind = new GameTooltipOwnerKey("pet-action", 3);
        Check(petSlot == samePetSlotAfterRebind &&
              petSlot != new GameTooltipOwnerKey("pet-action", 4) &&
              new GameTooltipOwnerKey("spellbook-button", 1) !=
                  new GameTooltipOwnerKey("spellbook-tab", 1) &&
              new GameTooltipOwnerKey("micro-button", 1) !=
                  new GameTooltipOwnerKey("main-menu-xp", 1) &&
              new GameTooltipOwnerKey("main-menu-xp", 1) !=
                  new GameTooltipOwnerKey("main-menu-performance", 1),
            "GameTooltip fixed pet/spellbook/micro/managed owner identity drift");
    }

    private readonly record struct PreparedItemPaint(
        string Kind,
        string Text,
        Vector4 Color);

    private static void CheckB3PreparedItemSnapshot()
    {
        object game = RuntimeHelpers.GetUninitializedObject(typeof(GameLoop));
        var item = new ItemTemplate
        {
            Name = "Clinical Sword",
            Quality = 3,
            Bonding = 2,
            Armor = 17,
            RequiredLevel = 20,
            ItemLevel = 25,
            Stackable = 5,
            Description = "Clinical flavor",
            Stats = [new ItemStat(3, 5), new ItemStat(99, -2)],
            Damages = [new ItemDamage(1.2f, 3.4f, 0)],
            Resistances = [1, 2, 3, 4, 5, 6],
        };
        object full = Invoke<object>(game, "PrepareItemTooltipBodySnapshot",
            item, 3u, 2u, 10u, false, (uint?)null, null);
        object compact = Invoke<object>(game, "PrepareItemTooltipBodySnapshot",
            item, 3u, 2u, 10u, true, (uint?)null, null);
        PreparedItemPaint[] fullBefore = PreparedItemPaints(full);
        PreparedItemPaint[] compactBefore = PreparedItemPaints(compact);

        Check(fullBefore.SequenceEqual(new PreparedItemPaint[]
            {
                new("Colored", "Clinical Sword", new Vector4(0, .44f, .87f, 1)),
                new("Plain", "Binds when equipped", default),
                new("Plain", "1 - 3 Damage", default),
                new("Plain", "17 Armor", default),
                new("Plain", "+5 Agility", default),
                new("Plain", "+2 Fire Resistance", default),
                new("Plain", "+3 Nature Resistance", default),
                new("Plain", "+4 Frost Resistance", default),
                new("Plain", "+5 Shadow Resistance", default),
                new("Plain", "+6 Arcane Resistance", default),
                new("Colored", "Durability 2 / 10", Vector4.One),
                new("Colored", "Requires Level 20",
                    new Vector4(1f, 32f / 255f, 32f / 255f, 1f)),
                new("Colored", "\"Clinical flavor\"",
                    new Vector4(1f, 210f / 255f, 0, 1)),
            }) &&
              compactBefore[0] == new PreparedItemPaint(
                  "Colored", "Clinical Sword", Vector4.One) &&
              compactBefore.All(operation => operation.Text != "\"Clinical flavor\"") &&
              compactBefore.Skip(1).SequenceEqual(fullBefore.Skip(1).SkipLast(1)),
            "B3 prepared item body changed text, tone, order, compact name, or description cut");

        item.Name = "Mutated";
        item.Quality = 6;
        item.Bonding = 1;
        item.Armor = 999;
        item.RequiredLevel = 60;
        item.ItemLevel = 70;
        item.Stackable = 99;
        item.Description = "Mutated flavor";
        item.Stats.Clear();
        item.Stats.Add(new ItemStat(4, 100));
        item.Damages.Clear();
        item.Damages.Add(new ItemDamage(100, 200, 0));
        item.Resistances[0] = 999;
        Check(PreparedItemPaints(full).SequenceEqual(fullBefore) &&
              PreparedItemPaints(compact).SequenceEqual(compactBefore),
            "B3 deferred item body retained mutable ItemTemplate/list/array state");

        var currentLawItem = new ItemTemplate
        {
            Name = "Charged Mystery",
            Quality = 1,
            RandomProperty = 44,
            Description = "A deliberately long flavor line whose logical row must retain " +
                          "wrapped geometry inside the positioned tooltip plate.",
            Spells =
            [
                new ItemSpellTemplate(4057, 0, -5, 0, 0, 0),
                default, default, default, default,
            ],
        };
        object currentLaw = Invoke<object>(game, "PrepareItemTooltipBodySnapshot",
            currentLawItem, 1u, 0u, 0u, false, (uint?)null, null);
        object[] currentOperations = Property<IEnumerable>(currentLaw, "Operations")
            .Cast<object>().ToArray();
        Check(currentOperations.Select(operation => Property<string>(operation, "Text"))
                  .SequenceEqual(new[]
                  {
                      "Charged Mystery", "<Random enchantment>", "5 Charges",
                      "\"A deliberately long flavor line whose logical row must retain " +
                      "wrapped geometry inside the positioned tooltip plate.\"",
                  }) &&
              !Property<bool>(currentOperations[1], "Wrap") &&
              Property<bool>(currentOperations[^1], "Wrap"),
            "B3 current item law lost random-enchant/charge order or wrapped flavor-row identity");

        Check(InvokeStatic<int>("HighestLiveComparisonOrdinal",
                  (IEnumerable<int>)new[] { 2 }) == 2 &&
              InvokeStatic<int>("HighestLiveComparisonOrdinal",
                  (IEnumerable<int>)Array.Empty<int>()) == 0,
            "B3 gapped ShoppingTooltip2 ordinal compacted or empty comparison count drifted");

        InvokeVoid(game, "BeginSharedGameTooltipFrame", 50d);
        var inventoryOwner = new GameTooltipOwnerKey("item:inventory-container:0", 16);
        Check(Invoke<bool>(game, "OfferPreparedItemTooltip", inventoryOwner, full,
                  (Vector2?)new Vector2(24, 18), 2, (Action?)null, (Vector2?)null),
            "B3 prepared item offer could not enter the guarded shared frame");
        object offered = Invoke<object>(game, "SharedGameTooltipSnapshot");
        Check(Property<int>(offered, "ComparisonCount") == 2 &&
              Property<GameTooltipLifecycleState>(offered, "Lifecycle").Owner == inventoryOwner,
            "B3 prepared item offer lost its highest live comparison ordinal or physical owner");
        _ = Invoke<GameTooltipOwnerToken>(game, "ClaimSharedGameTooltip",
            new GameTooltipOwnerKey("item:character-ammo", 0));
        Check(!Invoke<bool>(game, "ResolveAndDrawSharedGameTooltip"),
            "B3 stale prepared item callback rendered after owner-generation replacement");
        InvokeVoid(game, "EndSharedGameTooltipFrame");

        string root = ClientConfig.FindRepoRoot();
        string inventory = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Panels", "GameLoop.Inventory.cs"));
        Check(inventory.Contains("PrepareInventoryItemTooltipRenderer(body, at, preparedPivot)",
                  StringComparison.Ordinal) &&
              inventory.Contains("DrawPreparedInventoryItemTooltip(inventoryRenderer)",
                  StringComparison.Ordinal) &&
              inventory.Contains("GameTooltipUiLaw.Padding * scale", StringComparison.Ordinal) &&
              inventory.Contains("GameTooltipUiLaw.LogicalRowGap * scale", StringComparison.Ordinal) &&
              inventory.Contains("GameTooltipUiLaw.WrapText(operation.Text, wrapWidth",
                  StringComparison.Ordinal) &&
              inventory.Contains("GameTooltipHeaderText", StringComparison.Ordinal) &&
              inventory.Contains("GameTooltipText", StringComparison.Ordinal) &&
              inventory.Contains("SharedGameTooltipBackdropTints(1f)", StringComparison.Ordinal) &&
              inventory.Contains("SharedGameTooltipClampToScreen", StringComparison.Ordinal) &&
              inventory.Contains("InventoryUiLaw.ItemTooltipSeat(", StringComparison.Ordinal) &&
              inventory.Contains("tooltipSeat.Pivot", StringComparison.Ordinal) &&
              !inventory.Contains("ImGui.SetNextWindowPos(at, ImGuiCond.Always, preparedPivot)",
                  StringComparison.Ordinal),
            "B3 positioned inventory hover escaped the immutable FrameXML-font/rule seat renderer");
    }

    private static void CheckB3FixedOwnerIdentity()
    {
        // A six-, ten-, or sixteen-seat frame can bind different logical contents to the same
        // authored physical button. The fixed control, not that logical occupant, is the owner.
        GameTooltipOwnerKey sixSeat = InvokeStatic<GameTooltipOwnerKey>(
            "InventoryItemGameTooltipOwner", 2, 6 - 0);
        GameTooltipOwnerKey tenSeat = InvokeStatic<GameTooltipOwnerKey>(
            "InventoryItemGameTooltipOwner", 2, 10 - 4);
        GameTooltipOwnerKey sixteenSeat = InvokeStatic<GameTooltipOwnerKey>(
            "InventoryItemGameTooltipOwner", 2, 16 - 10);
        Check(sixSeat == tenSeat && tenSeat == sixteenSeat &&
              sixSeat == new GameTooltipOwnerKey("item:inventory-container:2", 6) &&
              sixSeat != InvokeStatic<GameTooltipOwnerKey>(
                  "InventoryItemGameTooltipOwner", 3, 6) &&
              sixSeat != InvokeStatic<GameTooltipOwnerKey>(
                  "InventoryItemGameTooltipOwner", 2, 5) &&
              new GameTooltipOwnerKey("item:character-paper-doll", 7) !=
                  new GameTooltipOwnerKey("item:inspect-paper-doll", 7) &&
              new GameTooltipOwnerKey("item:character-ammo", 0) !=
                  new GameTooltipOwnerKey("item:character-paper-doll", 0) &&
              new GameTooltipOwnerKey("bag-button", 0) !=
                  new GameTooltipOwnerKey("keyring-button", 0) &&
              new GameTooltipOwnerKey("paperdoll-stat:CharacterStrengthFrame", 0) !=
                  new GameTooltipOwnerKey("paperdoll-stat:CharacterArmorFrame", 0),
            "B3 fixed item/stat control owners collided or followed logical content rebinds");
    }

    private static void CheckB4PreparedAuraSnapshot()
    {
        object game = RuntimeHelpers.GetUninitializedObject(typeof(GameLoop));
        FieldInfo catalogField = typeof(GameLoop).GetField("_spellCatalog",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidDataException(
                "B4 prepared aura fixture could not find the spell-catalog snapshot boundary");
        catalogField.SetValue(game, new SpellCatalog());

        object aura = CreateGameLoopNested("AuraSnapshot",
            (byte)5, 77u, (byte)1, (byte)10, (byte)3);
        object timer = CreateGameLoopNested("AuraTimer", 5_000u, 12d, 7d);
        SpellInfo spell = default(SpellInfo) with
        {
            Id = 77,
            Name = "Clinical Aura",
            Rank = "Rank 2",
            IconPath = @"Interface\Icons\Temp.blp",
            Description = "Clinical description",
        };
        object prepared = Invoke<object>(game, "PreparePlayerAuraTooltip",
            aura, (SpellInfo?)spell, timer, 10d);
        Check(Property<string>(prepared, "Title") == "Clinical Aura (Rank 2)" &&
              Property<string?>(prepared, "StackLine") == "3 stacks" &&
              Property<string?>(prepared, "Description") == "Clinical description" &&
              Property<string?>(prepared, "RemainingLine") == "2s remaining" &&
              Property<string?>(prepared, "HelpfulLine") == "Right-click to cancel",
            "B4 prepared aura changed title/rank/stacks/description/time/cancel wording");

        // The queued callback owns only these final strings. Replacing the live catalog and the
        // caller's value after preparation must not alter the deferred render snapshot.
        catalogField.SetValue(game, null);
        spell = spell with
        {
            Name = "Mutated Aura",
            Rank = "Rank 9",
            Description = "Mutated description",
        };
        Check(Property<string>(prepared, "Title") == "Clinical Aura (Rank 2)" &&
              Property<string?>(prepared, "Description") == "Clinical description",
            "B4 deferred aura tooltip retained mutable spell-catalog/value state");

        object harmful = CreateGameLoopNested("AuraSnapshot",
            (byte)32, 88u, (byte)0, (byte)1, (byte)1);
        object fallback = Invoke<object>(game, "PreparePlayerAuraTooltip",
            harmful, (SpellInfo?)null, null, 20d);
        Check(Property<string>(fallback, "Title") == "Spell 88" &&
              Property<string?>(fallback, "StackLine") is null &&
              Property<string?>(fallback, "Description") is null &&
              Property<string?>(fallback, "RemainingLine") is null &&
              Property<string?>(fallback, "HelpfulLine") is null,
            "B4 harmful/permanent/unknown aura fallback content drifted");
    }

    private static void CheckB4B5FixedOwnerIdentity()
    {
        GameTooltipOwnerKey aura0 = InvokeStatic<GameTooltipOwnerKey>(
            "PlayerAuraGameTooltipOwner", 0);
        GameTooltipOwnerKey sameAuraButtonAfterRebind = InvokeStatic<GameTooltipOwnerKey>(
            "PlayerAuraGameTooltipOwner", 0);
        GameTooltipOwnerKey aura23 = InvokeStatic<GameTooltipOwnerKey>(
            "PlayerAuraGameTooltipOwner", 23);
        GameTooltipOwnerKey nodeA = InvokeStatic<GameTooltipOwnerKey>(
            "MinimapResourceGameTooltipOwner", 0x111UL);
        GameTooltipOwnerKey sameNode = InvokeStatic<GameTooltipOwnerKey>(
            "MinimapResourceGameTooltipOwner", 0x111UL);
        GameTooltipOwnerKey nodeB = InvokeStatic<GameTooltipOwnerKey>(
            "MinimapResourceGameTooltipOwner", 0x222UL);
        Check(aura0 == new GameTooltipOwnerKey("player-aura-button", 0) &&
              aura0 == sameAuraButtonAfterRebind &&
              aura23 == new GameTooltipOwnerKey("player-aura-button", 23) &&
              aura0 != aura23 &&
              nodeA == new GameTooltipOwnerKey("minimap-resource-dot", 0x111UL) &&
              nodeA == sameNode && nodeA != nodeB &&
              aura0 != nodeA,
            "B4/B5 fixed aura-button or resource-GUID tooltip owner identity drifted");
    }

    private static void CheckB5MinimapResourceLifecycle()
    {
        object game = RuntimeHelpers.GetUninitializedObject(typeof(GameLoop));
        object copper = CreateGameLoopNested("MinimapResourceTooltipCandidate",
            0xC0FFEEUL, "Copper Vein");
        Check(!Invoke<bool>(game, "UpdateAndQueueMinimapResourceTooltip", copper) &&
              !Invoke<GameTooltipOwnerToken>(game,
                  "CurrentSharedGameTooltipOwnerToken").IsValid,
            "B5 minimap resource offer claimed ownership outside the tooltip frame");

        InvokeVoid(game, "BeginSharedGameTooltipFrame", 100d);
        Check(Invoke<bool>(game, "UpdateAndQueueMinimapResourceTooltip", copper),
            "B5 hovered minimap resource could not queue its immutable renderer");
        object hovered = Invoke<object>(game, "SharedGameTooltipSnapshot");
        GameTooltipLifecycleState hoveredLifecycle =
            Property<GameTooltipLifecycleState>(hovered, "Lifecycle");
        Check(hoveredLifecycle.Owner ==
                  new GameTooltipOwnerKey("minimap-resource-dot", 0xC0FFEEUL) &&
              hoveredLifecycle.Visible && hoveredLifecycle.Alpha == 1f &&
              Property<GameTooltipLine[]>(hovered, "Lines").Length == 0,
            "B5 minimap resource claim lost its GUID owner, full alpha, or opaque clear");
        InvokeVoid(game, "EndSharedGameTooltipFrame");

        InvokeVoid(game, "BeginSharedGameTooltipFrame", 100.1d);
        Check(Invoke<bool>(game, "UpdateAndQueueMinimapResourceTooltip", (object?)null),
            "B5 minimap departure did not retain and requeue its renderer");
        GameTooltipLifecycleState fadeStart = Property<GameTooltipLifecycleState>(
            Invoke<object>(game, "SharedGameTooltipSnapshot"), "Lifecycle");
        Check(fadeStart.Owner == hoveredLifecycle.Owner && fadeStart.FadeStartedAt == 100.1d &&
              fadeStart.FadeSeconds == GameTooltipUiLaw.WorldFadeSeconds &&
              fadeStart.Alpha == 1f,
            "B5 minimap departure did not arm the explicit shared half-second fade");
        InvokeVoid(game, "EndSharedGameTooltipFrame");

        InvokeVoid(game, "BeginSharedGameTooltipFrame", 100.35d);
        Check(Invoke<bool>(game, "UpdateAndQueueMinimapResourceTooltip", (object?)null),
            "B5 active minimap fade did not requeue its prepared renderer");
        GameTooltipLifecycleState halfFade = Property<GameTooltipLifecycleState>(
            Invoke<object>(game, "SharedGameTooltipSnapshot"), "Lifecycle");
        Check(halfFade.FadeStartedAt == 100.1d && Near(halfFade.Alpha, .5f),
            "B5 repeated no-hover update restarted or skipped its retained fade");
        InvokeVoid(game, "EndSharedGameTooltipFrame");

        InvokeVoid(game, "BeginSharedGameTooltipFrame", 100.6d);
        Check(!Invoke<bool>(game, "UpdateAndQueueMinimapResourceTooltip", (object?)null) &&
              Property<GameTooltipLifecycleState>(
                  Invoke<object>(game, "SharedGameTooltipSnapshot"), "Lifecycle").Owner is null &&
              typeof(GameLoop).GetField("_minimapResourceTooltip",
                  BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(game) is null,
            "B5 terminal minimap fade retained an owner, callback, or local runtime");
        InvokeVoid(game, "EndSharedGameTooltipFrame");

        // DrawCombatHud's WorldMap return skips the producer and reaches End. The explicit
        // retained lease must arm departure without ever invoking the ImGui renderer callback.
        object worldMapGame = RuntimeHelpers.GetUninitializedObject(typeof(GameLoop));
        object herb = CreateGameLoopNested("MinimapResourceTooltipCandidate",
            0xABCDUL, "Peacebloom");
        InvokeVoid(worldMapGame, "BeginSharedGameTooltipFrame", 200d);
        Check(Invoke<bool>(worldMapGame, "UpdateAndQueueMinimapResourceTooltip", herb),
            "B5 WorldMap departure fixture could not retain its fade lease");
        InvokeVoid(worldMapGame, "EndSharedGameTooltipFrame");
        InvokeVoid(worldMapGame, "BeginSharedGameTooltipFrame", 201d);
        InvokeVoid(worldMapGame, "EndSharedGameTooltipFrame");
        GameTooltipLifecycleState mapDeparture = Property<GameTooltipLifecycleState>(
            Invoke<object>(worldMapGame, "SharedGameTooltipSnapshot"), "Lifecycle");
        Check(mapDeparture.Owner ==
                  new GameTooltipOwnerKey("minimap-resource-dot", 0xABCDUL) &&
              mapDeparture.FadeStartedAt == 201d && mapDeparture.Alpha == 1f,
            "B5 WorldMap renderer-free End did not arm the retained resource fade");
        InvokeVoid(worldMapGame, "BeginSharedGameTooltipFrame", 201.25d);
        Check(!Invoke<bool>(worldMapGame, "ResolveAndDrawSharedGameTooltip") &&
              Near(Property<GameTooltipLifecycleState>(
                  Invoke<object>(worldMapGame, "SharedGameTooltipSnapshot"),
                  "Lifecycle").Alpha, .5f),
            "B5 WorldMap departure repainted or failed to advance without a producer");
        InvokeVoid(worldMapGame, "EndSharedGameTooltipFrame");

        object staleGame = RuntimeHelpers.GetUninitializedObject(typeof(GameLoop));
        InvokeVoid(staleGame, "BeginSharedGameTooltipFrame", 300d);
        Check(Invoke<bool>(staleGame, "UpdateAndQueueMinimapResourceTooltip", copper),
            "B5 stale-owner fixture could not queue its resource callback");
        InvokeVoid(staleGame, "EndSharedGameTooltipFrame");
        InvokeVoid(staleGame, "BeginSharedGameTooltipFrame", 301d);
        GameTooltipOwnerToken replacement = Invoke<GameTooltipOwnerToken>(staleGame,
            "ClaimSharedGameTooltip", new GameTooltipOwnerKey("item:inventory-container:0", 16));
        Check(!Invoke<bool>(staleGame, "UpdateAndQueueMinimapResourceTooltip", (object?)null) &&
              Property<GameTooltipLifecycleState>(
                  Invoke<object>(staleGame, "SharedGameTooltipSnapshot"), "Lifecycle").Owner ==
                  replacement.Owner &&
              typeof(GameLoop).GetField("_minimapResourceTooltip",
                  BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(staleGame) is null &&
              !Invoke<bool>(staleGame, "ResolveAndDrawSharedGameTooltip"),
            "B5 stale minimap runtime faded, queued, painted, or cleared its replacement owner");
        InvokeVoid(staleGame, "EndSharedGameTooltipFrame");
    }

    private static void CheckB6CreatureQueryWireAndGate()
    {
        var hitWriter = new PacketWriter();
        hitWriter.WriteU32(12_397);
        hitWriter.WriteCString("Clinical Gnoll");
        hitWriter.WriteCString("");
        hitWriter.WriteCString("");
        hitWriter.WriteCString("");
        hitWriter.WriteCString("the Query Keeper");
        hitWriter.WriteU32(0xA5A5_1000u); // type flags
        hitWriter.WriteU32(7);            // Humanoid
        hitWriter.WriteU32(9);            // pet family (pet paper-doll family/diet feed)
        hitWriter.WriteU32(3);            // Boss
        hitWriter.WriteU32(0x1122_3344u); // unknown
        hitWriter.WriteU32(77);           // pet spell-list id
        hitWriter.WriteU32(456);          // display id
        hitWriter.WriteU8(1);             // civilian
        hitWriter.WriteU8(0);             // racial leader
        byte[] hitBody = hitWriter.ToArray();

        CreatureQueryResponse hit = CreatureQueryPacket.Parse(hitBody);
        Check(hit.Entry == 12_397 && hit.Info is
              {
                  Name: "Clinical Gnoll",
                  Subname: "the Query Keeper",
                  TypeFlags: 0xA5A5_1000u,
                  CreatureType: 7,
                  PetFamily: 9,
                  Rank: 3,
                  Civilian: true,
                  RacialLeader: false,
              },
            "B6 creature-query hit lost name/subname/type/family/rank/civilian/leader fields");

        var emptySubnameWriter = new PacketWriter();
        emptySubnameWriter.WriteU32(88);
        emptySubnameWriter.WriteCString("No Subtitle");
        for (int i = 0; i < 4; i++) emptySubnameWriter.WriteCString("");
        for (int i = 0; i < 7; i++) emptySubnameWriter.WriteU32(0);
        emptySubnameWriter.WriteU8(0);
        emptySubnameWriter.WriteU8(1);
        CreatureQueryResponse emptySubname =
            CreatureQueryPacket.Parse(emptySubnameWriter.ToArray());
        Check(emptySubname.Info is { Subname: null, RacialLeader: true },
            "B6 creature-query empty subname or racial-leader byte drift");

        var missWriter = new PacketWriter();
        missWriter.WriteU32(0x8000_0000u | 12_397u);
        byte[] missBody = missWriter.ToArray();
        CreatureQueryResponse miss = CreatureQueryPacket.Parse(missBody);
        Check(miss.Entry == 12_397 && miss.Info is null,
            "B6 creature-query high-bit miss was not retained as a negative entry record");

        for (int length = 0; length < hitBody.Length; length++)
        {
            byte[] truncated = hitBody.AsSpan(0, length).ToArray();
            ExpectPacketReject(() => CreatureQueryPacket.Parse(truncated),
                $"B6 creature-query accepted truncated hit length {length}");
        }
        for (int length = 0; length < missBody.Length; length++)
        {
            byte[] truncated = missBody.AsSpan(0, length).ToArray();
            ExpectPacketReject(() => CreatureQueryPacket.Parse(truncated),
                $"B6 creature-query accepted truncated miss length {length}");
        }
        ExpectPacketReject(() => CreatureQueryPacket.Parse([.. hitBody, 0]),
            "B6 creature-query accepted a hit with trailing bytes");
        ExpectPacketReject(() => CreatureQueryPacket.Parse([.. missBody, 0]),
            "B6 creature-query accepted a miss with trailing bytes");

        object game = RuntimeHelpers.GetUninitializedObject(typeof(GameLoop));
        var records = new Dictionary<uint, CreatureQueryInfo?>();
        var pending = new HashSet<uint>();
        SetField(game, "_creatureQueryRecords", records);
        SetField(game, "_queriedCreatureNames", pending);
        Check(!Invoke<bool>(game, "TryBeginCreatureQuery", 0u) &&
              Invoke<bool>(game, "TryBeginCreatureQuery", 101u) &&
              !Invoke<bool>(game, "TryBeginCreatureQuery", 101u),
            "B6 creature-query gate did not reject zero or coalesce one pending entry ask");
        pending.Remove(101);
        records[101] = hit.Info;
        records[202] = null;
        Check(!Invoke<bool>(game, "TryBeginCreatureQuery", 101u) &&
              !Invoke<bool>(game, "TryBeginCreatureQuery", 202u) &&
              Invoke<bool>(game, "TryBeginCreatureQuery", 303u),
            "B6 creature-query hit/negative cache did not suppress repeat asks by entry");

        Check(ObjectFields.UNIT_FIELD_PETNUMBER == 139,
            "B6 UNIT_FIELD_PETNUMBER build-5875 index drift");
        ObjectFields absentPet = CreateObjectFields([]);
        ObjectFields zeroPet = CreateObjectFields([(ObjectFields.UNIT_FIELD_PETNUMBER, 0u)]);
        ObjectFields pet = CreateObjectFields([(ObjectFields.UNIT_FIELD_PETNUMBER, 42u)]);
        ObjectFields flags = CreateObjectFields(
            [(ObjectFields.UNIT_FLAGS, 0x0400_1000u)]);
        Check(absentPet.PetNumber == 0 && !absentPet.IsPetOrCharm &&
              zeroPet.PetNumber == 0 && !zeroPet.IsPetOrCharm &&
              pet.PetNumber == 42 && pet.IsPetOrCharm &&
              (flags.UnitFlags & 0x0000_1000u) != 0 &&
              (flags.UnitFlags & 0x0400_0000u) != 0,
            "B6 pet-number accessor or raw PvP/skinnable unit-flag reads drift");
    }

    private static void CheckB6WorldUnitSemantics()
    {
        Check(InvokeStatic<string>("WorldUnitGameTooltipLiveToken", 0x42UL) ==
                  "world-unit:0000000000000042" &&
              InvokeStatic<string>("WorldUnitGameTooltipLiveToken", ulong.MaxValue) ==
                  "world-unit:FFFFFFFFFFFFFFFF",
            "B6 world-unit live token is not stable and GUID-specific");

        string[] creatureTypes =
        [
            "Beast", "Dragonkin", "Demon", "Elemental", "Giant", "Undead",
            "Humanoid", "Critter", "Mechanical",
        ];
        Check(creatureTypes.Select((word, index) =>
                  InvokeStatic<string>("WorldUnitCreatureTypeWord", (uint)(index + 1)) == word)
                  .All(value => value) &&
              InvokeStaticNullable<string>("WorldUnitCreatureTypeWord", 10u) is null &&
              InvokeStaticNullable<string>("WorldUnitCreatureTypeWord", 0u) is null,
            "B6 creature-type word table stopped matching ids 1..9/unspecified fallback");

        Check(InvokeStatic<int>("WorldUnitGameTooltipReaction",
                  FactionReaction.Hostile, false) == 2 &&
              InvokeStatic<int>("WorldUnitGameTooltipReaction",
                  FactionReaction.Neutral, false) == 4 &&
              InvokeStatic<int>("WorldUnitGameTooltipReaction",
                  FactionReaction.Friendly, false) == 5 &&
              InvokeStatic<int>("WorldUnitGameTooltipReaction",
                  FactionReaction.Hostile, true) == 5,
            "B6 target reaction did not map hostile/neutral/friendly/player fallback to 2/4/5/5");

        GameTooltipUnitSnapshot first = Unit(token: "world-unit:1", name: "Gnoll",
            subtitle: "the Clinical", level: 12, playerLevel: 10, reaction: 2,
            creatureType: "Humanoid", rank: 1, pvp: true, skinnable: true,
            civilian: true, leader: true, health: 50, maxHealth: 100);
        GameTooltipUnitSnapshot healthOnly = first with
        {
            Token = "world-unit:2",
            Exists = false,
            Health = 1,
            MaxHealth = 2,
        };
        GameTooltipUnitSnapshot staticChange = first with { Rank = 3 };
        object firstSignature = InvokeStatic<object>(
            "WorldUnitGameTooltipStaticSignature", first);
        object healthSignature = InvokeStatic<object>(
            "WorldUnitGameTooltipStaticSignature", healthOnly);
        object changedSignature = InvokeStatic<object>(
            "WorldUnitGameTooltipStaticSignature", staticChange);
        Check(firstSignature.Equals(healthSignature) &&
              !firstSignature.Equals(changedSignature),
            "B6 static signature included token/existence/health or omitted a rendered rank field");

        object fadeGame = RuntimeHelpers.GetUninitializedObject(typeof(GameLoop));
        GameTooltipOwnerToken fadeToken = Invoke<GameTooltipOwnerToken>(fadeGame,
            "ClaimSharedGameTooltip", new GameTooltipOwnerKey("world-unit", 1));
        GameTooltipContent retainedContent = GameTooltipUiLaw.UnitContent(first) ??
            throw new InvalidDataException("B6 departure fixture did not build unit content");
        Check(Invoke<bool>(fadeGame, "PublishSharedGameTooltip", fadeToken,
                  retainedContent, (Vector2?)null) &&
              Invoke<bool>(fadeGame, "BeginSharedGameTooltipFade", fadeToken, 10d,
                  GameTooltipUiLaw.WorldFadeSeconds),
            "B6 departure fixture could not publish/arm its retained unit state");
        object beforeFade = Invoke<object>(fadeGame, "SharedGameTooltipSnapshot");
        InvokeVoid(fadeGame, "TickSharedGameTooltip", 10.25d);
        object duringFade = Invoke<object>(fadeGame, "SharedGameTooltipSnapshot");
        Check(Property<GameTooltipHealthState>(beforeFade, "Health") ==
                  new GameTooltipHealthState(true, 100, 50) &&
              Property<GameTooltipHealthState>(duringFade, "Health") ==
                  Property<GameTooltipHealthState>(beforeFade, "Health") &&
              Property<GameTooltipLine[]>(duringFade, "Lines").SequenceEqual(
                  Property<GameTooltipLine[]>(beforeFade, "Lines")) &&
              Near(Property<GameTooltipLifecycleState>(duringFade, "Lifecycle").Alpha, .5f),
            "B6 lifecycle fade mutated/hid the retained departure lines or health bar");

        var normal = new UiParentManagedState(true, true, false, false, false, false, false);
        var pet = normal with { PetOrStanceShown = true };
        var rightRight = normal with { RightRightShown = true };
        var bothRight = normal with { RightLeftShown = true, RightRightShown = true };
        Vector2 display = new(1920, 1080);
        Vector2 size = new(200, 100);
        Check(InvokeStatic<Vector2>("SharedGameTooltipDefaultAnchor",
                  display, size, 2f, normal) == new Vector2(1694, 786) &&
              InvokeStatic<Vector2>("SharedGameTooltipDefaultAnchor",
                  display, size, 2f, pet) == new Vector2(1694, 740) &&
              InvokeStatic<Vector2>("SharedGameTooltipDefaultAnchor",
                  display, size, 2f, rightRight) == new Vector2(1604, 786) &&
              InvokeStatic<Vector2>("SharedGameTooltipDefaultAnchor",
                  display, size, 2f, bothRight) == new Vector2(1514, 786),
            "B6 managed default anchor lost bottom/pet/right-right/right-left geometry");
        Check(InvokeStatic<Vector2>("SharedGameTooltipClampToScreen",
                  new Vector2(-30, -40), new Vector2(20, 10),
                  new Vector2(100, 50)) == Vector2.Zero &&
              InvokeStatic<Vector2>("SharedGameTooltipClampToScreen",
                  Vector2.Zero, new Vector2(200, 100),
                  new Vector2(100, 50)) == new Vector2(-100, 0) &&
              InvokeStatic<Vector2>("SharedGameTooltipClampToScreen",
                  new Vector2(950, 740), new Vector2(100, 50),
                  new Vector2(1024, 768)) == new Vector2(924, 718) &&
              InvokeStatic<Vector2>("SharedGameTooltipClampToScreen",
                  new Vector2(25, 35), new Vector2(100, 50),
                  new Vector2(1024, 768)) == new Vector2(25, 35),
            "B6 ordered-edge clamped-to-screen law lost negative/oversize/in-bounds geometry");

        (Vector4 fillTint, Vector4 edgeTint) =
            InvokeStatic<(Vector4 Fill, Vector4 Edge)>(
                "SharedGameTooltipBackdropTints", .5f);
        Check(fillTint == new Vector4(.09f, .09f, .19f, .5f) &&
              edgeTint == new Vector4(1f, 1f, 1f, .5f),
            "B6 tooltip backdrop did not retain its dark-navy fill/white-edge fade tints");
        object thicken = InvokeStatic<object>("SharedGameTooltipThicken",
            new Vector2(10, 20), new Vector2(200, 100), 2f, .5f);
        Check(Property<Vector2>(thicken, "Minimum") == new Vector2(20, 30) &&
              Property<Vector2>(thicken, "Maximum") == new Vector2(200, 110) &&
              Property<Vector4>(thicken, "Tint") ==
                  new Vector4(.09f, .09f, .19f, .2f),
            "B6 $parentThicken lost its five-unit inset/navy/0.4-alpha law");

        Check(InvokeStatic<Vector4>("SharedGameTooltipReactionColor", 2) ==
                  new Vector4(.8f, .3f, .22f, 1f) &&
              InvokeStatic<Vector4>("SharedGameTooltipReactionColor", 4) ==
                  new Vector4(.9f, .7f, 0f, 1f) &&
              InvokeStatic<Vector4>("SharedGameTooltipReactionColor", 5) ==
                  new Vector4(0f, .6f, .1f, 1f) &&
              InvokeStatic<Vector4>("SharedGameTooltipReactionColor", 99) == Vector4.One &&
              InvokeStatic<Vector4>("SharedGameTooltipToneColor",
                  GameTooltipTextTone.Red, (int?)null) ==
                  new Vector4(1f, 32f / 255f, 32f / 255f, 1f) &&
              InvokeStatic<Vector4>("SharedGameTooltipToneColor",
                  GameTooltipTextTone.Green, (int?)null) ==
                  new Vector4(0f, 1f, 0f, 1f),
            "B6 world-unit reaction/red/green row tint mapping drift");
    }

    private static PreparedItemPaint[] PreparedItemPaints(object snapshot)
    {
        IEnumerable operations = Property<IEnumerable>(snapshot, "Operations");
        return operations.Cast<object>().Select(operation => new PreparedItemPaint(
            Property<object>(operation, "Kind").ToString() ?? "",
            Property<string>(operation, "Text"),
            Property<Vector4>(operation, "Color"))).ToArray();
    }

    private static void CheckFrameRendererCoordinator()
    {
        object immediatePolicy = ImmediateLeavePolicy();
        object fadePolicy = FadeLeavePolicy(.5d);

        object fadingGame = RuntimeHelpers.GetUninitializedObject(typeof(GameLoop));
        GameTooltipOwnerToken fadingToken = Invoke<GameTooltipOwnerToken>(fadingGame,
            "ClaimSharedGameTooltip", new GameTooltipOwnerKey("world-unit", 0x42));
        Check(Invoke<bool>(fadingGame, "PublishSharedGameTooltip", fadingToken,
                  new GameTooltipContent(GameTooltipAnchorKind.DefaultBottomRight,
                      [new("Fading unit", GameTooltipTextTone.White)]),
                  (Vector2?)null) &&
              Invoke<bool>(fadingGame, "BeginSharedGameTooltipFade", fadingToken, 20d, .5d),
            "GameTooltip frame fade-tick fixture could not arm");
        InvokeVoid(fadingGame, "BeginSharedGameTooltipFrame", 20.25d);
        object halfFade = Invoke<object>(fadingGame, "SharedGameTooltipSnapshot");
        Check(Near(Property<GameTooltipLifecycleState>(halfFade, "Lifecycle").Alpha, .5f),
            "GameTooltip frame begin did not advance the active fade");
        InvokeVoid(fadingGame, "EndSharedGameTooltipFrame");
        InvokeVoid(fadingGame, "BeginSharedGameTooltipFrame", 20.5d);
        object finishedFade = Invoke<object>(fadingGame, "SharedGameTooltipSnapshot");
        GameTooltipLifecycleState finishedLifecycle =
            Property<GameTooltipLifecycleState>(finishedFade, "Lifecycle");
        Check(finishedLifecycle.Owner is null && !finishedLifecycle.Visible &&
              Property<GameTooltipLine[]>(finishedFade, "Lines").Length == 0,
            "GameTooltip frame begin did not finish/clear the terminal fade");
        InvokeVoid(fadingGame, "EndSharedGameTooltipFrame");

        object immediateEndGame = RuntimeHelpers.GetUninitializedObject(typeof(GameLoop));
        GameTooltipOwnerToken immediateToken = Invoke<GameTooltipOwnerToken>(immediateEndGame,
            "ClaimSharedGameTooltip", new GameTooltipOwnerKey("inventory-slot", 3));
        Check(Invoke<bool>(immediateEndGame, "PublishSharedGameTooltip", immediateToken,
                  new GameTooltipContent(GameTooltipAnchorKind.OwnerRight,
                      [new("Immediate UI owner", GameTooltipTextTone.White)]),
                  (Vector2?)null),
            "GameTooltip immediate-departure fixture could not publish");
        int immediateCalls = 0;
        InvokeVoid(immediateEndGame, "BeginSharedGameTooltipFrame", 30d);
        Check(Invoke<bool>(immediateEndGame, "QueueSharedGameTooltipRenderer", immediateToken,
                  immediatePolicy, (Action)(() => immediateCalls++)) &&
              Invoke<bool>(immediateEndGame, "ResolveAndDrawSharedGameTooltip"),
            "GameTooltip immediate-departure fixture could not retain its explicit lease");
        InvokeVoid(immediateEndGame, "EndSharedGameTooltipFrame");
        InvokeVoid(immediateEndGame, "BeginSharedGameTooltipFrame", 31d);
        InvokeVoid(immediateEndGame, "EndSharedGameTooltipFrame");
        GameTooltipLifecycleState immediateDeparted = Property<GameTooltipLifecycleState>(
            Invoke<object>(immediateEndGame, "SharedGameTooltipSnapshot"), "Lifecycle");
        Check(immediateCalls == 1 && immediateDeparted.Owner is null &&
              !immediateDeparted.Visible,
            "GameTooltip unseen immediate owner survived End or invoked a stale renderer");

        object fadeEndGame = RuntimeHelpers.GetUninitializedObject(typeof(GameLoop));
        GameTooltipOwnerToken fadeToken = Invoke<GameTooltipOwnerToken>(fadeEndGame,
            "ClaimSharedGameTooltip", new GameTooltipOwnerKey("party-member", 2));
        Check(Invoke<bool>(fadeEndGame, "PublishSharedGameTooltip", fadeToken,
                  new GameTooltipContent(GameTooltipAnchorKind.OwnerRight,
                      [new("Fading retained owner", GameTooltipTextTone.White)]),
                  (Vector2?)null),
            "GameTooltip retained-fade fixture could not publish");
        int fadeCalls = 0;
        InvokeVoid(fadeEndGame, "BeginSharedGameTooltipFrame", 40d);
        Check(Invoke<bool>(fadeEndGame, "QueueSharedGameTooltipRenderer", fadeToken,
                  fadePolicy, (Action)(() => fadeCalls++)) &&
              Invoke<bool>(fadeEndGame, "ResolveAndDrawSharedGameTooltip"),
            "GameTooltip retained-fade fixture could not retain its explicit lease");
        InvokeVoid(fadeEndGame, "EndSharedGameTooltipFrame");
        InvokeVoid(fadeEndGame, "BeginSharedGameTooltipFrame", 41d);
        InvokeVoid(fadeEndGame, "EndSharedGameTooltipFrame");
        GameTooltipLifecycleState armedByEnd = Property<GameTooltipLifecycleState>(
            Invoke<object>(fadeEndGame, "SharedGameTooltipSnapshot"), "Lifecycle");
        Check(fadeCalls == 1 && armedByEnd.Owner == fadeToken.Owner &&
              armedByEnd.FadeStartedAt == 41d && armedByEnd.FadeSeconds == .5d &&
              armedByEnd.Visible && armedByEnd.Alpha == 1f,
            "GameTooltip unseen fade owner was not armed by renderer-free End");
        InvokeVoid(fadeEndGame, "BeginSharedGameTooltipFrame", 41.25d);
        Check(!Invoke<bool>(fadeEndGame, "ResolveAndDrawSharedGameTooltip"),
            "GameTooltip unseen retained fade unexpectedly resolved a renderer");
        GameTooltipLifecycleState halfRetainedFade = Property<GameTooltipLifecycleState>(
            Invoke<object>(fadeEndGame, "SharedGameTooltipSnapshot"), "Lifecycle");
        Check(halfRetainedFade.FadeStartedAt == 41d && Near(halfRetainedFade.Alpha, .5f),
            "GameTooltip repeated unseen departure restarted its retained fade");
        InvokeVoid(fadeEndGame, "EndSharedGameTooltipFrame");
        InvokeVoid(fadeEndGame, "BeginSharedGameTooltipFrame", 41.5d);
        InvokeVoid(fadeEndGame, "EndSharedGameTooltipFrame");
        Check(Property<GameTooltipLifecycleState>(
                  Invoke<object>(fadeEndGame, "SharedGameTooltipSnapshot"), "Lifecycle").Owner
                  is null && fadeCalls == 1,
            "GameTooltip retained fade did not terminally hide without repainting");

        object game = RuntimeHelpers.GetUninitializedObject(typeof(GameLoop));
        var ownerA = new GameTooltipOwnerKey("inventory-slot", 7);
        GameTooltipOwnerToken tokenA = Invoke<GameTooltipOwnerToken>(game,
            "ClaimSharedGameTooltip", ownerA);
        Check(Invoke<bool>(game, "PublishSharedGameTooltip", tokenA,
                  new GameTooltipContent(GameTooltipAnchorKind.OwnerRight,
                      [new("Existing renderer", GameTooltipTextTone.White)]),
                  (Vector2?)null),
            "GameTooltip frame coordinator fixture could not publish its exact owner");

        int replacedCalls = 0;
        int winningCalls = 0;
        Action replaced = () => replacedCalls++;
        Action winner = () => winningCalls++;
        Check(!Invoke<bool>(game, "QueueSharedGameTooltipRenderer", tokenA,
                  immediatePolicy, replaced),
            "GameTooltip accepted a renderer outside its frame window");

        InvokeVoid(game, "BeginSharedGameTooltipFrame", 0d);
        Check(Invoke<bool>(game, "QueueSharedGameTooltipRenderer", tokenA,
                  immediatePolicy, replaced) &&
              Invoke<bool>(game, "QueueSharedGameTooltipRenderer", tokenA,
                  immediatePolicy, winner),
            "GameTooltip exact-owner renderer could not occupy/replace the pending slot");
        object before = Invoke<object>(game, "SharedGameTooltipSnapshot");
        Check(Invoke<bool>(game, "ResolveAndDrawSharedGameTooltip") &&
              replacedCalls == 0 && winningCalls == 1,
            "GameTooltip frame did not invoke exactly the winning renderer once");
        object after = Invoke<object>(game, "SharedGameTooltipSnapshot");
        Check(SameSnapshot(before, after),
            "GameTooltip renderer resolution mutated semantic tooltip state");
        Check(!Invoke<bool>(game, "ResolveAndDrawSharedGameTooltip") &&
              !Invoke<bool>(game, "QueueSharedGameTooltipRenderer", tokenA,
                  immediatePolicy, replaced) &&
              winningCalls == 1,
            "GameTooltip frame resolved or accepted a callback more than once");
        InvokeVoid(game, "EndSharedGameTooltipFrame");

        int carriedCalls = 0;
        Action carried = () => carriedCalls++;
        InvokeVoid(game, "BeginSharedGameTooltipFrame", 0d);
        Check(Invoke<bool>(game, "QueueSharedGameTooltipRenderer", tokenA,
                  immediatePolicy, carried),
            "GameTooltip frame could not arm its carry-over fixture");
        InvokeVoid(game, "EndSharedGameTooltipFrame");
        InvokeVoid(game, "BeginSharedGameTooltipFrame", 0d);
        Check(!Invoke<bool>(game, "ResolveAndDrawSharedGameTooltip") && carriedCalls == 0,
            "GameTooltip renderer callback escaped the frame that submitted it");
        InvokeVoid(game, "EndSharedGameTooltipFrame");
        Check(Property<GameTooltipLifecycleState>(
                  Invoke<object>(game, "SharedGameTooltipSnapshot"), "Lifecycle").Owner is null,
            "GameTooltip unseen ordinary UI owner did not apply ImmediateHide at resolve");
        tokenA = Invoke<GameTooltipOwnerToken>(game, "ClaimSharedGameTooltip", ownerA);
        Check(Invoke<bool>(game, "PublishSharedGameTooltip", tokenA,
                  new GameTooltipContent(GameTooltipAnchorKind.OwnerRight,
                      [new("Replacement fixture", GameTooltipTextTone.White)]),
                  (Vector2?)null),
            "GameTooltip stale-owner fixture could not republish its opening owner");

        int staleCalls = 0;
        Action stale = () => staleCalls++;
        InvokeVoid(game, "BeginSharedGameTooltipFrame", 0d);
        Check(Invoke<bool>(game, "QueueSharedGameTooltipRenderer", tokenA,
                  immediatePolicy, stale),
            "GameTooltip frame could not arm its stale-owner fixture");
        GameTooltipOwnerToken tokenB = Invoke<GameTooltipOwnerToken>(game,
            "ClaimSharedGameTooltip", new GameTooltipOwnerKey("party-member", 2));
        Check(!Invoke<bool>(game, "ResolveAndDrawSharedGameTooltip") && staleCalls == 0,
            "GameTooltip stale owner rendered after an ownership-generation replacement");
        InvokeVoid(game, "EndSharedGameTooltipFrame");
        Check(Property<GameTooltipLifecycleState>(
                  Invoke<object>(game, "SharedGameTooltipSnapshot"), "Lifecycle").Owner ==
                  tokenB.Owner,
            "GameTooltip stale opening lease hid its replacement owner");

        int liveCalls = 0;
        Action live = () => liveCalls++;
        InvokeVoid(game, "BeginSharedGameTooltipFrame", 0d);
        Check(!Invoke<bool>(game, "QueueSharedGameTooltipRenderer", tokenA,
                  immediatePolicy, stale) &&
              Invoke<bool>(game, "QueueSharedGameTooltipRenderer", tokenB,
                  immediatePolicy, live) &&
              Invoke<bool>(game, "ResolveAndDrawSharedGameTooltip") &&
              staleCalls == 0 && liveCalls == 1,
            "GameTooltip enqueue/resolve did not enforce the exact live generation");
        InvokeVoid(game, "EndSharedGameTooltipFrame");
    }

    private static void CheckPartyProducerLifecycle()
    {
        object game = RuntimeHelpers.GetUninitializedObject(typeof(GameLoop));
        GameTooltipOwnerKey owner = PartyFrameUiLaw.TooltipOwner(0);
        GameTooltipOwnerToken token = Invoke<GameTooltipOwnerToken>(game,
            "ClaimSharedGameTooltip", owner);
        var firstView = new PartyTooltipView("Snapshot Alice",
            "Level 60 Human Warrior (Player)", "PvP", 80, 100);
        GameTooltipContent firstContent = PartyFrameUiLaw.SharedTooltipContent(0, firstView);
        Check(Invoke<bool>(game, "PublishSharedGameTooltip", token, firstContent,
                  (Vector2?)null),
            "party GameTooltip producer fixture could not publish its fixed-slot SetUnit snapshot");
        object first = Invoke<object>(game, "SharedGameTooltipSnapshot");
        GameTooltipLine[] retainedLines = Property<GameTooltipLine[]>(first, "Lines");
        Check(Property<GameTooltipLifecycleState>(first, "Lifecycle").Owner ==
                  new GameTooltipOwnerKey("party-member", 1) &&
              Property<string?>(first, "LiveUnitToken") == "party1" &&
              retainedLines.SequenceEqual(firstContent.Lines) &&
              Property<GameTooltipHealthState>(first, "Health") ==
                  new GameTooltipHealthState(true, 100, 80),
            "party GameTooltip fixed frame owner/partyN token/static snapshot drift");

        Check(Invoke<bool>(game, "TryRefreshSharedGameTooltipUnit", token,
                  PartyFrameUiLaw.TooltipHealthPush(0, tokenExists: true, 15, 60)),
            "party GameTooltip rejected same fixed-slot occupant live-health rebind");
        object rebound = Invoke<object>(game, "SharedGameTooltipSnapshot");
        Check(Property<GameTooltipLine[]>(rebound, "Lines").SequenceEqual(retainedLines) &&
              Property<GameTooltipHealthState>(rebound, "Health") ==
                  new GameTooltipHealthState(true, 60, 15) &&
              Property<GameTooltipLifecycleState>(rebound, "Lifecycle").Generation ==
                  token.Generation,
            "party same-slot occupant rebind rebuilt rows or changed owner generation");
        object beforeMismatch = Invoke<object>(game, "SharedGameTooltipSnapshot");
        Check(!Invoke<bool>(game, "TryRefreshSharedGameTooltipUnit", token,
                  PartyFrameUiLaw.TooltipHealthPush(1, tokenExists: true, 50, 100)) &&
              SameSnapshot(beforeMismatch,
                  Invoke<object>(game, "SharedGameTooltipSnapshot")),
            "party mismatched partyN health push mutated the retained fixed-slot tooltip");

        Check(Invoke<bool>(game, "BeginSharedGameTooltipFade", token, 10d,
                  GameTooltipUiLaw.WorldFadeSeconds),
            "party GameTooltip OnLeave could not arm the shared half-second fade");
        InvokeVoid(game, "BeginSharedGameTooltipFrame", 10.25d);
        object halfFade = Invoke<object>(game, "SharedGameTooltipSnapshot");
        int fadeRenderCalls = 0;
        Check(Near(Property<GameTooltipLifecycleState>(halfFade, "Lifecycle").Alpha, .5f) &&
              Property<GameTooltipLine[]>(halfFade, "Lines").SequenceEqual(retainedLines) &&
              Invoke<bool>(game, "QueueSharedGameTooltipRenderer", token,
                  FadeLeavePolicy(GameTooltipUiLaw.WorldFadeSeconds),
                  (Action)(() => fadeRenderCalls++)) &&
              Invoke<bool>(game, "ResolveAndDrawSharedGameTooltip") &&
              fadeRenderCalls == 1,
            "party shared fade lost retained rows/alpha or exact deferred renderer lease");
        InvokeVoid(game, "EndSharedGameTooltipFrame");

        GameTooltipOwnerToken reclaimed = Invoke<GameTooltipOwnerToken>(game,
            "ClaimSharedGameTooltip", owner);
        var reenteredView = new PartyTooltipView("Reentered Bob",
            "Level 45 Night Elf Rogue (Player)", null, 33, 50);
        Check(reclaimed == token &&
              Invoke<bool>(game, "PublishSharedGameTooltip", reclaimed,
                  PartyFrameUiLaw.SharedTooltipContent(0, reenteredView), (Vector2?)null),
            "party same-slot re-enter failed to reclaim and replace its fading snapshot");
        object reentered = Invoke<object>(game, "SharedGameTooltipSnapshot");
        GameTooltipLifecycleState reenteredLifecycle =
            Property<GameTooltipLifecycleState>(reentered, "Lifecycle");
        Check(reenteredLifecycle.FadeStartedAt is null && reenteredLifecycle.Alpha == 1f &&
              reenteredLifecycle.Generation == token.Generation &&
              Property<GameTooltipLine[]>(reentered, "Lines")[0].Text == "Reentered Bob" &&
              Property<GameTooltipHealthState>(reentered, "Health") ==
                  new GameTooltipHealthState(true, 50, 33),
            "party same-owner fade recovery did not publish a fresh full-alpha snapshot");

        GameTooltipLine[] disconnectRows = Property<GameTooltipLine[]>(reentered, "Lines");
        Check(Invoke<bool>(game, "TryRefreshSharedGameTooltipUnit", reclaimed,
                  PartyFrameUiLaw.TooltipHealthPush(0, tokenExists: false, 0, 0)) &&
              Invoke<bool>(game, "BeginSharedGameTooltipFade", reclaimed, 20d,
                  GameTooltipUiLaw.WorldFadeSeconds),
            "party disconnect/reset edge could not hide health and begin retained departure");
        object disconnect = Invoke<object>(game, "SharedGameTooltipSnapshot");
        Check(Property<GameTooltipLine[]>(disconnect, "Lines").SequenceEqual(disconnectRows) &&
              Property<GameTooltipHealthState>(disconnect, "Health") ==
                  GameTooltipHealthState.Hidden &&
              Property<GameTooltipLifecycleState>(disconnect, "Lifecycle").FadeStartedAt == 20d,
            "party disconnect/reset cleared retained rows or left an absent health bar visible");
        InvokeVoid(game, "BeginSharedGameTooltipFrame", 20.5d);
        object terminal = Invoke<object>(game, "SharedGameTooltipSnapshot");
        Check(Property<GameTooltipLifecycleState>(terminal, "Lifecycle").Owner is null &&
              Property<GameTooltipLine[]>(terminal, "Lines").Length == 0,
            "party disconnect/reset retained tooltip did not clear at terminal fade");
        InvokeVoid(game, "EndSharedGameTooltipFrame");

        GameTooltipOwnerToken renewedParty = Invoke<GameTooltipOwnerToken>(game,
            "ClaimSharedGameTooltip", owner);
        Check(Invoke<bool>(game, "PublishSharedGameTooltip", renewedParty, firstContent,
                  (Vector2?)null),
            "party replacement fixture could not republish its renewed owner generation");
        int staleRenderCalls = 0;
        InvokeVoid(game, "BeginSharedGameTooltipFrame", 30d);
        Check(Invoke<bool>(game, "QueueSharedGameTooltipRenderer", renewedParty,
                  FadeLeavePolicy(GameTooltipUiLaw.WorldFadeSeconds),
                  (Action)(() => staleRenderCalls++)),
            "party replacement fixture could not arm its deferred renderer");
        GameTooltipOwnerToken replacement = Invoke<GameTooltipOwnerToken>(game,
            "ClaimSharedGameTooltip", new GameTooltipOwnerKey("inventory-slot", 9));
        var replacementContent = new GameTooltipContent(GameTooltipAnchorKind.OwnerRight,
            [new("Replacement owner", GameTooltipTextTone.Gold)]);
        Check(Invoke<bool>(game, "PublishSharedGameTooltip", replacement, replacementContent,
                  (Vector2?)null) &&
              !Invoke<bool>(game, "TryRefreshSharedGameTooltipUnit", renewedParty,
                  PartyFrameUiLaw.TooltipHealthPush(0, tokenExists: true, 1, 1)) &&
              !Invoke<bool>(game, "BeginSharedGameTooltipFade", renewedParty, 30d,
                  GameTooltipUiLaw.WorldFadeSeconds) &&
              !Invoke<bool>(game, "QueueSharedGameTooltipRenderer", renewedParty,
                  FadeLeavePolicy(GameTooltipUiLaw.WorldFadeSeconds), (Action)(() => { })) &&
              !Invoke<bool>(game, "ResolveAndDrawSharedGameTooltip") &&
              staleRenderCalls == 0,
            "stale party generation pushed, faded, queued, or painted after owner replacement");
        InvokeVoid(game, "EndSharedGameTooltipFrame");
        object replaced = Invoke<object>(game, "SharedGameTooltipSnapshot");
        Check(Property<GameTooltipLifecycleState>(replaced, "Lifecycle").Owner ==
                  replacement.Owner &&
              Property<GameTooltipLine[]>(replaced, "Lines").SequenceEqual(
                  replacementContent.Lines),
            "stale party departure damaged the replacement GameTooltip owner");
    }

    private static void CheckFrameCoordinatorSourceFence()
    {
        string root = ClientConfig.FindRepoRoot();
        string client = Path.Combine(root, "MSUIClient");
        string combat = SourceText.Read(Path.Combine(client, "Program.CombatFeedback.cs"));
        string coordinator = SourceText.Read(Path.Combine(client, "Program.GameTooltip.cs"));
        string party = SourceText.Read(Path.Combine(client, "Program.PartyFrames.cs"));
        string minimap = SourceText.Read(Path.Combine(client, "Program.Minimap.cs"));
        string worldUnit = SourceText.Read(
            Path.Combine(client, "Program.GameTooltip.WorldUnit.cs"));
        string merchant = SourceText.Read(
            Path.Combine(client, "Program.Vendor.Render.cs"));
        string taxi = SourceText.Read(Path.Combine(client, "Program.Taxi.cs"));

        int drawStart = combat.IndexOf("private void DrawCombatHud()", StringComparison.Ordinal);
        int drawEnd = combat.IndexOf("private void DrawPlayerFrame()", drawStart,
            StringComparison.Ordinal);
        Check(drawStart >= 0 && drawEnd > drawStart,
            "GameTooltip DrawCombatHud coordinator source fence is missing");
        string draw = combat[drawStart..drawEnd];
        int begin = draw.IndexOf("BeginSharedGameTooltipFrame(NowSeconds());",
            StringComparison.Ordinal);
        int tryStart = draw.IndexOf("try", StringComparison.Ordinal);
        int bake = draw.IndexOf("BakeDirtyPortraits();", StringComparison.Ordinal);
        int worldMap = draw.IndexOf("if (_worldMapOpen)", StringComparison.Ordinal);
        int worldMapReturn = draw.IndexOf("return;", worldMap, StringComparison.Ordinal);
        int multiBars = draw.IndexOf("DrawMultiActionBars();", StringComparison.Ordinal);
        int resolve = draw.IndexOf("ResolveAndDrawSharedGameTooltip();",
            StringComparison.Ordinal);
        int complete = draw.IndexOf("CompleteDeferredPartyTooltipParityCapture();",
            resolve, StringComparison.Ordinal);
        int invite = draw.IndexOf("DrawPartyInvite();", StringComparison.Ordinal);
        int finallyStart = draw.IndexOf("finally", StringComparison.Ordinal);
        int end = draw.IndexOf("EndSharedGameTooltipFrame();", StringComparison.Ordinal);
        int fallbackComplete = draw.IndexOf("CompleteDeferredPartyTooltipParityCapture();",
            end, StringComparison.Ordinal);
        Check(begin >= 0 && begin < tryStart && tryStart < bake && bake < worldMap &&
              worldMap < worldMapReturn && worldMapReturn < multiBars &&
              multiBars < resolve && resolve < complete && complete < invite &&
              invite < finallyStart && finallyStart < end && end < fallbackComplete &&
              Count(draw, "BeginSharedGameTooltipFrame(NowSeconds());") == 1 &&
              Count(draw, "ResolveAndDrawSharedGameTooltip();") == 1 &&
              Count(draw, "EndSharedGameTooltipFrame();") == 1 &&
              Count(draw, "CompleteDeferredPartyTooltipParityCapture();") == 2,
            "GameTooltip frame begin/map-return/tooltip-stratum/Party completion order drift");

        int beginStart = coordinator.IndexOf(
            "private void BeginSharedGameTooltipFrame(double now)",
            StringComparison.Ordinal);
        int queueStart = coordinator.IndexOf(
            "private bool QueueSharedGameTooltipRenderer(", StringComparison.Ordinal);
        int departureStart = coordinator.IndexOf(
            "private bool ApplyRetainedDeparture()", StringComparison.Ordinal);
        int resolveStart = coordinator.IndexOf(
            "private bool ResolveAndDrawSharedGameTooltip()", StringComparison.Ordinal);
        int endStart = coordinator.IndexOf(
            "private void EndSharedGameTooltipFrame()", StringComparison.Ordinal);
        int claimStart = coordinator.IndexOf(
            "private GameTooltipOwnerToken ClaimSharedGameTooltip(", StringComparison.Ordinal);
        Check(beginStart >= 0 && queueStart > beginStart && departureStart > queueStart &&
              resolveStart > departureStart && endStart > resolveStart && claimStart > endStart,
            "GameTooltip renderer callback coordinator seam is missing");
        string frameBegin = coordinator[beginStart..queueStart];
        string queue = coordinator[queueStart..departureStart];
        string departure = coordinator[departureStart..resolveStart];
        string resolver = coordinator[resolveStart..endStart];
        string frameEnd = coordinator[endStart..claimStart];
        int clearPending = resolver.IndexOf("_pendingSharedTooltipRenderer = null;",
            StringComparison.Ordinal);
        int exactOwner = resolver.IndexOf("!SharedGameTooltipIsOwned(pending.Token)",
            StringComparison.Ordinal);
        int renderer = resolver.IndexOf("pending.Renderer();", StringComparison.Ordinal);
        int tickFade = frameBegin.IndexOf("TickSharedGameTooltip(now);",
            StringComparison.Ordinal);
        int clearFrameCallback = frameBegin.IndexOf("_pendingSharedTooltipRenderer = null;",
            StringComparison.Ordinal);
        Check(tickFade >= 0 && clearFrameCallback > tickFade &&
              frameBegin.Contains("_sharedTooltipOpeningOwnerToken = " +
                  "CurrentSharedGameTooltipOwnerToken();", StringComparison.Ordinal) &&
              frameBegin.Contains("_sharedTooltipFrameTime = now;", StringComparison.Ordinal) &&
              frameBegin.Contains("_sharedTooltipOpeningOwnerSeen = false;",
                  StringComparison.Ordinal) &&
              queue.Contains("!SharedGameTooltipIsOwned(token)", StringComparison.Ordinal) &&
              queue.Contains("_sharedTooltipFrameResolved", StringComparison.Ordinal) &&
              queue.Contains("_sharedTooltipRetainedPolicyToken = token;",
                  StringComparison.Ordinal) &&
              queue.Contains("_sharedTooltipRetainedLeavePolicy = leavePolicy;",
                  StringComparison.Ordinal) &&
              queue.Contains("token == _sharedTooltipOpeningOwnerToken",
                  StringComparison.Ordinal) &&
              queue.Contains("_sharedTooltipOpeningOwnerSeen = true;",
                  StringComparison.Ordinal) &&
              departure.Contains("opening != _sharedTooltipRetainedPolicyToken",
                  StringComparison.Ordinal) &&
              departure.Contains("!SharedGameTooltipIsOwned(opening)",
                  StringComparison.Ordinal) &&
              departure.Contains("SharedGameTooltipLeaveMode.ImmediateHide",
                  StringComparison.Ordinal) &&
              departure.Contains("SharedGameTooltipLeaveMode.Fade", StringComparison.Ordinal) &&
              !departure.Contains(".Surface", StringComparison.Ordinal) &&
              resolver.Contains("ApplyRetainedDeparture();", StringComparison.Ordinal) &&
              frameEnd.Contains("ApplyRetainedDeparture();", StringComparison.Ordinal) &&
              Count(coordinator, "ApplyRetainedDeparture();") == 2 &&
              clearPending >= 0 && clearPending < exactOwner &&
              exactOwner < renderer &&
              resolver.Contains("_sharedTooltipFrameResolved = true;", StringComparison.Ordinal),
            "GameTooltip pending renderer lost enqueue/resolve exact-generation safety");

        int preservedOfferStart = coordinator.IndexOf(
            "private bool OfferPreservedSharedGameTooltipRenderer(", StringComparison.Ordinal);
        int fadeStart = coordinator.IndexOf("private bool BeginSharedGameTooltipFade(",
            preservedOfferStart, StringComparison.Ordinal);
        Check(preservedOfferStart >= 0 && fadeStart > preservedOfferStart,
            "GameTooltip preserved-renderer adapter source fence is missing");
        string preservedOffer = coordinator[preservedOfferStart..fadeStart];
        int frameGuard = preservedOffer.IndexOf(
            "if (!_sharedTooltipFrameOpen || _sharedTooltipFrameResolved) return false;",
            StringComparison.Ordinal);
        int claim = preservedOffer.IndexOf("ClaimSharedGameTooltip(owner)",
            StringComparison.Ordinal);
        int clear = preservedOffer.IndexOf("ClearSharedGameTooltip(token)",
            StringComparison.Ordinal);
        int offerQueue = preservedOffer.IndexOf("QueueSharedGameTooltipRenderer(token,",
            StringComparison.Ordinal);
        Check(frameGuard >= 0 && frameGuard < claim && claim < clear && clear < offerQueue &&
              preservedOffer.Contains("SharedGameTooltipLeavePolicy.ImmediateHide",
                  StringComparison.Ordinal) &&
              !preservedOffer.Contains("PublishSharedGameTooltip", StringComparison.Ordinal) &&
              !preservedOffer.Contains("BeginSharedGameTooltipFade", StringComparison.Ordinal) &&
              !preservedOffer.Contains("HideSharedGameTooltip", StringComparison.Ordinal),
            "preserved GameTooltip adapter lost guard-before-Claim/full-clear/immediate lease order");

        int producerReferences = Directory.EnumerateFiles(client, "*.cs",
                SearchOption.AllDirectories)
            .Sum(path => Count(SourceText.Read(path), "QueueSharedGameTooltipRenderer"));
        Check(Count(coordinator, "QueueSharedGameTooltipRenderer") == 3 &&
              Count(party, "QueueSharedGameTooltipRenderer") == 1 &&
              Count(minimap, "QueueSharedGameTooltipRenderer") == 1 &&
              Count(worldUnit, "QueueSharedGameTooltipRenderer") == 1 &&
              Count(merchant, "QueueSharedGameTooltipRenderer") == 1 &&
              Count(taxi, "QueueSharedGameTooltipRenderer") == 1 &&
              producerReferences == 9,
            "GameTooltip Queue escaped coordinator/opaque helper/newbie responder, Party, B5 minimap, B6 world-unit/world-object, Merchant repair, or Taxi node lease");
    }

    private static void CheckB2ProducerSourceFence()
    {
        string client = Path.Combine(ClientConfig.FindRepoRoot(), "MSUIClient");
        string spellbook = SourceText.Read(Path.Combine(client, "Program.Spellbook.cs"));
        string actions = SourceText.Read(Path.Combine(client, "Program.ActionBars.cs"));
        string pet = SourceText.Read(Path.Combine(client, "Program.Pet.cs"));
        string adapters = spellbook + actions + pet;

        Check(!adapters.Contains("QueueSharedGameTooltipRenderer", StringComparison.Ordinal) &&
              !adapters.Contains("PublishSharedGameTooltip", StringComparison.Ordinal) &&
              !adapters.Contains("BeginSharedGameTooltipFade", StringComparison.Ordinal) &&
              !adapters.Contains("HideSharedGameTooltip", StringComparison.Ordinal) &&
              // Two existing spell/skill producers plus the rule-owned Spell/Pet type-tab pair.
              Count(spellbook, "OfferPreservedSharedGameTooltipRenderer") == 3 &&
              Count(actions, "OfferPreservedSharedGameTooltipRenderer") == 6 &&
              Count(pet, "OfferPreservedSharedGameTooltipRenderer") == 2,
            "B2 producer escaped the guarded opaque adapter or changed its bounded offer set");

        Check(actions.Contains("else if (itemInfo is not null)", StringComparison.Ordinal) &&
              actions.Contains("PrepareItemTooltipBodySnapshot(itemInfo, 1)",
                  StringComparison.Ordinal) &&
              actions.Contains("OfferPreparedItemTooltip(tooltipOwner, tooltipBody);",
                  StringComparison.Ordinal) &&
              !actions.Contains("DrawItemTooltip(", StringComparison.Ordinal) &&
              !actions.Contains("DrawItemTooltipBody", StringComparison.Ordinal),
            "B3 did not replace the deferred multibar rich-item branch with its immutable body");

        Check(spellbook.Contains("private readonly record struct SpellTooltipRenderSnapshot(",
                  StringComparison.Ordinal) &&
              spellbook.Contains("SpellTooltipView View,", StringComparison.Ordinal) &&
              spellbook.Contains("WowSkin Skin,", StringComparison.Ordinal) &&
              spellbook.Contains("Vector2 DisplaySize,", StringComparison.Ordinal) &&
              spellbook.Contains("Vector2 OwnerMin,", StringComparison.Ordinal) &&
              spellbook.Contains("Vector2 OwnerMax);", StringComparison.Ordinal) &&
              spellbook.Contains("SpellTooltipView view = SpellTooltipLaw.Build(",
                  StringComparison.Ordinal) &&
              !adapters.Contains("_hoveredSpellId", StringComparison.Ordinal) &&
              !adapters.Contains("_hoveredSpellMin", StringComparison.Ordinal) &&
              !adapters.Contains("_hoveredSpellMax", StringComparison.Ordinal) &&
              !adapters.Contains("_hoveredActionSpellId", StringComparison.Ordinal),
            "B2 spell renderer is not driven by an immutable prepared view/geometry snapshot");

        int spellRendererStart = spellbook.IndexOf(
            "private void DrawSpellTooltip(in SpellTooltipRenderSnapshot snapshot)",
            StringComparison.Ordinal);
        int spellRendererEnd = spellbook.IndexOf(
            "private static IEnumerable<string> WrapTooltipText", spellRendererStart,
            StringComparison.Ordinal);
        Check(spellRendererStart >= 0 && spellRendererEnd > spellRendererStart,
            "B2 prepared spell renderer source fence is missing");
        string spellRenderer = spellbook[spellRendererStart..spellRendererEnd];
        Check(!spellRenderer.Contains("_spellCatalog", StringComparison.Ordinal) &&
              !spellRenderer.Contains("_entities", StringComparison.Ordinal) &&
              !spellRenderer.Contains("_net", StringComparison.Ordinal) &&
              !spellRenderer.Contains("ImGui.GetIO().DisplaySize", StringComparison.Ordinal) &&
              spellRenderer.Contains("AddRow(view.Name", StringComparison.Ordinal) &&
              spellRenderer.Contains("view.Rank", StringComparison.Ordinal) &&
              spellRenderer.Contains("GameTooltipHeaderText", StringComparison.Ordinal) &&
              spellRenderer.Contains("GameTooltipText", StringComparison.Ordinal) &&
              spellRenderer.Contains("SpellTooltipLaw.FrameSize", StringComparison.Ordinal) &&
              spellRenderer.Contains("SpellTooltipLaw.DefaultBottomRightOrigin",
                  StringComparison.Ordinal) &&
              spellRenderer.Contains("SpellTooltipLaw.ClampOrigin", StringComparison.Ordinal) &&
              spellRenderer.Contains("SpellTooltipLaw.OwnerRightOrigin", StringComparison.Ordinal) &&
              spellRenderer.Contains("##spell-tooltip", StringComparison.Ordinal) &&
              spellRenderer.Contains("skin.DrawBackdrop", StringComparison.Ordinal) &&
              spellRenderer.Contains("new Vector4(.09f, .09f, .19f, 1f)",
                  StringComparison.Ordinal),
            "B2 changed spell tooltip content, anchor, font, backdrop, or snapshot isolation");

        Check(spellbook.Contains("new GameTooltipOwnerKey(\"spellbook-button\", " +
                  "(ulong)buttonOrdinal)", StringComparison.Ordinal) &&
              spellbook.Contains("new GameTooltipOwnerKey(\"spellbook-tab\", " +
                  "(ulong)tabOrdinal)", StringComparison.Ordinal) &&
              actions.Contains("new(\"action-main\", (ulong)(i + 1))",
                  StringComparison.Ordinal) &&
              actions.Contains("\"MultiBarBottomLeft\" => \"action-multi-bottom-left\"",
                  StringComparison.Ordinal) &&
              actions.Contains("\"MultiBarBottomRight\" => \"action-multi-bottom-right\"",
                  StringComparison.Ordinal) &&
              actions.Contains("\"MultiBarRight\" => \"action-multi-right\"",
                  StringComparison.Ordinal) &&
              actions.Contains("\"MultiBarLeft\" => \"action-multi-left\"",
                  StringComparison.Ordinal) &&
              actions.Contains("new(\"micro-button\", (ulong)button.Id + 1)",
                  StringComparison.Ordinal) &&
              actions.Contains("new(\"main-menu-xp\", 1)", StringComparison.Ordinal) &&
              actions.Contains("new(\"main-menu-performance\", 1)",
                  StringComparison.Ordinal) &&
              pet.Contains("new GameTooltipOwnerKey(\"pet-action\", (ulong)(i + 1))",
                  StringComparison.Ordinal) &&
              !pet.Contains("petGuid, (ulong)(i + 1)", StringComparison.Ordinal),
            "B2 tooltip owner keys stopped identifying fixed physical controls");

        int actionStart = actions.IndexOf("private void DrawActionBars()", StringComparison.Ordinal);
        int multiStart = actions.IndexOf("private void DrawMultiActionBars()", actionStart,
            StringComparison.Ordinal);
        int multiBarStart = actions.IndexOf("private void DrawMultiActionBar(", multiStart,
            StringComparison.Ordinal);
        Check(actionStart >= 0 && multiStart > actionStart && multiBarStart > multiStart,
            "B2 action/multibar ordering source fence is missing");
        string mainAction = actions[actionStart..multiStart];
        string multiAction = actions[multiStart..multiBarStart];
        int xp = mainAction.IndexOf("DrawExpBar(bg, barMin, scale);", StringComparison.Ordinal);
        int performance = mainAction.IndexOf("DrawPerformanceMeter(bg, barMin, scale, display);",
            StringComparison.Ordinal);
        int mainButtons = mainAction.IndexOf("for (int i = 0; i < 12; i++)",
            StringComparison.Ordinal);
        int micro = mainAction.IndexOf("DrawMicroMenu(barMin, scale);", StringComparison.Ordinal);
        int bottomLeft = multiAction.IndexOf("(\"MultiBarBottomLeft\"", StringComparison.Ordinal);
        int bottomRight = multiAction.IndexOf("(\"MultiBarBottomRight\"", StringComparison.Ordinal);
        int right = multiAction.IndexOf("(\"MultiBarRight\"", StringComparison.Ordinal);
        int left = multiAction.IndexOf("(\"MultiBarLeft\"", StringComparison.Ordinal);
        int finalActionSpell = multiAction.IndexOf(
            "if (_hoveredActionSpellTooltip is { } prepared)", StringComparison.Ordinal);
        int finishDrag = multiAction.IndexOf("FinishActionDrag();", StringComparison.Ordinal);
        Check(xp >= 0 && xp < performance && performance < mainButtons && mainButtons < micro &&
              bottomLeft >= 0 && bottomLeft < bottomRight && bottomRight < right && right < left &&
              left < finalActionSpell && finalActionSpell < finishDrag,
            "B2 action tooltip evaluation or final spell winner order drift");

        int spellbookStart = spellbook.IndexOf("private void DrawSpellbook()",
            StringComparison.Ordinal);
        int spellbookEnd = spellbook.IndexOf("private bool IsProfessionRecipeSpell", spellbookStart,
            StringComparison.Ordinal);
        string spellbookDraw = spellbook[spellbookStart..spellbookEnd];
        int spellButtons = spellbookDraw.IndexOf("DrawSpellButton(", StringComparison.Ordinal);
        int tabs = spellbookDraw.IndexOf("DrawSpellTab(", StringComparison.Ordinal);
        int finalSpell = spellbookDraw.IndexOf(
            "if (_hoveredSpellTooltip is { } hoveredSpellTooltip)", StringComparison.Ordinal);
        Check(spellButtons >= 0 && spellButtons < tabs && tabs < finalSpell &&
              spellbook.Contains("ImGui.TextUnformatted(preparedName);", StringComparison.Ordinal),
            "B2 spellbook tab/final-spell arbitration or preserved tab text drift");

        int petStart = pet.IndexOf("private void DrawPetActionBar(", StringComparison.Ordinal);
        int petEnd = pet.IndexOf("private void UsePetAction(", petStart, StringComparison.Ordinal);
        string petBar = pet[petStart..petEnd];
        int tokenOffer = petBar.IndexOf("() => DrawPetTokenTooltip(preparedName)",
            StringComparison.Ordinal);
        int petSpellOffer = petBar.IndexOf(
            "if (hoveredSpellTooltip is { } hoveredPetSpellTooltip)", StringComparison.Ordinal);
        int petTokenRendererStart = pet.IndexOf(
            "private static void DrawPetTokenTooltip(string preparedName)",
            StringComparison.Ordinal);
        int petTokenRendererEnd = pet.IndexOf("private void DrawPetAutocastSparkles(",
            petTokenRendererStart, StringComparison.Ordinal);
        string petTokenRenderer = petTokenRendererStart >= 0 &&
                                  petTokenRendererEnd > petTokenRendererStart
            ? pet[petTokenRendererStart..petTokenRendererEnd] : "";
        Check(tokenOffer >= 0 && tokenOffer < petSpellOffer &&
              petTokenRendererStart >= 0 && petTokenRendererEnd > petTokenRendererStart &&
              petTokenRenderer.Contains("ImGui.TextUnformatted(preparedName);",
                  StringComparison.Ordinal) &&
              !petTokenRenderer.Contains("ImGui.TextDisabled", StringComparison.Ordinal),
            "B2 pet token name-only renderer or post-loop spell winner order drift");

        Check(actions.Contains("$\"Experience: {current} / {maximum}\"",
                  StringComparison.Ordinal) &&
              actions.Contains("$\"Rested bonus: {rested} ({RestStateName(player.Fields.RestState)})\"",
                  StringComparison.Ordinal) &&
              actions.Contains("$\"Latency: {latency}ms\"", StringComparison.Ordinal) &&
              actions.Contains("ImGui.TextWrapped(newbieText);",
                  StringComparison.Ordinal) &&
              actions.Contains("ImGui.TextDisabled(tooltipAction);", StringComparison.Ordinal),
            "B2 changed preserved XP/performance/micro/action tooltip content");
    }

    private static void CheckB3ItemProducerSourceFence()
    {
        string client = Path.Combine(ClientConfig.FindRepoRoot(), "MSUIClient");
        string inventory = SourceText.Read(Path.Combine(client, "Program.Inventory.cs"));
        string character = SourceText.Read(Path.Combine(client, "Program.CharacterPage.cs"));
        string inspect = SourceText.Read(Path.Combine(client, "Program.Inspect.cs"));
        string combat = SourceText.Read(Path.Combine(client, "Program.CombatFeedback.cs"));
        string bank = SourceText.Read(Path.Combine(client, "Program.Bank.cs"));
        string mail = SourceText.Read(Path.Combine(client, "Program.Mail.cs"));
        string loot = SourceText.Read(Path.Combine(client, "Program.Loot.cs"));
        string quest = SourceText.Read(Path.Combine(client, "Program.Quest.cs"));
        string vendor = SourceText.Read(Path.Combine(client, "Program.Vendor.cs")) +
                        SourceText.Read(Path.Combine(client, "Program.Vendor.Render.cs"));
        string taxi = SourceText.Read(Path.Combine(client, "Program.Taxi.cs"));
        string actions = SourceText.Read(Path.Combine(client, "Program.ActionBars.cs"));

        string allPrograms = string.Concat(Directory.EnumerateFiles(client, "Program*.cs",
                SearchOption.TopDirectoryOnly).Select(File.ReadAllText));
        Check(!allPrograms.Contains("DrawItemTooltip(", StringComparison.Ordinal) &&
              !allPrograms.Contains("DrawItemTooltipBody", StringComparison.Ordinal),
            "B3 retained a legacy mutable/direct item-tooltip bypass");

        int prepareStart = inventory.IndexOf(
            "private ItemTooltipBodySnapshot PrepareItemTooltipBodySnapshot(",
            StringComparison.Ordinal);
        int replayStart = inventory.IndexOf(
            "private static void DrawPreparedItemTooltipBody(", prepareStart,
            StringComparison.Ordinal);
        int offerStart = inventory.IndexOf("private bool OfferPreparedItemTooltip(", replayStart,
            StringComparison.Ordinal);
        int comparisonPrepareStart = inventory.IndexOf(
            "PreparePaperDollComparisonTooltips(ItemTemplate hoveredItem)", offerStart,
            StringComparison.Ordinal);
        Check(prepareStart >= 0 && replayStart > prepareStart && offerStart > replayStart &&
              comparisonPrepareStart > offerStart,
            "B3 prepared item body/replay/offer seams are missing");
        string prepareBody = inventory[prepareStart..replayStart];
        string replayBody = inventory[replayStart..offerStart];
        string offerBody = inventory[offerStart..comparisonPrepareStart];
        Check(prepareBody.Contains("ImmutableArray.CreateBuilder<PreparedItemTooltipPaintOp>()",
                  StringComparison.Ordinal) &&
              prepareBody.Contains("ItemDamage[] damages = item.Damages",
                  StringComparison.Ordinal) &&
              prepareBody.Contains("foreach (ItemStat stat in item.Stats)",
                  StringComparison.Ordinal) &&
              prepareBody.Contains("compact ? Vector4.One : ItemTooltipQualityColor(item.Quality)",
                  StringComparison.Ordinal) &&
              prepareBody.Contains("if (!compact && !string.IsNullOrWhiteSpace(item.Description))",
                  StringComparison.Ordinal) &&
              replayBody.Contains("foreach (PreparedItemTooltipPaintOp operation in body.Operations)",
                  StringComparison.Ordinal) &&
              !replayBody.Contains("ItemTemplate", StringComparison.Ordinal) &&
              !replayBody.Contains("WorldEntity", StringComparison.Ordinal) &&
              !replayBody.Contains("ObjectFields", StringComparison.Ordinal) &&
              offerBody.Contains("OfferPreservedSharedGameTooltipRenderer(owner, () =>",
                  StringComparison.Ordinal) &&
              offerBody.Contains("DrawPreparedItemTooltipBody(preparedBody);",
                  StringComparison.Ordinal) &&
              offerBody.Contains("SetSharedGameTooltipComparisonCount(token, comparisonCount)",
                  StringComparison.Ordinal) &&
              !offerBody.Contains("ItemTemplate", StringComparison.Ordinal) &&
              !offerBody.Contains("_items", StringComparison.Ordinal) &&
              !offerBody.Contains("_entities", StringComparison.Ordinal),
            "B3 item snapshot/replay escaped immutable final paint operations");

        int comparisonDrawStart = inventory.IndexOf(
            "private void DrawPreparedPaperDollComparisonTooltips(", comparisonPrepareStart,
            StringComparison.Ordinal);
        int comparisonDrawEnd = inventory.IndexOf(
            "private void ArmDeferredShoppingTooltipParityCapture(", comparisonDrawStart,
            StringComparison.Ordinal);
        Check(comparisonDrawStart > comparisonPrepareStart &&
              comparisonDrawEnd > comparisonDrawStart,
            "B3 prepared ShoppingTooltip renderer seam is missing");
        string comparisonPrepare = inventory[comparisonPrepareStart..comparisonDrawStart];
        string comparisonDraw = inventory[comparisonDrawStart..comparisonDrawEnd];
        int ordinal = comparisonPrepare.IndexOf("int tooltipNumber = ordinal + 1;",
            StringComparison.Ordinal);
        int candidate = comparisonPrepare.IndexOf("ulong equippedGuid =",
            StringComparison.Ordinal);
        Check(ordinal >= 0 && ordinal < candidate &&
              comparisonPrepare.Contains("bool shift = ImGui.GetIO().KeyShift;",
                  StringComparison.Ordinal) &&
              comparisonPrepare.Contains("sourceIsEquipped: false", StringComparison.Ordinal) &&
              comparisonPrepare.Contains("PrepareItemTooltipBodySnapshot(equippedTemplate,",
                  StringComparison.Ordinal) &&
              comparisonDraw.Contains("DrawPreparedItemTooltipBody(comparison.Body);",
                  StringComparison.Ordinal) &&
              comparisonDraw.Contains("_shoppingTooltipParityRendererCollected = true;",
                  StringComparison.Ordinal) &&
              !comparisonDraw.Contains("ItemTemplate", StringComparison.Ordinal) &&
              !comparisonDraw.Contains("WorldEntity", StringComparison.Ordinal) &&
              !comparisonDraw.Contains("ObjectFields", StringComparison.Ordinal) &&
              !comparisonDraw.Contains("_items", StringComparison.Ordinal) &&
              !comparisonDraw.Contains("_entities", StringComparison.Ordinal) &&
              !comparisonDraw.Contains("_enchantCatalog", StringComparison.Ordinal) &&
              !comparisonDraw.Contains("ImGui.GetIO()", StringComparison.Ordinal) &&
              !comparisonDraw.Contains("PaperDollUiLaw", StringComparison.Ordinal),
            "B3 ShoppingTooltip ordinal/snapshot/renderer isolation drift");

        Check(inventory.Contains(
                  "return new($\"item:inventory-container:{container}\", (ulong)physicalButton);",
                  StringComparison.Ordinal) &&
              inventory.Contains(
                  "InventoryUiLaw.KeyringContainer => InventoryUiLaw.KeyringSize(owner.Level)",
                  StringComparison.Ordinal) &&
              inventory.Contains(
                  "InventoryItemGameTooltipOwner(container, physical), body, tooltipSeat.Position",
                  StringComparison.Ordinal) &&
              inventory.Contains("HighestLiveComparisonOrdinal(", StringComparison.Ordinal) &&
              inventory.Contains("ArmDeferredShoppingTooltipParityCapture(comparisons);",
                  StringComparison.Ordinal) &&
              inventory.Contains("new(\"item:inventory-bag-bar\", (ulong)container)",
                  StringComparison.Ordinal) &&
              inventory.Contains("new(\"bag-button\", 0)", StringComparison.Ordinal) &&
              inventory.Contains("new(\"keyring-button\", 0)", StringComparison.Ordinal),
            "B3 inventory physical/keyring/bag-button owner or comparison offer fence drift");

        Check(character.Contains(
                  "\"item:character-paper-doll\", (ulong)slot", StringComparison.Ordinal) &&
              character.Contains("new GameTooltipOwnerKey(\"item:character-ammo\", 0)",
                  StringComparison.Ordinal) &&
              character.Contains("$\"paperdoll-stat:{element}\", 0",
                  StringComparison.Ordinal) &&
              character.Contains("ImmutableArray<string> preparedLines", StringComparison.Ordinal) &&
              character.Contains("!_shoppingTooltipParityCompletionPending",
                  StringComparison.Ordinal) &&
              Count(character, "OfferPreparedItemTooltip(tooltipOwner, body)") == 2 &&
              Count(character, "OfferPreservedSharedGameTooltipRenderer(tooltipOwner, () =>") >= 3,
            "B3 character item/ammo/empty/stat fixed-owner or deferred completion drift");

        int inspectPrepareStart = inspect.IndexOf(
            "private ItemTooltipBodySnapshot PrepareInspectItemTooltipBody(",
            StringComparison.Ordinal);
        Check(inspect.Contains("\"item:inspect-paper-doll\", (ulong)slot",
                  StringComparison.Ordinal) &&
              inspect.Contains("OfferPreparedItemTooltip(tooltipOwner, body, max);",
                  StringComparison.Ordinal) &&
              inspectPrepareStart >= 0 &&
              inspect.Contains("PrepareItemTooltipBodySnapshot(item, 1)",
                  StringComparison.Ordinal) &&
              inspect.Contains("for (int enchantSlot = 0;", StringComparison.Ordinal) &&
              inspect.Contains("PreparedItemTooltipColored(enchant.Name,",
                  StringComparison.Ordinal) &&
              inspect.Contains("AppendPreparedItemTooltipBody(body, [.. enchantOperations])",
                  StringComparison.Ordinal) &&
              !inspect.Contains("DrawInspectItemTooltip", StringComparison.Ordinal),
            "B3 inspect public-enchant snapshot or fixed-slot fallback drift");

        int resolve = combat.IndexOf("ResolveAndDrawSharedGameTooltip();",
            StringComparison.Ordinal);
        int shopping = combat.IndexOf("CompleteDeferredShoppingTooltipParityCapture();",
            resolve, StringComparison.Ordinal);
        int party = combat.IndexOf("CompleteDeferredPartyTooltipParityCapture();", shopping,
            StringComparison.Ordinal);
        int end = combat.IndexOf("EndSharedGameTooltipFrame();", party,
            StringComparison.Ordinal);
        int fallbackShopping = combat.IndexOf(
            "CompleteDeferredShoppingTooltipParityCapture();", end, StringComparison.Ordinal);
        int fallbackParty = combat.IndexOf("CompleteDeferredPartyTooltipParityCapture();",
            fallbackShopping, StringComparison.Ordinal);
        Check(resolve >= 0 && resolve < shopping && shopping < party && party < end &&
              end < fallbackShopping && fallbackShopping < fallbackParty &&
              Count(combat, "CompleteDeferredShoppingTooltipParityCapture();") == 2 &&
              inventory.Contains(
                  "shared-tooltip-owner-replaced-before-tooltip-stratum",
                  StringComparison.Ordinal),
            "B3 ShoppingTooltip parity did not complete after Resolve with a finally fallback");

        string rich = bank + mail + loot + quest + vendor + actions;
        Check(Count(rich, "PrepareItemTooltipBodySnapshot") == 6 &&
              Count(rich, "OfferPreparedItemTooltip") == 6 &&
              vendor.Contains("PrepareVendorTemplateTooltip(item, player)",
                  StringComparison.Ordinal) &&
              vendor.Contains("OfferVendorTemplateTooltip(", StringComparison.Ordinal) &&
              vendor.Contains("new Vector2(0, 1)", StringComparison.Ordinal) &&
              !rich.Contains("DrawItemTooltip(", StringComparison.Ordinal) &&
              !rich.Contains("DrawItemTooltipBody", StringComparison.Ordinal),
            "B3 secondary rich-item producers escaped the six shared and two Merchant-specific immutable prepared offers");
        Check(bank.Contains("new(\"item:bank-item\", (ulong)i)", StringComparison.Ordinal) &&
              vendor.Contains("new(\"item:vendor-merchant-row\", (ulong)(physical - 1))",
                  StringComparison.Ordinal) &&
              loot.Contains("new(\"item:loot-row\", (ulong)visual)",
                  StringComparison.Ordinal) &&
              quest.Contains("$\"item:quest:{panel}:{kind}\", (ulong)index",
                  StringComparison.Ordinal) &&
              quest.Contains("GameTooltipOwnerKey tooltipOwner = QuestItemGameTooltipOwner(",
                  StringComparison.Ordinal) &&
              quest.Contains("QuestFrameUiLaw.ItemTooltipSeat", StringComparison.Ordinal) &&
              quest.Contains("tooltipPanel == QuestNpcPanel.None", StringComparison.Ordinal) &&
              actions.Contains("GameTooltipOwnerKey tooltipOwner = " +
                  "ActionBarGameTooltipOwner(name, i);", StringComparison.Ordinal) &&
              !bank.Contains("item:bank-item\", (ulong)instance", StringComparison.Ordinal) &&
              !vendor.Contains("item:vendor-merchant-row\", (ulong)row.Slot",
                  StringComparison.Ordinal) &&
              !loot.Contains("item:loot-row\", (ulong)row.", StringComparison.Ordinal),
            "B3 secondary fixed widget owner identity drift");

        Check(mail.Contains("DrawMailInboxRow(dl, rowMin, _mail[index], s, visible);",
                  StringComparison.Ordinal) &&
              mail.Contains("new(\"mail-inbox-expiry\", (ulong)visibleIndex)",
                  StringComparison.Ordinal) &&
              mail.Contains("new(\"item:mail-inbox\", (ulong)visibleIndex)",
                  StringComparison.Ordinal) &&
              Count(mail, "new(\"item:mail-send-attachment\", 0)") == 2 &&
              mail.Contains("new(\"item:mail-open-package\", 0)",
                  StringComparison.Ordinal) &&
              mail.Contains("new(\"mail-open-letter\", 0)", StringComparison.Ordinal) &&
              mail.Contains("ImGui.TextUnformatted(\"Enclosed amount\");",
                  StringComparison.Ordinal) &&
              mail.Contains("PreparedItemTooltipPlain(\"Cash on Delivery Amount:\")",
                  StringComparison.Ordinal) &&
              mail.Contains("ImGui.TextUnformatted(\"Attach an item to send.\");",
                  StringComparison.Ordinal) &&
              mail.Contains("Click to make a permanent\\ncopy of this letter.",
                  StringComparison.Ordinal) &&
              !mail.Contains("SetSharedGameTooltipMoney", StringComparison.Ordinal),
            "B3 Mail fixed owners or preserved item/money/letter wording drift");
        int formatCod = mail.IndexOf("string codAmount = FormatMoney(row.Cod);",
            StringComparison.Ordinal);
        int appendCod = mail.IndexOf("tooltipBody = AppendPreparedItemTooltipBody(tooltipBody,",
            formatCod, StringComparison.Ordinal);
        int separatorCod = mail.IndexOf("PreparedItemTooltipSeparator()", appendCod,
            StringComparison.Ordinal);
        int labelCod = mail.IndexOf(
            "PreparedItemTooltipPlain(\"Cash on Delivery Amount:\")", separatorCod,
            StringComparison.Ordinal);
        int amountCod = mail.IndexOf("PreparedItemTooltipPlain(codAmount)", labelCod,
            StringComparison.Ordinal);
        int offerCod = mail.IndexOf(
            "OfferPreparedItemTooltip(tooltipOwner, tooltipBody, tooltipSeat.Anchor,",
            amountCod, StringComparison.Ordinal);
        Check(formatCod >= 0 && formatCod < appendCod && appendCod < separatorCod &&
              separatorCod < labelCod && labelCod < amountCod && amountCod < offerCod,
            "B3 Mail COD tail lost separator/label/verbose-money snapshot order");
    }

    private static void CheckB4B5ProducerSourceFence()
    {
        string client = Path.Combine(ClientConfig.FindRepoRoot(), "MSUIClient");
        string unitFrames = SourceText.Read(Path.Combine(client, "Program.UnitFrames.cs"));
        string minimap = SourceText.Read(Path.Combine(client, "Program.Minimap.cs"));
        string combat = SourceText.Read(Path.Combine(client, "Program.CombatFeedback.cs"));

        int auraDrawStart = unitFrames.IndexOf("private void DrawPlayerAuraBar()",
            StringComparison.Ordinal);
        int auraSnapshotStart = unitFrames.IndexOf(
            "private readonly record struct PreparedPlayerAuraTooltip(", auraDrawStart,
            StringComparison.Ordinal);
        int auraOwnerStart = unitFrames.IndexOf(
            "private static GameTooltipOwnerKey PlayerAuraGameTooltipOwner(", auraSnapshotStart,
            StringComparison.Ordinal);
        int auraPrepareStart = unitFrames.IndexOf(
            "private PreparedPlayerAuraTooltip PreparePlayerAuraTooltip(", auraOwnerStart,
            StringComparison.Ordinal);
        int auraRendererStart = unitFrames.IndexOf(
            "private static void DrawPlayerAuraTooltip(in PreparedPlayerAuraTooltip prepared)",
            auraPrepareStart, StringComparison.Ordinal);
        int auraRendererEnd = unitFrames.IndexOf(
            "private void DrawUnitPortraitImage(", auraRendererStart, StringComparison.Ordinal);
        Check(auraDrawStart >= 0 && auraSnapshotStart > auraDrawStart &&
              auraOwnerStart > auraSnapshotStart && auraPrepareStart > auraOwnerStart &&
              auraRendererStart > auraPrepareStart && auraRendererEnd > auraRendererStart,
            "B4 prepared player-aura tooltip seams are missing");
        string auraDraw = unitFrames[auraDrawStart..auraSnapshotStart];
        string auraOwner = unitFrames[auraOwnerStart..auraPrepareStart];
        string auraPrepare = unitFrames[auraPrepareStart..auraRendererStart];
        string auraRenderer = unitFrames[auraRendererStart..auraRendererEnd];

        int auraButton = auraDraw.IndexOf(
            "int buttonIndex = harmful ? BuffUiLaw.HelpfulLimit + cohort : cohort;",
            StringComparison.Ordinal);
        int invisible = auraDraw.IndexOf("bool cancelReleased = ImGui.InvisibleButton(",
            auraButton, StringComparison.Ordinal);
        int itemHover = auraDraw.IndexOf("bool itemHovered = ImGui.IsItemHovered();", invisible,
            StringComparison.Ordinal);
        int captureOwner = auraDraw.IndexOf("hoveredButtonIndex = buttonIndex;", itemHover,
            StringComparison.Ordinal);
        int cancel = auraDraw.IndexOf("CancelPlayerAura(aura, \"UI_RIGHT_CLICK\");",
            captureOwner, StringComparison.Ordinal);
        int closeAuraWindow = auraDraw.LastIndexOf("ImGui.End();", StringComparison.Ordinal);
        int prepareOffer = auraDraw.IndexOf(
            "PreparedPlayerAuraTooltip prepared = PreparePlayerAuraTooltip(",
            StringComparison.Ordinal);
        int fixedOwner = auraDraw.IndexOf(
            "GameTooltipOwnerKey owner = PlayerAuraGameTooltipOwner(hoveredButtonIndex);",
            prepareOffer, StringComparison.Ordinal);
        int opaqueOffer = auraDraw.IndexOf("OfferPreservedSharedGameTooltipRenderer(owner,",
            fixedOwner, StringComparison.Ordinal);
        int preparedCallback = auraDraw.IndexOf("() => DrawPlayerAuraTooltip(prepared)",
            opaqueOffer, StringComparison.Ordinal);
        Check(auraButton >= 0 && auraButton < invisible && invisible < itemHover &&
              itemHover < captureOwner && captureOwner < cancel &&
              closeAuraWindow >= 0 && closeAuraWindow < prepareOffer &&
              prepareOffer < fixedOwner && fixedOwner < opaqueOffer &&
              opaqueOffer < preparedCallback &&
              Count(auraDraw, "OfferPreservedSharedGameTooltipRenderer") == 1 &&
              !auraDraw.Contains("QueueSharedGameTooltipRenderer", StringComparison.Ordinal) &&
              !auraDraw.Contains("PublishSharedGameTooltip", StringComparison.Ordinal) &&
              !auraDraw.Contains("BeginSharedGameTooltipFade", StringComparison.Ordinal),
            "B4 aura fixed-button capture, cancel, close-window, or guarded offer order drift");

        Check(auraOwner.Contains(
                  "return new(\"player-aura-button\", (ulong)buttonIndex);",
                  StringComparison.Ordinal) &&
              auraOwner.Contains("BuffUiLaw.HelpfulLimit + BuffUiLaw.HarmfulLimit",
                  StringComparison.Ordinal) &&
              auraPrepare.Contains("SpellTooltipLaw.Substitute(info.Description, info,",
                  StringComparison.Ordinal) &&
              auraPrepare.Contains("remainingLine = $\"{AuraTimeText(remaining)} remaining\";",
                  StringComparison.Ordinal) &&
              auraPrepare.Contains("\"Right-click to cancel\"", StringComparison.Ordinal) &&
              auraPrepare.Contains("\"Cannot be cancelled\"", StringComparison.Ordinal) &&
              !auraPrepare.Contains("ImGui.", StringComparison.Ordinal),
            "B4 aura owner bounds or final content preparation drift");

        int title = auraRenderer.IndexOf("ImGui.TextUnformatted(prepared.Title);",
            StringComparison.Ordinal);
        int stacks = auraRenderer.IndexOf("ImGui.TextDisabled(prepared.StackLine);", title,
            StringComparison.Ordinal);
        int descriptionSeparator = auraRenderer.IndexOf("ImGui.Separator();", stacks,
            StringComparison.Ordinal);
        int description = auraRenderer.IndexOf(
            "ImGui.TextUnformatted(prepared.Description);", descriptionSeparator,
            StringComparison.Ordinal);
        int remainingSeparator = auraRenderer.IndexOf("ImGui.Separator();", description + 1,
            StringComparison.Ordinal);
        int remaining = auraRenderer.IndexOf("ImGui.TextUnformatted(prepared.RemainingLine);",
            remainingSeparator, StringComparison.Ordinal);
        int helpful = auraRenderer.IndexOf("ImGui.TextDisabled(prepared.HelpfulLine);", remaining,
            StringComparison.Ordinal);
        Check(title >= 0 && title < stacks && stacks < descriptionSeparator &&
              descriptionSeparator < description && description < remainingSeparator &&
              remainingSeparator < remaining && remaining < helpful &&
              auraRenderer.Contains(
                  "ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + SpellTooltipLaw.WrapWidth);",
                  StringComparison.Ordinal) &&
              !auraRenderer.Contains("AuraSnapshot", StringComparison.Ordinal) &&
              !auraRenderer.Contains("AuraTimer", StringComparison.Ordinal) &&
              !auraRenderer.Contains("_spellCatalog", StringComparison.Ordinal) &&
              !auraRenderer.Contains("_entities", StringComparison.Ordinal) &&
              !auraRenderer.Contains("_net", StringComparison.Ordinal) &&
              !auraRenderer.Contains("NowSeconds", StringComparison.Ordinal) &&
              !auraRenderer.Contains("SpellTooltipLaw.Substitute", StringComparison.Ordinal) &&
              !auraRenderer.Contains("AuraTimeText", StringComparison.Ordinal) &&
              !auraRenderer.Contains("CollectUiParity", StringComparison.Ordinal),
            "B4 aura deferred callback retained mutable state or changed paint order");

        int minimapDrawStart = minimap.IndexOf("private void DrawMinimap()",
            StringComparison.Ordinal);
        int minimapDrawEnd = minimap.IndexOf("private void DrawMinimapZoneText(",
            minimapDrawStart, StringComparison.Ordinal);
        int resourceDrawStart = minimap.IndexOf(
            "private MinimapResourceTooltipCandidate? DrawMinimapResourceDots(",
            StringComparison.Ordinal);
        int resourceOwnerStart = minimap.IndexOf(
            "private static GameTooltipOwnerKey MinimapResourceGameTooltipOwner(",
            resourceDrawStart, StringComparison.Ordinal);
        int resourceUpdateStart = minimap.IndexOf(
            "private bool UpdateAndQueueMinimapResourceTooltip(", resourceOwnerStart,
            StringComparison.Ordinal);
        int resourceRendererStart = minimap.IndexOf(
            "private static void DrawMinimapResourceTooltip(string preparedName, " +
            "float preparedAlpha)", resourceUpdateStart, StringComparison.Ordinal);
        int resourceRendererEnd = minimap.IndexOf(
            "private void ReportMinimapResourceSet(", resourceRendererStart,
            StringComparison.Ordinal);
        Check(minimapDrawStart >= 0 && minimapDrawEnd > minimapDrawStart &&
              resourceDrawStart >= 0 && resourceOwnerStart > resourceDrawStart &&
              resourceUpdateStart > resourceOwnerStart &&
              resourceRendererStart > resourceUpdateStart &&
              resourceRendererEnd > resourceRendererStart,
            "B5 minimap resource tooltip seams are missing");
        string minimapDraw = minimap[minimapDrawStart..minimapDrawEnd];
        string resourceDraw = minimap[resourceDrawStart..resourceOwnerStart];
        string resourceOwner = minimap[resourceOwnerStart..resourceUpdateStart];
        string resourceUpdate = minimap[resourceUpdateStart..resourceRendererStart];
        string resourceRenderer = minimap[resourceRendererStart..resourceRendererEnd];

        Check(Count(minimapDraw, "UpdateAndQueueMinimapResourceTooltip(null);") == 2 &&
              Count(minimapDraw, "UpdateAndQueueMinimapResourceTooltip(") == 3 &&
              minimapDraw.Contains(
                  "DrawMinimapResourceDots(dl, player, playerPosition, mapMin, mapMax, s,",
                  StringComparison.Ordinal) &&
              minimapDraw.Contains(
                  "questTooltip ?? creatureTooltip ?? resourceTooltip ?? landmarkTooltip",
                  StringComparison.Ordinal) &&
              resourceDraw.Contains(
                  "ImGui.IsMouseHoveringRect(row.Dot - half, row.Dot + half, false)",
                  StringComparison.Ordinal) &&
              resourceDraw.Contains(
                  "hoveredTooltip = new(row.Go.Guid, row.Template.Name);",
                  StringComparison.Ordinal) &&
              !resourceDraw.Contains("ImGui.SetTooltip", StringComparison.Ordinal) &&
              !resourceDraw.Contains("InvisibleButton", StringComparison.Ordinal) &&
              !resourceDraw.Contains("OrderBy", StringComparison.Ordinal),
            "B5 minimap exit coverage, raw pointer hit, or later-painted overlap winner drift");
        int popClip = resourceDraw.IndexOf("dl.PopClipRect();", StringComparison.Ordinal);
        int reportSet = resourceDraw.IndexOf("ReportMinimapResourceSet(mask, visible);", popClip,
            StringComparison.Ordinal);
        int returnCandidate = resourceDraw.IndexOf("return hoveredTooltip;", reportSet,
            StringComparison.Ordinal);
        Check(popClip >= 0 && popClip < reportSet && reportSet < returnCandidate &&
              resourceOwner.Contains("=> new(\"minimap-resource-dot\", guid);",
                  StringComparison.Ordinal),
            "B5 minimap telemetry order or stable resource-GUID owner drift");

        int frameGuard = resourceUpdate.IndexOf(
            "if (!_sharedTooltipFrameOpen || _sharedTooltipFrameResolved) return false;",
            StringComparison.Ordinal);
        int claimNode = resourceUpdate.IndexOf("ClaimSharedGameTooltip(", frameGuard,
            StringComparison.Ordinal);
        int clearNode = resourceUpdate.IndexOf("ClearSharedGameTooltip(token)", claimNode,
            StringComparison.Ordinal);
        int fadeNode = resourceUpdate.IndexOf("BeginSharedGameTooltipFade(departing.Token,",
            clearNode, StringComparison.Ordinal);
        int lifecycleSnapshot = resourceUpdate.IndexOf(
            "GameTooltipLifecycleState lifecycle = SharedGameTooltipSnapshot().Lifecycle;",
            fadeNode, StringComparison.Ordinal);
        int prepareName = resourceUpdate.IndexOf("string preparedName = runtime.Name;",
            lifecycleSnapshot, StringComparison.Ordinal);
        int prepareAlpha = resourceUpdate.IndexOf("float preparedAlpha = lifecycle.Alpha;",
            prepareName, StringComparison.Ordinal);
        int queueNode = resourceUpdate.IndexOf("QueueSharedGameTooltipRenderer(runtime.Token,",
            prepareAlpha, StringComparison.Ordinal);
        int fadePolicy = resourceUpdate.IndexOf(
            "SharedGameTooltipLeavePolicy.Fade(GameTooltipUiLaw.WorldFadeSeconds)",
            queueNode, StringComparison.Ordinal);
        int immutableCallback = resourceUpdate.IndexOf(
            "() => DrawMinimapResourceTooltip(preparedName, preparedAlpha)",
            fadePolicy, StringComparison.Ordinal);
        Check(frameGuard >= 0 && frameGuard < claimNode && claimNode < clearNode &&
              clearNode < fadeNode && fadeNode < lifecycleSnapshot &&
              lifecycleSnapshot < prepareName && prepareName < prepareAlpha &&
              prepareAlpha < queueNode && queueNode < fadePolicy &&
              fadePolicy < immutableCallback &&
              resourceUpdate.Contains("!SharedGameTooltipIsOwned(departing.Token)",
                  StringComparison.Ordinal) &&
              resourceUpdate.Contains("!SharedGameTooltipIsOwned(runtime.Token)",
                  StringComparison.Ordinal) &&
              !resourceUpdate.Contains("PublishSharedGameTooltip", StringComparison.Ordinal) &&
              !resourceUpdate.Contains("OfferPreservedSharedGameTooltipRenderer",
                  StringComparison.Ordinal) &&
              !resourceUpdate.Contains("HideSharedGameTooltip", StringComparison.Ordinal),
            "B5 minimap guard/claim/clear/fade/snapshot/explicit-lease order drift");

        int fullAlphaTooltip = resourceRenderer.IndexOf("ImGui.SetTooltip(preparedName);",
            StringComparison.Ordinal);
        int pushAlpha = resourceRenderer.IndexOf("ImGui.PushStyleVar(ImGuiStyleVar.Alpha,",
            fullAlphaTooltip, StringComparison.Ordinal);
        int tryFade = resourceRenderer.IndexOf("try", pushAlpha, StringComparison.Ordinal);
        int fadedTooltip = resourceRenderer.IndexOf("ImGui.SetTooltip(preparedName);",
            fullAlphaTooltip + 1, StringComparison.Ordinal);
        int finallyFade = resourceRenderer.IndexOf("finally", fadedTooltip,
            StringComparison.Ordinal);
        int popAlpha = resourceRenderer.IndexOf("ImGui.PopStyleVar();", finallyFade,
            StringComparison.Ordinal);
        Check(fullAlphaTooltip >= 0 && fullAlphaTooltip < pushAlpha && pushAlpha < tryFade &&
              tryFade < fadedTooltip && fadedTooltip < finallyFade && finallyFade < popAlpha &&
              Count(resourceRenderer, "ImGui.SetTooltip(preparedName);") == 2 &&
              resourceRenderer.Contains("no public ImGui", StringComparison.Ordinal) &&
              resourceRenderer.Contains("inventing an offset would move the approved anchor",
                  StringComparison.Ordinal) &&
              !resourceRenderer.Contains("SetNextWindowPos", StringComparison.Ordinal) &&
              !resourceRenderer.Contains("BeginTooltip", StringComparison.Ordinal) &&
              !resourceRenderer.Contains("WorldEntity", StringComparison.Ordinal) &&
              !resourceRenderer.Contains("GameObjectTemplate", StringComparison.Ordinal) &&
              !resourceRenderer.Contains("_entities", StringComparison.Ordinal) &&
              !resourceRenderer.Contains("_gameObjectTemplates", StringComparison.Ordinal) &&
              !resourceRenderer.Contains("_locks", StringComparison.Ordinal) &&
              !resourceRenderer.Contains("NowSeconds", StringComparison.Ordinal),
            "B5 changed the frozen SetTooltip cursor path or callback snapshot isolation");

        int combatStart = combat.IndexOf("private void DrawCombatHud()", StringComparison.Ordinal);
        int combatEnd = combat.IndexOf("private void DrawPlayerFrame()", combatStart,
            StringComparison.Ordinal);
        string drawOrder = combat[combatStart..combatEnd];
        int party = drawOrder.IndexOf("DrawPartyFrames();", StringComparison.Ordinal);
        int aura = drawOrder.IndexOf("DrawPlayerAuraBar();", StringComparison.Ordinal);
        int map = drawOrder.IndexOf("DrawMinimap();", StringComparison.Ordinal);
        int actions = drawOrder.IndexOf("DrawActionBars();", StringComparison.Ordinal);
        int inventory = drawOrder.IndexOf("DrawInventory();", StringComparison.Ordinal);
        int character = drawOrder.IndexOf("DrawCharacterPage();", StringComparison.Ordinal);
        int inspect = drawOrder.IndexOf("DrawInspectFrame();", StringComparison.Ordinal);
        int spellbook = drawOrder.IndexOf("DrawSpellbook();", StringComparison.Ordinal);
        int multi = drawOrder.IndexOf("DrawMultiActionBars();", StringComparison.Ordinal);
        int resolve = drawOrder.IndexOf("ResolveAndDrawSharedGameTooltip();",
            StringComparison.Ordinal);
        Check(party >= 0 && party < aura && aura < map && map < actions &&
              actions < inventory && inventory < character && character < inspect &&
              inspect < spellbook && spellbook < multi && multi < resolve,
            "B4/B5 tooltip arbitration no longer yields to later B2/B3 physical controls");
    }

    private static void CheckB6WorldUnitSourceFence()
    {
        string client = Path.Combine(ClientConfig.FindRepoRoot(), "MSUIClient");
        string query = SourceText.Read(Path.Combine(client, "Net", "CreatureQuery.cs"));
        string fields = SourceText.Read(Path.Combine(client, "Net", "ObjectFields.cs"));
        string targeting = SourceText.Read(Path.Combine(client, "Program.Targeting.cs"));
        string nameplates = SourceText.Read(Path.Combine(client, "Program.Nameplates.cs"));
        string net = SourceText.Read(Path.Combine(client, "Program.Net.cs"));
        string world = SourceText.Read(
            Path.Combine(client, "Program.GameTooltip.WorldUnit.cs"));
        string renderer = SourceText.Read(
            Path.Combine(client, "Program.GameTooltip.Renderer.cs"));
        string combat = SourceText.Read(Path.Combine(client, "Program.CombatFeedback.cs"));

        Check(query.Contains("public sealed record CreatureQueryInfo(",
                  StringComparison.Ordinal) &&
              query.Contains("string Name,", StringComparison.Ordinal) &&
              query.Contains("string? Subname,", StringComparison.Ordinal) &&
              query.Contains("uint TypeFlags,", StringComparison.Ordinal) &&
              query.Contains("uint CreatureType,", StringComparison.Ordinal) &&
              query.Contains("uint PetFamily,", StringComparison.Ordinal) &&
              query.Contains("uint Rank,", StringComparison.Ordinal) &&
              query.Contains("bool Civilian,", StringComparison.Ordinal) &&
              query.Contains("bool RacialLeader);", StringComparison.Ordinal) &&
              query.Contains("uint entry = rawEntry & 0x7fff_ffffu;",
                  StringComparison.Ordinal) &&
              query.Contains("(rawEntry & 0x8000_0000u) != 0",
                  StringComparison.Ordinal) &&
              query.Contains("if (r.Remaining != 0)", StringComparison.Ordinal) &&
              Count(query, "if (r.Remaining != 0)") == 2 &&
              Count(query, "r.ReadCString(); // name") == 3 &&
              Count(query, "r.ReadU32(); //") == 3 &&
              query.Contains("bool civilian = r.ReadU8() != 0;", StringComparison.Ordinal) &&
              query.Contains("bool racialLeader = r.ReadU8() != 0;",
                  StringComparison.Ordinal),
            "B6 creature-query record or exact hit/miss/trailing-byte wire parser drift");

        int resetStart = targeting.IndexOf("private void ResetTargeting()",
            StringComparison.Ordinal);
        int resetEnd = targeting.IndexOf("private bool TryBeginCreatureQuery(", resetStart,
            StringComparison.Ordinal);
        int gateEnd = targeting.IndexOf("private void UpdateTargeting()", resetEnd,
            StringComparison.Ordinal);
        Check(resetStart >= 0 && resetEnd > resetStart && gateEnd > resetEnd,
            "B6 creature-query cache/gate source seams are missing");
        string reset = targeting[resetStart..resetEnd];
        string gate = targeting[resetEnd..gateEnd];
        Check(targeting.Contains(
                  "private readonly Dictionary<uint, CreatureQueryInfo?> " +
                  "_creatureQueryRecords = [];", StringComparison.Ordinal) &&
              reset.Contains("_queriedCreatureNames.Clear();", StringComparison.Ordinal) &&
              !reset.Contains("_creatureQueryRecords.Clear", StringComparison.Ordinal) &&
              !reset.Contains("_creatureNames.Clear", StringComparison.Ordinal) &&
              gate.Contains("entry != 0 && !_creatureQueryRecords.ContainsKey(entry)",
                  StringComparison.Ordinal) &&
              gate.Contains("_queriedCreatureNames.Add(entry);", StringComparison.Ordinal) &&
              Count(targeting, "TryBeginCreatureQuery(identity.Entry)") == 1 &&
              Count(nameplates, "TryBeginCreatureQuery(unit.Entry)") == 1 &&
              Count(nameplates, "_net.CreatureQuery(unit.Entry, unit.Guid);") == 1,
            "B6 per-entry query coalescing/negative-cache gate or zoning retention drift");

        int responseStart = net.IndexOf("case Op.SMSG_CREATURE_QUERY_RESPONSE:",
            StringComparison.Ordinal);
        int responseEnd = net.IndexOf("case Op.SMSG_SPELL_START:", responseStart,
            StringComparison.Ordinal);
        Check(responseStart >= 0 && responseEnd > responseStart,
            "B6 SMSG_CREATURE_QUERY_RESPONSE apply hunk is missing");
        string response = net[responseStart..responseEnd];
        int parse = response.IndexOf("CreatureQueryPacket.Parse(body)",
            StringComparison.Ordinal);
        int removePending = response.IndexOf("_queriedCreatureNames.Remove(response.Entry);",
            StringComparison.Ordinal);
        int retainRecord = response.IndexOf(
            "_creatureQueryRecords[response.Entry] = response.Info;",
            StringComparison.Ordinal);
        int retainName = response.IndexOf("_creatureNames[response.Entry] = info.Name;",
            StringComparison.Ordinal);
        int removeName = response.IndexOf("_creatureNames.Remove(response.Entry);",
            StringComparison.Ordinal);
        Check(parse >= 0 && parse < removePending && removePending < retainRecord &&
              retainRecord < retainName && retainName < removeName &&
              !response.Contains("new PacketReader", StringComparison.Ordinal),
            "B6 creature-query response did not atomically retain hit/miss and current name cache");

        Check(fields.Contains("public const ushort UNIT_FIELD_PETNUMBER = 139;",
                  StringComparison.Ordinal) &&
              fields.Contains(
                  "public uint PetNumber => GetU32(UNIT_FIELD_PETNUMBER) ?? 0;",
                  StringComparison.Ordinal) &&
              fields.Contains("public bool IsPetOrCharm => PetNumber != 0;",
                  StringComparison.Ordinal),
            "B6 bounded ObjectFields pet-number accessor drift");

        int buildStart = world.IndexOf(
            "private GameTooltipUnitSnapshot BuildWorldUnitGameTooltipSnapshot(WorldEntity unit)",
            StringComparison.Ordinal);
        int signatureStart = world.IndexOf(
            "private static WorldUnitTooltipStaticSignature " +
            "WorldUnitGameTooltipStaticSignature(", buildStart, StringComparison.Ordinal);
        int healthPushStart = world.IndexOf(
            "private static GameTooltipUnitSnapshot WorldUnitGameTooltipHealthPush(",
            signatureStart, StringComparison.Ordinal);
        int driverStart = world.IndexOf(
            "private bool UpdateAndQueueWorldUnitGameTooltip(double now)", healthPushStart,
            StringComparison.Ordinal);
        Check(buildStart >= 0 && signatureStart > buildStart &&
              healthPushStart > signatureStart && driverStart > healthPushStart,
            "B6 world-unit snapshot/signature/health/driver seams are missing");
        string builder = world[buildStart..signatureStart];
        string signature = world[signatureStart..healthPushStart];
        string driver = world[driverStart..];
        Check(builder.Contains("_creatureQueryRecords.TryGetValue(unit.Entry, out query);",
                  StringComparison.Ordinal) &&
              builder.Contains("query is { Name.Length: > 0 }", StringComparison.Ordinal) &&
              builder.Contains("_creatureNames.GetValueOrDefault(unit.Entry, \"\")",
                  StringComparison.Ordinal) &&
              builder.Contains("query?.Subname", StringComparison.Ordinal) &&
              builder.Contains("!unit.Fields.IsPetOrCharm ? query.Rank : 0",
                  StringComparison.Ordinal) &&
              builder.Contains("FactionName: null", StringComparison.Ordinal) &&
              builder.Contains("WorldUnitPvpFlag", StringComparison.Ordinal) &&
              builder.Contains("WorldUnitSkinnableFlag", StringComparison.Ordinal) &&
              builder.Contains("query?.Civilian ?? false", StringComparison.Ordinal) &&
              builder.Contains("query?.RacialLeader ?? false", StringComparison.Ordinal) &&
              world.Contains("private const uint WorldUnitPvpFlag = 0x0000_1000u;",
                  StringComparison.Ordinal) &&
              world.Contains("private const uint WorldUnitSkinnableFlag = 0x0400_0000u;",
                  StringComparison.Ordinal) &&
              builder.Contains("GuidInfo.PetNumber(unit.Guid) is not null",
                  StringComparison.Ordinal) &&
              builder.Contains("ResolveCreatureOrPetName(unit, \"\")",
                  StringComparison.Ordinal),
            "B6 world snapshot lost template name/subtitle/rank/type/flags or explicit gaps");

        string[] staticFields =
        [
            "unit.Name", "unit.Subtitle", "unit.Level", "unit.PlayerLevel",
            "unit.Reaction", "unit.IsPlayer", "unit.Race", "unit.Class",
            "unit.CreatureTypeName", "unit.Rank", "unit.Dead", "unit.FactionName",
            "unit.Pvp", "unit.Skinnable", "unit.Civilian", "unit.RacialLeader",
        ];
        Check(staticFields.All(field => signature.Contains(field, StringComparison.Ordinal)) &&
              !signature.Contains("unit.Token", StringComparison.Ordinal) &&
              !signature.Contains("unit.Exists", StringComparison.Ordinal) &&
              !signature.Contains("unit.Health", StringComparison.Ordinal) &&
              !signature.Contains("unit.MaxHealth", StringComparison.Ordinal),
            "B6 static signature omitted a rendered field or included token/live-health state");

        int frameGuard = driver.IndexOf(
            "if (!_sharedTooltipFrameOpen || _sharedTooltipFrameResolved) return false;",
            StringComparison.Ordinal);
        int hoverIngress = driver.IndexOf("_hoveredGuid != 0", frameGuard,
            StringComparison.Ordinal);
        int ensureName = driver.IndexOf("EnsureUnitNameRequested(hovered);", hoverIngress,
            StringComparison.Ordinal);
        int build = driver.IndexOf("BuildWorldUnitGameTooltipSnapshot(hovered)", ensureName,
            StringComparison.Ordinal);
        int staticSnapshot = driver.IndexOf("WorldUnitGameTooltipStaticSignature(unit)", build,
            StringComparison.Ordinal);
        int show = driver.IndexOf("TryShowWorldUnitGameTooltip(hovered.Guid, unit,",
            staticSnapshot, StringComparison.Ordinal);
        int hoverRefresh = driver.IndexOf("TryRefreshSharedGameTooltipUnit(", show,
            StringComparison.Ordinal);
        int departure = driver.IndexOf("if (_worldUnitTooltip is not { } departing)",
            hoverRefresh, StringComparison.Ordinal);
        int fade = driver.IndexOf("BeginSharedGameTooltipFade(departing.Token, now,",
            departure, StringComparison.Ordinal);
        int departureEnd = driver.IndexOf("if (_worldUnitTooltip is not { } runtime",
            fade, StringComparison.Ordinal);
        int prepareRenderer = driver.IndexOf("PrepareSharedGameTooltipRenderer(rendererSnapshot)",
            fade, StringComparison.Ordinal);
        int queueRenderer = driver.IndexOf("QueueSharedGameTooltipRenderer(runtime.Token,",
            prepareRenderer, StringComparison.Ordinal);
        Check(frameGuard >= 0 && frameGuard < hoverIngress && hoverIngress < ensureName &&
              ensureName < build && build < staticSnapshot && staticSnapshot < show &&
              show < hoverRefresh && hoverRefresh < departure &&
              departure < fade && fade < departureEnd && departureEnd < prepareRenderer &&
              prepareRenderer < queueRenderer &&
              Count(driver, "TryRefreshSharedGameTooltipUnit(") == 1 &&
              !driver[departure..departureEnd].Contains("_entities",
                  StringComparison.Ordinal) &&
              !driver[departure..departureEnd].Contains("Fields.Health",
                  StringComparison.Ordinal) &&
              !driver[departure..departureEnd].Contains("WorldUnitGameTooltipHealthPush",
                  StringComparison.Ordinal) &&
              driver[departure..departureEnd].Contains("retained mouseover UnitState",
                  StringComparison.Ordinal) &&
              driver.Contains("shared.Lifecycle.FadeStartedAt is not null",
                  StringComparison.Ordinal) &&
              driver.Contains("_worldUnitTooltip.Signature != signature || fading",
                  StringComparison.Ordinal) &&
              driver.Contains("SharedGameTooltipLeavePolicy.Fade(" +
                  "GameTooltipUiLaw.WorldFadeSeconds)", StringComparison.Ordinal) &&
              driver.Contains("() => DrawPreparedSharedGameTooltip(prepared)",
                  StringComparison.Ordinal) &&
              driver.Contains("World-gameobject hover has its own",
                  StringComparison.Ordinal) &&
              !driver.Contains("PickUnit(", StringComparison.Ordinal) &&
              !driver.Contains("Raycast", StringComparison.Ordinal) &&
              !driver.Contains("_selectionGuid", StringComparison.Ordinal) &&
              !driver.Contains("_vplateHits", StringComparison.Ordinal) &&
              !driver.Contains("TryShowWorldGameObjectGameTooltip", StringComparison.Ordinal) &&
              !driver.Contains("HideSharedGameTooltip", StringComparison.Ordinal),
            "B6 driver lost hover/static/health/fade/requeue order or changed world ingress");

        int prepareStart = renderer.IndexOf(
            "private PreparedSharedGameTooltipRenderer? PrepareSharedGameTooltipRenderer(",
            StringComparison.Ordinal);
        int drawStart = renderer.IndexOf(
            "private void DrawPreparedSharedGameTooltip(PreparedSharedGameTooltipRenderer prepared)",
            prepareStart, StringComparison.Ordinal);
        Check(prepareStart >= 0 && drawStart > prepareStart,
            "B6 immutable shared renderer preparation/draw seams are missing");
        string prepare = renderer[prepareStart..drawStart];
        string draw = renderer[drawStart..];
        int defaultAnchor = prepare.IndexOf(
            "SharedGameTooltipDefaultAnchor(display, size, scale, managed)",
            StringComparison.Ordinal);
        int clampAnchor = prepare.IndexOf(
            "SharedGameTooltipClampToScreen(position, size, display)",
            StringComparison.Ordinal);
        int drawThicken = draw.IndexOf(
            "draw.AddRectFilled(prepared.Thicken.Minimum, prepared.Thicken.Maximum,",
            StringComparison.Ordinal);
        int drawBackdrop = draw.IndexOf("prepared.Skin.DrawBackdrop", StringComparison.Ordinal);
        Check(renderer.Contains("WowSkin Skin,", StringComparison.Ordinal) &&
              renderer.Contains("float Scale,", StringComparison.Ordinal) &&
              renderer.Contains("ReadOnlyCollection<PreparedSharedGameTooltipRow> Rows,",
                  StringComparison.Ordinal) &&
              renderer.Contains("PreparedSharedGameTooltipThicken Thicken,",
                  StringComparison.Ordinal) &&
              renderer.Contains("Vector2 inset = new(5f * scale);",
                  StringComparison.Ordinal) &&
              renderer.Contains("new Vector4(.09f, .09f, .19f, .4f * " +
                  "Math.Clamp(alpha, 0f, 1f))", StringComparison.Ordinal) &&
              renderer.Contains("Vector4 BackdropFillTint,", StringComparison.Ordinal) &&
              renderer.Contains("Vector4 BackdropEdgeTint,", StringComparison.Ordinal) &&
              renderer.Contains("new Vector4(.09f, .09f, .19f, clamped)",
                  StringComparison.Ordinal) &&
              renderer.Contains("new Vector4(1f, 1f, 1f, clamped)",
                  StringComparison.Ordinal) &&
              prepare.Contains("GameTooltipHeaderText", StringComparison.Ordinal) &&
              prepare.Contains("GameTooltipText", StringComparison.Ordinal) &&
              prepare.Contains("GameTooltipUiLaw.Padding * scale", StringComparison.Ordinal) &&
              prepare.Contains("GameTooltipUiLaw.LogicalRowGap * scale",
                  StringComparison.Ordinal) &&
              prepare.Contains("Enumerable.Range(36, 12)", StringComparison.Ordinal) &&
              prepare.Contains("Enumerable.Range(24, 12)", StringComparison.Ordinal) &&
              prepare.Contains("PetOrStanceShown: PetOrStanceActionBarVisible",
                  StringComparison.Ordinal) &&
              defaultAnchor >= 0 && defaultAnchor < clampAnchor &&
              renderer.Contains("if (left < 0f)", StringComparison.Ordinal) &&
              renderer.Contains("right -= left;", StringComparison.Ordinal) &&
              renderer.Contains("if (right > display.X)", StringComparison.Ordinal) &&
              renderer.Contains("left -= right - display.X;", StringComparison.Ordinal) &&
              renderer.Contains("if (bottom > display.Y)", StringComparison.Ordinal) &&
              renderer.Contains("top -= bottom - display.Y;", StringComparison.Ordinal) &&
              renderer.Contains("if (top < 0f)", StringComparison.Ordinal) &&
              renderer.Contains("bottom -= top;", StringComparison.Ordinal) &&
              prepare.Contains("_gameplayArt?.Handle(", StringComparison.Ordinal) &&
              prepare.Contains("Array.AsReadOnly(rows)", StringComparison.Ordinal) &&
              drawBackdrop >= 0 && drawBackdrop < drawThicken &&
              draw.Contains("float savedSkinScale = prepared.Skin.Scale;",
                  StringComparison.Ordinal) &&
              draw.Contains("prepared.Skin.Scale = prepared.Scale;",
                  StringComparison.Ordinal) &&
              draw.Contains("finally", StringComparison.Ordinal) &&
              draw.Contains("prepared.Skin.Scale = savedSkinScale;",
                  StringComparison.Ordinal) &&
              draw.Contains("prepared.BackdropFillTint, prepared.BackdropEdgeTint",
                  StringComparison.Ordinal) &&
              draw.Contains("prepared.Scale, row.Color", StringComparison.Ordinal) &&
              !draw.Contains("_skin", StringComparison.Ordinal) &&
              !draw.Contains("_gameplayArt", StringComparison.Ordinal) &&
              !draw.Contains("_entities", StringComparison.Ordinal) &&
              !draw.Contains("_actions", StringComparison.Ordinal) &&
              !draw.Contains("_sharedTooltip", StringComparison.Ordinal) &&
              !draw.Contains("NowSeconds", StringComparison.Ordinal) &&
              !draw.Contains("ImGui.GetIO", StringComparison.Ordinal) &&
              !draw.Contains("WorldEntity", StringComparison.Ordinal) &&
              !draw.Contains("ObjectFields", StringComparison.Ordinal),
            "B6 renderer did not freeze scale/skin/rows/geometry/texture before its callback");

        int combatStart = combat.IndexOf("private void DrawCombatHud()",
            StringComparison.Ordinal);
        int combatEnd = combat.IndexOf("private void DrawPlayerFrame()", combatStart,
            StringComparison.Ordinal);
        string hud = combat[combatStart..combatEnd];
        int worldMap = hud.IndexOf("if (_worldMapOpen)", StringComparison.Ordinal);
        int worldMapReturn = hud.IndexOf("return;", worldMap, StringComparison.Ordinal);
        int worldOffer = hud.IndexOf("UpdateAndQueueWorldUnitGameTooltip(NowSeconds());",
            StringComparison.Ordinal);
        int floating = hud.IndexOf("DrawFloatingCombatText();", StringComparison.Ordinal);
        Check(worldMap >= 0 && worldMap < worldMapReturn && worldMapReturn < worldOffer &&
              worldOffer < floating &&
              Count(hud, "UpdateAndQueueWorldUnitGameTooltip(NowSeconds());") == 1,
            "B6 world-unit offer escaped its post-WorldMap/pre-ordinary-HUD ingress seam");
    }

    private static void CheckB7B8RuntimePresentationSourceFence()
    {
        string client = Path.Combine(ClientConfig.FindRepoRoot(), "MSUIClient");
        string law = SourceText.Read(Path.Combine(client, "Engine", "UI",
            "GameTooltipUiLaw.cs"));
        string coordinator = SourceText.Read(Path.Combine(client, "Program.GameTooltip.cs"));
        string renderer = SourceText.Read(
            Path.Combine(client, "Program.GameTooltip.Renderer.cs"));

        Check(law.Contains("ShowSilver: silver > 0", StringComparison.Ordinal) &&
              law.Contains("ShowCopper: copper > 0 || copperValue == 0",
                  StringComparison.Ordinal) &&
              !law.Contains("if (copperValue == 0) return null", StringComparison.Ordinal) &&
              law.Contains("if (silver > 0 || gold > 0) result += $\"{silver}s \";",
                  StringComparison.Ordinal) &&
              law.Contains("public const string MoneyFontObject = \"NumberFontNormal\";",
                  StringComparison.Ordinal) &&
              law.Contains("public const float MoneyCoinSize = 13f;",
                  StringComparison.Ordinal) &&
              law.Contains("public const float MoneyCoinGap = 4f;",
                  StringComparison.Ordinal) &&
              law.Contains("public const float MoneyRowInset = 4f;",
                  StringComparison.Ordinal) &&
              law.Contains("GameTooltipCoinKind.Gold => new(0f, 0f, .25f, 1f)",
                  StringComparison.Ordinal) &&
              law.Contains("GameTooltipCoinKind.Silver => new(.25f, 0f, .5f, 1f)",
                  StringComparison.Ordinal) &&
              law.Contains("GameTooltipCoinKind.Copper => new(.5f, 0f, .75f, 1f)",
                  StringComparison.Ordinal) &&
              law.Contains("float x = MoneyRowInset * scale;", StringComparison.Ordinal) &&
              law.Contains("float frameWidth = numberWidth + MoneyCoinSize * scale;",
                  StringComparison.Ordinal) &&
              law.Contains("x += frameWidth + MoneyCoinGap * scale;",
                  StringComparison.Ordinal),
            "B7 money semantic/UV/measured-slot laws drifted or MoneyString was collapsed");

        Check(law.Contains("public const float NewbieWrapWidth = 260f;",
                  StringComparison.Ordinal) &&
              law.Contains("public const float WrapWidthEpsilon = .25f;",
                  StringComparison.Ordinal) &&
              law.Contains("words.Add((lead, paragraph[wordStart..cursor]));",
                  StringComparison.Ordinal) &&
              law.Contains("string candidate = current.Length == 0 ? word : current + lead + word;",
                  StringComparison.Ordinal) &&
              law.Contains("LastFittingGlyph(remainder, maximumWidth, measureWidth)",
                  StringComparison.Ordinal) &&
              law.Contains("foreach (Rune rune in text.EnumerateRunes())",
                  StringComparison.Ordinal) &&
              law.Contains("width <= maximumWidth + WrapWidthEpsilon",
                  StringComparison.Ordinal),
            "B8 wrap law lost whitespace, epsilon, Unicode-glyph force-break, or no-progress bail");

        int prepareStart = renderer.IndexOf(
            "private PreparedSharedGameTooltipRenderer? PrepareSharedGameTooltipRenderer(",
            StringComparison.Ordinal);
        int drawStart = renderer.IndexOf(
            "private void DrawPreparedSharedGameTooltip(PreparedSharedGameTooltipRenderer prepared)",
            prepareStart, StringComparison.Ordinal);
        Check(prepareStart >= 0 && drawStart > prepareStart,
            "B7/B8 typed renderer preparation/draw seams are missing");
        string prepare = renderer[prepareStart..drawStart];
        string draw = renderer[drawStart..];
        int wrap = prepare.IndexOf("GameTooltipUiLaw.WrapText(snapshot.Lines[i].Text,",
            StringComparison.Ordinal);
        int wrapWidth = prepare.IndexOf("GameTooltipUiLaw.NewbieWrapWidth * scale", wrap,
            StringComparison.Ordinal);
        int wrapGroupHeight = prepare.IndexOf(
            "physicalTexts[i].Length * GameText.LinePitch(fontObject, scale)", wrapWidth,
            StringComparison.Ordinal);
        int moneyMeasure = prepare.IndexOf(
            "GameText.MeasureWidth(GameTooltipUiLaw.MoneyFontObject, text, scale)",
            StringComparison.Ordinal);
        int moneyGeometry = prepare.IndexOf(
            "GameTooltipUiLaw.MoneyRowGeometry(money, numberWidths, scale)", moneyMeasure,
            StringComparison.Ordinal);
        int moneyBlankRow = prepare.IndexOf("physicalTexts[moneyRowIndex] = [\"\"];",
            moneyGeometry, StringComparison.Ordinal);
        int moneyWidthFloor = prepare.IndexOf(
            "widths[moneyRowIndex] = moneyGeometry.ContentWidth;", moneyBlankRow,
            StringComparison.Ordinal);
        int logicalGap = prepare.IndexOf(
            "cursor += GameTooltipUiLaw.LogicalRowGap * scale;", moneyWidthFloor,
            StringComparison.Ordinal);
        int shellWidth = prepare.IndexOf(
            "widths.Max() + GameTooltipUiLaw.Padding * 2f * scale", logicalGap,
            StringComparison.Ordinal);
        int defaultAnchor = prepare.IndexOf(
            "SharedGameTooltipDefaultAnchor(display, size, scale, managed)", shellWidth,
            StringComparison.Ordinal);
        int clamp = prepare.IndexOf(
            "SharedGameTooltipClampToScreen(position, size, display)", defaultAnchor,
            StringComparison.Ordinal);
        Check(wrap >= 0 && wrap < wrapWidth && wrapWidth < wrapGroupHeight &&
              wrapGroupHeight < moneyMeasure && moneyMeasure < moneyGeometry &&
              moneyGeometry < moneyBlankRow && moneyBlankRow < moneyWidthFloor &&
              moneyWidthFloor < logicalGap && logicalGap < shellWidth &&
              shellWidth < defaultAnchor && defaultAnchor < clamp &&
              prepare.Contains(
                  "string fontObject = i == 0 ? \"GameTooltipHeaderText\" : \"GameTooltipText\";",
                  StringComparison.Ordinal) &&
              prepare.Contains("fontObjects[moneyRowIndex] = \"GameTooltipText\";",
                  StringComparison.Ordinal) &&
              prepare.Contains("new PreparedSharedGameTooltipPhysicalLine[physicalTexts[i].Length]",
                  StringComparison.Ordinal) &&
              prepare.Contains("rowTops[i] + line * linePitch", StringComparison.Ordinal),
            "B7/B8 renderer lost header/body fonts, grouped wrapping, money blank row, or shell floor");

        int moneyIconPaint = draw.IndexOf(
            "draw.AddImage((nint)prepared.MoneyTexture", StringComparison.Ordinal);
        int moneyNumberPaint = draw.IndexOf(
            "GameText.Draw(draw, GameTooltipUiLaw.MoneyFontObject, coin.AmountText",
            StringComparison.Ordinal);
        Check(renderer.Contains(
                  "ReadOnlyCollection<PreparedSharedGameTooltipPhysicalLine> PhysicalLines",
                  StringComparison.Ordinal) &&
              renderer.Contains(
                  "ReadOnlyCollection<PreparedSharedGameTooltipMoneyCoin> MoneyCoins",
                  StringComparison.Ordinal) &&
              prepare.Contains(
                  "(heights[moneyRowIndex] - GameTooltipUiLaw.MoneyCoinSize * scale) * .5f",
                  StringComparison.Ordinal) &&
              prepare.Contains(
                  "GameText.BoxCenteredTop(GameTooltipUiLaw.MoneyFontObject,",
                  StringComparison.Ordinal) &&
              prepare.Contains("new Vector2(coin.TexCoords.Left, coin.TexCoords.Top)",
                  StringComparison.Ordinal) &&
              prepare.Contains("_gameplayArt?.Handle(GameTooltipUiLaw.MoneyTexturePath)",
                  StringComparison.Ordinal) &&
              draw.Contains(
                  "foreach (PreparedSharedGameTooltipPhysicalLine line in row.PhysicalLines)",
                  StringComparison.Ordinal) &&
              draw.Contains("GameTooltipUiLaw.MoneyFontObject, coin.AmountText",
                  StringComparison.Ordinal) &&
              draw.Contains("coin.UvMinimum, coin.UvMaximum, coin.Tint",
                  StringComparison.Ordinal) &&
              moneyIconPaint >= 0 && moneyIconPaint < moneyNumberPaint &&
              !draw.Contains("_sharedTooltip", StringComparison.Ordinal) &&
              !draw.Contains("_actions", StringComparison.Ordinal) &&
              !draw.Contains("_entities", StringComparison.Ordinal) &&
              !draw.Contains("_gameplayArt", StringComparison.Ordinal) &&
              !draw.Contains("ImGui.GetIO", StringComparison.Ordinal),
            "B7/B8 prepared callback retained mutable state or lost measured money paint data");

        int responderStart = coordinator.IndexOf(
            "private bool TryShowNewbieGameTooltip(", StringComparison.Ordinal);
        int responderEnd = coordinator.IndexOf(
            "private GameTooltipRuntimeSnapshot SharedGameTooltipSnapshot()", responderStart,
            StringComparison.Ordinal);
        Check(responderStart >= 0 && responderEnd > responderStart,
            "B8 conditional newbie responder seam is missing");
        string responder = coordinator[responderStart..responderEnd];
        int frameGuard = responder.IndexOf(
            "if (!_sharedTooltipFrameOpen || _sharedTooltipFrameResolved) return false;",
            StringComparison.Ordinal);
        int semantic = responder.IndexOf("GameTooltipUiLaw.NewbieTip(showDetailedTips,",
            frameGuard, StringComparison.Ordinal);
        int ownerGeometryGate = responder.IndexOf(
            "newbie.Anchor != GameTooltipAnchorKind.DefaultBottomRight", semantic,
            StringComparison.Ordinal);
        int skinGate = responder.IndexOf("if (_skin is null) return false;", ownerGeometryGate,
            StringComparison.Ordinal);
        int claim = responder.IndexOf("token = ClaimSharedGameTooltip(owner);", skinGate,
            StringComparison.Ordinal);
        int publish = responder.IndexOf("PublishSharedGameTooltip(token,", claim,
            StringComparison.Ordinal);
        int immutablePrepare = responder.IndexOf(
            "PrepareSharedGameTooltipRenderer(SharedGameTooltipSnapshot())", publish,
            StringComparison.Ordinal);
        int queue = responder.IndexOf("QueueSharedGameTooltipRenderer(token,", immutablePrepare,
            StringComparison.Ordinal);
        int immediate = responder.IndexOf("SharedGameTooltipLeavePolicy.ImmediateHide", queue,
            StringComparison.Ordinal);
        int drawPrepared = responder.IndexOf("() => DrawPreparedSharedGameTooltip(prepared)",
            immediate, StringComparison.Ordinal);
        Check(frameGuard >= 0 && frameGuard < semantic && semantic < ownerGeometryGate &&
              ownerGeometryGate < skinGate && skinGate < claim && claim < publish &&
              publish < immutablePrepare && immutablePrepare < queue && queue < immediate &&
              immediate < drawPrepared,
            "B8 responder is not one guarded Claim/Publish/immutable-Prepare/Immediate-Queue offer");

        string resolver = coordinator[coordinator.IndexOf(
            "private bool ResolveAndDrawSharedGameTooltip()", StringComparison.Ordinal)..
            coordinator.IndexOf("private void EndSharedGameTooltipFrame()",
                StringComparison.Ordinal)];
        int clearPending = resolver.IndexOf("_pendingSharedTooltipRenderer = null;",
            StringComparison.Ordinal);
        int paint = resolver.IndexOf("pending.Renderer();", StringComparison.Ordinal);
        Check(clearPending >= 0 && clearPending < paint &&
              Count(resolver, "pending.Renderer();") == 1,
            "B8 responder callbacks can paint more than once or survive reentrant resolution");

        string[] programFiles = Directory.GetFiles(client, "Program*.cs",
                SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetFiles(Path.Combine(client, "GameLoop"), "GameLoop*.cs",
                SearchOption.AllDirectories))
            .ToArray();
        string allPrograms = string.Join('\n', programFiles.Select(File.ReadAllText));
        string mail = SourceText.Read(Path.Combine(client, "Program.Mail.cs"));
        string inventory = SourceText.Read(Path.Combine(client, "Program.Inventory.cs"));
        string vendor = SourceText.Read(Path.Combine(client, "Program.Vendor.cs")) +
                        SourceText.Read(Path.Combine(client, "Program.Vendor.Render.cs"));
        string minimap = SourceText.Read(Path.Combine(client, "Program.Minimap.cs"));
        string taxi = SourceText.Read(Path.Combine(client, "Program.Taxi.cs"));
        string actions = SourceText.Read(Path.Combine(client, "Program.ActionBars.cs"));
        string bindings = SourceText.Read(Path.Combine(client, "Program.Bindings.cs"));
        string settings = SourceText.Read(Path.Combine(client, "Program.Settings.cs"));
        Check(Count(allPrograms, "TryShowNewbieGameTooltip(") == 2 &&
              Count(allPrograms, "SetSharedGameTooltipMoney(") == 3 &&
              !settings.Contains("SHOW_NEWBIE_TIPS", StringComparison.Ordinal) &&
              !mail.Contains("SetSharedGameTooltipMoney", StringComparison.Ordinal) &&
              !inventory.Contains("SetSharedGameTooltipMoney", StringComparison.Ordinal) &&
              Count(vendor, "SetSharedGameTooltipMoney(") == 1 &&
              Count(taxi, "SetSharedGameTooltipMoney(") == 1 &&
              !minimap.Contains("SetSharedGameTooltipMoney", StringComparison.Ordinal) &&
              !actions.Contains("TryShowNewbieGameTooltip", StringComparison.Ordinal) &&
              !bindings.Contains("TryShowNewbieGameTooltip", StringComparison.Ordinal) &&
              mail.Contains("FormatMoney(row.Cod)", StringComparison.Ordinal) &&
              minimap.Contains("ImGui.SetTooltip(preparedName);", StringComparison.Ordinal),
            "B7/B8 invented a producer/setting or replaced preserved mail/item/minimap surfaces outside the source-pinned Merchant repair row");
    }

    private static void CheckOwnerGenerationAndClear()
    {
        GameTooltipLifecycleState empty = GameTooltipUiLaw.EmptyLifecycle;
        var ownerA = new GameTooltipOwnerKey("party-member", 1);
        var ownerB = new GameTooltipOwnerKey("world-unit", 0x42);

        GameTooltipLifecycleTransition first = GameTooltipUiLaw.Claim(empty, ownerA);
        Check(first.Accepted && !first.Replaced && first.Token.Generation == 1 &&
              first.State.Owner == ownerA && first.State.Visible && first.State.Alpha == 1f &&
              first.ClearScope == GameTooltipClearScope.All,
            "GameTooltip first-owner generation/full-clear drift");

        GameTooltipLifecycleTransition retained = GameTooltipUiLaw.Claim(first.State, ownerA);
        Check(retained.Token == first.Token && !retained.Replaced &&
              retained.ClearScope == GameTooltipClearScope.None,
            "GameTooltip exact-owner reclaim changed generation or cleared content");

        GameTooltipLifecycleTransition replaced = GameTooltipUiLaw.Claim(retained.State, ownerB);
        Check(replaced.Replaced && replaced.Token.Generation == 2 &&
              replaced.State.Owner == ownerB && replaced.ClearScope == GameTooltipClearScope.All,
            "GameTooltip owner replacement was not atomic/generational");

        GameTooltipLifecycleTransition staleClear =
            GameTooltipUiLaw.ClearContent(replaced.State, first.Token);
        GameTooltipLifecycleTransition staleHide = GameTooltipUiLaw.Hide(replaced.State, first.Token);
        GameTooltipLifecycleTransition staleFade =
            GameTooltipUiLaw.BeginFade(replaced.State, first.Token, 10);
        Check(!staleClear.Accepted && !staleHide.Accepted && !staleFade.Accepted &&
              staleClear.State == replaced.State && staleHide.State == replaced.State &&
              staleFade.State == replaced.State,
            "stale GameTooltip owner mutated its replacement");

        GameTooltipLifecycleTransition clear =
            GameTooltipUiLaw.ClearContent(replaced.State, replaced.Token);
        Check(clear.Accepted && clear.State.Owner == ownerB && clear.State.Generation == 2 &&
              clear.ClearScope == GameTooltipClearScope.All,
            "GameTooltip content clear dropped its live owner or omitted a channel");

        GameTooltipLifecycleTransition hide = GameTooltipUiLaw.Hide(clear.State, replaced.Token);
        Check(hide.Accepted && hide.State.Owner is null && !hide.State.Visible &&
              hide.State.Generation == 2 && hide.State.Alpha == 0f &&
              hide.ClearScope == GameTooltipClearScope.All,
            "GameTooltip real hide retained owner/content or reset generation");
        GameTooltipLifecycleTransition third = GameTooltipUiLaw.Claim(hide.State, ownerA);
        Check(third.Token.Generation == 3,
            "GameTooltip owner generation reused after hide");

        ExpectReject(() => GameTooltipUiLaw.Claim(empty, new("", 1)),
            "GameTooltip accepted an owner without a surface");
    }

    private static void CheckFadeLifecycle()
    {
        GameTooltipLifecycleTransition claim = GameTooltipUiLaw.Claim(
            GameTooltipUiLaw.EmptyLifecycle, new("world-unit", 1));
        GameTooltipLifecycleTransition fade =
            GameTooltipUiLaw.BeginFade(claim.State, claim.Token, 20);
        Check(fade.Accepted && fade.State.FadeStartedAt == 20 &&
              fade.State.FadeSeconds == GameTooltipUiLaw.WorldFadeSeconds &&
              fade.State.Alpha == 1f,
            "GameTooltip fade did not arm at full alpha");

        GameTooltipLifecycleTransition duplicate =
            GameTooltipUiLaw.BeginFade(fade.State, fade.Token, 20.1);
        Check(duplicate.State.FadeStartedAt == 20,
            "GameTooltip duplicate fade arm replaced its original timestamp");

        GameTooltipLifecycleTransition half = GameTooltipUiLaw.TickFade(fade.State, 20.25);
        Check(half.Accepted && Near(half.State.Alpha, .5f) && half.State.Visible &&
              half.ClearScope == GameTooltipClearScope.None,
            "GameTooltip half-fade alpha/visibility drift");

        GameTooltipLifecycleTransition resurrect = GameTooltipUiLaw.Show(half.State, half.Token);
        Check(resurrect.Accepted && resurrect.State.FadeStartedAt is null &&
              resurrect.State.Alpha == 1f && resurrect.State.Visible,
            "fresh GameTooltip content did not cancel fade at full alpha");

        GameTooltipLifecycleTransition rearmed =
            GameTooltipUiLaw.BeginFade(resurrect.State, resurrect.Token, 30);
        GameTooltipLifecycleTransition finished = GameTooltipUiLaw.TickFade(rearmed.State, 30.5);
        Check(finished.Accepted && finished.State.Owner is null && !finished.State.Visible &&
              finished.State.Alpha == 0f && finished.ClearScope == GameTooltipClearScope.All,
            "GameTooltip terminal fade did not perform a real hide/full clear");

        GameTooltipLifecycleTransition immediate = GameTooltipUiLaw.BeginFade(
            GameTooltipUiLaw.Claim(GameTooltipUiLaw.EmptyLifecycle, new("micro", 1)).State,
            new GameTooltipOwnerToken(new("micro", 1), 1), 0, 0);
        Check(immediate.State.Owner is null && immediate.ClearScope == GameTooltipClearScope.All,
            "GameTooltip nonpositive fade duration did not hide immediately");
    }

    private static void CheckMoneyAndNewbieContent()
    {
        (uint Copper, GameTooltipCoin[] Visible)[] exactMoney =
        [
            (0, [new(GameTooltipCoinKind.Copper, 0)]),
            (1, [new(GameTooltipCoinKind.Copper, 1)]),
            (100, [new(GameTooltipCoinKind.Silver, 1)]),
            (101, [new(GameTooltipCoinKind.Silver, 1),
                   new(GameTooltipCoinKind.Copper, 1)]),
            (10_000, [new(GameTooltipCoinKind.Gold, 1)]),
            (10_001, [new(GameTooltipCoinKind.Gold, 1),
                      new(GameTooltipCoinKind.Copper, 1)]),
            (10_100, [new(GameTooltipCoinKind.Gold, 1),
                      new(GameTooltipCoinKind.Silver, 1)]),
            (10_101, [new(GameTooltipCoinKind.Gold, 1),
                      new(GameTooltipCoinKind.Silver, 1),
                      new(GameTooltipCoinKind.Copper, 1)]),
        ];
        Check(exactMoney.All(test => GameTooltipUiLaw.Money(test.Copper) is { } money &&
                  money.VisibleCoins().SequenceEqual(test.Visible)),
            "GameTooltip money did not collapse every nonzero zero-denomination or retain 0c");
        Check(GameTooltipUiLaw.MoneyString(0) == "0c" &&
              GameTooltipUiLaw.MoneyString(100) == "1s 0c" &&
              GameTooltipUiLaw.MoneyString(10_000) == "1g 0s 0c" &&
              GameTooltipUiLaw.MoneyString(10_101) == "1g 1s 1c",
            "GameTooltip verbose MoneyString was incorrectly collapsed with the coin-row law");

        FontObjectSpec moneyFont = FontObjectLaw.Get(GameTooltipUiLaw.MoneyFontObject);
        Check(GameTooltipUiLaw.MoneyFontObject == "NumberFontNormal" &&
              moneyFont.Height == 14f && moneyFont.Color == 0xffffffff &&
              moneyFont.ShadowColor is null && moneyFont.Outline == 1 &&
              GameTooltipUiLaw.MoneyTexturePath ==
                  @"Interface\MoneyFrame\UI-MoneyIcons" &&
              GameTooltipUiLaw.MoneyCoinSize == 13f &&
              GameTooltipUiLaw.MoneyCoinGap == 4f &&
              GameTooltipUiLaw.MoneyRowInset == 4f,
            "GameTooltip money font, icon, size, gap, or inset law drift");

        GameTooltipMoneyParts allMoney = GameTooltipUiLaw.Money(10_101)!.Value;
        GameTooltipMoneyRowGeometry allGeometry = GameTooltipUiLaw.MoneyRowGeometry(
            allMoney, [7f, 11f, 13f]);
        Check(allGeometry.ContentWidth == 86f &&
              allGeometry.Coins.Select(coin =>
                  (coin.NumberX, coin.NumberWidth, coin.IconX, coin.FrameWidth)).SequenceEqual(
              [
                  (4f, 7f, 11f, 20f),
                  (28f, 11f, 39f, 24f),
                  (56f, 13f, 69f, 26f),
              ]),
            "GameTooltip measured money slot geometry lost inset/number/icon/gap/trailing width");
        GameTooltipMoneyRowGeometry scaledGeometry = GameTooltipUiLaw.MoneyRowGeometry(
            allMoney, [14f, 22f, 26f], 2f);
        Check(scaledGeometry.ContentWidth == 172f &&
              scaledGeometry.Coins[0].NumberX == 8f &&
              scaledGeometry.Coins[1].NumberX == 56f &&
              scaledGeometry.Coins[2].NumberX == 112f,
            "GameTooltip money geometry did not scale authored 13/4/4 constants exactly once");

        GameTooltipMoneyRowGeometry goldCopper = GameTooltipUiLaw.MoneyRowGeometry(
            GameTooltipUiLaw.Money(10_001)!.Value, [8f, 9f]);
        GameTooltipMoneyRowGeometry goldSilver = GameTooltipUiLaw.MoneyRowGeometry(
            GameTooltipUiLaw.Money(10_100)!.Value, [8f, 9f]);
        Check(goldCopper.Coins[0].TexCoords == new GameTooltipCoinTexCoords(0f, 0f, .25f, 1f) &&
              goldCopper.Coins[1].TexCoords == new GameTooltipCoinTexCoords(.5f, 0f, .75f, 1f) &&
              goldSilver.Coins[1].TexCoords == new GameTooltipCoinTexCoords(.25f, 0f, .5f, 1f),
            "GameTooltip runtime coin UV followed physical slot instead of denomination");

        object zeroGame = RuntimeHelpers.GetUninitializedObject(typeof(GameLoop));
        GameTooltipOwnerToken zeroToken = Invoke<GameTooltipOwnerToken>(zeroGame,
            "ClaimSharedGameTooltip", new GameTooltipOwnerKey("clinical-money", 1));
        Check(Invoke<bool>(zeroGame, "SetSharedGameTooltipMoney", zeroToken, 0u) &&
              Property<GameTooltipMoneyParts?>(
                  Invoke<object>(zeroGame, "SharedGameTooltipSnapshot"), "Money")?.VisibleCoins()
                  .SequenceEqual([new GameTooltipCoin(GameTooltipCoinKind.Copper, 0)]) == true &&
              Invoke<bool>(zeroGame, "ClearSharedGameTooltip", zeroToken) &&
              Property<GameTooltipMoneyParts?>(
                  Invoke<object>(zeroGame, "SharedGameTooltipSnapshot"), "Money") is null,
            "GameTooltip conflated a real zero-copper row with the explicit money clear");

        object staleMoneyGame = RuntimeHelpers.GetUninitializedObject(typeof(GameLoop));
        GameTooltipOwnerToken staleMoney = Invoke<GameTooltipOwnerToken>(staleMoneyGame,
            "ClaimSharedGameTooltip", new GameTooltipOwnerKey("clinical-money", 1));
        _ = Invoke<GameTooltipOwnerToken>(staleMoneyGame, "ClaimSharedGameTooltip",
            new GameTooltipOwnerKey("clinical-replacement", 2));
        Check(!Invoke<bool>(staleMoneyGame, "SetSharedGameTooltipMoney", staleMoney, 1u) &&
              Property<GameTooltipMoneyParts?>(
                  Invoke<object>(staleMoneyGame, "SharedGameTooltipSnapshot"), "Money") is null,
            "GameTooltip stale owner wrote a money row into its replacement generation");

        GameTooltipNewbieContent detailed = GameTooltipUiLaw.NewbieTip(true,
            "Performance", "Shows latency and frame rate.", noNormalText: false);
        Check(detailed.Visible && detailed.Anchor == GameTooltipAnchorKind.DefaultBottomRight &&
              detailed.Lines.Length == 2 && detailed.Lines[0].Text == "Performance" &&
              detailed.Lines[0].Tone == GameTooltipTextTone.OwnerColor &&
              !detailed.Lines[0].Wrap && detailed.Lines[1].Wrap &&
              detailed.Lines[1].Tone == GameTooltipTextTone.Normal,
            "detailed GameTooltip newbie label/detail/default-anchor drift");

        GameTooltipNewbieContent explanationOnly = GameTooltipUiLaw.NewbieTip(true,
            null, "Rested experience explanation", noNormalText: true);
        Check(explanationOnly.Visible && explanationOnly.Lines.Length == 1 &&
              explanationOnly.Lines[0].Wrap &&
              explanationOnly.Lines[0].Tone == GameTooltipTextTone.OwnerColor,
            "detailed no-normal-text GameTooltip branch drift");

        GameTooltipNewbieContent terse = GameTooltipUiLaw.NewbieTip(false,
            "Performance", "ignored", noNormalText: false);
        GameTooltipNewbieContent hidden = GameTooltipUiLaw.NewbieTip(false,
            "Experience", "ignored", noNormalText: true);
        Check(terse.Visible && terse.Anchor == GameTooltipAnchorKind.OwnerRight &&
              terse.Lines.Length == 1 && terse.Lines[0].Text == "Performance" &&
              !hidden.Visible && hidden.Lines.Length == 0,
            "terse/hidden GameTooltip newbie branch drift");

        Check(GameTooltipUiLaw.NewbieWrapWidth == 260f &&
              GameTooltipUiLaw.WrapWidthEpsilon == .25f &&
              GameTooltipUiLaw.Padding == 10f && GameTooltipUiLaw.LogicalRowGap == 2f &&
              GameTooltipUiLaw.WrapText("12345678 12345678 12345678 x", 260f,
                  text => text.Length * 10f).SequenceEqual(
                  ["12345678 12345678 12345678", "x"]) &&
              GameTooltipUiLaw.WrapText("Hello.  World", 20f,
                  text => text.Length).SequenceEqual(["Hello.  World"]) &&
              GameTooltipUiLaw.WrapText("Hello.  World", 7f,
                  text => text.Length).SequenceEqual(["Hello.", "World"]) &&
              GameTooltipUiLaw.WrapText("   ", 260f,
                  text => text.Length).SequenceEqual(["   "]) &&
              GameTooltipUiLaw.WrapText("Supercalifragilistic ok", 8f,
                  text => text.Length).SequenceEqual(["Supercal", "ifragili", "stic ok"]) &&
              GameTooltipUiLaw.WrapText("34", 2f,
                  text => text.Length == 2 ? 2.2f : text.Length).SequenceEqual(["34"]) &&
              GameTooltipUiLaw.WrapText("abc", .5f,
                  text => text.Length).Length == 0,
            "detailed GameTooltip newbie wrapping lost its exact 260-pixel logical ceiling");

        object responder = RuntimeHelpers.GetUninitializedObject(typeof(GameLoop));
        GameTooltipOwnerToken retainedToken = Invoke<GameTooltipOwnerToken>(responder,
            "ClaimSharedGameTooltip", new GameTooltipOwnerKey("retained-owner", 7));
        Check(Invoke<bool>(responder, "PublishSharedGameTooltip", retainedToken,
                  new GameTooltipContent(GameTooltipAnchorKind.DefaultBottomRight,
                      [new("Retained", GameTooltipTextTone.White)]), (Vector2?)null),
            "newbie conditional-responder fixture could not publish its retained owner");
        object beforeOffer = Invoke<object>(responder, "SharedGameTooltipSnapshot");
        object?[] outOfFrameArguments =
        [
            new GameTooltipOwnerKey("detailed-out-of-frame", 8), true, "Performance",
            "Shows latency and frame rate.", false, default(GameTooltipOwnerToken),
        ];
        Check(!Invoke<bool>(responder, "TryShowNewbieGameTooltip", outOfFrameArguments) &&
              !((GameTooltipOwnerToken)outOfFrameArguments[5]!).IsValid &&
              SameSnapshot(beforeOffer, Invoke<object>(responder, "SharedGameTooltipSnapshot")),
            "out-of-frame newbie responder mutated ownership or published content");

        InvokeVoid(responder, "BeginSharedGameTooltipFrame", 90d);
        object?[] terseArguments =
        [
            new GameTooltipOwnerKey("future-owner-right", 9), false, "Performance", "Ignored",
            false, default(GameTooltipOwnerToken),
        ];
        Check(!Invoke<bool>(responder, "TryShowNewbieGameTooltip", terseArguments) &&
              !((GameTooltipOwnerToken)terseArguments[5]!).IsValid &&
              SameSnapshot(beforeOffer,
                  Invoke<object>(responder, "SharedGameTooltipSnapshot")),
            "newbie OwnerRight responder claimed/replaced without immutable owner geometry");
        InvokeVoid(responder, "EndSharedGameTooltipFrame");
    }

    private static void CheckUnitContentAndLiveToken()
    {
        GameTooltipUnitSnapshot creature = Unit(level: 1, playerLevel: 10, reaction: 2,
            rank: 2, subtitle: "Stable Master", creatureType: "Beast", faction: "Defias",
            pvp: true, skinnable: true, civilian: true, leader: true,
            health: 150, maxHealth: 100);
        GameTooltipContent? content = GameTooltipUiLaw.UnitContent(creature);
        Check(content is not null && content.Anchor == GameTooltipAnchorKind.DefaultBottomRight &&
              content.LiveUnitToken == "mouseover" && content.UnitReaction == 2 &&
              content.Lines.Select(x => x.Text).SequenceEqual(
                  ["Gnoll", "Stable Master", "Level 1 Beast (Elite)", "Defias",
                   "PvP", "Skinnable", "Civilian", "Leader"]) &&
              content.Health == new GameTooltipHealthState(true, 100, 100),
            "GameTooltip unit line order/gates/health clamp drift");

        Check(GameTooltipUiLaw.LevelReadsUnknown(Unit(level: 20, playerLevel: 10, reaction: 2)) &&
              !GameTooltipUiLaw.LevelReadsUnknown(Unit(level: 19, playerLevel: 10, reaction: 2)) &&
              !GameTooltipUiLaw.LevelReadsUnknown(Unit(level: 20, playerLevel: 10, reaction: 4)) &&
              GameTooltipUiLaw.LevelReadsUnknown(Unit(level: 1, rank: 3)) &&
              GameTooltipUiLaw.LevelReadsUnknown(Unit(level: 0)) &&
              !GameTooltipUiLaw.LevelReadsUnknown(Unit(level: 0, player: true)),
            "GameTooltip unknown-level hostile/boss/player gate drift");
        Check(GameTooltipUiLaw.RankWord(1) == "Elite" &&
              GameTooltipUiLaw.RankWord(2) == "Elite" &&
              GameTooltipUiLaw.RankWord(3) == "Boss" &&
              GameTooltipUiLaw.RankWord(4) is null,
            "GameTooltip rank-word table drift");
        Check(GameTooltipUiLaw.UnitLevelLine(Unit(level: 12, player: true,
                  race: "Human", @class: "Mage")) == "Level 12 Human Mage (Player)" &&
              GameTooltipUiLaw.UnitLevelLine(Unit(level: 12, dead: true, rank: 1)) ==
                  "Level 12 Corpse (Elite)" &&
              GameTooltipUiLaw.UnitLevelLine(Unit(level: 12, reaction: 5,
                  creatureType: "Humanoid")) == "Level 12",
            "GameTooltip player/corpse/friendly-creature level-line slots drift");

        Check(GameTooltipUiLaw.UnitContent(Unit(exists: false)) is null &&
              GameTooltipUiLaw.UnitHealth(Unit(exists: false)) == GameTooltipHealthState.Hidden &&
              GameTooltipUiLaw.UnitHealth(Unit(health: 0, maxHealth: 0)) ==
                  new GameTooltipHealthState(true, 1, 0),
            "GameTooltip absent/zero-maximum health law drift");

        Check(GameTooltipUiLaw.TryLiveUnitHealth("mouseover", Unit(health: 40, maxHealth: 80),
                  out GameTooltipHealthState pushed) &&
              pushed == new GameTooltipHealthState(true, 80, 40) &&
              !GameTooltipUiLaw.TryLiveUnitHealth("party1", Unit(), out _) &&
              GameTooltipUiLaw.TryLiveUnitHealth("mouseover", Unit(exists: false),
                  out GameTooltipHealthState removed) && removed == GameTooltipHealthState.Hidden,
            "GameTooltip live-token health-only refresh/mismatch/removal drift");
    }

    private static void CheckGameObjectResponderContent()
    {
        var gameObject = new GameTooltipGameObjectSnapshot("Locked Chest",
        [
            new("Requires Golden Key", GameTooltipTextTone.White),
            new("Locked", GameTooltipTextTone.Red),
            new("Lockpicking 75", GameTooltipTextTone.LockOpen),
        ], CursorAnchored: true);
        GameTooltipContent content = GameTooltipUiLaw.GameObjectContent(gameObject);
        Check(content.Anchor == GameTooltipAnchorKind.Cursor &&
              content.Lines.Select(x => (x.Text, x.Tone)).SequenceEqual(
              [
                  ("Locked Chest", GameTooltipTextTone.Gold),
                  ("Requires Golden Key", GameTooltipTextTone.White),
                  ("Locked", GameTooltipTextTone.Red),
                  ("Lockpicking 75", GameTooltipTextTone.LockOpen),
              ]),
            "GameTooltip GO name/requirement tint/cursor-anchor drift");
        Check(GameTooltipUiLaw.GameObjectContent(gameObject with { CursorAnchored = false }).Anchor ==
              GameTooltipAnchorKind.DefaultBottomRight,
            "GameTooltip GO corner-anchor branch drift");
        ExpectReject(() => GameTooltipUiLaw.GameObjectContent(gameObject with
            {
                Lines = [new("invented tone", GameTooltipTextTone.Green)],
            }), "GameTooltip GO accepted a non-reference requirement tint");
    }

    private static void CheckManagedOffsets()
    {
        var all = new UiParentManagedState(true, true, true, true, true, true, true);
        Check(Placement(UiParentManagedConsumer.MultiBarBottomLeft, all, 0, 21) &&
              Placement(UiParentManagedConsumer.GroupLoot, all, 0, 153) &&
              Placement(UiParentManagedConsumer.Tutorial, all, 0, 153) &&
              Placement(UiParentManagedConsumer.FramerateLabel, all, 0, 157) &&
              Placement(UiParentManagedConsumer.CastingBar, all, 0, 149),
            "UIParent bottom-stack managed offsets drift");
        Check(Placement(UiParentManagedConsumer.ChatLeft, all, 32, 146) &&
              Placement(UiParentManagedConsumer.ChatRight, all, -120, 106) &&
              Placement(UiParentManagedConsumer.ShapeshiftBar, all, 30, 49),
            "UIParent chat/stance managed offsets or +23 overlap law drift");
        Check(Placement(UiParentManagedConsumer.ContainerOffsetX, all, 90, 0) &&
              Placement(UiParentManagedConsumer.ContainerOffsetY, all, 0, 129) &&
              Placement(UiParentManagedConsumer.BattlefieldTabOffsetY, all, 0, 259) &&
              Placement(UiParentManagedConsumer.PetActionBarOffsetY, all, 0, 144),
            "UIParent managed variable offsets drift");

        var rightOnly = all with { RightLeftShown = false };
        Check(Placement(UiParentManagedConsumer.ChatRight, rightOnly, -75, 106) &&
              Placement(UiParentManagedConsumer.ContainerOffsetX, rightOnly, 45, 0),
            "UIParent right-left precedence/right-right offset drift");
        UiParentManagedPlacement chat = UiParentUiLaw.Resolve(
            UiParentManagedConsumer.ChatLeft, all);
        Check(chat.Kind == UiParentManagedValueKind.FrameAnchor &&
              chat.AnchorTo == "UIParent" && chat.Point == UiParentAnchorPoint.BottomLeft &&
              chat.RelativePoint == UiParentAnchorPoint.BottomLeft &&
              UiParentUiLaw.Resolve(UiParentManagedConsumer.ContainerOffsetX, all).Kind ==
                  UiParentManagedValueKind.XVariable,
            "UIParent managed anchor/value-kind drift");
    }

    private static void CheckBindingText()
    {
        var names = new Dictionary<string, string>
        {
            ["KEY_2"] = "Two",
            ["KEY_SPACE"] = "Space",
            ["KEY_SPACE_MAC"] = "Mac Space",
        };
        string? Localized(string key) => names.GetValueOrDefault(key);

        Check(UiParentUiLaw.BindingText(null) == "" &&
              UiParentUiLaw.BindingText("SHIFT-2", "KEY_", true, Localized) == "s-Two" &&
              UiParentUiLaw.BindingText("CTRL-SHIFT-2", "KEY_", true, Localized) == "·" &&
              UiParentUiLaw.BindingText("CTRL-SPACE", "KEY_", false, Localized, "deDE") ==
                  "STRG-Space" &&
              UiParentUiLaw.BindingText("ALT-SPACE", "KEY_", false, Localized,
                  macClient: true) == "ALT-Mac Space" &&
              UiParentUiLaw.BindingText("BUTTON1", "KEY_", false, Localized) == "BUTTON1",
            "UIParent binding full/abbreviated/dot/deDE/Mac/raw fallback drift");
    }

    private static GameTooltipUnitSnapshot Unit(
        string token = "mouseover",
        bool exists = true,
        string name = "Gnoll",
        string? subtitle = null,
        int level = 10,
        uint playerLevel = 10,
        int reaction = 4,
        bool player = false,
        string? race = null,
        string? @class = null,
        string? creatureType = null,
        uint rank = 0,
        bool dead = false,
        string? faction = null,
        bool pvp = false,
        bool skinnable = false,
        bool civilian = false,
        bool leader = false,
        uint health = 10,
        uint maxHealth = 10)
        => new(token, exists, name, subtitle, level, playerLevel, reaction, player,
            race, @class, creatureType, rank, dead, faction, pvp, skinnable,
            civilian, leader, health, maxHealth);

    private static bool Placement(UiParentManagedConsumer consumer,
        in UiParentManagedState state, float x, float y)
    {
        UiParentManagedPlacement p = UiParentUiLaw.Resolve(consumer, state);
        return p.X == x && p.Y == y;
    }

    private static bool Near(float left, float right) => MathF.Abs(left - right) < .0001f;

    private static bool SameSnapshot(object left, object right)
        => Property<GameTooltipLifecycleState>(left, "Lifecycle") ==
               Property<GameTooltipLifecycleState>(right, "Lifecycle") &&
           Property<GameTooltipAnchorKind>(left, "Anchor") ==
               Property<GameTooltipAnchorKind>(right, "Anchor") &&
           Property<GameTooltipLine[]>(left, "Lines").SequenceEqual(
               Property<GameTooltipLine[]>(right, "Lines")) &&
           Property<GameTooltipMoneyParts?>(left, "Money") ==
               Property<GameTooltipMoneyParts?>(right, "Money") &&
           Property<int>(left, "ComparisonCount") ==
               Property<int>(right, "ComparisonCount") &&
           Property<string?>(left, "LiveUnitToken") ==
               Property<string?>(right, "LiveUnitToken") &&
           Property<GameTooltipHealthState>(left, "Health") ==
               Property<GameTooltipHealthState>(right, "Health") &&
           Property<int?>(left, "UnitReaction") ==
               Property<int?>(right, "UnitReaction") &&
           Property<Vector2?>(left, "Cursor") == Property<Vector2?>(right, "Cursor");

    private static int Count(string source, string value)
    {
        int count = 0;
        int at = 0;
        while ((at = source.IndexOf(value, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += value.Length;
        }
        return count;
    }

    private static object ImmediateLeavePolicy()
    {
        Type? policy = typeof(GameLoop).GetNestedType("SharedGameTooltipLeavePolicy",
            BindingFlags.NonPublic);
        FieldInfo? immediate = policy?.GetField("ImmediateHide",
            BindingFlags.Public | BindingFlags.Static);
        return immediate?.GetValue(null) ?? throw new InvalidDataException(
            "GameTooltip explicit ImmediateHide leave policy seam missing");
    }

    private static object FadeLeavePolicy(double fadeSeconds)
    {
        Type? policy = typeof(GameLoop).GetNestedType("SharedGameTooltipLeavePolicy",
            BindingFlags.NonPublic);
        MethodInfo? fade = policy?.GetMethod("Fade", BindingFlags.Public | BindingFlags.Static);
        return fade?.Invoke(null, [fadeSeconds]) ?? throw new InvalidDataException(
            "GameTooltip explicit Fade leave policy seam missing");
    }

    private static object CreateGameLoopNested(string typeName, params object?[] arguments)
    {
        Type? nested = typeof(GameLoop).GetNestedType(typeName, BindingFlags.NonPublic);
        ConstructorInfo? constructor = nested?.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(candidate =>
                candidate.GetParameters().Length == arguments.Length);
        return constructor?.Invoke(arguments) ?? throw new InvalidDataException(
            $"GameTooltip clinical nested fixture seam missing: {typeName}");
    }

    private static ObjectFields CreateObjectFields(
        IReadOnlyList<(ushort Field, uint Value)> fields)
    {
        int blocks = fields.Count == 0 ? 1 : fields.Max(pair => pair.Field) / 32 + 1;
        var writer = new PacketWriter();
        writer.WriteU8((byte)blocks);
        for (int block = 0; block < blocks; block++)
        {
            uint mask = 0;
            foreach ((ushort field, _) in fields)
                if (field / 32 == block) mask |= 1u << (field & 31);
            writer.WriteU32(mask);
        }
        foreach ((_, uint value) in fields.OrderBy(pair => pair.Field))
            writer.WriteU32(value);
        return ObjectFields.Read(new PacketReader(writer.ToArray())).AsCreated();
    }

    private static void SetField(object target, string field, object value)
    {
        FieldInfo? selected = target.GetType().GetField(field,
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (selected is null)
            throw new InvalidDataException($"GameTooltip clinical field seam missing: {field}");
        selected.SetValue(target, value);
    }

    private static T Invoke<T>(object target, string method, params object?[] arguments)
    {
        MethodInfo? selected = target.GetType().GetMethod(method,
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (selected is null)
            throw new InvalidDataException($"GameTooltip clinical method seam missing: {method}");
        object? result = selected.Invoke(target, arguments);
        return result is T value
            ? value
            : throw new InvalidDataException(
                $"GameTooltip clinical method seam returned the wrong type: {method}");
    }

    private static T InvokeStatic<T>(string method, params object?[] arguments)
    {
        MethodInfo? selected = typeof(GameLoop).GetMethod(method,
            BindingFlags.Static | BindingFlags.NonPublic);
        if (selected is null)
            throw new InvalidDataException($"GameTooltip clinical static seam missing: {method}");
        object? result = selected.Invoke(null, arguments);
        return result is T value
            ? value
            : throw new InvalidDataException(
                $"GameTooltip clinical static seam returned the wrong type: {method}");
    }

    private static T? InvokeStaticNullable<T>(string method, params object?[] arguments)
        where T : class
    {
        MethodInfo? selected = typeof(GameLoop).GetMethod(method,
            BindingFlags.Static | BindingFlags.NonPublic);
        if (selected is null)
            throw new InvalidDataException($"GameTooltip clinical static seam missing: {method}");
        object? result = selected.Invoke(null, arguments);
        if (result is null) return null;
        return result is T value
            ? value
            : throw new InvalidDataException(
                $"GameTooltip clinical static seam returned the wrong type: {method}");
    }

    private static void InvokeVoid(object target, string method, params object?[] arguments)
    {
        MethodInfo? selected = target.GetType().GetMethod(method,
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (selected is null)
            throw new InvalidDataException($"GameTooltip clinical method seam missing: {method}");
        selected.Invoke(target, arguments);
    }

    private static T Property<T>(object target, string property)
    {
        PropertyInfo? selected = target.GetType().GetProperty(property,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (selected is null)
            throw new InvalidDataException(
                $"GameTooltip clinical snapshot property missing: {property}");
        object? result = selected.GetValue(target);
        if (result is null && default(T) is null) return default!;
        return result is T value
            ? value
            : throw new InvalidDataException(
                $"GameTooltip clinical snapshot property has the wrong type: {property}");
    }

    private static void ExpectReject(Action action, string message)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }
        throw new InvalidDataException(message);
    }

    private static void ExpectPacketReject(Action action, string message)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
        {
            return;
        }
        throw new InvalidDataException(message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
