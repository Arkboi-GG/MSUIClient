using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private uint CurrentGuildRank() =>
        _entities.TryGet(LocalPlayerGuid, out WorldEntity player)
            ? player.Fields.PlayerGuildRank : uint.MaxValue;

    private bool CanEditGuildInfo() =>
        GuildFrameUiLaw.CanEditInfo(CurrentGuildRank(), _guildRankRights);

    private void ToggleGuildInfoFrame()
    {
        if (_guildInfoOpen)
        {
            _guildInfoOpen = false;
            return;
        }

        _guildInfoDraft = GuildFrameUiLaw.InitialInfoText(_guildInfo, CanEditGuildInfo());
        _guildMemberDetailOpen = false;
        _guildControlOpen = false;
        _guildInfoOpen = true;
    }

    private void SaveGuildInfo()
    {
        if (!CanEditGuildInfo()) return;
        string text = GuildFrameUiLaw.TruncateInfo(_guildInfoDraft);
        _guildInfo = text;
        _net?.GuildInfoText(text);
        RequestGuildRoster();
        _guildInfoOpen = false;
    }

    private void DrawGuildInfoFrame()
    {
        if (!_guildInfoOpen) return;
        if (!_guildOpen || _gameplayArt is null || _skin is null)
        {
            _guildInfoOpen = false;
            return;
        }

        float scale = GameplayUiScale();
        Vector2 guildOrigin = UiPanelFrameOrigin(UiPanelOwnershipRegistry[14], scale);
        Vector2 origin = GuildFrameUiLaw.InfoFrameOrigin(guildOrigin, scale);
        Vector2 size = GuildFrameUiLaw.InfoFrame.Size * scale;
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse;
        if (!ImGui.Begin("##guild-info-frame", flags))
        {
            ImGui.End();
            return;
        }

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        _skin.DrawBackdrop(draw, origin, origin + size, WowSkin.Dialog);
        DrawArt(draw, @"Interface\DialogFrame\UI-DialogBox-Corner",
            origin + GuildFrameUiLaw.InfoCorner.Min * scale,
            GuildFrameUiLaw.InfoCorner.Size, scale);
        DrawArt(draw, @"Interface\FriendsFrame\UI-GuildMember-Patch",
            origin + GuildFrameUiLaw.InfoBottomPatch.Min * scale,
            GuildFrameUiLaw.InfoBottomPatch.Size, scale);
        GameText.Draw(draw, "GameFontNormal", "Guild Information",
            origin + GuildFrameUiLaw.InfoTitle.Min * scale, scale);

        GuildFrameUiLaw.LogicalRect pane = GuildFrameUiLaw.InfoTextBackground;
        _skin.DrawBackdrop(draw, origin + pane.Min * scale,
            origin + (pane.Min + pane.Size) * scale, WowSkin.Tooltip);

        bool canEdit = CanEditGuildInfo();
        GuildFrameUiLaw.LogicalRect edit = GuildFrameUiLaw.InfoEditBox;
        Vector2 editMin = origin + edit.Min * scale;
        if (canEdit)
        {
            ImGui.SetCursorScreenPos(editMin);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0);
            ImGui.PushStyleColor(ImGuiCol.Text, Vector4.One);
            ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.Border, Vector4.Zero);
            if (ImGui.InputTextMultiline("##guild-info-edit", ref _guildInfoDraft,
                    2048, edit.Size * scale))
                _guildInfoDraft = GuildFrameUiLaw.TruncateInfo(_guildInfoDraft);
            ImGui.PopStyleColor(5);
            ImGui.PopStyleVar(2);
        }
        else
        {
            DrawGuildFixedText(draw, origin, scale, edit,
                GuildFrameUiLaw.InfoTextFont, _guildInfoDraft, 0xffa6a6a6);
        }

        GuildFrameUiLaw.LogicalRect save = GuildFrameUiLaw.InfoSaveButton;
        if (VanillaButton(draw, "##guild-info-save", "Accept",
                origin + save.Min * scale, save.Size, scale, canEdit,
                normalFont: GuildFrameUiLaw.SmallButtonNormalFont,
                highlightFont: GuildFrameUiLaw.SmallButtonHighlightFont,
                disabledFont: GuildFrameUiLaw.SmallButtonDisabledFont))
            SaveGuildInfo();
        GuildFrameUiLaw.LogicalRect cancel = GuildFrameUiLaw.InfoCancelButton;
        if (VanillaButton(draw, "##guild-info-cancel", "Close",
                origin + cancel.Min * scale, cancel.Size, scale,
                normalFont: GuildFrameUiLaw.SmallButtonNormalFont,
                highlightFont: GuildFrameUiLaw.SmallButtonHighlightFont,
                disabledFont: GuildFrameUiLaw.SmallButtonDisabledFont))
            _guildInfoOpen = false;

        GuildFrameUiLaw.LogicalRect close = GuildFrameUiLaw.InfoCloseButton;
        DrawImageButton(draw, "##guild-info-close",
            origin + close.Min * scale, close.Size * scale,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked()) _guildInfoOpen = false;

        ImGui.End();
    }
}
