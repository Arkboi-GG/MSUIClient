using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private ulong _petGuid;
    private readonly uint[] _petActions = new uint[PetActionBarUiLaw.SlotCount];
    private readonly List<uint> _petBookSpells = [];
    private readonly PlayerActions _petCooldowns = new();
    private uint _petState;
    private bool _petAttacking;
    private int _pressedPetActionSlot = -1;
    private ImGuiMouseButton _petActionPressMouseButton = ImGuiMouseButton.Left;
    private Vector2 _petActionPressPosition;
    private uint? _draggingPetAction;
    private bool _draggingPetActionPassive;
    private string _draggingPetActionIcon = "";
    private CreatureFamilyCatalog? _creatureFamilies;

    private static readonly IReadOnlyDictionary<string, string> PetStringFallbacks =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PET_SPELL_NOPATH"] = "Your pet has no path to that location.",
            ["SPELL_FAILED_OUT_OF_RANGE"] = "Out of range.",
            ["ERR_ATTACK_DEAD"] = "You can't attack while dead.",
            ["ERR_ATTACK_CHARMED"] = "You can't attack while charmed.",
            ["ERR_ATTACK_STUNNED"] = "You can't attack while stunned.",
            ["ERR_ATTACK_PACIFIED"] = "You can't attack while pacified.",
            ["ERR_ATTACK_FLEEING"] = "You can't attack while fleeing.",
            ["ERR_ATTACK_CONFUSED"] = "You can't attack while confused.",
            ["ERR_ATTACK_MOUNTED"] = "You can't attack while mounted.",
            ["ERR_NULL_PETNAME"] = "You must enter a name for your pet.",
        };
    private readonly Dictionary<string, string> _petGlobalStrings = [];
    private bool _petGlobalStringsLoaded;

    private void InitPetPaperDollData()
    {
        if (_mpq is null) return;
        try { _creatureFamilies = CreatureFamilyCatalog.Load(_mpq); }
        catch (Exception ex)
        {
            Console.WriteLine($"[pet-paper-doll] family catalogs failed: {ex.Message}");
        }
    }

    // Build-5875 SMSG_PET_SPELLS, including both the client and vmangos cooldown tails.
    private void ApplyPetSpells(byte[] body)
    {
        var r = new PacketReader(body);
        ulong guid = r.ReadU64();
        if (r.Remaining == 0)
        {
            // The only shipped guid-only form is the zero-guid teardown. Preserve the frozen
            // executable's honest non-zero quirk too: it becomes a blank bar, never stale slots.
            if (guid == 0) ResetPetActionBar();
            else
            {
                if (_petGuid != guid) _petAttacking = false;
                _petGuid = guid;
                _petState = 0;
                Array.Clear(_petActions);
                _petBookSpells.Clear();
                _petCooldowns.Clear();
            }
            return;
        }
        if (r.Remaining < 48)
            throw new InvalidDataException("short SMSG_PET_SPELLS");
        r.ReadU32(); // charm duration; zero for an ordinary pet
        uint state = r.ReadU32();
        uint[] actions = new uint[PetActionBarUiLaw.SlotCount];
        for (int i = 0; i < actions.Length; i++) actions[i] = r.ReadU32();

        if (r.Remaining < 1) throw new InvalidDataException("SMSG_PET_SPELLS missing spell count");
        int spellCount = r.ReadU8();
        var bookSpells = new List<uint>(spellCount);
        for (int i = 0; i < spellCount; i++) bookSpells.Add(r.ReadU32());
        if (r.Remaining < 1) throw new InvalidDataException("SMSG_PET_SPELLS missing cooldown count");
        int cooldownCount = r.ReadU8();
        bool vmangos = cooldownCount > 0 && r.Remaining == 1 + 14 * cooldownCount;
        if (vmangos) r.ReadU8(); // high byte of vmangos's u16 count
        var cooldowns = new List<(uint Spell, uint Category, uint SpellMs, uint CategoryMs)>(cooldownCount);
        for (int i = 0; i < cooldownCount; i++)
        {
            uint spell = vmangos ? r.ReadU32() : r.ReadU16();
            uint category = r.ReadU16();
            uint spellMs = r.ReadU32();
            uint categoryMs = PetActionBarUiLaw.StripPermanentCooldownMarker(r.ReadU32());
            cooldowns.Add((spell, category, spellMs, categoryMs));
        }

        // Parsing precedes mutation: a truncated replacement cannot paint a half-new bar.
        if (guid == 0)
        {
            ResetPetActionBar();
            return;
        }
        if (_petGuid != guid) _petAttacking = false;
        _petGuid = guid;
        _petState = state;
        actions.CopyTo(_petActions, 0);
        _petBookSpells.Clear();
        _petBookSpells.AddRange(bookSpells);
        _petCooldowns.Clear();
        double now = MovementInfo.ClientUptimeMs() / 1000.0;
        foreach (var cooldown in cooldowns)
        {
            bool wildcard = _spellCatalog?.TryGet(cooldown.Spell, out SpellInfo seededSpell) == true &&
                seededSpell.CategoryWildcard;
            _petCooldowns.StartCooldown(cooldown.Spell, cooldown.Category,
                cooldown.SpellMs, cooldown.CategoryMs, now, onHold: false,
                categoryWildcard: wildcard);
        }
    }

    // SMSG_SPELL_COOLDOWN is addressed. A pet's override belongs to the pet store, never the
    // player's same-spell clock; a player-addressed packet keeps the existing player path.
    private void ApplyAddressedSpellCooldowns(byte[] body)
    {
        if (body.Length < 8 || (body.Length - 8) % 8 != 0)
            throw new InvalidDataException("bad SMSG_SPELL_COOLDOWN body");
        var r = new PacketReader(body);
        ulong caster = r.ReadU64();
        PlayerActions? store = AddressedCooldownStore(caster);
        double now = MovementInfo.ClientUptimeMs() / 1000.0;
        while (r.Remaining > 0)
        {
            uint spellId = r.ReadU32();
            uint wireMs = r.ReadU32();
            if (store is null) continue;
            SpellInfo spell = default;
            bool resolved = _spellCatalog?.TryGet(spellId, out spell) == true;
            store.ApplyWireCooldown(spellId, wireMs, resolved ? spell : null, now);
        }
    }

    private PlayerActions? AddressedCooldownStore(ulong caster) =>
        caster != 0 && caster == _petGuid ? _petCooldowns :
        caster == LocalPlayerGuid || caster == ControlledGuid ? ActionsFor(caster) : null;

    private void ApplyItemCooldown(byte[] body, PlayerActions? forcedStore = null)
    {
        CooldownPackets.ItemCooldown packet = CooldownPackets.ParseItem(body);
        if (!_entities.TryGet(packet.ItemGuid, out WorldEntity item) ||
            item.Type is not (ObjectTypeId.Item or ObjectTypeId.Container)) return;
        (forcedStore ?? OwnActions).StartItemPacketCooldown(packet.SpellId, item.Entry,
            MovementInfo.ClientUptimeMs() / 1000.0);
    }

    private void ApplyCooldownEvent(byte[] body, bool clear)
    {
        string packetName = clear ? "SMSG_CLEAR_COOLDOWN" : "SMSG_COOLDOWN_EVENT";
        CooldownPackets.AddressedSpell packet =
            CooldownPackets.ParseAddressedSpell(body, packetName);
        PlayerActions? store = AddressedCooldownStore(packet.CasterGuid);
        if (store is null) return;
        if (clear) store.ClearCooldown(packet.SpellId);
        else store.StartCooldownEvent(packet.SpellId,
            MovementInfo.ClientUptimeMs() / 1000.0);
    }

    private void ApplyCooldownCheat(byte[] body)
    {
        ulong caster = CooldownPackets.ParseCheat(body);
        AddressedCooldownStore(caster)?.ClearAllCooldowns();
    }

    private void ApplyPetMode(byte[] body)
    {
        if (body.Length != 12) throw new InvalidDataException("bad SMSG_PET_MODE body");
        var r = new PacketReader(body);
        ulong guid = r.ReadU64();
        uint state = r.ReadU32();
        if (guid == _petGuid) _petState = state;
    }

    private void ApplyPetActionFeedback(byte[] body)
    {
        if (body.Length == 0) return;
        if (PetActionBarUiLaw.FeedbackKey(body[0]) is { } key)
            ShowUiError(PetGlobalString(key));
    }

    private void ApplyPetCastFailed(byte[] body)
    {
        if (body.Length < 5) return;
        var r = new PacketReader(body);
        uint spellId = r.ReadU32();
        byte status = r.ReadU8();
        if (status != 2) return;
        byte reason = r.ReadU8();
        string power = _spellCatalog?.TryGet(spellId, out SpellInfo spell) == true
            ? PowerName((byte)spell.PowerType) : "POWER";
        string text = SpellCastResultNames.Text(reason, power);
        ShowSpellError(spellId, SpellCastResultNames.Name(reason), text, "SMSG_PET_CAST_FAILED");
    }

    private void ResetPetActionBar()
    {
        _petGuid = 0;
        _petState = 0;
        _petAttacking = false;
        _petCooldowns.Clear();
        _draggingPetAction = null;
        _pressedPetActionSlot = -1;
        Array.Clear(_petActions);
        _petBookSpells.Clear();
    }

    private string PetGlobalString(string key)
    {
        if (!_petGlobalStringsLoaded)
        {
            _petGlobalStringsLoaded = true;
            byte[]? bytes = _mpq?.ReadFile(@"Interface\FrameXML\GlobalStrings.lua");
            if (bytes is not null)
            {
                string source = System.Text.Encoding.UTF8.GetString(bytes);
                foreach (string wanted in PetStringFallbacks.Keys)
                    if (TryReadLuaString(source, wanted, out string value))
                        _petGlobalStrings[wanted] = value;
            }
        }
        return _petGlobalStrings.GetValueOrDefault(key,
            PetStringFallbacks.GetValueOrDefault(key, key));
    }

    private bool TryGetControlledPet(out WorldEntity pet)
    {
        pet = null!;
        if (_net is null || !_entities.TryGet(ControlledGuid, out WorldEntity player)) return false;
        ulong guid = _petGuid != 0 ? _petGuid : player.Fields.Summon ?? player.Fields.Charm ?? 0;
        return guid != 0 && _entities.TryGet(guid, out pet) && pet.IsUnit;
    }

    private uint FeedPetSpell()
    {
        if (_spellCatalog is null) return 0;
        uint result = 0;
        // The driven body's spellbook (== OwnActions unpossessed).
        foreach (uint spellId in _actions.KnownSpells.OrderBy(id => id))
            if (_spellCatalog.TryGet(spellId, out SpellInfo spell) &&
                FeedPetLaw.IsFeedPetSpell(spell))
                result = spellId;
        return result;
    }

    private bool TryFeedCarriedItemToPet(WorldEntity pet)
    {
        if (!CanAuthorControlledGameplay || !HasCarriedItem || _net is null ||
            ResolveCarriedItem() is not { } item)
            return false;
        if (RefuseTacticalFrozenActor(pet.Guid, "feed it")) return false;
        uint spellId = FeedPetSpell();
        if (!FeedPetLaw.CanFeed(pet.Guid, _petGuid, pet.Fields.CreatedBySpell,
                pet.Fields.CreatedBy, ControlledGuid, spellId, item.Guid) ||
            !_net.CastSpellOnItem(spellId, item.Guid))
            return false;

        ClearCarriedItem();
        EmitInterface("pet", "feed", "SENT", pet.Guid,
            $"spell={spellId};item={item.Guid};entry={item.Entry}");
        return true;
    }

    private bool PetActionBarVisible => _petGuid != 0;

    /// <summary>
    /// [SUI] Control changed hands: the pet bar, loot window and bank session are
    /// body-scoped. The server pushes the new body's SMSG_PET_SPELLS right after the
    /// control ack (grant: the bot's, mirrored; release: the own character's), so an
    /// empty bar here means "this body has no pet", never a stale one.
    /// </summary>
    private void ResetBodySessionUiOnControlChange()
    {
        ResetPetActionBar();
        ClearLootOnControlChange();
        CloseBankSession(playSound: false);
        // A taxi map or ride belonged to the body we just left: the released bot keeps
        // flying under its own AI (the server never breaks possession for a flight, the
        // human simply hopped elsewhere), and nothing of that ride may steer this body.
        CloseTaxiMap(playSound: false);
        DiscardServerRideWithoutAck();
    }

    private void StopPetAttackForOldTargetChange(ulong previous, ulong current)
    {
        if (!CanAuthorControlledGameplay ||
            !PetActionBarUiLaw.StopsAttackOnSelectionChange(_petAttacking, previous, current)) return;
        if (RefuseTacticalFrozenActor(_petGuid, "change its attack state")) return;
        _net?.PetStopAttack(_petGuid);
        _petAttacking = false;
    }

    private void DrawPetFrameAndActionBar()
    {
        if (_gameplayArt is null) return;
        if (_freeView) return;   // commander console: no body chrome
        float s = GameplayUiScale();
        if (TryGetControlledPet(out WorldEntity framePet))
            DrawPetFrame(framePet, s); // Existing MSUI portrait/frame is intentionally preserved.
        if (!PetActionBarVisible) return;
        WorldEntity? actionPet = _entities.TryGet(_petGuid, out WorldEntity entity) && entity.IsUnit
            ? entity : null;
        DrawPetActionBar(_petGuid, actionPet, s);
    }

    /// <summary>
    /// The vanilla PetFrame, laid out from <see cref="PetFrameUiLaw"/> - every offset comes
    /// out of the shipped PetFrame.xml. What was here before was authored by eye: the frame
    /// sat at (10, 86) instead of the anchored (61, 64), and inside it the two status bars
    /// were 75x7 at (39, 12)/(39, 21) instead of 70x8 at (47, 22)/(47, 29) - eight pixels
    /// left and eight to ten pixels high of the recess UI-SmallTargetingFrame paints for
    /// them, which is the misalignment that shows.
    /// </summary>
    private void DrawPetFrame(WorldEntity pet, float s)
    {
        Vector2 p = PetFrameUiLaw.Origin * s;
        CollectGameplayLayout("pet-frame", PetFrameUiLaw.Origin.X, PetFrameUiLaw.Origin.Y,
            PetFrameUiLaw.Width, PetFrameUiLaw.Height, p, PetFrameUiLaw.Size * s);
        ImGui.SetNextWindowPos(p, ImGuiCond.Always);
        ImGui.SetNextWindowSize(PetFrameUiLaw.Size * s, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.Zero);
        if (!ImGui.Begin("##vanilla-pet-frame", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        { ImGui.End(); ImGui.PopStyleVar(3); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        dl.PushClipRectFullScreen();
        ImGui.SetCursorScreenPos(p);
        // The whole frame stays the click target; vanilla's HitRectInsets narrow it to the
        // portrait half, which PetFrameUiLaw records but this does not apply.
        ImGui.InvisibleButton("##pet-frame-input", PetFrameUiLaw.Size * s,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        bool leftClicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        bool rightClicked = ImGui.IsItemClicked(ImGuiMouseButton.Right);
        Vector2 portrait = p + PetFrameUiLaw.PortraitOffset * s;
        DrawUnitPortraitImage(dl, pet, portrait, PetFrameUiLaw.PortraitSize * s, 0, false);
        DrawArt(dl, PetFrameUiLaw.FrameTexture, p + PetFrameUiLaw.TextureOffset * s,
            PetFrameUiLaw.TextureSize, s);
        DrawVanillaStatusBar(dl, p + PetFrameUiLaw.HealthBarOffset * s,
            PetFrameUiLaw.BarSize * s, pet.HealthFraction, new Vector4(0, 1, 0, 1));
        DrawVanillaStatusBar(dl, p + PetFrameUiLaw.ManaBarOffset * s,
            PetFrameUiLaw.BarSize * s, pet.PowerFraction, PowerColor(pet.Fields.PowerType));
        // PetName is LEFT-aligned in the shipped frame, so it is placed by its own box
        // rather than through the centre-anchored player/target name helper.
        string name = ResolveCreatureOrPetName(pet, "Pet");
        GameText.Draw(dl, PetFrameUiLaw.NameFont, name,
            p + new Vector2(PetFrameUiLaw.NameLeft, PetFrameUiLaw.NameBottom) * s -
                new Vector2(0, GameText.EmPixels(PetFrameUiLaw.NameFont, s)),
            s, UiGoldU32());
        dl.PopClipRect();
        ImGui.End();
        ImGui.PopStyleVar(3);
        if (leftClicked)
        {
            // PetFrame_OnClick gives a held item to the pet instead of selecting it. A refusal is
            // silent and retains the cursor; only a successful item-target cast clears it.
            if (HasCarriedItem) TryFeedCarriedItemToPet(pet);
            else CommitSelection(pet.Guid, beginAttack: false);
        }
        else if (rightClicked)
            OpenUnitPopup(pet.Guid, UnitPopupWhich.Pet, ImGui.GetMousePos(),
                InspectBinding.Target);
    }

    private void DrawPetActionBar(ulong petGuid, WorldEntity? pet, float s)
    {
        Vector2 display = ImGui.GetIO().DisplaySize;
        Vector2 mainBar = GameplayBarMin(display, s);
        // MSUI's two bottom multibars are always drawn. The pet row therefore always occupies
        // their authored upper seat and the optional standalone shelf is never painted.
        float topOffset = PetActionBarUiLaw.BaseTopOffset + PetActionBarUiLaw.BottomMultiBarStep;
        Vector2 p = new(mainBar.X + PetActionBarUiLaw.BaseX * s,
            display.Y - topOffset * s);
        bool proof = _uiParityArmed && _uiParityPanel == "pet-action-bar";
        bool gridShown = _draggingPetAction.HasValue;
        var resolvedSpells = new SpellInfo?[PetActionBarUiLaw.SlotCount];
        var iconPaths = new string[PetActionBarUiLaw.SlotCount];
        var namedSlots = new bool[PetActionBarUiLaw.SlotCount];
        var activeAuraSlots = new bool[PetActionBarUiLaw.SlotCount];
        var interactiveSlots = new bool[PetActionBarUiLaw.SlotCount];
        for (int i = 0; i < PetActionBarUiLaw.SlotCount; i++)
        {
            uint packed = _petActions[i];
            uint action = PetActionBarUiLaw.Action(packed);
            byte kind = PetActionBarUiLaw.Kind(packed);
            if (PetActionBarUiLaw.IsSpell(packed) &&
                _spellCatalog?.TryGet(action, out SpellInfo spell) == true)
            {
                resolvedSpells[i] = spell;
                namedSlots[i] = true;
                activeAuraSlots[i] = IsPetSpellShowingActive(packed, spell, pet);
                iconPaths[i] = activeAuraSlots[i] ? spell.ActiveIconPath : spell.IconPath;
            }
            else if (PetTokenName(action, kind) is not null)
            {
                namedSlots[i] = true;
                iconPaths[i] = PetActionIcon(action, kind, pet);
            }
            interactiveSlots[i] = PetActionBarUiLaw.InteractiveSlot(namedSlots[i], gridShown);
        }

        if (_pressedPetActionSlot >= 0 &&
            ActionBarLockLaw.DragGestureAllowed(Settings.Controls.LockActionBars) &&
            ImGui.IsMouseDown(_petActionPressMouseButton) &&
            Vector2.Distance(ImGui.GetIO().MousePos, _petActionPressPosition) > 6f * s)
        {
            PickupPetAction(_pressedPetActionSlot, petGuid, pet);
            _pressedPetActionSlot = -1;
        }

        var clickedLeft = new bool[PetActionBarUiLaw.SlotCount];
        var clickedRight = new bool[PetActionBarUiLaw.SlotCount];
        var hoveredSlots = new bool[PetActionBarUiLaw.SlotCount];
        var pushedSlots = new bool[PetActionBarUiLaw.SlotCount];
        ImGuiWindowFlags inputFlags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoBringToFrontOnFocus;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.Zero);
        for (int i = 0; i < PetActionBarUiLaw.SlotCount; i++)
        {
            if (!interactiveSlots[i]) continue;
            Vector2 min = p + new Vector2(PetActionBarUiLaw.ButtonX(i),
                PetActionBarUiLaw.ButtonTop) * s;
            Vector2 size = new Vector2(PetActionBarUiLaw.ButtonSize) * s;
            ImGui.SetNextWindowPos(min, ImGuiCond.Always);
            ImGui.SetNextWindowSize(size, ImGuiCond.Always);
            ImGui.SetNextWindowBgAlpha(0);
            if (ImGui.Begin($"##pet-action-hit-{i}", inputFlags))
            {
                ImGui.SetCursorScreenPos(min);
                bool clicked = ImGui.InvisibleButton($"##pet-action-{i}", size,
                    ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
                hoveredSlots[i] = ImGui.IsItemHovered(
                    ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
                pushedSlots[i] = ImGui.IsItemActive() || BindingDown(BonusActionBinding(i));
                if (ImGui.IsItemActivated())
                {
                    _pressedPetActionSlot = i;
                    _petActionPressPosition = ImGui.GetIO().MousePos;
                    _petActionPressMouseButton = ImGui.IsMouseDown(ImGuiMouseButton.Right)
                        ? ImGuiMouseButton.Right : ImGuiMouseButton.Left;
                }
                clickedLeft[i] = clicked && ImGui.IsMouseReleased(ImGuiMouseButton.Left);
                clickedRight[i] = clicked && ImGui.IsMouseReleased(ImGuiMouseButton.Right);
            }
            ImGui.End();
        }
        ImGui.PopStyleVar(3);

        ImGui.SetNextWindowPos(p, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(PetActionBarUiLaw.FrameWidth,
            PetActionBarUiLaw.FrameHeight) * s, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGuiWindowFlags drawFlags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoMouseInputs;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.Zero);
        if (!ImGui.Begin("##vanilla-pet-action-bar", drawFlags))
        {
            ImGui.End();
            ImGui.PopStyleVar(3);
            return;
        }

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        dl.PushClipRectFullScreen();
        Vector4 fullClip = new(0, 0, display.X, display.Y);
        if (proof)
        {
            BeginUiParityFrame(p, s);
            CollectUiParityDraw("PetActionBarFrame", "Frame", p,
                new Vector2(PetActionBarUiLaw.FrameWidth, PetActionBarUiLaw.FrameHeight) * s, "",
                new("", 0, "IMGUI_HOST", "TOPLEFT", "MainMenuBar", "BOTTOMLEFT",
                    PetActionBarUiLaw.BaseX, topOffset, ClipRect: fullClip, Visible: true,
                    Enabled: false, InteractionState: "parent-not-mouse-enabled", Strata: "HIGH"));
            ClassifyUiParity("PetActionBarTexture0", "Texture", "PetActionBarFrame", "NOT-DRAWN",
                "bottom-multibars-always-visible");
            ClassifyUiParity("PetActionBarTexture1", "Texture", "PetActionBarFrame", "NOT-DRAWN",
                "bottom-multibars-always-visible");
        }

        int hoveredSlot = -1;
        PreparedSharedSpellTooltip? hoveredSpellTooltip = null;
        bool usable = PetActionBarUiLaw.Usable(_petState, pet?.Fields.UnitFlags ?? 0);
        double now = MovementInfo.ClientUptimeMs() / 1000.0;
        bool pickedUpOnClick = false;
        for (int i = 0; i < PetActionBarUiLaw.SlotCount; i++)
        {
            string button = $"PetActionButton{i + 1}";
            uint packed = _petActions[i];
            uint action = PetActionBarUiLaw.Action(packed);
            byte kind = PetActionBarUiLaw.Kind(packed);
            Vector2 min = p + new Vector2(PetActionBarUiLaw.ButtonX(i),
                PetActionBarUiLaw.ButtonTop) * s;
            Vector2 max = min + new Vector2(PetActionBarUiLaw.ButtonSize) * s;
            bool hovered = hoveredSlots[i];
            bool pushed = pushedSlots[i];
            bool interactive = interactiveSlots[i];
            if (hovered) hoveredSlot = i;
            if (proof)
                CollectUiParityDraw(button, "CheckButton", min, max - min, "PetActionBarFrame",
                    new("", 0, "IMGUI_HIT_TARGET", i == 0 ? "BOTTOMLEFT" : "LEFT",
                        i == 0 ? "PetActionBarFrame" : $"PetActionButton{i}",
                        i == 0 ? "BOTTOMLEFT" : "RIGHT", i == 0 ? PetActionBarUiLaw.BaseX :
                            i == 6 ? PetActionBarUiLaw.MiddleStep : 8, PetActionBarUiLaw.ButtonBottomOffset,
                        ClipRect: fullClip, Visible: interactive, Enabled: interactive,
                        InteractionState: !interactive ? "hidden-unnamed" : pushed ? "pushed" :
                            hovered ? "hovered" : "normal",
                        HitMin: interactive ? min : null, HitMax: interactive ? max : null,
                        Strata: "HIGH"));

            if (!namedSlots[i] && !gridShown)
            {
                if (proof)
                {
                    ClassifyUiParity(button + "Icon", "Texture", button, "NOT-DRAWN",
                        "unnamed-slot-hidden");
                    ClassifyUiParity(button + "NormalTexture", "NormalTexture", button, "NOT-DRAWN",
                        "unnamed-slot-hidden");
                    ClassifyUiParity(button + "AutoCastable", "Texture", button, "NOT-DRAWN",
                        "unnamed-slot-hidden");
                    ClassifyUiParity(button + "AutoCastShine", "Texture", button, "NOT-DRAWN",
                        "unnamed-slot-hidden");
                    ClassifyUiParity(button + "Cooldown", "Cooldown", button, "NOT-DRAWN",
                        "unnamed-slot-hidden");
                    ClassifyUiParity(button + "CheckedTexture", "CheckedTexture", button, "NOT-DRAWN",
                        "unnamed-slot-hidden");
                    ClassifyUiParity(button + "PushedTexture", "PushedTexture", button, "NOT-DRAWN",
                        "unnamed-slot-hidden");
                    ClassifyUiParity(button + "HighlightTexture", "HighlightTexture", button, "NOT-DRAWN",
                        "unnamed-slot-hidden");
                }
                continue;
            }

            bool hasNamedPayload = namedSlots[i];
            string iconPath = iconPaths[i];
            uint icon = hasNamedPayload && iconPath.Length > 0 ? _gameplayArt!.Handle(iconPath) : 0;
            uint iconTint = usable ? 0xffff_ffffu : 0xff80_8080u;
            if (icon != 0)
                dl.AddImage((nint)icon, min, max, Vector2.Zero, Vector2.One, iconTint);
            if (proof)
            {
                if (icon != 0)
                    CollectUiParityDraw(button + "Icon", "Texture", min, max - min, button,
                        new(iconPath, iconTint, "BACKGROUND", "CENTER", button, "CENTER", 0, 0,
                            ClipRect: fullClip, Visible: true, BlendMode: "BLEND", Strata: "HIGH"));
                else
                    ClassifyUiParity(button + "Icon", "Texture", button, "NOT-DRAWN",
                        hasNamedPayload ? "resolved-action-has-no-drawable-icon" : "empty-drop-grid-slot");
            }

            string ringPath = iconPath.Length > 0
                ? @"Interface\Buttons\UI-Quickslot2" : @"Interface\Buttons\UI-Quickslot";
            uint ring = _gameplayArt!.Handle(ringPath);
            Vector2 ringCenter = (min + max) * .5f +
                new Vector2(PetActionBarUiLaw.NormalTextureOffset.X,
                    -PetActionBarUiLaw.NormalTextureOffset.Y) * s;
            Vector2 ringHalf = new(PetActionBarUiLaw.NormalTextureSize * .5f * s);
            if (ring != 0)
                dl.AddImage((nint)ring, ringCenter - ringHalf, ringCenter + ringHalf);
            if (proof)
                CollectUiParityDraw(button + "NormalTexture", "NormalTexture",
                    ringCenter - ringHalf, ringHalf * 2, button,
                    new(ringPath, 0xffffffff, "ARTWORK", "CENTER", button, "CENTER", 0, -1,
                        ClipRect: fullClip, Visible: ring != 0, BlendMode: "BLEND", Strata: "HIGH"));

            bool autocastable = resolvedSpells[i] is not null &&
                PetActionBarUiLaw.Autocastable(packed, spellResolved: true);
            if (autocastable)
            {
                uint overlay = _gameplayArt.Handle(@"Interface\Buttons\UI-AutoCastableOverlay");
                float overlayMargin = (PetActionBarUiLaw.AutoCastOverlaySize -
                    PetActionBarUiLaw.ButtonSize) * .5f;
                Vector2 overlayMin = min - new Vector2(overlayMargin) * s;
                Vector2 overlayMax = max + new Vector2(overlayMargin) * s;
                if (overlay != 0) dl.AddImage((nint)overlay, overlayMin, overlayMax);
                if (proof)
                    CollectUiParityDraw(button + "AutoCastable", "Texture", overlayMin,
                        overlayMax - overlayMin, button,
                        new(@"Interface\Buttons\UI-AutoCastableOverlay", 0xffffffff, "OVERLAY",
                            "CENTER", button, "CENTER", 0, 0, ClipRect: fullClip,
                            Visible: overlay != 0, BlendMode: "BLEND", Strata: "HIGH"));
                if (PetActionBarUiLaw.Autocasting(packed))
                {
                    DrawPetAutocastSparkles(dl, min, s, now);
                    if (proof)
                        CollectUiParityDraw(button + "AutoCastShine", "Texture",
                            min + new Vector2(.6f) * s, new Vector2(28.8f) * s, button,
                            new(@"Interface\Buttons\GlowStar", 0xffffffff, "OVERLAY",
                                "CENTER", button, "CENTER", 0, 0, ClipRect: fullClip,
                                ClipMask: "FOUR_EMITTER_M2_LINEAR_TRAILS", Visible: true,
                                BlendMode: "ADD", InteractionState: "autocasting", Strata: "HIGH"));
                }
                else if (proof)
                    ClassifyUiParity(button + "AutoCastShine", "Texture", button, "NOT-DRAWN",
                        "autocast-disabled");
            }
            else if (proof)
            {
                ClassifyUiParity(button + "AutoCastable", "Texture", button, "NOT-DRAWN",
                    "spell-unresolved-or-not-autocastable");
                ClassifyUiParity(button + "AutoCastShine", "Texture", button, "NOT-DRAWN",
                    "spell-unresolved-or-not-autocasting");
            }

            bool cooldownVisible = false;
            if (resolvedSpells[i] is SpellInfo cooldownSpell &&
                _petCooldowns.TryCooldownDisplay(action, 0, cooldownSpell, now,
                    out CooldownDisplay cooldown))
            {
                Vector2 center = (min + max) * .5f +
                    new Vector2(PetActionBarUiLaw.CooldownOffset.X,
                        -PetActionBarUiLaw.CooldownOffset.Y) * s;
                Vector2 half = new(PetActionBarUiLaw.CooldownSize * .5f * s);
                if (cooldown.SweepFraction is { } sweep)
                {
                    cooldownVisible = true;
                    DrawCooldownSwipe(dl, center - half, center + half, sweep);
                    if (proof)
                        CollectUiParityDraw(button + "Cooldown", "Cooldown", center - half,
                            half * 2, button, new("", 0x99000000, "ARTWORK", "CENTER", button,
                                "CENTER", -2, -1, ClipRect: fullClip,
                                ClipMask: $"RADIAL_SWEEP:{sweep:R}", BlendMode: "BLEND",
                                InteractionState: "pet-cooldown-sweep", Strata: "HIGH"));
                }
                else if (cooldown.FlashProgress is { } flash)
                {
                    cooldownVisible = true;
                    DrawCooldownFlash(dl, center - half, center + half, flash);
                    if (proof)
                        CollectUiParityDraw(button + "Cooldown", "Cooldown", center - half,
                            half * 2, button, new("", 0xffffffff, "ARTWORK", "CENTER", button,
                                "CENTER", -2, -1, ClipRect: fullClip,
                                ClipMask: $"COOLDOWN_FINISH_FLASH:{flash:R}", BlendMode: "ADD",
                                InteractionState: "pet-cooldown-finish-flash", Strata: "HIGH"));
                }
            }
            if (proof && !cooldownVisible)
                ClassifyUiParity(button + "Cooldown", "Cooldown", button, "NOT-DRAWN",
                    "no-active-pet-cooldown");

            bool checkedState = PetActionBarUiLaw.Active(packed, _petState, _petAttacking);
            if (checkedState)
            {
                uint check = _gameplayArt.AdditiveHandle(@"Interface\Buttons\CheckButtonHilight");
                if (check != 0) dl.AddImage((nint)check, min, max);
                if (proof)
                    CollectUiParityDraw(button + "CheckedTexture", "CheckedTexture", min,
                        max - min, button,
                        new(@"Interface\Buttons\CheckButtonHilight", 0xffffffff, "ARTWORK",
                            "CENTER", button, "CENTER", 0, 0, ClipRect: fullClip,
                            Visible: check != 0, BlendMode: "ADD", Strata: "HIGH"));
            }
            else if (proof)
                ClassifyUiParity(button + "CheckedTexture", "CheckedTexture", button, "NOT-DRAWN",
                    "action-not-checked");

            if (pushed)
            {
                uint depress = _gameplayArt.Handle(@"Interface\Buttons\UI-Quickslot-Depress");
                if (depress != 0) dl.AddImage((nint)depress, min, max);
                if (proof)
                    CollectUiParityDraw(button + "PushedTexture", "PushedTexture", min,
                        max - min, button,
                        new(@"Interface\Buttons\UI-Quickslot-Depress", 0xffffffff, "ARTWORK",
                            "CENTER", button, "CENTER", 0, 0, ClipRect: fullClip,
                            Visible: depress != 0, BlendMode: "BLEND", Strata: "HIGH"));
            }
            else if (proof)
                ClassifyUiParity(button + "PushedTexture", "PushedTexture", button, "NOT-DRAWN",
                    "button-not-pushed");

            if (hovered)
            {
                uint highlight = _gameplayArt.BrightHighlightHandle(
                    @"Interface\Buttons\ButtonHilight-Square");
                if (highlight != 0) dl.AddImage((nint)highlight, min, max);
                if (proof)
                    CollectUiParityDraw(button + "HighlightTexture", "HighlightTexture", min,
                        max - min, button,
                        new(@"Interface\Buttons\ButtonHilight-Square", 0xffffffff, "HIGHLIGHT",
                            "CENTER", button, "CENTER", 0, 0, ClipRect: fullClip,
                            Visible: highlight != 0, BlendMode: "ADD", Strata: "HIGH"));
                if (resolvedSpells[i] is not null)
                {
                    hoveredSpellTooltip = PrepareSharedSpellTooltip(
                        new GameTooltipOwnerKey("pet-action", (ulong)(i + 1)),
                        action, s, SpellTooltipPlacement.DefaultBottomRight);
                }
                else if (hasNamedPayload && PetTokenName(action, kind) is { } tokenName)
                {
                    string preparedName = tokenName;
                    var owner = new GameTooltipOwnerKey("pet-action", (ulong)(i + 1));
                    OfferPreservedSharedGameTooltipRenderer(owner,
                        () => DrawPetTokenTooltip(preparedName));
                }
            }
            else if (proof)
                ClassifyUiParity(button + "HighlightTexture", "HighlightTexture", button, "NOT-DRAWN",
                    "button-not-hovered");

            bool released = clickedLeft[i] || clickedRight[i];
            if (released && ShiftHeld())
            {
                PickupPetAction(i, petGuid, pet);
                pickedUpOnClick = true;
                _pressedPetActionSlot = -1;
            }
            else if (clickedRight[i] && _pressedPetActionSlot == i &&
                     !_draggingPetAction.HasValue)
            {
                if (autocastable) TogglePetAutocast(i, petGuid);
                _pressedPetActionSlot = -1;
            }
            else if (clickedLeft[i] && _pressedPetActionSlot == i &&
                     !_draggingPetAction.HasValue)
            {
                UsePetAction(i, petGuid, pet);
                _pressedPetActionSlot = -1;
            }
        }

        dl.PopClipRect();
        if (proof) MarkUiParityFrameComplete();
        ImGui.End();
        ImGui.PopStyleVar(3);

        bool releasedDragButton = ImGui.IsMouseReleased(ImGuiMouseButton.Left) ||
            ImGui.IsMouseReleased(ImGuiMouseButton.Right);
        if (releasedDragButton && _draggingPetAction.HasValue && !pickedUpOnClick)
        {
            if (hoveredSlot >= 0)
            {
                if (ActionBarLockLaw.ReceiveDragAllowed(Settings.Controls.LockActionBars))
                    PlacePetAction(hoveredSlot, petGuid, pet);
            }
            else if (!MouseOverActionBarDropTarget()) ClearPetActionCursor();
        }
        if (releasedDragButton) _pressedPetActionSlot = -1;

        if (hoveredSpellTooltip is { } hoveredPetSpellTooltip)
        {
            PreparedSharedSpellTooltip prepared = hoveredPetSpellTooltip;
            OfferPreservedSharedGameTooltipRenderer(prepared.Owner,
                () => DrawSpellTooltip(prepared.Snapshot));
        }
        if (_draggingPetAction.HasValue && !_window.MouseCaptured)
        {
            uint cursor = _gameplayArt!.Handle(_draggingPetActionIcon);
            Vector2 at = ImGui.GetIO().MousePos + new Vector2(8) * s;
            if (cursor != 0)
                ImGui.GetForegroundDrawList().AddImage((nint)cursor, at,
                    at + new Vector2(PetActionBarUiLaw.ButtonSize) * s);
        }
    }

    private void UsePetAction(int slot, ulong petGuid, WorldEntity? pet)
    {
        if (!CanAuthorControlledGameplay) return;
        if (RefuseTacticalFrozenActor(petGuid, "command it")) return;
        uint packed = _petActions[slot];
        if (!PetActionBarUiLaw.HasPayload(packed)) return;
        uint action = PetActionBarUiLaw.Action(packed);
        byte kind = PetActionBarUiLaw.Kind(packed);

        if (PetActionBarUiLaw.IsSpell(packed) &&
            _spellCatalog?.TryGet(action, out SpellInfo spell) == true &&
            IsPetSpellShowingActive(packed, spell, pet))
        {
            _net?.PetCancelAura(petGuid, action);
            return;
        }

        bool attack = kind == 7 && action == 2;
        if (attack && _petAttacking)
        {
            if (_net?.PetStopAttack(petGuid) == true) _petAttacking = false;
            return;
        }
        if (attack && pet is not null)
        {
            string? refusal = PetActionBarUiLaw.AttackRefusalKey(
                pet.Fields.Health, pet.Fields.CharmedBy, ControlledGuid,
                pet.Fields.UnitFlags, pet.Fields.MountDisplayId);
            if (refusal is not null)
            {
                ShowUiError(PetGlobalString(refusal));
                return;
            }
        }

        ulong actionTarget = PetActionBarUiLaw.ActionTarget(_selectionGuid);
        if (RefuseTacticalFrozenActor(actionTarget, "target it with a pet action")) return;
        if (_net?.PetAction(petGuid, packed, actionTarget) != true) return;
        _petState = PetActionBarUiLaw.LatchPress(_petState, packed);
        if (attack) _petAttacking = true;
    }

    private bool IsPetSpellShowingActive(uint packed, in SpellInfo spell, WorldEntity? pet)
    {
        bool cancelableAura = pet is not null && pet.Fields.Auras().Any(a =>
            a.SpellId == PetActionBarUiLaw.Action(packed) && (a.Flags & 0x01) != 0);
        return PetActionBarUiLaw.ActiveAuraPress(packed, spell.ActiveIconId, cancelableAura);
    }

    private void TogglePetAutocast(int slot, ulong petGuid)
    {
        if (!CanAuthorControlledGameplay) return;
        if (RefuseTacticalFrozenActor(petGuid, "change its autocast")) return;
        uint toggled = PetActionBarUiLaw.ToggleAutocast(_petActions[slot]);
        _petActions[slot] = toggled; // the server does not echo this half
        _net?.PetSetAction(petGuid, new[] { ((uint)slot, toggled) });
    }

    private void PickupPetAction(int slot, ulong petGuid, WorldEntity? pet)
    {
        if (!CanAuthorControlledGameplay) return;
        if (RefuseTacticalFrozenActor(petGuid, "change its action bar")) return;
        uint unitFlags = pet?.Fields.UnitFlags ?? 0;
        if (!PetActionBarUiLaw.PickupAllowed(unitFlags)) return;
        if (_draggingPetAction.HasValue)
        {
            PlacePetAction(slot, petGuid, pet);
            return;
        }
        uint packed = _petActions[slot];
        if (!PetActionBarUiLaw.HasPayload(packed)) return;
        _draggingPetAction = packed;
        uint action = PetActionBarUiLaw.Action(packed);
        byte kind = PetActionBarUiLaw.Kind(packed);
        _draggingPetActionPassive = kind == 1 &&
            _spellCatalog?.TryGet(action, out SpellInfo spell) == true && spell.Passive;
        _draggingPetActionIcon = PetActionIcon(action, kind, pet);
        if (kind == 1)
        {
            uint blank = packed & 0xFFFF_0000u;
            _petActions[slot] = blank;
            _net?.PetSetAction(petGuid, new[] { ((uint)slot, blank) });
        }
    }

    private void PlacePetAction(int target, ulong petGuid, WorldEntity? pet)
    {
        if (!CanAuthorControlledGameplay)
        {
            ClearPetActionCursor();
            return;
        }
        if (RefuseTacticalFrozenActor(petGuid, "change its action bar"))
        {
            ClearPetActionCursor();
            return;
        }
        uint unitFlags = pet?.Fields.UnitFlags ?? 0;
        if (!PetActionBarUiLaw.PickupAllowed(unitFlags) || !_draggingPetAction.HasValue) return;
        uint source = _draggingPetAction.Value;
        bool sourcePassive = _draggingPetActionPassive;
        uint oldOccupant = _petActions[target];
        ClearPetActionCursor(); // the reference clears first, including refusal paths
        if (!PetActionBarUiLaw.TryAssign(_petActions, target, source, sourcePassive,
                out PetActionAssignment assigned)) return;
        var entries = new List<(uint Position, uint Packed)>(2);
        if (assigned.Relocated)
            entries.Add(((uint)assigned.RelocationSlot, _petActions[assigned.RelocationSlot]));
        entries.Add(((uint)target, _petActions[target]));
        _net?.PetSetAction(petGuid, entries);
        if (!assigned.Relocated && PetActionBarUiLaw.HasPayload(oldOccupant))
        {
            _draggingPetAction = oldOccupant;
            uint action = PetActionBarUiLaw.Action(oldOccupant);
            byte kind = PetActionBarUiLaw.Kind(oldOccupant);
            _draggingPetActionPassive = kind == 1 &&
                _spellCatalog?.TryGet(action, out SpellInfo spell) == true && spell.Passive;
            _draggingPetActionIcon = PetActionIcon(action, kind, pet);
        }
    }

    private void ClearPetActionCursor()
    {
        _draggingPetAction = null;
        _draggingPetActionPassive = false;
        _draggingPetActionIcon = "";
    }

    private static string? PetTokenName(uint action, byte kind) => kind switch
    {
        7 => action switch
        {
            0 => "Stay",
            1 => "Follow",
            2 => "Attack",
            3 => "Dismiss",
            _ => null,
        },
        6 => action switch
        {
            0 => "Passive",
            1 => "Defensive",
            2 => "Aggressive",
            _ => null,
        },
        _ => null,
    };

    private static void DrawPetTokenTooltip(string preparedName)
    {
        ImGui.BeginTooltip();
        ImGui.TextUnformatted(preparedName);
        ImGui.EndTooltip();
    }

    private void DrawPetAutocastSparkles(ImDrawListPtr dl, Vector2 buttonMin, float s, double now)
    {
        uint star = _gameplayArt!.AdditiveHandle(@"Interface\Buttons\GlowStar");
        if (star == 0) return;
        float cycle = (float)(now % 2.0 / 2.0);
        for (int emitter = 0; emitter < 4; emitter++)
        for (int trail = 0; trail < 8; trail++)
        {
            float age = trail / 8f;
            float phase = cycle + emitter * .25f - age * .125f;
            Vector2 point = PetActionBarUiLaw.SparklePoint(phase) * s;
            float width = PetActionBarUiLaw.SparkleSize(age);
            Vector4 color = PetActionBarUiLaw.SparkleColor(age);
            Vector2 center = buttonMin + new Vector2(.6f) * s + point;
            Vector2 half = new(width * .5f * s);
            dl.AddImage((nint)star, center - half, center + half, Vector2.Zero, Vector2.One,
                ImGui.ColorConvertFloat4ToU32(color));
        }
    }

    private string PetActionIcon(uint action, byte kind, WorldEntity? pet)
    {
        if (kind is >= 1 and <= 5 &&
            _spellCatalog?.TryGet(action, out SpellInfo spell) == true)
        {
            uint packed = action | ((uint)kind << 24);
            return IsPetSpellShowingActive(packed, spell, pet)
                ? spell.ActiveIconPath : spell.IconPath;
        }
        if (kind == 7) return action switch
        {
            0 => @"Interface\Icons\Spell_Nature_TimeStop.blp",
            1 => @"Interface\Icons\Ability_Tracking.blp",
            2 => @"Interface\Icons\Ability_GhoulFrenzy.blp",
            3 => @"Interface\Icons\Spell_Shadow_Teleport.blp",
            _ => ""
        };
        if (kind == 6) return action switch
        {
            0 => @"Interface\Icons\Ability_Seal.blp",
            1 => @"Interface\Icons\Ability_Defend.blp",
            2 => @"Interface\Icons\Ability_Racial_BloodRage.blp",
            _ => ""
        };
        return "";
    }
}
