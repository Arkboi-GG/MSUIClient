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
    private uint _petState;
    private bool _petAttacking;
    private ulong _petAttackSelection;
    private int _pressedPetActionSlot = -1;
    private Vector2 _petActionPressPosition;
    private uint? _draggingPetAction;
    private bool _draggingPetActionPassive;
    private string _draggingPetActionIcon = "";

    // Build-5875 SMSG_PET_SPELLS, including both the client and vmangos cooldown tails.
    private void ApplyPetSpells(byte[] body)
    {
        var r = new PacketReader(body);
        ulong guid = r.ReadU64();
        if (r.Remaining == 0)
        {
            _petGuid = guid;
            if (guid == 0) ResetPetActionBar();
            return;
        }
        if (guid == 0 || r.Remaining < 48)
            throw new InvalidDataException("short SMSG_PET_SPELLS");
        if (_petGuid != guid) _petAttacking = false;
        _petGuid = guid;
        r.ReadU32(); // charm duration; zero for an ordinary pet
        _petState = r.ReadU32();
        for (int i = 0; i < _petActions.Length; i++) _petActions[i] = r.ReadU32();

        if (r.Remaining == 0) return;
        int spellCount = r.ReadU8();
        for (int i = 0; i < spellCount; i++) r.ReadU32();
        if (r.Remaining == 0) return;
        int cooldownCount = r.ReadU8();
        if (cooldownCount == 0) return;
        bool vmangos = r.Remaining == 1 + 14 * cooldownCount;
        if (vmangos) r.ReadU8(); // high byte of its u16 count
        double now = MovementInfo.ClientUptimeMs() / 1000.0;
        for (int i = 0; i < cooldownCount; i++)
        {
            uint spell = vmangos ? r.ReadU32() : r.ReadU16();
            uint category = r.ReadU16();
            uint spellMs = r.ReadU32();
            uint categoryMs = r.ReadU32();
            _actions.StartCooldown(spell, category, spellMs, categoryMs, now);
        }
    }

    private void ApplyPetMode(byte[] body)
    {
        var r = new PacketReader(body);
        ulong guid = r.ReadU64();
        uint state = r.ReadU32();
        if (guid == _petGuid) _petState = state;
    }

    private void ApplyPetActionFeedback(byte[] body)
    {
        if (body.Length == 0) return;
        string text = body[0] switch
        {
            1 => "Your pet is dead.",
            2 => "Your pet is out of range.",
            3 => "Your pet has no path to that location.",
            4 => "Your pet is confused.",
            5 => "Your pet is fleeing.",
            _ => "Your pet cannot do that."
        };
        ShowUiError(text);
    }

    private void ApplyPetCastFailed(byte[] body)
    {
        if (body.Length < 5) return;
        var r = new PacketReader(body);
        uint spellId = r.ReadU32();
        byte status = r.ReadU8();
        if (status != 2) return;
        if (r.Remaining > 0) r.ReadU8();
        string name = _spellCatalog?.TryGet(spellId, out SpellInfo spell) == true ? spell.Name : "Pet spell";
        ShowUiError($"{name} failed.");
    }

    private void ResetPetActionBar()
    {
        _petGuid = 0;
        _petState = 0;
        _petAttacking = false;
        _petAttackSelection = 0;
        _draggingPetAction = null;
        _pressedPetActionSlot = -1;
        Array.Clear(_petActions);
    }

    private void StagePetActionBarProof()
    {
        if (_net is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return;
        _petGuid = player.Guid; // capture-only controlled-unit fixture; no server state is changed
        _petState = 1u | (1u << 8); // defensive + follow
        uint spellA = _actions.KnownSpells.FirstOrDefault(id =>
            _spellCatalog?.TryGet(id, out SpellInfo info) == true && !info.Passive);
        uint spellB = _actions.KnownSpells.FirstOrDefault(id => id != spellA &&
            _spellCatalog?.TryGet(id, out SpellInfo info) == true && !info.Passive);
        uint Pack(uint id, byte kind, uint flags = 0) => id | ((uint)kind << 24) | flags;
        uint[] staged =
        [
            Pack(0, 7), Pack(1, 7), Pack(2, 7),
            spellA == 0 ? 0 : Pack(spellA, 1, PetActionBarUiLaw.AutocastAllowed | PetActionBarUiLaw.AutocastEnabled),
            0,
            spellB == 0 ? 0 : Pack(spellB, 1, PetActionBarUiLaw.AutocastAllowed),
            0, Pack(0, 6), Pack(1, 6), Pack(2, 6)
        ];
        staged.CopyTo(_petActions, 0);
    }

    private bool TryGetControlledPet(out WorldEntity pet)
    {
        pet = null!;
        if (_net is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return false;
        ulong guid = _petGuid != 0 ? _petGuid : player.Fields.Summon ?? player.Fields.Charm ?? 0;
        return guid != 0 && _entities.TryGet(guid, out pet) && pet.IsUnit;
    }

    private void DrawPetFrameAndActionBar()
    {
        if (_gameplayArt is null || !TryGetControlledPet(out WorldEntity pet)) return;
        if (_petAttacking && _petAttackSelection != _selectionGuid)
        {
            _net?.PetStopAttack(pet.Guid);
            _petAttacking = false;
        }
        float s = GameplayUiScale();
        DrawPetFrame(pet, s); // Existing MSUI portrait/frame is intentionally preserved.
        DrawPetActionBar(pet, s);
    }

    private void DrawPetFrame(WorldEntity pet, float s)
    {
        Vector2 p = new Vector2(10, 86) * s;
        ImGui.SetNextWindowPos(p, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(128, 42) * s, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        if (!ImGui.Begin("##vanilla-pet-frame", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav))
        { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 portrait = p + new Vector2(5, 4) * s;
        DrawUnitPortraitImage(dl, pet, portrait, 32 * s, 0, false);
        DrawArt(dl, @"Interface\TargetingFrame\UI-SmallTargetingFrame", p, new Vector2(128, 64), s);
        DrawVanillaStatusBar(dl, p + new Vector2(39, 12) * s, new Vector2(75, 7) * s,
            pet.HealthFraction, new Vector4(0, 1, 0, 1));
        DrawVanillaStatusBar(dl, p + new Vector2(39, 21) * s, new Vector2(75, 7) * s,
            pet.PowerFraction, PowerColor(pet.Fields.PowerType));
        string name = _creatureNames.GetValueOrDefault(pet.Entry, "Pet");
        DrawUnitFrameText(dl, p + new Vector2(75, 7) * s, name, 9 * s, UiGoldU32());
        ImGui.End();
    }

    private void DrawPetActionBar(WorldEntity pet, float s)
    {
        Vector2 display = ImGui.GetIO().DisplaySize;
        Vector2 mainBar = GameplayBarMin(display, s);
        bool bottomMultiBar = Enumerable.Range(48, 24).Any(slot => _actions[slot] is not null);
        float bottom = PetActionBarUiLaw.BaseBottom +
            (bottomMultiBar ? PetActionBarUiLaw.BottomMultiBarStep : 0f);
        Vector2 p = new(mainBar.X + PetActionBarUiLaw.BaseX * s,
            display.Y - (bottom + PetActionBarUiLaw.FrameHeight) * s);
        ImGui.SetNextWindowPos(p, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(PetActionBarUiLaw.FrameWidth,
            PetActionBarUiLaw.FrameHeight) * s, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        if (!ImGui.Begin("##vanilla-pet-action-bar", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoBringToFrontOnFocus))
        { ImGui.End(); return; }

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        bool proof = _uiParityArmed && _uiParityPanel == "pet-action-bar";
        if (proof)
        {
            BeginUiParityFrame(p, s);
            CollectUiParityDraw("PetActionBarFrame", "Frame", p,
                new Vector2(PetActionBarUiLaw.FrameWidth, PetActionBarUiLaw.FrameHeight) * s, "",
                new("", 0, "IMGUI_WINDOW", "BOTTOMLEFT", "MainMenuBar", "BOTTOMLEFT",
                    PetActionBarUiLaw.BaseX, bottom));
        }
        uint shelf = _gameplayArt!.Handle(@"Interface\PetActionBar\UI-PetBar");
        if (shelf != 0)
        {
            dl.AddImage((nint)shelf, p, p + new Vector2(256, 44) * s,
                new(0, .015625f), new(1, .359375f));
            dl.AddImage((nint)shelf, p + new Vector2(256, 0) * s,
                p + new Vector2(440, 44) * s, new(0, .375f), new(.71875f, .71875f));
            if (proof)
            {
                CollectUiParityDraw("PetActionBarTexture0", "Texture", p, new Vector2(256, 44) * s,
                    "PetActionBarFrame", new(@"Interface\PetActionBar\UI-PetBar", 0xffffffff,
                        "IMGUI_IMAGE", "TOPLEFT", "PetActionBarFrame", "TOPLEFT", 0, 0));
                CollectUiParityDraw("PetActionBarTexture1", "Texture", p + new Vector2(256, 0) * s,
                    new Vector2(184, 44) * s, "PetActionBarFrame", new(@"Interface\PetActionBar\UI-PetBar",
                        0xffffffff, "IMGUI_IMAGE", "LEFT", "PetActionBarTexture0", "RIGHT", 0, 0));
            }
        }

        int hoveredSlot = -1;
        uint hoveredSpell = 0;
        if (_pressedPetActionSlot >= 0 && ImGui.IsMouseDown(ImGuiMouseButton.Left) &&
            Vector2.Distance(ImGui.GetIO().MousePos, _petActionPressPosition) > 6f * s)
        {
            PickupPetAction(_pressedPetActionSlot, pet);
            _pressedPetActionSlot = -1;
        }

        bool usable = PetActionBarUiLaw.Usable(_petState, pet.Fields.UnitFlags);
        double now = MovementInfo.ClientUptimeMs() / 1000.0;
        for (int i = 0; i < _petActions.Length; i++)
        {
            uint packed = _petActions[i];
            uint action = PetActionBarUiLaw.Action(packed);
            byte kind = PetActionBarUiLaw.Kind(packed);
            string iconPath = PetActionIcon(action, kind);
            bool hasIcon = PetActionBarUiLaw.HasPayload(packed) && iconPath.Length > 0;
            bool show = hasIcon || _draggingPetAction.HasValue;
            Vector2 min = p + new Vector2(PetActionBarUiLaw.ButtonX(i), PetActionBarUiLaw.ButtonY) * s;
            Vector2 max = min + new Vector2(PetActionBarUiLaw.ButtonSize) * s;
            if (proof && i is 0 or 6 or 9)
                CollectUiParityDraw($"PetActionButton{i + 1}", "CheckButton", min, max - min,
                    "PetActionBarFrame", new(@"Interface\Buttons\UI-Quickslot2", 0xffffffff,
                        "IMGUI_IMAGE", "BOTTOMLEFT", "PetActionBarFrame", "BOTTOMLEFT",
                        PetActionBarUiLaw.ButtonX(i), PetActionBarUiLaw.ButtonY));
            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton($"##pet-action-{i}", max - min);
            bool hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
            if (hovered) hoveredSlot = i;
            if (ImGui.IsItemActivated())
            {
                _pressedPetActionSlot = i;
                _petActionPressPosition = ImGui.GetIO().MousePos;
            }

            if (show)
            {
                uint ring = _gameplayArt.Handle(hasIcon ? @"Interface\Buttons\UI-Quickslot2" :
                    @"Interface\Buttons\UI-Quickslot");
                if (hasIcon)
                {
                    uint icon = _gameplayArt.Handle(iconPath);
                    if (icon != 0) dl.AddImage((nint)icon, min, max, Vector2.Zero, Vector2.One,
                        usable ? 0xffff_ffffu : 0xff66_6666u);
                }
                if (ring != 0)
                    dl.AddImage((nint)ring, min - new Vector2(12, 13) * s,
                        max + new Vector2(12, 11) * s);
            }

            if (PetActionBarUiLaw.Autocastable(packed))
            {
                uint overlay = _gameplayArt.Handle(@"Interface\Buttons\UI-AutoCastableOverlay");
                if (overlay != 0) dl.AddImage((nint)overlay, min - new Vector2(14) * s,
                    max + new Vector2(14) * s);
                if (PetActionBarUiLaw.Autocasting(packed)) DrawPetAutocastSparkles(dl, min, s, now);
            }
            if (PetActionBarUiLaw.IsSpell(packed) && _spellCatalog?.TryGet(action, out SpellInfo spell) == true &&
                _actions.TryCooldownDisplay(action, now, spell.Category, out CooldownDisplay cooldown))
            {
                Vector2 center = (min + max) * .5f + new Vector2(-2, -1) * s;
                Vector2 half = new(16.5f * s);
                if (cooldown.SweepFraction is { } sweep) DrawCooldownSwipe(dl, center - half, center + half, sweep);
                if (cooldown.FlashProgress is { } flash) DrawCooldownFlash(dl, center - half, center + half, flash);
            }
            if (PetActionBarUiLaw.Active(packed, _petState, _petAttacking))
            {
                uint check = _gameplayArt.AdditiveHandle(@"Interface\Buttons\CheckButtonHilight");
                if (check != 0) dl.AddImage((nint)check, min, max);
            }
            if (ImGui.IsItemActive())
            {
                uint depress = _gameplayArt.Handle(@"Interface\Buttons\UI-Quickslot-Depress");
                if (depress != 0) dl.AddImage((nint)depress, min, max);
            }
            if (hovered)
            {
                uint highlight = _gameplayArt.BrightHighlightHandle(@"Interface\Buttons\ButtonHilight-Square");
                if (highlight != 0) dl.AddImage((nint)highlight, min, max);
                if (PetActionBarUiLaw.IsSpell(packed)) hoveredSpell = action;
                else if (hasIcon) DrawPetTokenTooltip(action, kind);

                if (ImGui.IsMouseReleased(ImGuiMouseButton.Right) &&
                    PetActionBarUiLaw.Autocastable(packed)) TogglePetAutocast(i, pet.Guid);
                if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) && ImGui.GetIO().KeyShift)
                {
                    PickupPetAction(i, pet);
                    _pressedPetActionSlot = -1;
                }
                else if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) &&
                         _pressedPetActionSlot == i && !_draggingPetAction.HasValue)
                {
                    UsePetAction(i, pet, usable);
                    _pressedPetActionSlot = -1;
                }
            }
        }

        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) && _draggingPetAction.HasValue)
        {
            if (hoveredSlot >= 0) PlacePetAction(hoveredSlot, pet);
            else ClearPetActionCursor();
        }
        if (proof) MarkUiParityFrameComplete();
        ImGui.End();

        if (hoveredSpell != 0) DrawSpellTooltip(hoveredSpell, s, SpellTooltipPlacement.DefaultBottomRight);
        if (_draggingPetAction.HasValue && !_window.MouseCaptured)
        {
            uint cursor = _gameplayArt.Handle(_draggingPetActionIcon);
            Vector2 at = ImGui.GetIO().MousePos + new Vector2(8) * s;
            if (cursor != 0) ImGui.GetForegroundDrawList().AddImage((nint)cursor, at, at + new Vector2(30) * s);
        }
    }

    private void UsePetAction(int slot, WorldEntity pet, bool usable)
    {
        uint packed = _petActions[slot];
        if (!usable || !PetActionBarUiLaw.HasPayload(packed)) return;
        uint action = PetActionBarUiLaw.Action(packed);
        byte kind = PetActionBarUiLaw.Kind(packed);
        if (kind == 7 && action == 2 && _petAttacking)
        {
            if (_net?.PetStopAttack(pet.Guid) == true) _petAttacking = false;
            return;
        }
        ulong target = kind == 7 && action == 2 ? _selectionGuid :
            PetActionBarUiLaw.IsSpell(packed) ? (_selectionGuid != 0 ? _selectionGuid : pet.Guid) : 0;
        if (_net?.PetAction(pet.Guid, packed, target) != true) return;
        _petState = PetActionBarUiLaw.LatchPress(_petState, packed);
        if (kind == 7 && action == 2)
        {
            _petAttacking = true;
            _petAttackSelection = _selectionGuid;
        }
    }

    private void TogglePetAutocast(int slot, ulong petGuid)
    {
        uint toggled = PetActionBarUiLaw.ToggleAutocast(_petActions[slot]);
        _petActions[slot] = toggled; // the server does not echo this half
        _net?.PetSetAction(petGuid, new[] { ((uint)slot, toggled) });
    }

    private void PickupPetAction(int slot, WorldEntity pet)
    {
        if (!PetActionBarUiLaw.PickupAllowed(pet.Fields.UnitFlags)) return;
        if (_draggingPetAction.HasValue) { PlacePetAction(slot, pet); return; }
        uint packed = _petActions[slot];
        if (!PetActionBarUiLaw.HasPayload(packed)) return;
        _draggingPetAction = packed;
        uint action = PetActionBarUiLaw.Action(packed);
        byte kind = PetActionBarUiLaw.Kind(packed);
        _draggingPetActionPassive = kind == 1 &&
            _spellCatalog?.TryGet(action, out SpellInfo spell) == true && spell.Passive;
        _draggingPetActionIcon = PetActionIcon(action, kind);
        if (kind == 1)
        {
            uint blank = packed & 0xFFFF_0000u;
            _petActions[slot] = blank;
            _net?.PetSetAction(pet.Guid, new[] { ((uint)slot, blank) });
        }
    }

    private void PlacePetAction(int target, WorldEntity pet)
    {
        if (!PetActionBarUiLaw.PickupAllowed(pet.Fields.UnitFlags) || !_draggingPetAction.HasValue) return;
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
        _net?.PetSetAction(pet.Guid, entries);
        if (!assigned.Relocated && PetActionBarUiLaw.HasPayload(oldOccupant))
        {
            _draggingPetAction = oldOccupant;
            uint action = PetActionBarUiLaw.Action(oldOccupant);
            byte kind = PetActionBarUiLaw.Kind(oldOccupant);
            _draggingPetActionPassive = kind == 1 &&
                _spellCatalog?.TryGet(action, out SpellInfo spell) == true && spell.Passive;
            _draggingPetActionIcon = PetActionIcon(action, kind);
        }
    }

    private void ClearPetActionCursor()
    {
        _draggingPetAction = null;
        _draggingPetActionPassive = false;
        _draggingPetActionIcon = "";
    }

    private void DrawPetTokenTooltip(uint action, byte kind)
    {
        (string name, string sub) = kind == 7 ? action switch
        {
            0 => ("Stay", "Pet Command"), 1 => ("Follow", "Pet Command"),
            2 => ("Attack", "Pet Command"), 3 => ("Dismiss", "Pet Command"),
            _ => ("Pet Command", "")
        } : action switch
        {
            0 => ("Passive", "Pet Stance"), 1 => ("Defensive", "Pet Stance"),
            2 => ("Aggressive", "Pet Stance"), _ => ("Pet Stance", "")
        };
        ImGui.BeginTooltip();
        ImGui.TextUnformatted(name);
        if (sub.Length > 0) ImGui.TextDisabled(sub);
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
            float life = 1f - age;
            float width = life > .66f ? 14.4f : life > .33f ? 5.76f : 2.88f;
            Vector4 color = life > .66f ? new(.976f, .875f, .192f, life) :
                life > .33f ? new(.996f, .945f, .745f, life) : new(1, 1, 1, life);
            Vector2 center = buttonMin + new Vector2(.6f) * s + point;
            Vector2 half = new(width * .5f * s);
            dl.AddImage((nint)star, center - half, center + half, Vector2.Zero, Vector2.One,
                ImGui.ColorConvertFloat4ToU32(color));
        }
    }

    private string PetActionIcon(uint action, byte kind)
    {
        if (kind is >= 1 and <= 5 && _spellCatalog?.TryGet(action, out SpellInfo spell) == true)
            return spell.IconPath;
        if (kind == 7) return action switch
        {
            0 => @"Interface\Icons\Spell_Nature_TimeStop.blp",
            1 => @"Interface\Icons\Ability_Tracking.blp",
            2 => @"Interface\Icons\Ability_GhoulFrenzy.blp",
            3 => @"Interface\Icons\Spell_Shadow_Teleport.blp",
            _ => @"Interface\Icons\INV_Misc_QuestionMark.blp"
        };
        if (kind == 6) return action switch
        {
            0 => @"Interface\Icons\Ability_Seal.blp",
            1 => @"Interface\Icons\Ability_Defend.blp",
            2 => @"Interface\Icons\Ability_Racial_BloodRage.blp",
            _ => @"Interface\Icons\INV_Misc_QuestionMark.blp"
        };
        return "";
    }
}
