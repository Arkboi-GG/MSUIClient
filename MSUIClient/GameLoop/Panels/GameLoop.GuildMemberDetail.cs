using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private GuildMember? SelectedGuildMember() =>
        _guildSelected >= 0 && _guildSelected < _guildMembers.Count
            ? _guildMembers[_guildSelected] : null;

    private void ShowGuildMemberPopup(StaticPopupCoordinatorLaw.Definition definition)
    {
        if (SelectedGuildMember() is not { } member) return;
        ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Show(_staticPopupSlots,
            definition, playerDeadOrGhost: false, dataToken: member.Name));
    }

    private bool DrawGuildRankArrow(ImDrawListPtr draw, string id, Vector2 origin,
        GuildFrameUiLaw.LogicalRect rect, float scale, bool up, bool enabled)
    {
        string stem = up ? "ScrollUp" : "ScrollDown";
        Vector2 min = origin + rect.Min * scale;
        Vector2 size = rect.Size * scale;
        GuildFrameUiLaw.LogicalRect hit = GuildFrameUiLaw.MemberRankArrowHit(rect);
        Vector2 hitMin = origin + hit.Min * scale;
        Vector2 hitSize = hit.Size * scale;
        ImGui.SetCursorScreenPos(hitMin);
        if (!enabled) ImGui.BeginDisabled();
        bool clicked = ImGui.InvisibleButton(id, hitSize);
        bool held = enabled && ImGui.IsItemActive();
        bool hovered = enabled && ImGui.IsItemHovered();
        if (!enabled) ImGui.EndDisabled();
        string state = !enabled ? "Disabled" : held ? "Down" : "Up";
        uint texture = _gameplayArt?.Handle(
            $@"Interface\MainMenuBar\UI-MainMenu-{stem}Button-{state}") ?? 0;
        if (texture != 0) draw.AddImage((nint)texture, min, min + size);
        if (hovered)
        {
            uint highlight = _gameplayArt?.AdditiveHandle(
                $@"Interface\MainMenuBar\UI-MainMenu-{stem}Button-Highlight") ?? 0;
            if (highlight != 0) draw.AddImage((nint)highlight, min, min + size);
        }
        OfferVanillaNewbieTooltip(new("guild-member-rank", up ? 1ul : 2ul),
            up ? "Promote" : "Demote",
            up ? GuildFrameUiLaw.PromoteMemberTooltip :
                GuildFrameUiLaw.DemoteMemberTooltip);
        return enabled && clicked;
    }

    private void DrawGuildMemberDetailFrame()
    {
        if (!_guildMemberDetailOpen) return;
        if (!_guildOpen || _gameplayArt is null || _skin is null ||
            SelectedGuildMember() is not { } member)
        {
            _guildMemberDetailOpen = false;
            return;
        }

        uint myRank = CurrentGuildRank();
        uint bottomRank = _guildRankRights.Count == 0
            ? uint.MaxValue : (uint)_guildRankRights.Count - 1;
        bool mayEditPublic = GuildFrameUiLaw.HasRight(myRank, _guildRankRights,
            GuildFrameUiLaw.EditPublicNoteRight);
        bool mayViewOfficer = GuildFrameUiLaw.HasRight(myRank, _guildRankRights,
            GuildFrameUiLaw.ViewOfficerNoteRight);
        bool mayEditOfficer = GuildFrameUiLaw.HasRight(myRank, _guildRankRights,
            GuildFrameUiLaw.EditOfficerNoteRight);
        bool mayPromote = !_guildPromotePending && GuildFrameUiLaw.CanPromote(
            myRank, member.Rank, _guildRankRights);
        bool mayDemote = !_guildDemotePending && GuildFrameUiLaw.CanDemote(
            myRank, member.Rank, bottomRank, _guildRankRights);
        bool mayRemove = GuildFrameUiLaw.CanRemove(myRank, member.Rank, _guildRankRights);
        float logicalHeight = GuildFrameUiLaw.MemberDetailHeight(mayViewOfficer);
        float scale = GameplayUiScale();
        Vector2 guildOrigin = UiPanelFrameOrigin(UiPanelOwnershipRegistry[14], scale);
        Vector2 origin = GuildFrameUiLaw.MemberDetailOrigin(guildOrigin, scale);
        Vector2 size = GuildFrameUiLaw.MemberDetailSize(logicalHeight) * scale;

        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse;
        if (!ImGui.Begin("##guild-member-detail", flags))
        {
            ImGui.End();
            return;
        }

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        _skin.DrawBackdrop(draw, origin, origin + size, WowSkin.Dialog);
        GuildFrameUiLaw.LogicalRect patch = GuildFrameUiLaw.MemberBottomPatch(logicalHeight);
        DrawArt(draw, @"Interface\FriendsFrame\UI-GuildMember-Patch",
            origin + patch.Min * scale, patch.Size, scale);
        GuildFrameUiLaw.LogicalRect corner = GuildFrameUiLaw.MemberCorner(logicalHeight);
        DrawArt(draw, @"Interface\DialogFrame\UI-DialogBox-Corner",
            origin + corner.Min * scale, corner.Size, scale);

        string zone = GuildFrameUiLaw.ResolvedRosterLabel(_areas?.ZoneName(member.Zone));
        string rank = member.Rank < _guildRankNames.Length
            ? _guildRankNames[member.Rank] : "";
        string online = member.Online ? "Online" : GuildFrameUiLaw.LastOnline(member.OfflineDays);
        DrawGuildMemberText(draw, origin, scale, member.Name,
            $"Level {member.Level} {GuildFrameUiLaw.ResolvedRosterLabel(ClassName(member.Class))}",
            zone, rank, online);

        DrawGuildNotePane(draw, origin, scale, GuildFrameUiLaw.MemberNotePane,
            GuildFrameUiLaw.MemberNoteText,
            GuildFrameUiLaw.DisplayNote(member.PublicNote, mayEditPublic,
                GuildFrameUiLaw.PublicNotePlaceholder), mayEditPublic,
            "##guild-public-note", GuildFrameUiLaw.SetPublicNoteDefinition);
        if (mayViewOfficer)
        {
            GameText.Draw(draw, "GameFontNormalSmall", "Officer's Note",
                origin + GuildFrameUiLaw.MemberOfficerLabel.Min * scale, scale);
            DrawGuildNotePane(draw, origin, scale, GuildFrameUiLaw.MemberOfficerPane,
                GuildFrameUiLaw.MemberOfficerText,
                GuildFrameUiLaw.DisplayNote(member.OfficerNote, mayEditOfficer,
                    GuildFrameUiLaw.OfficerNotePlaceholder), mayEditOfficer,
                "##guild-officer-note", GuildFrameUiLaw.SetOfficerNoteDefinition);
        }

        GuildFrameUiLaw.LogicalRect remove = GuildFrameUiLaw.MemberRemoveButton(logicalHeight);
        if (VanillaButton(draw, "##guild-member-remove", "Remove",
                origin + remove.Min * scale, remove.Size, scale, mayRemove,
                normalFont: GuildFrameUiLaw.SmallButtonNormalFont,
                highlightFont: GuildFrameUiLaw.SmallButtonHighlightFont,
                disabledFont: GuildFrameUiLaw.SmallButtonDisabledFont))
            ShowGuildMemberPopup(GuildFrameUiLaw.RemoveMemberDefinition);
        OfferVanillaNewbieTooltip(new("guild-member-action", 1), "Remove",
            GuildFrameUiLaw.RemoveMemberTooltip);
        GuildFrameUiLaw.LogicalRect invite = GuildFrameUiLaw.MemberInviteButton(logicalHeight);
        bool canInvite = member.Online && !string.Equals(member.Name,
            ResolveUnitName(LocalPlayerGuid), StringComparison.OrdinalIgnoreCase);
        if (VanillaButton(draw, "##guild-member-invite", "Group Invite",
                origin + invite.Min * scale, invite.Size, scale, canInvite,
                normalFont: GuildFrameUiLaw.SmallButtonNormalFont,
                highlightFont: GuildFrameUiLaw.SmallButtonHighlightFont,
                disabledFont: GuildFrameUiLaw.SmallButtonDisabledFont))
            _net?.GroupInvite(member.Name);
        OfferVanillaNewbieTooltip(new("guild-member-action", 2), "Group Invite",
            FriendsFrameUiLaw.GroupInviteTooltip);

        if (mayPromote || mayDemote)
        {
            if (DrawGuildRankArrow(draw, "##guild-member-promote", origin,
                    GuildFrameUiLaw.MemberPromoteButton(logicalHeight), scale,
                    up: true, enabled: mayPromote))
            {
                PromoteGuildMember(member.Name);
                PlayUiSound("UChatScrollButton", "ui.guild");
                _guildPromotePending = true;
            }
            if (DrawGuildRankArrow(draw, "##guild-member-demote", origin,
                    GuildFrameUiLaw.MemberDemoteButton(logicalHeight), scale,
                    up: false, enabled: mayDemote))
            {
                DemoteGuildMember(member.Name);
                PlayUiSound("UChatScrollButton", "ui.guild");
                _guildDemotePending = true;
            }
        }

        GuildFrameUiLaw.LogicalRect close = GuildFrameUiLaw.MemberCloseButton(logicalHeight);
        DrawImageButton(draw, "##guild-member-close", origin + close.Min * scale,
            close.Size * scale, @"Interface\Buttons\UI-Panel-MinimizeButton-Up",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked()) _guildMemberDetailOpen = false;
        ImGui.End();
    }

    private static void DrawGuildMemberText(ImDrawListPtr draw, Vector2 origin, float scale,
        string name, string level, string zone, string rank, string online)
    {
        GameText.Draw(draw, "GameFontNormal", name,
            origin + GuildFrameUiLaw.MemberName.Min * scale, scale);
        GameText.Draw(draw, "GameFontHighlightSmall", level,
            origin + GuildFrameUiLaw.MemberLevel.Min * scale, scale);
        DrawGuildDetailPair(draw, origin, scale, GuildFrameUiLaw.MemberZoneLabel,
            GuildFrameUiLaw.MemberZoneText, "Zone:", zone);
        DrawGuildDetailPair(draw, origin, scale, GuildFrameUiLaw.MemberRankLabel,
            GuildFrameUiLaw.MemberRankText, "Rank:", rank);
        DrawGuildDetailPair(draw, origin, scale, GuildFrameUiLaw.MemberOnlineLabel,
            GuildFrameUiLaw.MemberOnlineText, "Last Online:", online);
        GameText.Draw(draw, "GameFontNormalSmall", "Note:",
            origin + GuildFrameUiLaw.MemberNoteLabel.Min * scale, scale);
    }

    private static void DrawGuildDetailPair(ImDrawListPtr draw, Vector2 origin, float scale,
        GuildFrameUiLaw.LogicalRect labelRect, GuildFrameUiLaw.LogicalRect valueRect,
        string label, string value)
    {
        GameText.Draw(draw, "GameFontNormalSmall", label,
            origin + labelRect.Min * scale, scale);
        GameText.Draw(draw, "GameFontHighlight", value,
            origin + valueRect.Min * scale, scale);
    }

    private void DrawGuildNotePane(ImDrawListPtr draw, Vector2 origin, float scale,
        GuildFrameUiLaw.LogicalRect pane, GuildFrameUiLaw.LogicalRect text,
        string value, bool editable, string id,
        StaticPopupCoordinatorLaw.Definition definition)
    {
        Vector2 min = origin + pane.Min * scale;
        _skin!.DrawBackdrop(draw, min, min + pane.Size * scale, WowSkin.Tooltip,
            new Vector4(1, 1, 1, .25f), new Vector4(1, 1, 1, .5f));
        if (editable)
        {
            ImGui.SetCursorScreenPos(min);
            if (ImGui.InvisibleButton(id, pane.Size * scale))
                ShowGuildMemberPopup(definition);
        }
        uint color = editable ? 0xffffffff : 0xffa6a6a6;
        DrawGuildFixedText(draw, origin, scale, text,
            GuildFrameUiLaw.MemberNoteFont, value, color);
    }

    private static void DrawGuildFixedText(ImDrawListPtr draw, Vector2 origin, float scale,
        GuildFrameUiLaw.LogicalRect box, string font, string value, uint color)
    {
        float pitch = GameText.LinePitch(font, scale);
        int maximumLines = Math.Max(1, (int)MathF.Floor(box.Height * scale / pitch));
        string[] lines = WrapTooltipText(value, font, scale, box.Width * scale)
            .Take(maximumLines).ToArray();
        Vector2 boxMin = origin + box.Min * scale;
        draw.PushClipRect(boxMin, boxMin + box.Size * scale, true);
        for (int line = 0; line < lines.Length; line++)
            GameText.Draw(draw, font, lines[line],
                GuildFrameUiLaw.FixedTextLineMin(origin, box, scale, line, pitch),
                scale, color);
        draw.PopClipRect();
    }
}
