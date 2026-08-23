using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;
using MSUIClient.World.Units;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private readonly record struct DressUpPiece(string Name, uint DisplayId,
        uint InventoryType, int EquipmentSlot, byte ItemClass, byte ItemSubclass,
        byte Material, byte Sheath);

    private bool _dressUpOpen;
    private float _dressUpRotation = DressUpFrameUiLaw.InitialFacing;
    private bool _dressUpDirty = true;
    private double _dressUpPaneLastUpdate;
    private CharacterRenderer? _dressUpRenderer;
    private readonly Dictionary<int, ItemTemplate> _dressUpSubstitutions = [];
    private readonly List<int> _dressUpHeldOrder = [];
    private readonly List<uint> _dressUpPending = [];

    private void TryOnDressUp(uint entry)
    {
        if (entry == 0 || _items is null) return;
        if (!_dressUpOpen)
        {
            _dressUpOpen = true;
            ResetDressUpPreview(playSound: false);
            PlayUiSound("igCharacterInfoOpen", "ui.dress-up");
        }
        if (!_dressUpPending.Contains(entry)) _dressUpPending.Add(entry);
        if (_net is not null) _items.Require(entry, 0, _net);
        ResolveDressUpPending();
        EmitInterface("dress-up", "try-on", "QUEUED", 0, $"entry={entry}");
    }

    private void ResolveDressUpPending()
    {
        if (!_dressUpOpen || _items is null || _dressUpPending.Count == 0) return;
        bool changed = false;
        for (int i = 0; i < _dressUpPending.Count;)
        {
            uint entry = _dressUpPending[i];
            if (_net is not null) _items.Require(entry, 0, _net);
            if (!_items.TryGet(entry, out ItemTemplate? item) || item is null)
            {
                i++;
                continue;
            }
            _dressUpPending.RemoveAt(i);
            int slot = DressUpFrameUiLaw.EquipmentSlot(item.InventoryType);
            if (slot < 0) continue;
            _dressUpSubstitutions[slot] = item;
            if (DressUpFrameUiLaw.HeldSlot(slot))
            {
                _dressUpHeldOrder.Remove(slot);
                _dressUpHeldOrder.Add(slot);
            }
            changed = true;
        }
        if (changed) RebuildDressUpLook();
    }

    private void ResetDressUpPreview(bool playSound = true)
    {
        _dressUpSubstitutions.Clear();
        _dressUpHeldOrder.Clear();
        _dressUpPending.Clear();
        _dressUpPaneLastUpdate = 0;
        RebuildDressUpLook();
        if (playSound) PlayUiSound("gsTitleOptionOK", "ui.dress-up");
        EmitInterface("dress-up", "reset", "RESET", 0, "source=player-look");
    }

    private void CloseDressUp(bool playSound = true)
    {
        if (!_dressUpOpen) return;
        _dressUpOpen = false;
        _dressUpSubstitutions.Clear();
        _dressUpHeldOrder.Clear();
        _dressUpPending.Clear();
        _dressUpPaneLastUpdate = 0;
        if (playSound) PlayUiSound("igCharacterInfoClose", "ui.dress-up");
        EmitInterface("dress-up", "close", "CLOSED", 0, "room=emptied");
    }

    private bool EnsureDressUpRenderer()
    {
        if (_gl is null || _character is not { Loaded: true }) return false;
        try
        {
            if (_dressUpRenderer is null)
            {
                _dressUpRenderer = new CharacterRenderer(_gl, _config);
                string shaderDirectory = Path.Combine(AppContext.BaseDirectory, "Shaders");
                if (!File.Exists(Path.Combine(shaderDirectory, "character.vert")))
                    shaderDirectory = Path.Combine(_config.RepoRoot, "MSUIClient", "Shaders");
                _dressUpRenderer.LoadShaders(shaderDirectory);
            }
            if (!_dressUpRenderer.Race.Equals(_character.Race, StringComparison.OrdinalIgnoreCase) ||
                !_dressUpRenderer.Gender.Equals(_character.Gender, StringComparison.OrdinalIgnoreCase))
            {
                if (!_dressUpRenderer.Load(_character.Race, _character.Gender)) return false;
            }
            _dressUpRenderer.CopyRuntimeTuningFrom(_character);
            _dressUpRenderer.Enabled = true;
            _dressUpRenderer.ModelScale = 1f;
            _dressUpRenderer.BindPose = false;
            _dressUpRenderer.FrozenStandPose = false;
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[dress-up] renderer unavailable: {ex.Message}");
            _dressUpRenderer?.Dispose();
            _dressUpRenderer = null;
            return false;
        }
    }

    private void RebuildDressUpLook()
    {
        if (!EnsureDressUpRenderer() || _character is null || _dressUpRenderer is null) return;

        _dressUpRenderer.SkinId = _character.SkinId;
        _dressUpRenderer.FaceId = _character.FaceId;
        _dressUpRenderer.HairStyleId = _character.HairStyleId;
        _dressUpRenderer.HairColorId = _character.HairColorId;
        _dressUpRenderer.FacialHairId = _character.FacialHairId;

        var kit = new CharacterEquipment { GuildEmblem = _character.Equipment.GuildEmblem };
        uint playerFlags = _net is not null &&
            _entities.TryGet(_net.PlayerGuid, out WorldEntity ownPlayer) && ownPlayer.IsPlayer
                ? ownPlayer.Fields.PlayerFlags : _net?.Player?.Flags ?? 0;
        DressUpPiece? baseMain = null;
        DressUpPiece? baseOff = null;
        foreach (CharacterEquipment.Piece piece in _character.Equipment.Pieces)
        {
            DressUpPiece candidate = FromDressUpPiece(piece);
            int slot = piece.EquipmentSlot >= 0
                ? piece.EquipmentSlot
                : DressUpFrameUiLaw.EquipmentSlot((uint)piece.InventoryType);
            if (!EquipmentDisplayPreferenceLaw.DressUpPieceShown(
                    slot, explicitlyTriedOn: false, playerFlags)) continue;
            if (slot == 15) { if (!_dressUpSubstitutions.ContainsKey(15)) baseMain = candidate; continue; }
            if (slot == 16) { if (!_dressUpSubstitutions.ContainsKey(16)) baseOff = candidate; continue; }
            if (slot == 17) continue; // the frozen melee booth does not clone the live ranged arm
            if (_dressUpSubstitutions.ContainsKey(slot)) continue;
            AddDressUpPiece(kit, candidate);
        }

        foreach ((int slot, ItemTemplate item) in _dressUpSubstitutions)
        {
            if (DressUpFrameUiLaw.HeldSlot(slot)) continue;
            AddDressUpPiece(kit, FromDressUpItem(item, slot));
        }

        DressUpPiece? main = baseMain;
        DressUpPiece? off = baseOff;
        foreach (int slot in _dressUpHeldOrder)
        {
            if (!_dressUpSubstitutions.TryGetValue(slot, out ItemTemplate? item)) continue;
            DressUpPiece incoming = FromDressUpItem(item, slot);
            bool toOff = slot == 16 || slot == 17 &&
                DressUpFrameUiLaw.RangedUsesOffLane(item.InventoryType);
            if (toOff)
            {
                if (main is { } other && !DressUpFrameUiLaw.HeldLanesCoexist(
                        other.InventoryType, incoming.InventoryType))
                    main = null;
                off = incoming;
            }
            else
            {
                if (off is { } other && !DressUpFrameUiLaw.HeldLanesCoexist(
                        incoming.InventoryType, other.InventoryType))
                    off = null;
                main = incoming;
            }
        }
        if (main is { } mainPiece)
            AddDressUpPiece(kit, mainPiece with
            {
                EquipmentSlot = mainPiece.InventoryType is 15 or 25 or 26 ? 17 : 15,
            });
        if (off is { } offPiece)
            AddDressUpPiece(kit, offPiece with
            {
                EquipmentSlot = offPiece.InventoryType is 15 or 25 or 26 ? 17 : 16,
            });

        _dressUpRenderer.Equipment = kit;
        _dressUpRenderer.Reload();
        _dressUpDirty = true;
    }

    private static DressUpPiece FromDressUpPiece(CharacterEquipment.Piece piece) => new(
        piece.Name, piece.DisplayId, (uint)piece.InventoryType, piece.EquipmentSlot,
        piece.ItemClass, piece.ItemSubclass, piece.Material, piece.Sheath);

    private static DressUpPiece FromDressUpItem(ItemTemplate item, int slot) => new(
        item.Name, item.DisplayInfoId, item.InventoryType, slot,
        (byte)item.Class, (byte)item.Subclass, (byte)item.Material, (byte)item.Sheath);

    private static void AddDressUpPiece(CharacterEquipment kit, DressUpPiece piece) =>
        kit.Add(piece.Name, piece.DisplayId, (int)piece.InventoryType, piece.EquipmentSlot,
            piece.ItemClass, piece.ItemSubclass, piece.Material, piece.Sheath);

    private void DrawDressUpFrame()
    {
        if (!_dressUpOpen || _gameplayArt is null ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player)) return;
        ResolveDressUpPending();
        if (!BeginVanillaWindow("##dress-up", UiPanelFrameLogicalOrigin(UiPanelOwnershipRegistry[18]),
                DressUpFrameUiLaw.FrameSize,
                out ImDrawListPtr draw, out Vector2 origin, out float scale))
        {
            ImGui.End();
            return;
        }

        DressUpFrameUiLaw.LogicalRect portraitRect = DressUpFrameUiLaw.Portrait;
        Vector2 portraitMin = origin + portraitRect.Min * scale;
        uint portrait = RoundAperturePortrait(_playerPortrait, _playerPortraitUsable);
        if (portrait != 0)
            draw.AddImage((nint)portrait, portraitMin, portraitMin + portraitRect.Size * scale,
                DressUpFrameUiLaw.PortraitUvMin, DressUpFrameUiLaw.PortraitUvMax);
        else
            DrawUnitPortraitImage(draw, player, portraitMin, portraitRect.Width * scale, 0, true);

        DrawFourPieceShell(draw, origin, scale,
            @"Interface\PaperDollInfoFrame\UI-Character-General-TopLeft",
            @"Interface\PaperDollInfoFrame\UI-Character-General-TopRight",
            @"Interface\PaperDollInfoFrame\SkillFrame-BotLeft",
            @"Interface\PaperDollInfoFrame\SkillFrame-BotRight");

        string race = DressUpFrameUiLaw.BackgroundRace(_character?.Race ?? "Orc");
        string background = $@"Interface\DressUpFrame\DressUpBackground-{race}";
        DrawDressUpArt(draw, origin, scale, DressUpFrameUiLaw.BackdropTopLeft, background + "1");
        DrawDressUpArt(draw, origin, scale, DressUpFrameUiLaw.BackdropTopRight, background + "2");
        DrawDressUpArt(draw, origin, scale, DressUpFrameUiLaw.BackdropBottomLeft, background + "3");
        DrawDressUpArt(draw, origin, scale, DressUpFrameUiLaw.BackdropBottomRight, background + "4");

        DressUpFrameUiLaw.LogicalRect modelRect = DressUpFrameUiLaw.Model;
        Vector2 modelMin = origin + modelRect.Min * scale;
        if (_dressUpTarget is not null && _dressUpTarget.TextureHandle != 0)
            draw.AddImage((nint)_dressUpTarget.TextureHandle, modelMin,
                modelMin + modelRect.Size * scale, DressUpFrameUiLaw.PortraitUvMin,
                DressUpFrameUiLaw.PortraitUvMax);

        GameText.DrawCentered(draw, "GameFontHighlight", "Dressing Room",
            origin + DressUpFrameUiLaw.TitleCenter * scale, scale);
        GameText.DrawCentered(draw, "GameFontNormalSmall",
            "CTRL-Left Click additional items to display them",
            origin + DressUpFrameUiLaw.DescriptionLineOneCenter * scale, scale);
        GameText.DrawCentered(draw, "GameFontNormalSmall", "on your character.",
            origin + DressUpFrameUiLaw.DescriptionLineTwoCenter * scale, scale);

        DrawDressUpRotate(draw, origin, scale, left: true);
        DrawDressUpRotate(draw, origin, scale, left: false);
        if (VanillaButton(draw, "##dress-up-reset", "Reset",
                origin + DressUpFrameUiLaw.Reset.Min * scale,
                DressUpFrameUiLaw.Reset.Size, scale))
            ResetDressUpPreview();
        if (VanillaButton(draw, "##dress-up-close-bottom", "Close",
                origin + DressUpFrameUiLaw.Close.Min * scale,
                DressUpFrameUiLaw.Close.Size, scale))
            CloseDressUp();
        DrawImageButton(draw, "##dress-up-close-x",
            origin + DressUpFrameUiLaw.CloseX.Min * scale,
            DressUpFrameUiLaw.CloseX.Size * scale,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked()) CloseDressUp();
        ImGui.End();
    }

    private void DrawDressUpArt(ImDrawListPtr draw, Vector2 origin, float scale,
        DressUpFrameUiLaw.LogicalRect rect, string path) =>
        DrawArt(draw, path, origin + rect.Min * scale, rect.Size, scale);

    private void DrawDressUpRotate(ImDrawListPtr draw, Vector2 origin, float scale, bool left)
    {
        DressUpFrameUiLaw.LogicalRect rect = left
            ? DressUpFrameUiLaw.RotateLeft : DressUpFrameUiLaw.RotateRight;
        string stem = left ? "UI-RotationLeft-Button" : "UI-RotationRight-Button";
        DrawImageButton(draw, left ? "##dress-up-rotate-left" : "##dress-up-rotate-right",
            origin + rect.Min * scale, rect.Size * scale,
            $@"Interface\Buttons\{stem}-Up", $@"Interface\Buttons\{stem}-Down",
            @"Interface\Buttons\ButtonHilight-Round");
        bool changed = false;
        if (ImGui.IsItemClicked())
        {
            _dressUpRotation = DressUpFrameUiLaw.ClickFacing(_dressUpRotation, left);
            PlayUiSound("igInventoryRotateCharacter", "ui.dress-up");
            changed = true;
        }
        if (ImGui.IsItemActive())
        {
            _dressUpRotation = DressUpFrameUiLaw.HeldFacing(
                _dressUpRotation, left, ImGui.GetIO().DeltaTime);
            changed = true;
        }
        if (ImGui.IsItemDeactivated())
        {
            _dressUpRotation = DressUpFrameUiLaw.ClickFacing(_dressUpRotation, left);
            PlayUiSound("igInventoryRotateCharacter", "ui.dress-up");
            changed = true;
        }
        if (changed) _dressUpDirty = true;
    }
}
