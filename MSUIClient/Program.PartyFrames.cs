using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private const string PartyInvitePopupType = PartyFrameUiLaw.PartyInvitePopupType;

    // Keep the complete wire roster. Social/Raid and Minimap consume every subgroup; the four
    // PartyMemberFrame tokens are a derived own-subgroup view and must never replace this list.
    private sealed record PartyMember(ulong Guid, string Name, byte Status, byte Subgroup, byte Flags);

    private sealed record PartyMemberView(
        PartyMember Member,
        WorldEntity? Unit,
        byte Status,
        bool Connected,
        bool Dead,
        bool Ghost,
        bool Pvp,
        uint Health,
        uint MaxHealth,
        byte PowerType,
        uint Power,
        uint MaxPower,
        uint Level);

    private sealed record PartyTooltipRuntime(PartyTooltipView View, Vector4 NameColor);

    private readonly List<PartyMember> _partyMembers = [];
    private readonly Dictionary<ulong, PartyMemberStatsSnapshot> _partyStats = [];
    private readonly ulong[] _partyRaidTargets = new ulong[8];
    private bool _partyInGroup;
    private byte _partyGroupType;
    private byte _partyOwnFlags;
    private ulong _partyLeaderGuid;
    private ulong _partyMasterLooterGuid;
    private byte _partyLootMethod;
    private byte _partyLootThreshold;
    private int _partyRosterRevision;
    private StaticPopupCoordinatorLaw.Slots _staticPopupSlots =
        StaticPopupCoordinatorLaw.Slots.Empty;
    private long _staticPopupLastUpdateTicks = Stopwatch.GetTimestamp();
    private bool _partyInviteAccepted;
    private readonly float[] _partyLowHealthTimers = new float[PartyFrameUiLaw.MemberCount];
    private double _partyLowHealthLastAt;
    private int _partyPressIndex = -1;
    private PartyPointerButton _partyPressButton;
    private PartyTooltipRuntime? _partyTooltip;
    private int _partyTooltipSlot = -1;
    private GameTooltipOwnerToken _partyTooltipOwnerToken;
    private bool _partyTooltipParityCompletionPending;
    private bool _partyTooltipParityRendererCollected;

    private PartyMember[] PartyFrameMembers() => _partyMembers
        .Where(member => (member.Subgroup & 0x7f) == (_partyOwnFlags & 0x7f))
        .Take(PartyFrameUiLaw.MemberCount)
        .ToArray();

    private ulong PartyFrameMemberGuid(int zeroBasedIndex)
    {
        PartyMember[] slots = PartyFrameMembers();
        return zeroBasedIndex >= 0 && zeroBasedIndex < slots.Length ? slots[zeroBasedIndex].Guid : 0;
    }

    private void ResetParty()
    {
        bool rosterChanged = _partyMembers.Count != 0 || _partyStats.Count != 0 ||
            _partyLeaderGuid != 0 || _partyMasterLooterGuid != 0 || _partyLootMethod != 0 ||
            _partyLootThreshold != 0 || _partyGroupType != 0 || _partyInGroup ||
            _partyOwnFlags != 0 || _partyRaidTargets.Any(guid => guid != 0);
        HidePartyInvite();
        _partyMembers.Clear();
        _partyStats.Clear();
        Array.Clear(_partyRaidTargets);
        _partyInGroup = false;
        _partyGroupType = 0;
        _partyOwnFlags = 0;
        _partyLeaderGuid = 0;
        _partyMasterLooterGuid = 0;
        _partyLootMethod = 0;
        _partyLootThreshold = 0;
        if (rosterChanged) _partyRosterRevision++;
        // PartyMemberFrame objects retain flashTimer while their unit token is absent. Session
        // teardown pauses the four slot timers; it does not recreate the frames.
        _partyLowHealthLastAt = 0;
        ClearPartyPress();
        BeginPartyTooltipDeparture(NowSeconds(), tokenExists: false);
        // The retained PartyMemberFrame token runs OnLeave after the roster disappears. Keep its
        // SetUnit row snapshot and slot token while the departure path immediately hides the
        // now-absent live health bar and starts the 0.5s fade.
    }

    // Build-5875 SMSG_GROUP_LIST: group type, local flags, recipient-excluded members, leader,
    // and (only when count > 0) loot method/master/threshold plus dungeon difficulty.
    private void ApplyPartyRoster(byte[] body)
    {
        PartyRosterWire wire = PartyFramePacketLaw.ParseRoster(body);
        PartyRosterWireMember[] previous = _partyMembers
            .Select(member => new PartyRosterWireMember(member.Name, member.Guid, member.Status,
                (byte)(member.Subgroup | member.Flags)))
            .ToArray();
        string[] systemLines = GroupUiLaw.RosterLines(_partyGroupType, previous, wire);
        bool leaving = PartyFrameUiLaw.IsLeaveRoster(wire);
        // The all-zero leave reply clears GroupState. Its retained feed edge hides any pending
        // PARTY_INVITE popup, which closes first and then emits the OnHide decline intent.
        if (leaving) HidePartyInvite();
        var next = wire.Members.Select(member => new PartyMember(member.Guid, member.Name,
            member.Status, (byte)(member.MemberFlags & 0x7f),
            (byte)(member.MemberFlags & 0x80))).ToList();

        _partyMembers.Clear();
        _partyMembers.AddRange(next);
        foreach (PartyMember member in next)
            if (!_playerNames.ContainsKey(member.Guid)) _playerNames[member.Guid] = member.Name;
        _partyOwnFlags = wire.OwnFlags;
        _partyRosterRevision++;
        _partyLeaderGuid = wire.LeaderGuid;
        _partyLootMethod = wire.LootMethod;
        _partyMasterLooterGuid = wire.MasterLooterGuid;
        _partyLootThreshold = wire.LootThreshold;
        _partyInGroup = !leaving;
        _partyGroupType = leaving ? (byte)0 : wire.GroupType;
        if (leaving) Array.Clear(_partyRaidTargets);

        HashSet<ulong> retained = next.Select(x => x.Guid).ToHashSet();
        foreach (ulong guid in _partyStats.Keys.Where(x => !retained.Contains(x)).ToArray())
            _partyStats.Remove(guid);
        foreach (PartyMember member in next)
            if (PartyFrameUiLaw.Has(member.Status, PartyFrameUiLaw.Online) &&
                !_entities.TryGet(member.Guid, out _))
                _net?.RequestPartyMemberStats(member.Guid);
        foreach (string line in systemLines) AddChatMessage(line);
    }

    private void ApplyPartyMemberStats(byte[] body, bool fullSnapshot)
    {
        PartyMemberStatsWire wire = PartyFramePacketLaw.ParseMemberStats(body);
        _partyStats.TryGetValue(wire.Guid, out PartyMemberStatsSnapshot previous);
        _partyStats[wire.Guid] = PartyFrameUiLaw.MergeStats(previous, wire.Snapshot, fullSnapshot);
    }

    private void ApplyPartyInvite(byte[] body)
    {
        string inviter = PartyFramePacketLaw.ParseInvite(body);
        AddChatMessage(GroupUiLaw.InvitedLine(inviter));
        bool playerDeadOrGhost = _net is not null &&
            _entities.TryGet(_net.PlayerGuid, out WorldEntity player) && player.IsDead;
        ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Show(
            _staticPopupSlots,
            PartyFrameUiLaw.PartyInvitePopupDefinition,
            playerDeadOrGhost,
            dataToken: inviter));
    }

    private void ApplyPartyDecline(byte[] body) =>
        AddChatMessage(GroupUiLaw.DeclinedLine(PartyFramePacketLaw.ParseDecline(body)));

    private void ApplyPartyUninvited(byte[] body)
    {
        PartyFramePacketLaw.ParseEmptyNotice(body, nameof(Op.SMSG_GROUP_UNINVITE));
        AddChatMessage(GroupUiLaw.UninvitedLine);
    }

    private void ApplyPartyDestroyed(byte[] body)
    {
        PartyFramePacketLaw.ParseEmptyNotice(body, nameof(Op.SMSG_GROUP_DESTROYED));
        foreach (string line in GroupUiLaw.DestroyedLines(_partyInGroup)) AddChatMessage(line);
    }

    private void ApplyPartyLeaderChanged(byte[] body)
    {
        string name = PartyFramePacketLaw.ParseLeaderChanged(body);
        AddChatMessage(GroupUiLaw.LeaderChangedLine(name, _net?.PlayerName));
    }

    private void ApplyPartyCommandResult(byte[] body)
    {
        PartyCommandResultWire wire = PartyFramePacketLaw.ParseCommandResult(body);
        foreach (string line in GroupUiLaw.CommandResultLines(wire)) AddChatMessage(line);
    }

    private void ApplyPartyRaidTargetUpdate(byte[] body)
    {
        PartyRaidTargetUpdateWire wire = PartyFramePacketLaw.ParseRaidTargetUpdate(body);
        if (wire.IsDelta)
            GroupUiLaw.ApplyRaidTarget(_partyRaidTargets, wire.Icon, wire.Guid);
        else
            GroupUiLaw.ApplyRaidTargetList(_partyRaidTargets, wire.Entries);
    }

    private void ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Plan plan)
    {
        // Slot ownership changes atomically before any callback, sound, or wire effect runs.
        _staticPopupSlots = plan.Slots;
        foreach (StaticPopupCoordinatorLaw.Effect effect in plan.Effects)
        {
            // PARTY_INVITE is the only production entry integrated in this bounded slice.
            if (!string.Equals(effect.Type, PartyInvitePopupType, StringComparison.Ordinal))
                continue;
            switch (effect.Kind)
            {
                case StaticPopupCoordinatorLaw.EffectKind.Accept:
                    _net?.GroupAccept();
                    _partyInviteAccepted = true;
                    break;
                case StaticPopupCoordinatorLaw.EffectKind.CancelWithoutReason:
                case StaticPopupCoordinatorLaw.EffectKind.CancelOverride:
                case StaticPopupCoordinatorLaw.EffectKind.CancelClicked:
                case StaticPopupCoordinatorLaw.EffectKind.CancelTimeout:
                    _net?.GroupDecline();
                    break;
                case StaticPopupCoordinatorLaw.EffectKind.MainMenuOpenSound:
                case StaticPopupCoordinatorLaw.EffectKind.MainMenuCloseSound:
                case StaticPopupCoordinatorLaw.EffectKind.EntrySound:
                    if (!string.IsNullOrEmpty(effect.Value)) PlayUiSound(effect.Value);
                    break;
                case StaticPopupCoordinatorLaw.EffectKind.OnShow:
                    _partyInviteAccepted = false;
                    break;
                case StaticPopupCoordinatorLaw.EffectKind.OnHide:
                    if (!_partyInviteAccepted) _net?.GroupDecline();
                    _partyInviteAccepted = false;
                    break;
            }
        }
    }

    private void HidePartyInvite() => ExecuteStaticPopupPlan(
        StaticPopupCoordinatorLaw.HideByType(_staticPopupSlots, PartyInvitePopupType));

    private bool TryDismissStaticPopupOnEscape()
    {
        StaticPopupCoordinatorLaw.Plan plan = StaticPopupCoordinatorLaw.Escape(_staticPopupSlots);
        ExecuteStaticPopupPlan(plan);
        return plan.Outcome != StaticPopupCoordinatorLaw.Outcome.NothingVisible;
    }

    private void UpdatePartyInviteLifecycle()
    {
        long now = Stopwatch.GetTimestamp();
        long previous = _staticPopupLastUpdateTicks;
        _staticPopupLastUpdateTicks = now;
        double elapsedSeconds = now >= previous
            ? (now - previous) / (double)Stopwatch.Frequency
            : 0;
        for (int slot = 1; slot <= StaticPopupCoordinatorLaw.SlotCount; slot++)
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Advance(
                _staticPopupSlots, slot, elapsedSeconds));
    }

    private PartyMemberView BuildPartyMemberView(PartyMember member)
    {
        WorldEntity? unit = _entities.TryGet(member.Guid, out WorldEntity found) ? found : null;
        _partyStats.TryGetValue(member.Guid, out PartyMemberStatsSnapshot stats);
        byte status = PartyFrameUiLaw.EffectiveStatus(member.Status, stats.Status);
        bool connected = PartyFrameUiLaw.Has(status, PartyFrameUiLaw.Online);
        bool dead = unit?.IsDead == true || PartyFrameUiLaw.Has(status, PartyFrameUiLaw.Dead);
        bool ghost = PartyFrameUiLaw.Has(status, PartyFrameUiLaw.Ghost);
        bool pvp = PartyFrameUiLaw.MergedPvp(status, unit?.Fields.UnitFlags);
        uint health = unit?.Fields.Health ?? stats.Health ?? 0;
        uint maxHealth = unit?.Fields.MaxHealth ?? stats.MaxHealth ?? 0;
        byte powerType = unit?.Fields.PowerType ?? stats.PowerType ?? 0;
        uint power = unit?.Fields.ActivePower ?? stats.Power ?? 0;
        uint maxPower = unit?.Fields.ActiveMaxPower ?? stats.MaxPower ?? 0;
        uint level = unit?.Fields.Level ?? stats.Level ?? 0;
        return new(member, unit, status, connected, dead, ghost, pvp, health, maxHealth,
            powerType, power, maxPower, level);
    }

    private void DrawPartyFrames()
    {
        if (_net is null || _gameplayArt is null) return;
        PartyMember[] members = PartyFrameMembers();
        if (members.Length == 0)
        {
            _partyLowHealthLastAt = 0;
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) ||
                ImGui.IsMouseReleased(ImGuiMouseButton.Right)) ClearPartyPress();
            // Hiding the final owner frame runs its OnLeave path. Advance an existing tooltip's
            // fade even though there is no Party parity surface to arm or complete this frame.
            UpdateAndQueuePartyTooltip(-1, null, NowSeconds(), capture: false);
            return;
        }

        bool capture = _uiParityArmed && _uiParityPanel == "party-frame";
        float s = GameplayUiScale();
        Vector2 origin = new(PartyFrameUiLaw.FirstX * s, PartyFrameUiLaw.FirstY * s);
        if (capture)
        {
            BeginUiParityFrame(origin, s);
            _partyTooltipParityCompletionPending = false;
            _partyTooltipParityRendererCollected = false;
        }
        double now = NowSeconds();
        float dt = _partyLowHealthLastAt <= 0 ? 0f :
            MathF.Max(0f, (float)(now - _partyLowHealthLastAt));
        _partyLowHealthLastAt = now;

        int hoveredIndex = -1;
        PartyMemberView? hoveredView = null;
        for (int i = 0; i < members.Length; i++)
        {
            PartyMemberView view = BuildPartyMemberView(members[i]);
            if (DrawPartyMemberFrame(i, view, dt, capture))
            {
                hoveredIndex = i;
                hoveredView = view;
            }
        }
        if (capture)
            for (int i = members.Length; i < PartyFrameUiLaw.MemberCount; i++)
                ClassifyUiParity($"PartyMemberFrame{i + 1}", "Button", "UIParent", "NOT-DRAWN",
                    "own-subgroup-slot-is-empty");
        ClassifyUiParity("PartyMemberFrame5", "Button", "UIParent", "NOT-DRAWN",
            "reference-caps-party-slots-at-four");

        ResolvePartyPointerRelease(hoveredIndex, members);
        bool tooltipQueued = UpdateAndQueuePartyTooltip(hoveredIndex, hoveredView, now, capture);
        if (capture && !tooltipQueued) MarkUiParityFrameComplete();
    }

    private void ResolvePartyPointerRelease(int hoveredIndex, PartyMember[] members)
    {
        ImGuiMouseButton released = ImGuiMouseButton.COUNT;
        if (_partyPressIndex >= 0)
        {
            ImGuiMouseButton expected = _partyPressButton == PartyPointerButton.Left
                ? ImGuiMouseButton.Left : ImGuiMouseButton.Right;
            if (ImGui.IsMouseReleased(expected)) released = expected;
        }
        if (released == ImGuiMouseButton.COUNT) return;
        // PartyMemberFrameN is the pointer token. If its roster occupant rebinds while held,
        // ButtonUp still acts on that slot's current occupant, exactly like frame.unit.
        PartyPointerAction action = PartyFrameUiLaw.ReleaseAction(_partyPressIndex,
            hoveredIndex, _partyPressButton);
        if (action != PartyPointerAction.None && hoveredIndex >= 0 &&
            hoveredIndex < members.Length)
        {
            PartyMember member = members[hoveredIndex];
            if (action == PartyPointerAction.Target)
                CommitSelection(member.Guid, beginAttack: false);
            else
            {
                // PARTY origin/token is explicit. Right-click intentionally leaves selection alone.
                OpenUnitPopup(member.Guid, UnitPopupWhich.Party,
                    new Vector2(PartyFrameUiLaw.FirstX + 47,
                        PartyFrameUiLaw.MemberY(hoveredIndex) + 15) * GameplayUiScale(),
                    InspectBinding.Party(hoveredIndex));
            }
        }
        ClearPartyPress();
    }

    private void ClearPartyPress()
    {
        _partyPressIndex = -1;
    }

    private bool DrawPartyMemberFrame(int index, PartyMemberView view, float dt, bool capture)
    {
        if (_gameplayArt is null) return false;
        float s = GameplayUiScale();
        Vector2 p = new Vector2(PartyFrameUiLaw.FirstX, PartyFrameUiLaw.MemberY(index)) * s;
        Vector2 frameSize = new(PartyFrameUiLaw.FrameWidth * s, PartyFrameUiLaw.FrameHeight * s);
        ImGui.SetNextWindowPos(p, ImGuiCond.Always);
        ImGui.SetNextWindowSize(frameSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoBringToFrontOnFocus;
        bool begun = ImGui.Begin($"##vanilla-party-member-{index + 1}", flags);
        ImGui.PopStyleVar(2);
        if (!begun) { ImGui.End(); return false; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 display = ImGui.GetIO().DisplaySize;
        Vector4 fullClip = new(0, 0, display.X, display.Y);
        string root = $"PartyMemberFrame{index + 1}";

        ImGui.SetCursorScreenPos(p);
        ImGui.InvisibleButton($"##party-member-hit-{index + 1}", frameSize,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        bool hovered = ImGui.IsItemHovered();
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            _partyPressIndex = index;
            _partyPressButton = PartyPointerButton.Left;
        }
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            _partyPressIndex = index;
            _partyPressButton = PartyPointerButton.Right;
        }
        string interaction = ImGui.IsItemActive() ? "pressed" : hovered ? "hovered" : "normal";
        if (capture)
            CollectUiParityDraw(root, "Button", p, frameSize, "UIParent",
                new("", 0, "HOST", "TOPLEFT", "UIParent", "TOPLEFT",
                    PartyFrameUiLaw.FirstX, -PartyFrameUiLaw.MemberY(index),
                    ContentRect: new(p.X, p.Y, p.X + frameSize.X, p.Y + frameSize.Y),
                    ClipRect: fullClip, ClipMask: "UIParent", Visible: true, Enabled: true,
                    InteractionState: interaction, HitMin: p, HitMax: p + frameSize, Strata: "LOW"));

        float healthFraction = view.MaxHealth > 0
            ? Math.Clamp((float)view.Health / view.MaxHealth, 0, 1) : 0;
        float powerFraction = view.MaxPower > 0
            ? Math.Clamp((float)view.Power / view.MaxPower, 0, 1) : 0;
        bool low = view.MaxHealth > 0 && view.Health > 0 && healthFraction <= .2f;
        _partyLowHealthTimers[index] = PartyFrameUiLaw.AdvanceLowHealthTimer(
            _partyLowHealthTimers[index], exists: true, connected: view.Connected,
            lowLivingHealth: low, dt: dt);
        // FrameXML's OnUpdate changes alpha independently from the status RGB. A disconnect
        // pauses the slot timer and therefore retains its current alpha; a low-health ghost
        // remains blue while pulsing.
        float portraitAlpha = PartyFrameUiLaw.LowHealthAlpha(_partyLowHealthTimers[index]);
        Vector3 portraitRgb = !view.Connected ? new(.5f, .5f, .5f)
            : view.Dead ? new(.35f, .35f, .35f)
            : view.Ghost ? new(.2f, .2f, .75f)
            : low ? new(1, 0, 0)
            : Vector3.One;
        Vector4 portraitColor = new(portraitRgb, portraitAlpha);
        uint portraitTint = ImGui.ColorConvertFloat4ToU32(portraitColor);
        Vector2 portraitMin = p + new Vector2(7, 6) * s;
        Vector2 portraitSize = new Vector2(37) * s;

        dl.PushClipRectFullScreen();
        string portraitPath = "";
        if (view.Unit is not null)
        {
            (byte race, _, byte gender, _) = view.Unit.Fields.Bytes0;
            string sex = gender == 1 ? "Female" : "Male";
            string raceName = race == 5 ? "Scourge" : RaceName(race).Replace(" ", "");
            portraitPath = $@"Interface\CharacterFrame\TemporaryPortrait-{sex}-{raceName}";
            uint portrait = _gameplayArt.CircularHandle(portraitPath);
            if (portrait != 0)
                dl.AddImage((nint)portrait, portraitMin, portraitMin + portraitSize,
                    Vector2.Zero, Vector2.One, portraitTint);
            if (capture && portrait != 0)
                CollectUiParityDraw(root + "Portrait", "Texture", portraitMin, portraitSize, root,
                    new(portraitPath, portraitTint, "BACKGROUND", "TOPLEFT", root, "TOPLEFT", 7, -6,
                        TexCoords: "0|0|1|1", ClipRect: fullClip,
                        ClipMask: "circular-alpha-mask;UIParent", BlendMode: "BLEND", Strata: "LOW"));
            else if (capture)
                ClassifyUiParity(root + "Portrait", "Texture", root, "NOT-DRAWN",
                    "temporary-player-portrait-asset-unavailable");
        }
        else if (capture)
            ClassifyUiParity(root + "Portrait", "Texture", root, "NOT-DRAWN",
                "party-token-guid-is-not-streamed");

        Vector2 healthMin = p + new Vector2(47, 12) * s;
        Vector2 barSize = new Vector2(70, 8) * s;
        float drawnHealth = view.Connected ? healthFraction : 1f;
        PartyTooltipHealthState memberHealth = PartyFrameUiLaw.MemberHealth(
            view.Connected, view.Health, view.MaxHealth);
        DrawVanillaStatusBar(dl, healthMin, barSize, drawnHealth,
            view.Connected ? new Vector4(0, 1, 0, 1) : new Vector4(.5f, .5f, .5f, 1));
        if (capture)
        {
            Vector4 color = view.Connected ? new(0, 1, 0, 1) : new(.5f, .5f, .5f, 1);
            CollectUiParityDraw(root + "HealthBar", "StatusBar", healthMin, barSize, root,
                new(@"Interface\TargetingFrame\UI-StatusBar", ImGui.ColorConvertFloat4ToU32(color),
                    "BARS", "TOPLEFT", root, "TOPLEFT", 47, -12,
                    TexCoords: $"0|0|{drawnHealth:R}|1",
                    ContentRect: new(healthMin.X, healthMin.Y,
                        healthMin.X + barSize.X * drawnHealth, healthMin.Y + barSize.Y),
                    ClipRect: fullClip, ClipMask: "UIParent", BlendMode: "BLEND",
                    Visible: true,
                    InteractionState:
                        $"min=0;max={memberHealth.Maximum};value={memberHealth.Value}",
                    Strata: "LOW"));
        }

        Vector2 powerMin = p + new Vector2(47, 21) * s;
        if (view.MaxPower > 0)
        {
            Vector4 powerColor = PowerColor(view.PowerType);
            DrawVanillaStatusBar(dl, powerMin, barSize, powerFraction, powerColor);
            if (capture)
                CollectUiParityDraw(root + "ManaBar", "StatusBar", powerMin, barSize, root,
                    new(@"Interface\TargetingFrame\UI-StatusBar",
                        ImGui.ColorConvertFloat4ToU32(powerColor), "BARS", "TOPLEFT", root,
                        "TOPLEFT", 47, -21, TexCoords: $"0|0|{powerFraction:R}|1",
                        ContentRect: new(powerMin.X, powerMin.Y,
                            powerMin.X + barSize.X * powerFraction, powerMin.Y + barSize.Y),
                        ClipRect: fullClip, ClipMask: "UIParent", BlendMode: "BLEND",
                        Visible: true,
                        InteractionState:
                            $"min=0;max={view.MaxPower};value={Math.Min(view.Power, view.MaxPower)}",
                        Strata: "LOW"));
        }
        else if (capture)
            ClassifyUiParity(root + "ManaBar", "StatusBar", root, "NOT-DRAWN",
                "max-power-is-zero");

        Vector2 artMin = p + new Vector2(0, 2) * s;
        Vector2 artSize = new Vector2(128, 64) * s;
        DrawArt(dl, @"Interface\TargetingFrame\UI-PartyFrame", artMin, new Vector2(128, 64), s);
        if (capture)
            CollectUiParityDraw(root + "/Frame/FrameTexture", "Texture", artMin, artSize, root,
                new(@"Interface\TargetingFrame\UI-PartyFrame", 0xffffffff, "ART", "TOPLEFT",
                    root, "TOPLEFT", 0, -2, ClipRect: fullClip, ClipMask: "UIParent",
                    BlendMode: "BLEND", Strata: "LOW"));

        (Vector2 nameMin, Vector2 nameSize) = DrawUnitFrameText(dl,
            p + new Vector2(83, 8) * s, view.Member.Name, 10 * s, UiGoldU32());
        if (capture)
            CollectUiParityDraw(root + "Name", "FontString", nameMin, nameSize, root,
                new("", UiGoldU32(), "TEXT", "CENTER", root, "TOPLEFT", 83, -8,
                    FontSize: 10, ClipRect: fullClip, ClipMask: "UIParent", Strata: "LOW"));

        TracePartyIcon(dl, capture, root, p, root + "LeaderIcon",
            @"Interface\GroupFrame\UI-Group-LeaderIcon", p, new Vector2(16) * s,
            view.Member.Guid == _partyLeaderGuid, fullClip, "member-is-not-leader");
        TracePartyIcon(dl, capture, root, p, root + "MasterIcon",
            @"Interface\GroupFrame\UI-Group-MasterLooter", p + new Vector2(32, 0) * s,
            new Vector2(16) * s,
            _partyLootMethod == 2 && view.Member.Guid == _partyMasterLooterGuid,
            fullClip, "member-is-not-master-looter");
        TracePartyIcon(dl, capture, root, p, root + "Disconnect",
            @"Interface\CharacterFrame\Disconnect-Icon", p + new Vector2(-7, -5) * s,
            new Vector2(64) * s, !view.Connected, fullClip, "member-is-connected");

        bool ffa = PartyFrameUiLaw.Has(view.Status, PartyFrameUiLaw.PvpFfa);
        bool pvp = view.Pvp;
        string pvpPath = @"Interface\TargetingFrame\UI-PVP-FFA";
        bool pvpIconVisible = ffa;
        string pvpHiddenReason = "member-is-not-pvp-flagged";
        if (!ffa && pvp)
        {
            byte? memberRace = view.Unit?.Fields.Bytes0.Race;
            byte? ownRace = _entities.TryGet(_net!.PlayerGuid, out WorldEntity own)
                ? own.Fields.Bytes0.Race : null;
            string? faction = PartyFrameUiLaw.PvpFaction(memberRace, ownRace);
            if (faction is not null)
            {
                pvpPath = $@"Interface\GroupFrame\UI-Group-PVP-{faction}";
                pvpIconVisible = true;
            }
            else pvpHiddenReason = "party-pvp-faction-is-unresolved";
        }
        TracePartyIcon(dl, capture, root, p, root + "PVPIcon", pvpPath,
            p + new Vector2(-9, 15) * s, new Vector2(32) * s, pvpIconVisible,
            fullClip, pvpHiddenReason);
        dl.PopClipRect();
        ImGui.End();
        return hovered;
    }

    private void TracePartyIcon(ImDrawListPtr dl, bool capture, string root, Vector2 rootMin,
        string element, string path, Vector2 min, Vector2 size, bool visible, Vector4 clip,
        string hiddenReason)
    {
        if (visible) DrawArt(dl, path, min, size / GameplayUiScale(), GameplayUiScale());
        if (!capture) return;
        if (!visible)
        {
            ClassifyUiParity(element, "Texture", root, "NOT-DRAWN", hiddenReason);
            return;
        }
        CollectUiParityDraw(element, "Texture", min, size, root,
            new(path, 0xffffffff, "OVERLAY", "TOPLEFT", root, "TOPLEFT",
                (min.X - rootMin.X) / GameplayUiScale(),
                -(min.Y - rootMin.Y) / GameplayUiScale(),
                ClipRect: clip, ClipMask: "UIParent", BlendMode: "BLEND", Strata: "LOW"));
    }

    private bool UpdateAndQueuePartyTooltip(int hoveredSlot, PartyMemberView? hovered,
        double now, bool capture)
    {
        GameTooltipRuntimeSnapshot shared = SharedGameTooltipSnapshot();
        bool exactOwner = _partyTooltip is not null &&
            SharedGameTooltipIsOwned(_partyTooltipOwnerToken);
        bool fading = exactOwner && shared.Lifecycle.FadeStartedAt is not null;

        if (hovered is not null)
        {
            bool beginSnapshot = PartyFrameUiLaw.BeginTooltipSnapshot(_partyTooltipSlot,
                hoveredSlot, _partyTooltip is not null, fading) || !exactOwner;
            if (beginSnapshot)
            {
                string? race = null, @class = null;
                if (hovered.Unit is { IsPlayer: true } unit)
                {
                    var bytes = unit.Fields.Bytes0;
                    if (bytes.Race != 0) race = RaceName(bytes.Race);
                    if (bytes.Class != 0) @class = ClassName(bytes.Class);
                }
                // Frozen SetUnit prints the PvP line from the ordinary pvp field only. FFA is a
                // separate icon state and must not manufacture this tooltip line.
                bool pvp = hovered.Pvp;
                Vector4 reaction = PartyFrameUiLaw.TooltipNameColor;
                _partyTooltip = new(PartyFrameUiLaw.Tooltip(hovered.Member.Name, hovered.Level,
                    race, @class, hovered.Dead,
                    pvp, hovered.Health, hovered.MaxHealth), reaction);
                _partyTooltipSlot = hoveredSlot;
                _partyTooltipOwnerToken = ClaimSharedGameTooltip(
                    PartyFrameUiLaw.TooltipOwner(hoveredSlot));
                if (!PublishSharedGameTooltip(_partyTooltipOwnerToken,
                        PartyFrameUiLaw.SharedTooltipContent(hoveredSlot, _partyTooltip.View)))
                {
                    ClearPartyTooltipRuntime();
                    if (capture)
                        ClassifyUiParity("GameTooltip", "GameTooltip", "UIParent", "NOT-DRAWN",
                            "party-tooltip-publication-rejected");
                    return false;
                }
            }
        }
        else if (exactOwner)
        {
            BeginSharedGameTooltipFade(_partyTooltipOwnerToken, now,
                GameTooltipUiLaw.WorldFadeSeconds);
        }

        if (_partyTooltip is null)
        {
            if (capture) ClassifyUiParity("GameTooltip", "GameTooltip", "UIParent", "NOT-DRAWN",
                "no-party-member-hover-or-fade");
            return false;
        }
        if (!SharedGameTooltipIsOwned(_partyTooltipOwnerToken))
        {
            shared = SharedGameTooltipSnapshot();
            string reason = shared.Lifecycle.Owner is null
                ? "party-tooltip-fade-complete"
                : "shared-tooltip-owner-replaced";
            ClearPartyTooltipRuntime();
            if (capture) ClassifyUiParity("GameTooltip", "GameTooltip", "UIParent", "NOT-DRAWN",
                reason);
            return false;
        }

        PartyMember[] currentSlots = PartyFrameMembers();
        bool tokenExists = _partyTooltipSlot >= 0 && _partyTooltipSlot < currentSlots.Length;
        uint health = 0, maxHealth = 0;
        if (tokenExists)
        {
            PartyMemberView current = BuildPartyMemberView(currentSlots[_partyTooltipSlot]);
            health = current.Health;
            maxHealth = current.MaxHealth;
        }
        if (!TryRefreshSharedGameTooltipUnit(_partyTooltipOwnerToken,
                PartyFrameUiLaw.TooltipHealthPush(_partyTooltipSlot, tokenExists,
                    health, maxHealth)))
        {
            if (capture) ClassifyUiParity("GameTooltip", "GameTooltip", "UIParent", "NOT-DRAWN",
                "party-tooltip-live-token-rejected");
            return false;
        }

        shared = SharedGameTooltipSnapshot();
        if (!shared.Lifecycle.Visible || shared.Lifecycle.Alpha <= 0f)
        {
            ClearPartyTooltipRuntime();
            if (capture) ClassifyUiParity("GameTooltip", "GameTooltip", "UIParent", "NOT-DRAWN",
                "party-tooltip-fade-complete");
            return false;
        }

        PartyTooltipRuntime rendererRuntime = _partyTooltip;
        PartyTooltipHealthState rendererHealth = new(shared.Health.Visible,
            shared.Health.Maximum, shared.Health.Value);
        float rendererAlpha = shared.Lifecycle.Alpha;
        bool rendererFading = shared.Lifecycle.FadeStartedAt is not null;
        bool queued = QueueSharedGameTooltipRenderer(_partyTooltipOwnerToken,
            SharedGameTooltipLeavePolicy.Fade(GameTooltipUiLaw.WorldFadeSeconds), () =>
            {
                DrawPartyUnitTooltip(rendererRuntime, rendererHealth, rendererAlpha,
                    rendererFading, capture);
                if (capture) _partyTooltipParityRendererCollected = true;
            });
        if (queued && capture) _partyTooltipParityCompletionPending = true;
        if (!queued && capture)
            ClassifyUiParity("GameTooltip", "GameTooltip", "UIParent", "NOT-DRAWN",
                "party-tooltip-renderer-queue-rejected");
        return queued;
    }

    private void BeginPartyTooltipDeparture(double now, bool tokenExists)
    {
        if (_partyTooltip is null || _partyTooltipSlot < 0 ||
            !SharedGameTooltipIsOwned(_partyTooltipOwnerToken))
            return;
        if (!tokenExists)
            TryRefreshSharedGameTooltipUnit(_partyTooltipOwnerToken,
                PartyFrameUiLaw.TooltipHealthPush(_partyTooltipSlot, false, 0, 0));
        BeginSharedGameTooltipFade(_partyTooltipOwnerToken, now,
            GameTooltipUiLaw.WorldFadeSeconds);
    }

    private void ClearPartyTooltipRuntime()
    {
        _partyTooltip = null;
        _partyTooltipSlot = -1;
        _partyTooltipOwnerToken = default;
    }

    private void CompleteDeferredPartyTooltipParityCapture()
    {
        if (!_partyTooltipParityCompletionPending) return;
        bool rendererCollected = _partyTooltipParityRendererCollected;
        _partyTooltipParityCompletionPending = false;
        _partyTooltipParityRendererCollected = false;
        if (!rendererCollected)
            ClassifyUiParity("GameTooltip", "GameTooltip", "UIParent", "NOT-DRAWN",
                "shared-tooltip-owner-replaced-before-tooltip-stratum");
        MarkUiParityFrameComplete();
    }

    private void DrawPartyUnitTooltip(PartyTooltipRuntime runtime,
        PartyTooltipHealthState tooltipHealth, float alpha, bool fading, bool capture)
    {
        if (_skin is null || _gameplayArt is null) return;
        float s = GameplayUiScale();
        string[] lines = runtime.View.PvpLine is null
            ? [runtime.View.Name, runtime.View.LevelLine!]
            : [runtime.View.Name, runtime.View.LevelLine!, runtime.View.PvpLine];
        string[] fontObjects = lines.Select((_, index) => index == 0
            ? "GameTooltipHeaderText" : "GameTooltipText").ToArray();
        float[] rowWidths = lines.Select((line, index) =>
            GameText.MeasureWidth(fontObjects[index], line, s)).ToArray();
        float[] rowHeights = fontObjects.Select(font => (float)GameText.EmPixels(font, s)).ToArray();
        PartyTooltipLayout layout = PartyFrameUiLaw.TooltipLayout(rowWidths, rowHeights, s);
        float width = layout.Width;
        float height = layout.Height;
        Vector2 display = ImGui.GetIO().DisplaySize;
        bool multiBarLeftVisible = Enumerable.Range(36, 12).Any(slot => _actions[slot] is not null);
        bool multiBarRightVisible = Enumerable.Range(24, 12).Any(slot => _actions[slot] is not null);
        float tooltipRightOffset = PartyFrameUiLaw.TooltipRightOffset(
            multiBarLeftVisible, multiBarRightVisible);
        // Both bottom multibar frames are always present in MSUI. No normal-runtime reputation
        // watch exists; the parity-only fixture is not allowed to move an observational tooltip.
        float tooltipBottomOffset = PartyFrameUiLaw.TooltipBottomOffset(
            bottomLeftVisible: true, bottomRightVisible: true,
            petOrStanceVisible: PetActionBarVisible, reputationVisible: false);
        Vector2 pos = new(display.X + tooltipRightOffset * s - width,
            display.Y - tooltipBottomOffset * s - height);
        Vector2 size = new(width, height);
        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        bool begun = ImGui.Begin("##party-unit-tooltip",
            ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.Tooltip);
        ImGui.PopStyleVar(2);
        if (!begun) { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        dl.PushClipRectFullScreen();
        var fadeTint = new Vector4(1, 1, 1, alpha);
        _skin.DrawBackdrop(dl, pos, pos + size, WowSkin.Tooltip, fadeTint, fadeTint);
        for (int i = 0; i < lines.Length; i++)
        {
            Vector4 color = i == 0 ? runtime.NameColor : Vector4.One;
            color.W *= alpha;
            GameText.Draw(dl, fontObjects[i], lines[i],
                pos + new Vector2(PartyFrameUiLaw.TooltipPadding * s, layout.RowTops[i]), s,
                ImGui.ColorConvertFloat4ToU32(color));
        }
        Vector2 barMin = pos + new Vector2(2 * s, size.Y + s);
        Vector2 barSize = new(size.X - 4 * s, 8 * s);
        float healthFraction = tooltipHealth.Visible
            ? Math.Clamp((float)tooltipHealth.Value / tooltipHealth.Maximum, 0, 1)
            : 0;
        uint bar = _gameplayArt.Handle(@"Interface\TargetingFrame\UI-TargetingFrame-BarFill");
        if (tooltipHealth.Visible && bar != 0 && healthFraction > 0)
            dl.AddImage((nint)bar, barMin,
                new Vector2(barMin.X + barSize.X * healthFraction, barMin.Y + barSize.Y),
                Vector2.Zero, new Vector2(healthFraction, 1),
                ImGui.ColorConvertFloat4ToU32(new Vector4(0, 1, 0, alpha)));

        if (capture)
        {
            Vector4 clip = new(0, 0, display.X, display.Y);
            CollectUiParityDraw("GameTooltip", "GameTooltip", pos, size, "UIParent",
                new("", 0, "TOOLTIP", "BOTTOMRIGHT", "UIParent", "BOTTOMRIGHT",
                    tooltipRightOffset, tooltipBottomOffset,
                    ClipRect: clip, ClipMask: "UIParent", Visible: true,
                    InteractionState: fading ? $"fading:{alpha:R}" : "shown",
                    Strata: "TOOLTIP"));
            CollectUiParityDraw("GameTooltip/Backdrop", "Backdrop", pos, size, "GameTooltip",
                new(@"Interface\Tooltips\UI-Tooltip-Background", 0xffffffff, "BACKGROUND",
                    "TOPLEFT", "GameTooltip", "TOPLEFT", 0, 0, ClipRect: clip,
                    ClipMask: "GameTooltip", BlendMode: "BLEND", Strata: "TOOLTIP"));
            for (int i = 0; i < lines.Length; i++)
            {
                Vector2 lineMin = pos + new Vector2(
                    PartyFrameUiLaw.TooltipPadding * s, layout.RowTops[i]);
                Vector2 lineSize = new(rowWidths[i], rowHeights[i]);
                Vector4 color = i == 0 ? runtime.NameColor : Vector4.One;
                color.W *= alpha;
                FontObjectSpec font = FontObjectLaw.Get(fontObjects[i]);
                CollectUiParityDraw($"GameTooltipTextLeft{i + 1}", "FontString", lineMin,
                    lineSize, "GameTooltip", new("", ImGui.ColorConvertFloat4ToU32(color),
                        "TEXT", "TOPLEFT", "GameTooltip", "TOPLEFT",
                        PartyFrameUiLaw.TooltipPadding, -(layout.RowTops[i] / s),
                        FontPath: font.Face, FontSize: font.Height,
                        ClipRect: clip, ClipMask: "GameTooltip", Strata: "TOOLTIP"));
            }
            if (tooltipHealth.Visible)
                CollectUiParityDraw("GameTooltipStatusBar", "StatusBar", barMin, barSize,
                    "GameTooltip", new(@"Interface\TargetingFrame\UI-TargetingFrame-BarFill",
                        ImGui.ColorConvertFloat4ToU32(new Vector4(0, 1, 0, alpha)), "BARS",
                        "TOPLEFT", "GameTooltip", "BOTTOMLEFT", 2, -1,
                        TexCoords: $"0|0|{healthFraction:R}|1",
                        ContentRect: new(barMin.X, barMin.Y,
                            barMin.X + barSize.X * healthFraction, barMin.Y + barSize.Y),
                        ClipRect: clip, ClipMask: "UIParent", BlendMode: "BLEND",
                        Visible: true, InteractionState:
                            $"min=0;max={tooltipHealth.Maximum};value={tooltipHealth.Value}",
                        Strata: "TOOLTIP"));
            else
                ClassifyUiParity("GameTooltipStatusBar", "StatusBar", "GameTooltip",
                    "NOT-DRAWN", "party-tooltip-slot-token-is-absent-during-fade");
        }
        dl.PopClipRect();
        ImGui.End();
    }

    private void DrawPartyInvite()
    {
        (int Slot, StaticPopupCoordinatorLaw.Instance Instance)? popup =
            PartyFrameUiLaw.PartyInvitePopup(_staticPopupSlots);
        // PARTY_INVITE is currently the sole participant and therefore owns slot one. Shared
        // slot-two presentation remains an explicit later integration boundary.
        if (popup is not { } visible || visible.Slot != 1 || _skin is null) return;
        float s = GameplayUiScale();
        string text = $"{visible.Instance.DataToken ?? ""} invites you to a group.";
        string[] lines = WrapTooltipText(text, "GameFontHighlight", s,
            PartyFrameUiLaw.PopupTextWidth * s).ToArray();
        float textHeight = lines.Length * GameText.LinePitch("GameFontHighlight", s);
        Vector2 display = ImGui.GetIO().DisplaySize;
        Vector2 size = new(PartyFrameUiLaw.PopupWidth * s,
            PartyFrameUiLaw.PopupHeight(textHeight / s) * s);
        Vector2 origin = new((display.X - size.X) * .5f, 128 * s);
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav;
        bool begun = ImGui.Begin("##party-invite", flags);
        ImGui.PopStyleVar(2);
        if (!begun) { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        bool capture = _uiParityArmed && _uiParityPanel == "party-invite";
        if (capture) BeginUiParityFrame(origin, s);
        Vector4 clip = new(0, 0, display.X, display.Y);
        dl.PushClipRectFullScreen();
        _skin.DrawBackdrop(dl, origin, origin + size, WowSkin.Dialog);
        for (int i = 0; i < lines.Length; i++)
            GameText.DrawCentered(dl, "GameFontHighlight", lines[i],
                origin + new Vector2(PartyFrameUiLaw.PopupWidth * .5f,
                    PartyFrameUiLaw.PopupTextTop +
                    (i + .5f) * GameText.LinePitch("GameFontHighlight", 1)) * s, s);
        float buttonTop = PartyFrameUiLaw.PopupButtonTop(textHeight / s);
        bool accept = DrawPartyInviteButton(dl, "StaticPopup1Button1", "Accept",
            origin + new Vector2(PartyFrameUiLaw.PopupButtonOneX, buttonTop) * s,
            s, capture, clip);
        bool decline = DrawPartyInviteButton(dl, "StaticPopup1Button2", "Decline",
            origin + new Vector2(PartyFrameUiLaw.PopupButtonTwoX, buttonTop) * s,
            s, capture, clip);
        if (capture)
        {
            CollectUiParityDraw("StaticPopup1", "Frame", origin, size, "UIParent",
                new("", 0, "HOST", "TOP", "UIParent", "TOP", 0, -128,
                    ClipRect: clip, ClipMask: "UIParent", Strata: "DIALOG"));
            CollectUiParityDraw("StaticPopup1/Backdrop", "Backdrop", origin, size, "StaticPopup1",
                new(@"Interface\DialogFrame\UI-DialogBox-Background", 0xffffffff,
                    "BACKGROUND", "TOPLEFT", "StaticPopup1", "TOPLEFT", 0, 0,
                    ClipRect: clip, ClipMask: "UIParent", BlendMode: "BLEND", Strata: "DIALOG"));
            CollectUiParityDraw("StaticPopup1/BackdropBorder", "Texture", origin, size,
                "StaticPopup1", new(@"Interface\DialogFrame\UI-DialogBox-Border", 0xffffffff,
                    "BORDER", "TOPLEFT", "StaticPopup1", "TOPLEFT", 0, 0,
                    ClipRect: clip, ClipMask: "UIParent", BlendMode: "BLEND", Strata: "DIALOG"));
            for (int i = 0; i < lines.Length; i++)
            {
                Vector2 lineSize = new(GameText.MeasureWidth("GameFontHighlight", lines[i], s),
                    GameText.LinePitch("GameFontHighlight", s));
                Vector2 lineMin = origin + new Vector2((size.X - lineSize.X) * .5f,
                    PartyFrameUiLaw.PopupTextTop * s + i * lineSize.Y);
                CollectUiParityDraw($"StaticPopup1Text/Line{i + 1}", "FontString", lineMin,
                    lineSize, "StaticPopup1", new("", FontObjectLaw.Get("GameFontHighlight").Color,
                        "TEXT", "TOP", "StaticPopup1", "TOP", 0,
                        -(PartyFrameUiLaw.PopupTextTop +
                          i * GameText.LinePitch("GameFontHighlight", 1)),
                        FontPath: FontObjectLaw.Get("GameFontHighlight").Face,
                        FontSize: FontObjectLaw.Get("GameFontHighlight").Height,
                        ClipRect: clip, ClipMask: "UIParent", Strata: "DIALOG"));
            }
            ClassifyUiParity("StaticPopup1AlertIcon", "Texture", "StaticPopup1", "NOT-DRAWN",
                "PARTY_INVITE-showAlert-is-absent");
            MarkUiParityFrameComplete();
        }
        dl.PopClipRect();
        ImGui.End();
        // PARTY_INVITE callbacks neither keep the dialog open nor synchronously replace its type;
        // the shared click driver's callback-reentry branches remain an explicit later boundary.
        if (accept)
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
                _staticPopupSlots, visible.Slot, buttonIndex: 1));
        else if (decline)
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
                _staticPopupSlots, visible.Slot, buttonIndex: 2));
    }

    private bool DrawPartyInviteButton(ImDrawListPtr dl, string element, string caption,
        Vector2 min, float s, bool capture, Vector4 clip)
    {
        Vector2 size = new Vector2(PartyFrameUiLaw.PopupButtonWidth,
            PartyFrameUiLaw.PopupButtonHeight) * s;
        ImGui.SetCursorScreenPos(min);
        bool clicked = ImGui.InvisibleButton($"##party-{caption}", size);
        bool held = ImGui.IsItemActive();
        bool hovered = ImGui.IsItemHovered();
        bool pushed = PartyFrameUiLaw.InviteButtonPushed(held, hovered);
        string statePath = pushed
            ? @"Interface\Buttons\UI-DialogBox-Button-Down"
            : @"Interface\Buttons\UI-DialogBox-Button-Up";
        uint art = _skin!.TextureHandle(pushed ? "dialog.button.down" : "dialog.button.up");
        if (art != 0) dl.AddImage((nint)art, min, min + size, Vector2.Zero, new Vector2(1f, .625f));
        if (hovered)
        {
            uint hi = _gameplayArt?.AdditiveHandle(
                @"Interface\Buttons\UI-DialogBox-Button-Highlight") ?? 0;
            if (hi != 0) dl.AddImage((nint)hi, min, min + size,
                Vector2.Zero, new Vector2(1f, .625f));
        }
        string fontObject = hovered ? "GameFontHighlight" : "GameFontNormal";
        GameText.DrawCentered(dl, fontObject, caption, min + size * .5f, s);
        if (capture)
        {
            string interaction = pushed ? "pressed" : hovered ? "hovered" : "normal";
            CollectUiParityDraw(element, "Button", min, size, "StaticPopup1",
                new("", 0, "FRAMES", "TOPLEFT", "StaticPopup1", "TOPLEFT",
                    (min.X - _uiParityOrigin.X) / s,
                    -((min.Y - _uiParityOrigin.Y) / s), ClipRect: clip,
                    ClipMask: "UIParent", Visible: true, Enabled: true,
                    InteractionState: interaction, HitMin: min, HitMax: min + size,
                    Strata: "DIALOG"));
            CollectUiParityDraw(element + (pushed ? "/PushedTexture" : "/NormalTexture"),
                pushed ? "PushedTexture" : "NormalTexture", min, size, element,
                new(statePath, 0xffffffff, "ARTWORK", "TOPLEFT", element, "TOPLEFT", 0, 0,
                    TexCoords: "0|0|1|0.625", ClipRect: clip, ClipMask: "UIParent",
                    BlendMode: "BLEND", Strata: "DIALOG"));
            if (hovered)
                CollectUiParityDraw(element + "/HighlightTexture", "HighlightTexture", min,
                    size, element, new(@"Interface\Buttons\UI-DialogBox-Button-Highlight",
                        0xffffffff, "HIGHLIGHT", "TOPLEFT", element, "TOPLEFT", 0, 0,
                        TexCoords: "0|0|1|0.625", ClipRect: clip, ClipMask: "UIParent",
                        BlendMode: "ADD", Strata: "DIALOG"));
            else
                ClassifyUiParity(element + "/HighlightTexture", "HighlightTexture", element,
                    "NOT-DRAWN", "button-is-not-hovered");
            FontObjectSpec font = FontObjectLaw.Get(fontObject);
            Vector2 textSize = new(GameText.MeasureWidth(fontObject, caption, s),
                GameText.EmPixels(fontObject, s));
            Vector2 textMin = min + (size - textSize) * .5f;
            CollectUiParityDraw(element + "/Text", "FontString", textMin, textSize, element,
                new("", font.Color, "OVERLAY", "CENTER", element, "CENTER", 0, 0,
                    FontPath: font.Face, FontSize: font.Height, ClipRect: clip,
                    ClipMask: "UIParent", Strata: "DIALOG"));
        }
        return clicked;
    }
}
