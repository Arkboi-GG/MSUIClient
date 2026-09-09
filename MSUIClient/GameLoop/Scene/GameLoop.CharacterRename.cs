using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private NetworkClient? _characterRenameSession;
    private ulong _characterRenameGuid;
    private readonly byte[] _characterRenameText = new byte[64];
    private bool _characterRenameWaiting, _characterRenameFocus;
    private string _characterRenameError = "";

    private bool PrepareCharacterRenameDialog()
    {
        if (!ReferenceEquals(_characterRenameSession, _net)) CloseCharacterRename();
        if (_net?.TryTakeRenameResult(out var result) == true && _characterRenameGuid != 0)
        {
            _characterRenameWaiting = false;
            if (result.Succeeded) CloseCharacterRename();
            else
            {
                _characterRenameError = result.Code == 0x2F ? "That name is unavailable. Please choose another." : CharResultText(result.Code);
                _characterRenameFocus = true;
            }
        }
        if (_characterRenameGuid != 0 && !_characterRenameWaiting &&
            _net?.Characters.FirstOrDefault(c => c.Guid == _characterRenameGuid)?.RequiresRename != true)
            CloseCharacterRename();
        return _characterRenameGuid != 0;
    }

    private bool BeginRequiredCharacterRename(ulong guid)
    {
        if (_net?.Characters.FirstOrDefault(c => c.Guid == guid)?.RequiresRename != true) return false;
        CloseDeleteConfirm();
        CancelPendingWorldCurtain();
        _characterRenameSession = _net;
        _characterRenameGuid = guid;
        _characterRenameWaiting = false;
        _characterRenameFocus = true;
        _characterRenameError = "";
        Array.Clear(_characterRenameText);
        return true;
    }

    private void CloseCharacterRename()
    {
        _characterRenameSession = null;
        _characterRenameGuid = 0;
        _characterRenameWaiting = false;
        _characterRenameError = "";
        Array.Clear(_characterRenameText);
    }

    private void DrawCharacterRenameDialog(ImDrawListPtr draw, Vector2 display, float scale)
    {
        if (_characterRenameGuid == 0) return;
        var dialog = CharSelectUiLaw.DeleteDialog(display, scale);
        if (_skin is not null) _skin.DrawBackdrop(draw, dialog.Frame.Min, dialog.Frame.Max, WowSkin.Dialog);
        else draw.AddRectFilled(dialog.Frame.Min, dialog.Frame.Max, 0xF0000000);
        GlueText(draw, "Rename Character", dialog.LeadCenter.X, dialog.LeadCenter.Y, 18 * scale, WowSkin.GlueGold, 1);
        string oldName = _net?.Characters.FirstOrDefault(c => c.Guid == _characterRenameGuid)?.Name ?? "";
        GlueText(draw, oldName, dialog.IdentityCenter.X, dialog.IdentityCenter.Y, 18 * scale, WowSkin.Normal, 1);
        GlueText(draw, "This character requires a new name.", dialog.InstructionsCenter.X,
            dialog.InstructionsCenter.Y, 12 * scale, WowSkin.GlueGold, 1);
        ImGui.SetCursorScreenPos(dialog.Edit.Min);
        ImGui.SetNextItemWidth(dialog.Edit.Size.X);
        bool startedWaiting = _characterRenameWaiting;
        if (startedWaiting) ImGui.BeginDisabled();
        if (_characterRenameFocus) { ImGui.SetKeyboardFocusHere(); _characterRenameFocus = false; }
        ImGui.InputText("##required-character-name", _characterRenameText, (uint)_characterRenameText.Length);
        string name = BufToString(_characterRenameText);
        bool valid = CharacterRenamePackets.ValidRequestName(name);
        bool enter = ImGui.IsKeyPressed(ImGuiKey.Enter, false);
        ImGui.SetCursorScreenPos(dialog.Okay.Min);
        bool accept = _skin?.GlueButton("Okay", dialog.Okay.Size, valid) ?? ImGui.Button("Okay", dialog.Okay.Size);
        if (!_characterRenameWaiting && valid && (accept || enter))
        {
            _characterRenameWaiting = _net?.RenameCharacter(_characterRenameGuid, name) == true;
            if (!_characterRenameWaiting) _characterRenameError = "Unable to submit the name. Please try again.";
        }
        ImGui.SetCursorScreenPos(dialog.Cancel.Min);
        bool cancel = _skin?.GlueButton("Cancel", dialog.Cancel.Size) ?? ImGui.Button("Cancel", dialog.Cancel.Size);
        // The request is owned until its ordered result/roster arrives.
        if (startedWaiting) ImGui.EndDisabled();
        if (!_characterRenameWaiting && (cancel || ImGui.IsKeyPressed(ImGuiKey.Escape, false))) CloseCharacterRename();
        string message = _characterRenameWaiting ? "Renaming..." : _characterRenameError;
        if (message.Length != 0)
            DrawWrappedText(draw, message, dialog.Frame.Min.X + 24 * scale,
                dialog.Edit.Max.Y + 12 * scale, dialog.Frame.Size.X - 48 * scale, 12 * scale, WowSkin.Normal);
    }
}
