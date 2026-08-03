using System.Numerics;
using ImGuiNET;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private ulong _petGuid;
    private readonly uint[] _petActions = new uint[10];
    private byte _petReaction;
    private byte _petCommand;

    // Build-5875 SMSG_PET_SPELLS: GUID, duration, four status bytes, ten
    // packed action buttons, then variable spell/cooldown tails.
    private void ApplyPetSpells(byte[] body)
    {
        var r = new PacketReader(body);
        ulong guid = r.ReadU64();
        if (guid == 0)
        {
            _petGuid = 0;
            Array.Clear(_petActions);
            return;
        }
        if (r.Remaining < 48) throw new InvalidDataException("short SMSG_PET_SPELLS");
        _petGuid = guid;
        r.ReadI32();
        _petReaction = r.ReadU8();
        _petCommand = r.ReadU8();
        r.ReadU8();
        r.ReadU8();
        for (int i = 0; i < _petActions.Length; i++) _petActions[i] = r.ReadU32();
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
        float s = GameplayUiScale();
        DrawPetFrame(pet, s);
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
        Vector2 p = new((display.X - 412 * s) * 0.5f, display.Y - 118 * s);
        ImGui.SetNextWindowPos(p, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(412, 40) * s, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        if (!ImGui.Begin("##vanilla-pet-action-bar", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav))
        { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        for (int i = 0; i < _petActions.Length; i++)
        {
            uint packed = _petActions[i];
            uint action = packed & 0x00ff_ffffu;
            byte type = (byte)(packed >> 24);
            Vector2 min = p + new Vector2(i * 41, 2) * s;
            Vector2 max = min + new Vector2(36) * s;
            ImGui.SetCursorScreenPos(min);
            if (ImGui.InvisibleButton($"##pet-action-{i}", max - min) && packed != 0)
            {
                ulong target = type == 0x07 && action == 2 ? _selectionGuid :
                    type is 0x81 or 0xc1 or 0x01 ? (_selectionGuid != 0 ? _selectionGuid : pet.Guid) : 0;
                _net?.PetAction(pet.Guid, packed, target);
                if (type == 0x07) _petCommand = (byte)action;
                if (type == 0x06) _petReaction = (byte)action;
            }
            uint icon = packed == 0 ? 0 : _gameplayArt!.Handle(PetActionIcon(action, type));
            if (icon != 0) dl.AddImage((nint)icon, min, max);
            DrawArt(dl, @"Interface\Buttons\UI-Quickslot2", min, new Vector2(36), s);
            bool active = type == 0x07 && action == _petCommand || type == 0x06 && action == _petReaction;
            if (active) dl.AddRect(min + Vector2.One * s, max - Vector2.One * s, 0xff00d1ffu,
                0, ImDrawFlags.None, 2 * s);
        }
        ImGui.End();
    }

    private string PetActionIcon(uint action, byte type)
    {
        if (type is 0x81 or 0xc1 or 0x01 && _spellCatalog?.TryGet(action, out var spell) == true)
            return spell.IconPath;
        if (type == 0x07) return action switch
        {
            0 => @"Interface\Icons\Ability_GolemThunderClap.blp",
            1 => @"Interface\Icons\Ability_Tracking.blp",
            2 => @"Interface\Icons\Ability_Hunter_Pet_Bear.blp",
            3 => @"Interface\Icons\Spell_Shadow_SummonImp.blp",
            _ => @"Interface\Icons\INV_Misc_QuestionMark.blp"
        };
        if (type == 0x06) return action switch
        {
            0 => @"Interface\Icons\Ability_Seal.blp",
            1 => @"Interface\Icons\Ability_Defend.blp",
            2 => @"Interface\Icons\Ability_Druid_Maul.blp",
            _ => @"Interface\Icons\INV_Misc_QuestionMark.blp"
        };
        return @"Interface\Icons\INV_Misc_QuestionMark.blp";
    }
}
