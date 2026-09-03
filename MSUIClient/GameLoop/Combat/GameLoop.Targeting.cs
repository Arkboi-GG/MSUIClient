using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Units;

namespace MSUIClient;

/// <summary>
/// Local target acquisition and the small server command state machine shared by
/// selection and auto-attack. Units pick against the renderer's last completed posed
/// triangles; static world collision still wins when it is strictly nearer.
/// </summary>
public sealed partial class GameLoop
{
    private const uint NotSelectable = 1u << 25;
    private const uint AttackDisqualifiers =
        (1u << 1) | (1u << 7) | (1u << 16) | (1u << 20) | (1u << 25);
    private const float TargetPickDistance = 200f;

    private ulong _hoveredGuid;
    private ulong _selectionGuid;
    private ulong _attackTargetGuid;
    private long _targetCombatSeen;
    private ulong _selectionVitalsGuid;
    private bool _selectionWasDead;
    private TargetPressPick _leftTargetPressPick;
    private TargetPressPick _rightTargetPressPick;
    private bool _targetLeftWasDown;
    private bool _targetRightWasDown;
    private readonly List<UnitPickCandidate> _unitPickCandidates = [];
    private readonly Dictionary<ulong, string> _playerNames = [];
    private readonly Dictionary<ulong, PlayerTraits> _playerTraits = [];
    private readonly Dictionary<uint, string> _creatureNames = [];
    private readonly Dictionary<uint, string> _petNames = [];
    private readonly Dictionary<uint, CreatureQueryInfo?> _creatureQueryRecords = [];
    private readonly HashSet<ulong> _queriedPlayerNames = [];
    private readonly HashSet<uint> _queriedCreatureNames = [];
    private readonly HashSet<uint> _queriedPetNames = [];
    private readonly record struct UnitPickCandidate(
        ulong Guid, bool Dead, SpellUnitPose Pose);

    private void ResetTargeting()
    {
        CloseInspect(playSound: false);
        ResetAutoFollowSession();
        _questMarkerModels?.Clear();
        ResetNpcGreetingSoundState();
        ResetGameObjectSoundState();
        _hoveredGuid = 0;
        _hoveredGameObjectGuid = 0;
        _selectionGuid = 0;
        _attackTargetGuid = 0;
        _selectionVitalsGuid = 0;
        _selectionWasDead = false;
        _leftTargetPressPick = default;
        _rightTargetPressPick = default;
        _targetLeftWasDown = false;
        _targetRightWasDown = false;
        CancelRtsUnitCastTargeting(silent: true);
        _targetCycleHistory.Clear();
        // Resolved hit and negative records are template identities and survive zoning/session
        // teardown. Only an unanswered writer ask must become re-askable.
        _queriedCreatureNames.Clear();
        _queriedPetNames.Clear();
        _targetCombatSeen = _combat.AttackRevision;
        _window.ClearWorldClicks();
    }

    /// <summary>
    /// Player GUID lows belong to the mounted characters database, not the client
    /// process. An MMO/RTS world swap can reuse one for a different name, so only
    /// an authenticated session boundary may retain none of these identities.
    /// </summary>
    private void ResetPlayerIdentitySession()
    {
        _itemProficiencies.Clear();
        _playerNames.Clear();
        _playerTraits.Clear();
        _petNames.Clear();
        _queriedPlayerNames.Clear();
        _queriedPetNames.Clear();
        _chatNameQueried.Clear();
        _pendingChatMacros.Clear();
        _pendingChatXp.Clear();
        _pendingChatChannelNotices.Clear();
        _chatChannels.Clear();
        _chatLastTellTarget = "";
        CloseChatMenu();
    }

    private bool TryBeginCreatureQuery(uint entry) =>
        entry != 0 && !_creatureQueryRecords.ContainsKey(entry) &&
        _queriedCreatureNames.Add(entry);

    private string ResolveCreatureOrPetName(WorldEntity unit, string fallback)
    {
        if (GuidInfo.PetNumber(unit.Guid) is uint petNumber)
        {
            EnsureUnitNameRequested(unit);
            return _petNames.GetValueOrDefault(petNumber, fallback);
        }
        return _creatureNames.GetValueOrDefault(unit.Entry, fallback);
    }

    private void UpdateTargeting()
    {
        // Creator mode targets its locally spawned practice dummies with no net at all.
        if (_net is not { IsInWorld: true } && !_creatorWorldRequested) return;

        UpdateQuestGiverStatusQueries();
        UpdateTaxiNodeStatusQueries();

        // Freeze the exact previous-frame hover on the first primary-button down edge,
        // before this frame's camera-look gate clears hover. Release consumes this subject;
        // it never re-picks whatever later moved beneath the stored press pixel.
        bool leftDown = _window.MouseLeftDown;
        bool rightDown = _window.MouseRightDown;
        _leftTargetPressPick = TargetPressPickLaw.Update(
            _leftTargetPressPick, leftDown, _targetLeftWasDown, rightDown,
            _hoveredGuid, _hoveredGameObjectGuid, _groundCursorPoint);
        _rightTargetPressPick = TargetPressPickLaw.Update(
            _rightTargetPressPick, rightDown, _targetRightWasDown, leftDown,
            _hoveredGuid, _hoveredGameObjectGuid, _groundCursorPoint);
        (_leftTargetPressPick, _rightTargetPressPick) = TargetPressPickLaw.CancelChord(
            leftDown, rightDown, _leftTargetPressPick, _rightTargetPressPick);
        _targetLeftWasDown = leftDown;
        _targetRightWasDown = rightDown;

        // Reconcile the speculative local attack latch with the authoritative echo.
        if (_net is not null && _combat.AttackRevision != _targetCombatSeen)
        {
            _targetCombatSeen = _combat.AttackRevision;
            ulong previousAttack = _attackTargetGuid;
            _attackTargetGuid = _combat.TryGetAttackTarget(ControlledGuid, out ulong victim)
                ? victim
                : 0;
            if (previousAttack != _attackTargetGuid)
                ObserveCombatIntent(_attackTargetGuid != 0,
                    _attackTargetGuid != 0 ? _attackTargetGuid : previousAttack,
                    _attackTargetGuid != 0 ? "server-start" : _lastCombatStopCause);
        }

        // A dead target STAYS selected (the 1.12 client keeps the corpse in the target frame,
        // which is what the frame's "DEAD" line and corpse looting both rely on). Only a
        // despawn clears the selection.
        if (_selectionGuid == 0)
        {
            _selectionVitalsGuid = 0;
            _selectionWasDead = false;
        }
        else if (!_entities.TryGet(_selectionGuid, out WorldEntity? selectedVitals))
        {
            CommitSelection(0, beginAttack: false);
            _selectionVitalsGuid = 0;
            _selectionWasDead = false;
        }
        else
        {
            bool dead = selectedVitals.IsDead;
            bool died = SelectionRingLaw.DiedWhileSelected(
                _selectionVitalsGuid, _selectionWasDead, _selectionGuid, dead);
            _selectionVitalsGuid = _selectionGuid;
            _selectionWasDead = dead;
            if (died)
            {
                CommitSelection(0, beginAttack: false);
                _selectionVitalsGuid = 0;
                _selectionWasDead = false;
            }
        }

        if (!_window.MouseCaptured && !ImGui.GetIO().WantCaptureMouse && !_settingsOpen &&
            !IsQuestWatchTitleAt(_window.MousePosition))
        {
            _hoveredGuid = PickUnit(_window.MousePosition, out float unitHit);
            // Vanilla nearest-wins: a gameobject hovers only when its hit is
            // strictly nearer than any unit hit, and then it owns the hover -
            // the two hovers are exclusive by construction. Drives the doodad
            // highlight tint and the world-GO name tooltip.
            _hoveredGameObjectGuid = PickGameObject(_window.MousePosition, unitHit, out _);
            if (_hoveredGameObjectGuid != 0) _hoveredGuid = 0;
        }
        else
        {
            _hoveredGuid = 0;
            _hoveredGameObjectGuid = 0;
        }

        // Armed ground AoE: track the terrain point under the cursor every frame so the
        // render pass can draw the 1.12 targeting rune circle there in realtime.
        _groundCursorPoint = _groundCastSpell != 0 && !_window.MouseCaptured &&
            !ImGui.GetIO().WantCaptureMouse && !IsQuestWatchTitleAt(_window.MousePosition) &&
            TryPickGround(_window.MousePosition, out Vector3 aim)
            ? aim : null;

        // Targeting-cursor mode (armed ground AoE): a world left-click binds the terrain
        // point under the cursor and commits the cast; a right-click cancels. Matches the
        // 1.12 SpellIsTargeting machine — while armed, clicks never select or attack.
        // (The "Select target area" cursor hint is drawn from the action-bar ImGui pass —
        // this method runs in the update phase, where touching ImGui draw lists crashes.)
        while (_window.TryDequeueWorldClick(out WorldMouseClick click))
        {
            TargetPressPick pressPick = click.Button == MouseButton.Left
                ? _leftTargetPressPick : _rightTargetPressPick;
            if (click.Button == MouseButton.Left) _leftTargetPressPick = default;
            else _rightTargetPressPick = default;
            if (_settingsOpen || ImGui.GetIO().WantCaptureMouse) continue;
            if (TryToggleQuestWatchAt(
                click.Position, click.Button == MouseButton.Left)) continue;
            // NPC dev window: an armed edit mode (waypoint drawing / spawn move) owns
            // every world click, ahead of the free-view router - no stray RTS orders
            // while placing path nodes. No-op unless a mode is armed.
            if (HandleDevEditClick(click)) continue;
            // Encounter Lab: an armed placement (probe body, scenario actor, boss)
            // owns the click the same way, so dropping a probe never also issues an
            // order. No-op unless a placement is armed.
            if (HandleEncounterLabClick(click)) continue;
            // CRPG free view: clicks are selection + RTS orders, never target/attack/loot.
            // Keyed on the CAMERA, not the control state — commanding a toon from the sky
            // is still the sky, and its clicks are still orders.
            if (_freeView)
            {
                HandleFreeCamWorldClick(click, pressPick);
                continue;
            }
            if (_groundCastSpell != 0)
            {
                uint armed = _groundCastSpell;
                _groundCastSpell = 0;
                if (click.Button == MouseButton.Left)
                {
                    if (pressPick.Armed && pressPick.GroundPoint is Vector3 latchedGround)
                        CommitGroundCast(armed, latchedGround);
                    else if (!pressPick.Armed && TryPickGround(click.Position, out Vector3 spot))
                        CommitGroundCast(armed, spot);
                }
                continue;
            }
            ulong picked;
            float pickedUnitHit;
            if (pressPick.Armed)
            {
                picked = pressPick.UnitGuid;
                pickedUnitHit = picked == 0 ? float.PositiveInfinity : 0f;
            }
            else
            {
                picked = PickUnit(click.Position, out pickedUnitHit);
            }
            // A gameobject strictly in front of any unit owns a right-click:
            // vanilla routes it to the object's interaction (mailbox opens
            // mail, chest sends CMSG_GAMEOBJ_USE), never to selection.
            // UseGameObject already gates range, type and world-state.
            ulong goClicked = click.Button == MouseButton.Right
                ? pressPick.Armed
                    ? pressPick.GameObjectGuid
                    : PickGameObject(click.Position, pickedUnitHit, out _)
                : 0;
            if (goClicked != 0)
            {
                if (_entities.TryGet(goClicked, out WorldEntity clickedGo) &&
                    GameObjectHighlightable(clickedGo))
                    UseGameObject(goClicked);
                continue;
            }
            if (click.Button == MouseButton.Left)
            {
                // NPC dev window focus set: Ctrl+LeftClick multi-selects for the
                // "Selected only" overlay scope and consumes the click.
                if (HandleDevFocusClick(picked)) continue;
                // The world-model twin of PetFrame's DropItemOnUnit("pet"). Selection still
                // proceeds from this same press-latched pick whether feeding succeeds or refuses.
                if (HasCarriedItem && picked == _petGuid &&
                    _entities.TryGet(picked, out WorldEntity pickedPet) && pickedPet.IsUnit)
                    TryFeedCarriedItemToPet(pickedPet);
                RequestNpcSelectionGreeting(picked);
                CommitSelection(TargetClickLaw.LeftClickSelection(
                    _selectionGuid, picked, Settings.Controls.StickyTargeting),
                    beginAttack: false);
            }
            else if (click.Button == MouseButton.Right && picked != 0)
            {
                // Right-click routes by classification (benilla target/click.rs): a dead unit
                // carrying UNIT_DYNFLAG_LOOTABLE opens its loot; other corpses just select;
                // live hostiles begin the swing.
                if (_entities.TryGet(picked, out WorldEntity corpse) && corpse.IsDead)
                {
                    CommitSelection(picked, beginAttack: false);
                    if (corpse.IsCreature && corpse.Fields.Lootable) RequestLoot(picked);
                }
                else if (_entities.TryGet(picked, out WorldEntity npc) &&
                         WorldCursorServiceKind(npc) is { } service)
                {
                    CommitSelection(picked, beginAttack: false);
                    // Route from the same lowest-bit-wins classifier that chose the cursor.
                    // This keeps a gossip+vendor on Speak/gossip and a plain vendor on pouch/list.
                    if (service == WorldCursorKind.Pickup) RequestVendor(picked);
                    else if (service == WorldCursorKind.Taxi) RequestTaxiMap(picked);
                    else if (service == WorldCursorKind.Buy &&
                             (npc.NpcFlags & NpcBanker) != 0) RequestBank(picked);
                    // The auctioneer shares the Buy cursor with the banker (benilla
                    // target/click.rs: Buy + AUCTIONEER => MSG_AUCTION_HELLO). Without this
                    // arm it fell to gossip and the auction house could only be opened from
                    // the dev console. Reported 2026-09-01.
                    else if (service == WorldCursorKind.Buy &&
                             (npc.NpcFlags & NpcAuctioneer) != 0) RequestAuction(picked);
                    else RequestGossip(picked);
                }
                else if (_entities.TryGet(picked, out WorldEntity player) && player.IsPlayer)
                {
                    CommitSelection(picked, beginAttack: false);
                    // Portrait right-click always owns the menu. World-model right-click is
                    // selection-only by default, but MSUI Options can opt back into the same
                    // party/player/self menu at the pointer.
                    if (Settings.Controls.WorldPlayerContextMenus &&
                        UnitFrameMenuWhich(player) is { } which)
                        OpenUnitPopup(picked, which, click.Position, InspectBinding.Target);
                }
                else CommitSelection(picked, beginAttack: true); // empty right preserves
            }
        }

        if (_creatures is not null)
        {
            _creatures.HoveredGuid = _hoveredGuid;
            _creatures.SelectedGuid = _selectionGuid;
            // Free-view multi-selection wears the same target highlight as single targets;
            // while a marquee drag is live, the members it covers light up as a preview.
            _creatures.GroupSelectedGuids.Clear();
            foreach (ulong guid in _freecamSelection)
                _creatures.GroupSelectedGuids.Add(guid);
            AddMarqueePreview(_creatures.GroupSelectedGuids);

            // Encounter Lab is an authoring surface: the selected puppet's BODY, not merely
            // its ground ring, must remain unmistakable after the click. Keep this separate
            // from the ordinary RTS/target highlight so normal play is not blown out.
            _creatures.ProminentSelectedGuids.Clear();
            if (_encounterLabOpen)
                foreach (ulong guid in _creatures.GroupSelectedGuids)
                    if (EncounterRaidPuppetKey(guid) is not null)
                        _creatures.ProminentSelectedGuids.Add(guid);
        }
        // The hovered gameobject brightens exactly like a hovered creature; the
        // doodad renderer applies the same 64/255 boost to that one dynamic
        // placement in both its opaque and blended passes.
        if (_doodads is not null)
            _doodads.HighlightedDynamicKey = _hoveredGameObjectGuid != 0 &&
                _entities.TryGet(_hoveredGameObjectGuid, out WorldEntity hoveredGo) &&
                GameObjectBrightens(hoveredGo)
                    ? _hoveredGameObjectGuid : 0;
        UpdateInspectLifecycle();
    }

    private void DrawSelectionRing()
    {
        // Free View owns its selected-target ring in RenderRtsGroundFx: one clean projected halo,
        // including the WMO-floor fallback, rather than a stock ring plus an RTS marker stacked.
        if (_freeView || _spellEffectMeshes is null || _selectionGuid == 0 ||
            !_entities.TryGet(_selectionGuid, out WorldEntity target)) return;
        FactionReaction reaction = ReactionTargetTowardPlayer(target);
        uint uptime = MovementInfo.ClientUptimeMs();
        Vector3 color = SelectionRingLaw.TargetRgb(reaction, target.IsDead,
            _attackTargetGuid == target.Guid, uptime);
        float radius = _creatures?.SelectionRadius(target) ?? .7f * MathF.Max(.01f, target.Scale);
        _spellEffectMeshes.RenderUnitSelectionRing(
            _window.Camera, target.Position, radius, color);
    }

    private void DrawFishingLines()
    {
        if (_fishingLineRenderer is null || _doodads is null) return;

        IEnumerable<FishingPoleTipPlacement> tips =
            _character?.FishingPoleTips ?? Array.Empty<FishingPoleTipPlacement>();
        if (_creatures is not null) tips = tips.Concat(_creatures.FishingPoleTips);

        var spans = new List<FishingLineSpan>();
        foreach (FishingPoleTipPlacement tip in tips)
        {
            if (!_entities.TryGet(tip.OwnerGuid, out WorldEntity owner)) continue;
            ulong? targetGuid = owner.Fields.ChannelObject;
            if (targetGuid is not > 0 ||
                !_entities.TryGet(targetGuid.Value, out WorldEntity bobber) ||
                !FishingLineLaw.Eligible(owner.Fields.ChannelSpell, targetGuid,
                    bobber.IsGameObject, bobber.GameObjectType) ||
                !_doodads.TryGetDynamicFishingLineEnd(targetGuid.Value, out Vector3 far))
                continue;
            spans.Add(new FishingLineSpan(tip.WorldPosition, far));
        }

        Vector3 ambient = Vector3.Clamp(
            _atmosphere.AmbientColor * _atmosphere.AmbientIntensity,
            Vector3.Zero, Vector3.One);
        _fishingLineRenderer.Render(_window.Camera, spans, ambient);
    }

    private void CommitSelection(ulong guid, bool beginAttack)
    {
        if (_net is null && !_creatorWorldRequested) return;

        bool canAuthor = CanAuthorControlledGameplay;
        bool wasAttacking = canAuthor && (_attackTargetGuid != 0 ||
            (_net is not null && _combat.IsEngaged(ControlledGuid)));
        bool changed = guid != _selectionGuid;
        if (changed && canAuthor) StopPetAttackForOldTargetChange(_selectionGuid, guid);
        if (changed && wasAttacking)
        {
            EmitCombat("TargetSwitch", "selection-change", guid,
                $"from=0x{_selectionGuid:X16} to=0x{guid:X16}");
            StopAttack("target-switch");
        }

        if (changed)
        {
            _selectionGuid = guid;
            if (canAuthor) _net?.SetSelection(guid);
            if (guid != 0 && _net is not null && _entities.TryGet(guid, out WorldEntity identity))
            {
                if (identity.IsPlayer && _queriedPlayerNames.Add(guid)) _net.NameQuery(guid);
                else if (GuidInfo.PetNumber(identity.Guid) is uint petNumber)
                {
                    if (!_petNames.ContainsKey(petNumber) && _queriedPetNames.Add(petNumber))
                        _net.PetNameQuery(petNumber, guid);
                }
                else if (identity.IsCreature && TryBeginCreatureQuery(identity.Entry))
                    _net.CreatureQuery(identity.Entry, guid);
            }
        }

        // A running swing follows a valid target switch. A clean right click
        // starts it when it was not already active. Never offline - the creator
        // dummy is scenery, not an opponent.
        if (canAuthor && _net is not null && guid != 0 &&
            (beginAttack || (changed && wasAttacking)) &&
            _entities.TryGet(guid, out WorldEntity entity) && CanAttack(entity))
        {
            if (_attackTargetGuid != guid)
            {
                if (!ObserveAttackPrecondition(entity)) return;
                _net.AttackSwing(guid);
                _attackTargetGuid = guid; // speculative until SMSG_ATTACKSTART/STOP
                ObserveCombatIntent(true, guid, changed && wasAttacking ? "target-switch" : "user-start");
            }
        }
    }

    private bool TryClearTargetOnEscape()
    {
        if (_selectionGuid == 0) return false;
        CommitSelection(0, beginAttack: false);
        return true;
    }

    private void StopAttack(string cause = "user-cancel")
    {
        if (!CanAuthorControlledGameplay || _net is null ||
            (_attackTargetGuid == 0 && !_combat.IsEngaged(ControlledGuid))) return;
        _net.AttackStop();
        ObserveCombatIntent(false, _attackTargetGuid, cause);
        _attackTargetGuid = 0;
    }

    private bool CanAttack(WorldEntity target)
    {
        if (_net is null || target.Guid == ControlledGuid || target.IsDead ||
            (target.Fields.UnitFlags & AttackDisqualifiers) != 0)
            return false;

        // PvP/duel/group reaction is a later slice. Do not turn arbitrary nearby
        // players into hostile targets while that state is absent.
        if (target.IsPlayer) return false;

        return ReactionPlayerToward(target) != FactionReaction.Friendly;
    }

    private FactionReaction ReactionPlayerToward(WorldEntity target)
    {
        if (_net is null || _factions is null ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player) ||
            !_factions.TryGet(player.Fields.FactionTemplate, out FactionTemplateRow own) ||
            !_factions.TryGet(target.Fields.FactionTemplate, out FactionTemplateRow other))
            return FactionReaction.Neutral;
        return own.ReactionToward(other);
    }

    private FactionReaction ReactionTargetTowardPlayer(WorldEntity target)
    {
        if (_net is null || _factions is null ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player) ||
            !_factions.TryGet(target.Fields.FactionTemplate, out FactionTemplateRow other) ||
            !_factions.TryGet(player.Fields.FactionTemplate, out FactionTemplateRow own))
            return FactionReaction.Neutral;
        return other.ReactionToward(own);
    }

    /// <summary>Terrain point currently under the cursor while ground-targeting is armed
    /// (null when not armed or nothing pickable). Feeds the rune-circle marker draw.</summary>
    private Vector3? _groundCursorPoint;

    /// <summary>
    /// Resolve the terrain/world point under a window pixel for a ground-target cast.
    /// Prefers the collision mesh; falls back to marching the camera ray against the
    /// terrain heightfield and bisecting the crossing.
    /// </summary>
    private bool TryPickGround(Vector2 pixel, out Vector3 point) =>
        TryPickGround(pixel, out point, out _);

    /// <summary><paramref name="onTerrain"/>: the hit was the height field, not building or
    /// prop geometry - the Command View order gate treats a roof differently from a hill.</summary>
    private bool TryPickGround(Vector2 pixel, out Vector3 point, out bool onTerrain)
    {
        point = default;
        onTerrain = false;
        var ray = _window.Camera.ScreenPointToRay(pixel, _window.FramebufferSize);
        if (ray is null) return false;
        (Vector3 origin, Vector3 direction) = ray.Value;
        const float maxDistance = 250f;
        // Command View cut plane: geometry sliced out of the picture is sliced out of the pick
        // too, or a move order from above lands on the roof the player cannot see.
        bool CutAway(Vector3 p) => CommandViewCutAway(p);
        Vector3 castFrom = origin;
        float remaining = maxDistance;
        for (int pass = 0; pass < 8 && _collision is not null; pass++)
        {
            if (_collision.Raycast(castFrom, direction, remaining) is not { } hit) break;
            if (!CutAway(hit.Point))
            {
                point = hit.Point;
                return true;
            }
            float advance = hit.Distance + 0.05f;
            castFrom += direction * advance;
            remaining -= advance;
            if (remaining <= 0f) break;
        }
        if (_terrain is null) return false;
        float previous = 0f;
        for (float t = 1f; t <= maxDistance; t += 1f)
        {
            Vector3 sample = origin + direction * t;
            if (_terrain.SampleHeight(sample.X, sample.Y) is float ground && sample.Z <= ground &&
                !CutAway(sample with { Z = ground }))
            {
                float lo = previous, hi = t;
                for (int i = 0; i < 16; i++)
                {
                    float mid = (lo + hi) * .5f;
                    Vector3 m = origin + direction * mid;
                    if (_terrain.SampleHeight(m.X, m.Y) is float g && m.Z <= g) hi = mid;
                    else lo = mid;
                }
                Vector3 found = origin + direction * hi;
                point = found with { Z = _terrain.SampleHeight(found.X, found.Y) ?? found.Z };
                onTerrain = true;
                return true;
            }
            previous = t;
        }
        return false;
    }

    /// <summary>A world point the Command View cut plane has carved out of the picture.</summary>
    private bool CommandViewCutAway(Vector3 p) =>
        _freeView && _wmo?.ActiveCut is WorldCut c && p.Z > c.CutZ && c.Contains(p.X, p.Y);

    /// <summary>Solid world between the eye and a point <paramref name="reach"/> along the ray,
    /// ignoring geometry the cut plane has removed.</summary>
    private bool CommandViewOccluded(Vector3 origin, Vector3 direction, float reach)
    {
        if (_collision is null) return false;
        Vector3 from = origin;
        float remaining = reach;
        for (int pass = 0; pass < 8 && remaining > 0f; pass++)
        {
            if (_collision.Raycast(from, direction, remaining) is not { } hit) return false;
            if (!CommandViewCutAway(hit.Point)) return hit.Distance < remaining;
            float advance = hit.Distance + 0.05f;
            from += direction * advance;
            remaining -= advance;
        }
        return false;
    }

    private ulong PickUnit(Vector2 pixel) => PickUnit(pixel, out _);

    /// <summary>Same pick, plus how FAR the hit is — so the gameobject picker
    /// can lose to a unit in front of it. A nameplate rect hit reports 0 (UI
    /// always wins); no unit reports +infinity.</summary>
    private ulong PickUnit(Vector2 pixel, out float hitDistance)
    {
        hitDistance = float.PositiveInfinity;

        // Benilla vplates.rs:47-50,111-116: last frame's mouse-enabled plate rects feed
        // the shared hover/selection pick before the 3-D world ray.
        for (int i = _vplateHits.Count - 1; i >= 0; i--)
            if (_vplateHits[i].Rect.Contains(pixel))
            {
                hitDistance = 0f;
                return _vplateHits[i].Guid;
            }

        var ray = _window.Camera.ScreenPointToRay(pixel, _window.FramebufferSize);
        if (ray is null) return 0;

        (Vector3 origin, Vector3 direction) = ray.Value;
        float nearest = float.PositiveInfinity;
        ulong picked = 0;
        ulong previousPick = _hoveredGuid;
        _unitPickCandidates.Clear();

        foreach (WorldEntity entity in _entities.Units)
        {
            // Corpses stay pickable - selecting and right-click looting a dead unit is a
            // 1.12 behavior, not an exception. Only NOT_SELECTABLE and the player skip.
            // The controlled-unit skip lifts in the FREE VIEW: the controller is a
            // detached camera there and that body is just another toon on the field —
            // it remains selectable for RTS orders and explicit Alt+click direct control.
            // The free view's own streaming eye is the CAMERA, not a body on the field:
            // it must never be clickable, marquee-selectable, or orderable.
            if ((entity.Guid == ControlledGuid && !_freeView) ||
                IsViewAnchorUnit(entity.Guid) ||
                (entity.Fields.UnitFlags & NotSelectable) != 0)
                continue;

            // A pose is published only after the model actually drew. This makes the pick set
            // obey the same scene election as rendering: an unloaded, culled, or skipped body
            // cannot be targeted through a wall simply because its network row still exists.
            if (_creatures?.TryGetSpellPose(entity.Guid, out SpellUnitPose pose) != true)
                continue;
            _unitPickCandidates.Add(new UnitPickCandidate(entity.Guid, entity.IsDead, pose));
            if (_creatures.TryGetMountSpellPose(entity.Guid, out SpellUnitPose mountPose))
                _unitPickCandidates.Add(new UnitPickCandidate(entity.Guid, entity.IsDead, mountPose));
        }

        // Pass one: exact posed render triangles, pure nearest-wins across every unit and mount.
        foreach (UnitPickCandidate candidate in _unitPickCandidates)
            if (TargetMeshPickLaw.TryPick(candidate.Pose, origin, direction,
                    inflated: false, out float hit) && hit < nearest)
            {
                nearest = hit;
                picked = candidate.Guid;
            }

        // Pass two runs only when nothing hit exactly anywhere. Its priority ladder is sticky
        // previous hover, then alive over dead, with distance breaking equal-priority ties.
        if (picked == 0)
        {
            uint bestPriority = 0;
            foreach (UnitPickCandidate candidate in _unitPickCandidates)
            {
                if (!TargetMeshPickLaw.TryPick(candidate.Pose, origin, direction,
                        inflated: true, out float hit))
                    continue;
                uint priority = TargetMeshPickLaw.HaloPriority(
                    candidate.Guid == previousPick, candidate.Dead);
                if (!TargetMeshPickLaw.HaloWins(hit, priority, nearest, bestPriority)) continue;
                nearest = hit;
                bestPriority = priority;
                picked = candidate.Guid;
            }
        }

        // World geometry between the eye and the unit hides it from the pick - except geometry
        // the Command View cut away, which is still in the collision world. A corpse under a
        // roof or a mob under a cave ceiling was unhoverable from the sky until this looked
        // through the cut (owner, 2026-09-02: no loot cursor, sword half the time).
        if (picked != 0 && CommandViewOccluded(origin, direction, nearest))
            return 0;
        if (picked != 0) hitDistance = nearest;
        return picked;
    }

    private void DrawTargetFrame()
    {
        if (_selectionGuid == 0 || !_entities.TryGet(_selectionGuid, out WorldEntity target)) return;
        FactionReaction reaction = ReactionTargetTowardPlayer(target);
        string name = target.IsPlayer
            ? _playerNames.GetValueOrDefault(target.Guid, "Player")
            : ResolveCreatureOrPetName(target, $"Creature {target.Entry}");
        uint portrait = target.IsPlayer && target.Guid == ControlledGuid &&
                PlayerPortraitCurrent && !_freeView
            ? UnitFramePortrait(_playerPortrait, _playerPortraitUsable)
            : _portraitTargetGuid == target.Guid
                ? UnitFramePortrait(_targetPortrait, _targetPortraitUsable)
                : target.IsPlayer ? PartyPortraitHandle(target.Guid) : 0;
        DrawVanillaUnitFrame(target, new Vector2(250, 4), playerFrame: false,
            name, reaction, portrait, _targetCombatFlash);
    }
}
