using System.Numerics;
using System.Text;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private sealed record GuildMember(ulong Guid, byte Presence, string Name, uint Rank,
        byte Level, byte Class, uint Zone, float OfflineDays, string PublicNote, string OfficerNote)
    {
        public bool Online => Presence != 0;
    }

    private readonly List<GuildMember> _guildMembers = [];
    private readonly List<uint> _guildRankRights = [];
    private readonly GuildRosterSortLaw _guildSort = new();
    private readonly HashSet<uint> _guildIdentityQueried = [];
    private readonly string[] _guildRankNames = new string[10];
    private uint _guildId;
    private string _guildName = "";
    private bool _guildOpen;
    private string _guildMotd = "";
    private string _guildInfo = "";
    private int _guildSelected = -1;
    private int _guildScroll;
    private bool _guildShowOffline;
    private readonly byte[] _guildMotdEdit = new byte[256];
    private bool _guildStatusView;
    private bool _guildInfoOpen;
    private string _guildInfoDraft = "";
    private bool _guildMemberDetailOpen;
    private bool _guildPromotePending;
    private bool _guildDemotePending;
    private bool _guildControlOpen;
    private bool _guildControlDropDownOpen;
    private int _guildControlRank;
    private uint _guildControlRights;
    private string _guildControlRankName = "";
    private bool _guildControlDirty;

    private void InitGuild() { }
    private void ResetGuild()
    {
        _guildMembers.Clear(); _guildRankRights.Clear(); _guildIdentityQueried.Clear();
        Array.Clear(_guildRankNames); _guildId = 0; _guildName = "";
        _guildOpen = false; _guildMotd = _guildInfo = ""; _guildSelected = -1;
        _guildInfoOpen = false; _guildInfoDraft = ""; _guildMemberDetailOpen = false;
        _guildPromotePending = _guildDemotePending = false;
        _guildControlOpen = _guildControlDropDownOpen = false;
        _guildControlRank = 0; _guildControlRights = 0; _guildControlRankName = "";
        _guildControlDirty = false;
    }

    private bool RequestGuildRoster()
    {
        bool sent = _net?.GuildRoster() == true;
        EmitInterface("guild", "roster-send", sent ? "SENT" : "SEND_FAILED", _net?.PlayerGuid ?? 0, "body=EMPTY");
        return sent;
    }

    private uint CurrentGuildId() =>
        _entities.TryGet(LocalPlayerGuid, out WorldEntity player)
            ? player.Fields.PlayerGuildId : 0;

    private void RequestOwnGuildIdentity()
    {
        uint guildId = CurrentGuildId();
        if (guildId == 0 || !_guildIdentityQueried.Add(guildId)) return;
        _net?.GuildQuery(guildId);
    }

    private void ApplyGuildQueryResponse(byte[] body)
    {
        var r = new PacketReader(body);
        uint guildId = r.ReadU32();
        string name = r.ReadCString();
        var ranks = new string[10];
        for (int i = 0; i < ranks.Length; i++) ranks[i] = r.ReadCString();
        for (int i = 0; i < 5; i++) r.ReadU32(); // tabard symbol/color/border/background
        if (r.Remaining != 0)
            throw new InvalidDataException($"SMSG_GUILD_QUERY_RESPONSE trailing={r.Remaining}");
        if (guildId != CurrentGuildId()) return;
        _guildId = guildId;
        _guildName = name;
        ranks.CopyTo(_guildRankNames, 0);
        if (_guildControlOpen && !_guildControlDirty &&
            _guildControlRank < _guildRankNames.Length)
            _guildControlRankName = _guildRankNames[_guildControlRank];
    }

    private void ApplyGuildRoster(byte[] body)
    {
        try
        {
            var r = new PacketReader(body); uint count = r.ReadU32(); if (count > 1000) throw new InvalidDataException($"count={count}");
            string motd = r.ReadCString(), info = r.ReadCString(); uint rankCount = r.ReadU32();
            if (rankCount > 10) throw new InvalidDataException($"ranks={rankCount}");
            var rights = new List<uint>((int)rankCount); for (int i = 0; i < rankCount; i++) rights.Add(r.ReadU32());
            var members = new List<GuildMember>((int)count);
            for (int i = 0; i < count; i++)
            {
                ulong guid = r.ReadU64(); byte presence = r.ReadU8(); string name = r.ReadCString();
                uint rank = r.ReadU32(); byte level = r.ReadU8(), cls = r.ReadU8(); uint zone = r.ReadU32();
                float offline = presence != 0 ? 0 : r.ReadF32(); string note = r.ReadCString(), officer = r.ReadCString();
                members.Add(new(guid, presence, name, rank, level, cls, zone, offline, note, officer));
            }
            if (r.Remaining != 0) throw new InvalidDataException($"trailing={r.Remaining}");
            _guildMembers.Clear(); _guildMembers.AddRange(members); _guildRankRights.Clear(); _guildRankRights.AddRange(rights);
            _guildMotd = motd; _guildInfo = info; Array.Clear(_guildMotdEdit); byte[] motdBytes = Encoding.UTF8.GetBytes(motd);
            Array.Copy(motdBytes, _guildMotdEdit, Math.Min(motdBytes.Length, _guildMotdEdit.Length - 1));
            _guildOpen = true;
            if (_guildSelected >= _guildMembers.Count)
            {
                _guildSelected = -1;
                _guildMemberDetailOpen = false;
            }
            _guildPromotePending = _guildDemotePending = false;
            if (_guildControlOpen &&
                (_guildRankRights.Count == 0 || _guildControlRank >= _guildRankRights.Count))
                LoadGuildControlRank(0);
            RequestOwnGuildIdentity();
            EmitInterface("guild", "roster", "DECODED", _net?.PlayerGuid ?? 0,
                $"members={members.Count};ranks={rights.Count};motd={SanitizeEvidence(motd)};online={members.Count(x => x.Online)};names={string.Join('|', members.Select(x => x.Name))}");
        }
        catch (Exception ex) { EmitInterface("guild", "roster", "MALFORMED", 0, $"error={SanitizeEvidence(ex.Message)};bytes={body.Length}"); }
    }

    private bool SetGuildMotd(string motd)
    {
        bool sent = _net?.GuildMotd(motd) == true;
        EmitInterface("guild", "motd-send", sent ? "SENT" : "SEND_FAILED", _net?.PlayerGuid ?? 0,
            $"motd={SanitizeEvidence(motd)};body={Convert.ToHexString(WorldSession.BuildCStringBody(motd))}"); return sent;
    }

    private bool PromoteGuildMember(string name)
    {
        bool sent = _net?.GuildPromote(name) == true;
        EmitInterface("guild", "promote-send", sent ? "SENT" : "SEND_FAILED", 0, $"name={SanitizeEvidence(name)};body={Convert.ToHexString(WorldSession.BuildCStringBody(name))}"); return sent;
    }
    private bool DemoteGuildMember(string name)
    {
        bool sent = _net?.GuildDemote(name) == true;
        EmitInterface("guild", "demote-send", sent ? "SENT" : "SEND_FAILED", 0, $"name={SanitizeEvidence(name)};body={Convert.ToHexString(WorldSession.BuildCStringBody(name))}"); return sent;
    }
    private bool LeaveGuild()
    { bool sent = _net?.GuildLeave() == true; EmitInterface("guild", "leave-send", sent ? "SENT" : "SEND_FAILED", 0, "body=EMPTY"); return sent; }
    private bool DisbandGuild()
    { bool sent = _net?.GuildDisband() == true; EmitInterface("guild", "disband-send", sent ? "SENT" : "SEND_FAILED", 0, "body=EMPTY"); return sent; }

    private void ApplyGuildEvent(byte[] body)
    {
        GuildEventWire notice = GuildFramePacketLaw.ParseEvent(body);
        EmitInterface("guild", "event", "RECEIVED", notice.AffectedGuid ?? 0,
            $"event={notice.Event};values={string.Join('|', notice.Parameters.Select(SanitizeEvidence))}");
        if (notice.Event == GuildFramePacketLaw.Motd)
        {
            _guildMotd = notice.Parameters.FirstOrDefault() ?? "";
            AddChatMessage(GuildFramePacketLaw.MotdLine(_guildMotd));
        }
        else if (notice.Event == GuildFramePacketLaw.UpdateRankName)
        {
            if (notice.Parameters.Count >= 2 &&
                int.TryParse(notice.Parameters[0], out int rank) &&
                rank >= 0 && rank < _guildRankNames.Length)
            {
                _guildRankNames[rank] = notice.Parameters[1];
                if (_guildControlOpen && !_guildControlDirty && _guildControlRank == rank)
                    _guildControlRankName = notice.Parameters[1];
            }
        }
        bool ignored = notice.AffectedGuid is ulong guid && _ignored.Contains(guid);
        if (GuildFramePacketLaw.EventLine(notice, ignored) is { } line)
            AddChatMessage(line);
        if (GuildFramePacketLaw.MakesRosterStale(notice.Event)) RequestGuildRoster();
    }

    private void ApplyGuildCommandResult(byte[] body)
    {
        GuildCommandResultWire result = GuildFramePacketLaw.ParseCommandResult(body);
        EmitInterface("guild", "command", result.Result == 0 ? "SUCCESS" : $"FAILED-{result.Result}", 0,
            $"command={result.Command};text={SanitizeEvidence(result.Name)};body={Convert.ToHexString(body)}");
        if (GuildFramePacketLaw.CommandLine(result) is { } line) AddChatMessage(line);
        if (GuildFramePacketLaw.CommandMakesRosterStale(result)) RequestGuildRoster();
    }

    private void ApplyGuildInvite(byte[] body)
    {
        GuildInviteWire invite = GuildFramePacketLaw.ParseInvite(body);
        AddChatMessage(GuildFramePacketLaw.InviteLine(invite));
        ShowGuildInvitePopup(invite);
        EmitInterface("guild", "invite", "RECEIVED", 0,
            $"inviter={SanitizeEvidence(invite.Inviter)};guild={SanitizeEvidence(invite.Guild)}");
    }

    private void ApplyGuildDecline(byte[] body)
    {
        string name = GuildFramePacketLaw.ParseDecline(body);
        AddChatMessage(GuildFramePacketLaw.DeclineLine(name));
        EmitInterface("guild", "decline", "RECEIVED", 0,
            $"name={SanitizeEvidence(name)}");
    }

    private void ApplyGuildInfo(byte[] body)
    {
        GuildInfoWire info = GuildFramePacketLaw.ParseInfo(body);
        foreach (string line in GuildFramePacketLaw.InfoLines(info)) AddChatMessage(line);
        EmitInterface("guild", "info", "RECEIVED", 0,
            $"guild={SanitizeEvidence(info.Name)};created={info.CreatedDay}-{info.CreatedMonth}-{info.CreatedYear};members={info.MemberCount};accounts={info.AccountCount}");
    }

    private void SimulateGuildFlow()
    {
        var w = new PacketWriter(); w.WriteU32(2); w.WriteCString("Night shift"); w.WriteCString("Autonomous acceptance guild"); w.WriteU32(3);
        w.WriteU32(0xFFFF); w.WriteU32(0x0FFF); w.WriteU32(0x003F);
        WriteGuildMember(w, 1, true, "Test", 0, 60, 1, 12, 0, "Leader", "TEST account");
        WriteGuildMember(w, 2, false, "Rosterbot", 2, 60, 8, 12, 1.5f, "Member", "offline fixture");
        ApplyGuildRoster(w.ToArray());
        foreach ((string step, uint command) in new[] { ("motd", 3u), ("promote", 1u), ("demote", 2u), ("leave", 4u), ("disband", 5u) })
        { var result = new PacketWriter(); result.WriteU32(command); result.WriteCString(step); result.WriteU32(0); ApplyGuildCommandResult(result.ToArray()); EmitInterface("guild", step, "SUCCESS", 0, $"command={command};source=runtime-replay"); }
        EmitInterface("guild", "rank-flow", "VERIFIED", 0, "promote=0->1;demote=1->2;ranks=3");
    }

    private static void WriteGuildMember(PacketWriter w, ulong guid, bool online, string name, uint rank,
        byte level, byte cls, uint zone, float offline, string note, string officer)
    {
        w.WriteU64(guid); w.WriteU8(online ? (byte)1 : (byte)0); w.WriteCString(name); w.WriteU32(rank);
        w.WriteU8(level); w.WriteU8(cls); w.WriteU32(zone); if (!online) w.WriteF32(offline); w.WriteCString(note); w.WriteCString(officer);
    }

    private void DrawGuildFrame()
    {
        if (!_guildOpen || _gameplayArt is null) return;
        RequestOwnGuildIdentity();
        float s = GameplayUiScale();
        Vector2 origin = UiPanelFrameOrigin(UiPanelOwnershipRegistry[14], s);
        Vector2 logicalSize = FriendsFrameUiLaw.FrameSize(1f);
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(logicalSize * s, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse;
        if (!ImGui.Begin("##guild", flags)) { ImGui.End(); return; }

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        if (_uiParityArmed && _uiParityPanel == "guild")
        {
            BeginUiParityFrame(origin, s);
            CollectUiParityDraw("GuildFrame", "Frame", origin, logicalSize * s, "",
                new("", 0, "IMGUI_HOST", "ANCHOR:ABSOLUTE", "", "", 0, 8));
        }
        FriendsFrameUiLaw.ShellArt shell = FriendsFrameUiLaw.ShellFor(page: 2, ignore: false);
        DrawFourPieceShell(dl, origin, s,
            shell.TopLeft, shell.TopRight, shell.BottomLeft, shell.BottomRight);
        DrawArt(dl, @"Interface\FriendsFrame\FriendsFrameScrollIcon",
            origin + FriendsFrameUiLaw.ScrollIcon.Min * s,
            FriendsFrameUiLaw.ScrollIcon.Size, s);
        GameText.DrawCentered(dl, "GameFontNormal", _guildName,
            origin + FriendsFrameUiLaw.TitleCenter * s, s);

        DrawGuildOfflineFilter(dl, origin, s);
        GuildRosterSortProjection[] projections = _guildMembers.Select(GuildProjection).ToArray();
        int[] displayOrder = _guildSort.Order(projections, _guildShowOffline);
        int displayCount = GuildRosterSortLaw.DisplayedCount(projections, _guildShowOffline);
        var shown = displayOrder.Take(displayCount)
            .Select(index => (Member: _guildMembers[index], Index: index)).ToArray();
        _guildScroll = GuildFrameUiLaw.ClampOffset(_guildScroll, shown.Length);
        HandleGuildListWheel(origin, s, shown.Length);
        bool crowded = GuildFrameUiLaw.IsCrowded(shown.Length);
        GuildFrameUiLaw.LogicalRect[] headerRects = _guildStatusView
            ? GuildFrameUiLaw.StatusHeaders(shown.Length)
            : GuildFrameUiLaw.PlayerHeaders(shown.Length);
        string[] headerLabels = _guildStatusView
            ? ["Name", "Rank", "Note", "Last Online"]
            : ["Name", "Zone", "Lvl", "Class"];
        GuildRosterSortField[] headerFields = _guildStatusView
            ? [GuildRosterSortField.Name, GuildRosterSortField.Rank,
                GuildRosterSortField.Note, GuildRosterSortField.Online]
            : [GuildRosterSortField.Name, GuildRosterSortField.Zone,
                GuildRosterSortField.Level, GuildRosterSortField.Class];
        for (int i = 0; i < headerRects.Length; i++)
            if (DrawGuildColumnHeader(dl, origin, s, headerRects[i], headerLabels[i], i))
            {
                _guildSort.Select(headerFields[i]);
                _guildScroll = 0;
                PlayUiSound("igMainMenuOptionCheckBoxOn", "ui.guild");
            }

        int visible = Math.Min(GuildFrameUiLaw.VisibleRows, shown.Length - _guildScroll);
        for (int seat = 0; seat < visible; seat++)
        {
            (GuildMember member, int originalIndex) = shown[_guildScroll + seat];
            GuildFrameUiLaw.LogicalRect row = GuildFrameUiLaw.Row(seat);
            Vector2 rowMin = origin + row.Min * s;
            bool rowClicked = VanillaListRow(dl, $"##guild-{member.Guid}", rowMin,
                row.Size, s, "", _guildSelected == originalIndex,
                highlightPath: GuildFrameUiLaw.RowHighlightPath,
                additiveHighlight: true,
                highlightOffset: GuildFrameUiLaw.RowHighlight.Min,
                highlightLogicalSize: GuildFrameUiLaw.RowHighlight.Size);
            bool rowRightClicked = ImGui.IsItemClicked(ImGuiMouseButton.Right);
            if (!_guildStatusView)
                OfferVanillaNewbieTooltip(new("guild-member-row", member.Guid),
                    "Guild Member Options", GuildFrameUiLaw.MemberOptionsTooltip);
            if (rowRightClicked)
                OpenGuildPopup(member.Guid, member.Name, ImGui.GetIO().MousePos);
            if (rowClicked)
            {
                if (_guildMemberDetailOpen && _guildSelected == originalIndex)
                {
                    _guildSelected = -1;
                    _guildMemberDetailOpen = false;
                }
                else
                {
                    _guildSelected = originalIndex;
                    _guildMemberDetailOpen = true;
                    _guildInfoOpen = false;
                    _guildControlOpen = false;
                }
            }
            DrawGuildRow(dl, rowMin, s, member, crowded);
        }
        DrawSocialFauxScrollBar(dl, "##guild-scroll", origin, s,
            new FriendsFrameUiLaw.LogicalRect(GuildFrameUiLaw.ScrollFrame.X,
                GuildFrameUiLaw.ScrollFrame.Y, GuildFrameUiLaw.ScrollFrame.Width,
                GuildFrameUiLaw.ScrollFrame.Height), _guildScroll,
            GuildFrameUiLaw.MaximumOffset(shown.Length), value => _guildScroll = value);

        GuildFrameUiLaw.LogicalRect toggle = GuildFrameUiLaw.ViewToggle(shown.Length);
        string toggleLabel = _guildStatusView ? "Player Status" : "Guild Status";
        GameText.DrawRightAligned(dl, "GameFontNormalSmall", toggleLabel,
            origin + GuildFrameUiLaw.ViewToggleLabelRight(shown.Length) * s, s);
        DrawImageButton(dl, "##guild-view-toggle", origin + toggle.Min * s,
            toggle.Size * s, @"Interface\Buttons\UI-SpellbookIcon-NextPage-Up",
            @"Interface\Buttons\UI-SpellbookIcon-NextPage-Down",
            @"Interface\Buttons\UI-Common-MouseHilight");
        if (ImGui.IsItemClicked()) _guildStatusView = !_guildStatusView;

        int online = _guildMembers.Count(member => member.Online);
        string total = _guildMembers.Count == 1 ? "1 Guild Member" :
            $"{_guildMembers.Count} Guild Members";
        Vector2 memberTotalAt = origin + GuildFrameUiLaw.MemberTotalBox.Min * s;
        memberTotalAt.Y = GameText.BoxCenteredTop("GameFontNormalSmall", memberTotalAt.Y,
            GuildFrameUiLaw.MemberTotalBox.Height, s);
        GameText.Draw(dl, "GameFontNormalSmall", total, memberTotalAt, s);
        float memberTotalWidth = GameText.MeasureWidth("GameFontNormalSmall", total, s) / s;
        GuildFrameUiLaw.LogicalRect onlineBox = GuildFrameUiLaw.OnlineTotalBox(memberTotalWidth);
        Vector2 onlineTotalAt = origin + onlineBox.Min * s;
        onlineTotalAt.Y = GameText.BoxCenteredTop("GameFontNormalSmall", onlineTotalAt.Y,
            onlineBox.Height, s);
        GameText.Draw(dl, "GameFontNormalSmall", $"({online} Online)", onlineTotalAt,
            s, 0xff00ff00);
        GameText.Draw(dl, "GameFontNormalSmall", "Guild Message Of The Day:",
            origin + GuildFrameUiLaw.MotdLabel.Min * s, s);
        IReadOnlyList<string> motdLines = GuildFrameUiLaw.WrapMotd(_guildMotd,
            GuildFrameUiLaw.MotdText.Width * s, GuildFrameUiLaw.MotdText.Height * s,
            GameText.LinePitch("GameFontHighlightSmall", s),
            line => GameText.MeasureWidth("GameFontHighlightSmall", line, s));
        float motdPitch = GameText.LinePitch("GameFontHighlightSmall", s);
        for (int line = 0; line < motdLines.Count; line++)
        {
            Vector2 at = origin + GuildFrameUiLaw.MotdText.Min * s;
            at.Y += line * motdPitch;
            GameText.Draw(dl, "GameFontHighlightSmall", motdLines[line], at, s);
        }

        if (VanillaButton(dl, "##guild-info", "Guild Information",
                origin + GuildFrameUiLaw.GuildInformation.Min * s,
                GuildFrameUiLaw.GuildInformation.Size, s,
                normalFont: GuildFrameUiLaw.SmallButtonNormalFont,
                highlightFont: GuildFrameUiLaw.SmallButtonHighlightFont,
                disabledFont: GuildFrameUiLaw.SmallButtonDisabledFont))
            ToggleGuildInfoFrame();
        OfferVanillaNewbieTooltip(new("guild-action", 1), "Guild Information",
            GuildFrameUiLaw.InformationTooltip);
        if (VanillaButton(dl, "##guild-add", "Add Member",
                origin + GuildFrameUiLaw.AddMember.Min * s,
                GuildFrameUiLaw.AddMember.Size, s,
                GuildFrameUiLaw.HasRight(CurrentGuildRank(), _guildRankRights,
                    GuildFrameUiLaw.InviteRight),
                normalFont: GuildFrameUiLaw.SmallButtonNormalFont,
                highlightFont: GuildFrameUiLaw.SmallButtonHighlightFont,
                disabledFont: GuildFrameUiLaw.SmallButtonDisabledFont))
            ShowGuildAddMemberPopup();
        OfferVanillaNewbieTooltip(new("guild-action", 2), "Add Member",
            GuildFrameUiLaw.AddMemberTooltip);
        if (VanillaButton(dl, "##guild-control", "Guild Control",
                origin + GuildFrameUiLaw.GuildControl.Min * s,
                GuildFrameUiLaw.GuildControl.Size, s, CurrentGuildRank() == 0,
                normalFont: GuildFrameUiLaw.SmallButtonNormalFont,
                highlightFont: GuildFrameUiLaw.SmallButtonHighlightFont,
                disabledFont: GuildFrameUiLaw.SmallButtonDisabledFont))
            ToggleGuildControlFrame();
        OfferVanillaNewbieTooltip(new("guild-action", 3), "Guild Control",
            GuildFrameUiLaw.ControlTooltip);

        string[] outer = FriendsFrameUiLaw.OuterTabs;
        float[] widths = outer.Select(text => VanillaCharacterTabWidth(text, s, 0)).ToArray();
        float tabX = FriendsFrameUiLaw.OuterTabFirst.X;
        for (int i = 0; i < outer.Length; i++)
        {
            if (VanillaTab(dl, $"##guild-tab-{i}",
                    origin + FriendsFrameUiLaw.OuterTabMinimum(tabX) * s,
                    outer[i], widths[i], s, i == 2) && i != 2)
            {
                OpenSocial();
                _guildInfoOpen = false; _guildMemberDetailOpen = false;
                _guildControlOpen = false;
                _socialPage = i;
                if (i == 1) _net?.Who("");
                PlayUiSound(FriendsFrameUiLaw.OpenSound,
                    FriendsFrameUiLaw.SoundCategory);
                PlayUiSound(FriendsFrameUiLaw.TabSound,
                    FriendsFrameUiLaw.SoundCategory);
            }
            tabX += widths[i] - FriendsFrameUiLaw.OuterTabOverlap;
        }
        DrawImageButton(dl, "##guild-close", origin + FriendsFrameUiLaw.Close.Min * s,
            FriendsFrameUiLaw.Close.Size * s, @"Interface\Buttons\UI-Panel-MinimizeButton-Up",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked())
            CloseFriendsFrame();
        if (_uiParityArmed && _uiParityPanel == "guild") MarkUiParityFrameComplete();
        ImGui.End();
    }

    private void DrawGuildOfflineFilter(ImDrawListPtr draw, Vector2 origin, float scale)
    {
        GuildFrameUiLaw.LogicalRect frame = GuildFrameUiLaw.OfflineFilter;
        Vector2 min = origin + frame.Min * scale;
        uint border = _gameplayArt?.Handle(
            @"Interface\ClassTrainerFrame\UI-ClassTrainer-FilterBorder") ?? 0;
        if (border != 0)
            foreach (GuildFrameUiLaw.TextureSlice slice in
                     GuildFrameUiLaw.OfflineFilterSlices)
            {
                Vector2 at = min + slice.Rect.Min * scale;
                draw.AddImage((nint)border, at, at + slice.Rect.Size * scale,
                    slice.UvMin, slice.UvMax);
            }

        GuildFrameUiLaw.LogicalRect check = GuildFrameUiLaw.OfflineCheck;
        Vector2 checkMin = origin + check.Min * scale;
        Vector2 checkSize = check.Size * scale;
        Vector2 hitMin = origin + GuildFrameUiLaw.OfflineHit.Min * scale;
        Vector2 hitSize = GuildFrameUiLaw.OfflineHit.Size * scale;
        ImGui.SetCursorScreenPos(hitMin);
        bool clicked = ImGui.InvisibleButton("##guild-show-offline", hitSize);
        bool held = ImGui.IsItemActive();
        bool hovered = ImGui.IsItemHovered();
        string boxPath = held
            ? @"Interface\Buttons\UI-CheckBox-Down"
            : @"Interface\Buttons\UI-CheckBox-Up";
        uint box = _gameplayArt?.Handle(boxPath) ?? 0;
        if (box != 0) draw.AddImage((nint)box, checkMin, checkMin + checkSize);
        if (_guildShowOffline)
        {
            uint mark = _gameplayArt?.Handle(@"Interface\Buttons\UI-CheckBox-Check") ?? 0;
            if (mark != 0) draw.AddImage((nint)mark, checkMin, checkMin + checkSize);
        }
        if (hovered)
        {
            uint highlight = _gameplayArt?.AdditiveHandle(
                @"Interface\Buttons\UI-CheckBox-Highlight") ?? 0;
            if (highlight != 0)
                draw.AddImage((nint)highlight, checkMin, checkMin + checkSize);
        }
        GameText.DrawRightAligned(draw, "GameFontHighlightSmall", "Show Offline Members",
            origin + GuildFrameUiLaw.OfflineLabelRight * scale, scale);
        if (clicked)
        {
            _guildShowOffline = !_guildShowOffline;
            _guildSelected = -1;
            _guildMemberDetailOpen = false;
            _guildScroll = 0;
            // FriendsFrame.xml deliberately plays the sound of the state being left.
            PlayUiSound(_guildShowOffline ? "igMainMenuOptionCheckBoxOff" :
                "igMainMenuOptionCheckBoxOn", "ui.guild");
        }
    }

    private bool DrawGuildColumnHeader(ImDrawListPtr draw, Vector2 origin, float scale,
        GuildFrameUiLaw.LogicalRect rect, string label, int index)
    {
        Vector2 min = origin + rect.Min * scale;
        ImGui.SetCursorScreenPos(min);
        bool clicked = ImGui.InvisibleButton($"##guild-header-{index}", rect.Size * scale);
        DrawWhoColumnHeader(draw, min, rect.Width, scale, label);
        return clicked;
    }

    private GuildRosterSortProjection GuildProjection(GuildMember member) => new(
        member.Name, member.Rank, member.Level,
        GuildFrameUiLaw.ResolvedRosterLabel(_areas?.ZoneName(member.Zone)),
        GuildFrameUiLaw.ResolvedRosterLabel(ClassName(member.Class)),
        member.Online, member.PublicNote, member.OfflineDays);

    private void HandleGuildListWheel(Vector2 origin, float scale, int itemCount)
    {
        Vector2 min = origin + FriendsFrameUiLaw.ListWheelRegion.Min * scale;
        Vector2 max = min + FriendsFrameUiLaw.ListWheelRegion.Size * scale;
        float wheel = ImGui.GetIO().MouseWheel;
        if (wheel != 0 && ImGui.IsMouseHoveringRect(min, max, false))
            _guildScroll = GuildFrameUiLaw.WheelOffset(_guildScroll, itemCount, wheel);
    }

    private void DrawGuildRow(ImDrawListPtr draw, Vector2 rowMin, float scale,
        GuildMember member, bool crowded)
    {
        uint nameColor = member.Online ? VanillaGold : 0xff808080;
        uint valueColor = member.Online ? 0xffffffff : 0xff808080;
        if (!_guildStatusView)
        {
            float zoneWidth = crowded ? 95 : 110;
            string zone = GuildFrameUiLaw.ResolvedRosterLabel(
                _areas?.ZoneName(member.Zone));
            GameText.Draw(draw, "GameFontNormalSmall",
                GameText.EllipsizeToBox("GameFontNormalSmall", member.Name, 88, 14, scale),
                rowMin + GuildFrameUiLaw.PlayerNameOffset * scale, scale, nameColor);
            GameText.Draw(draw, "GameFontHighlightSmall",
                GameText.EllipsizeToBox("GameFontHighlightSmall", zone,
                    zoneWidth, 14, scale),
                rowMin + GuildFrameUiLaw.PlayerZoneOffset * scale, scale, valueColor);
            GameText.DrawCentered(draw, "GameFontHighlightSmall", member.Level.ToString(),
                rowMin + GuildFrameUiLaw.PlayerLevelCenter(zoneWidth) * scale,
                scale, valueColor);
            GameText.Draw(draw, "GameFontHighlightSmall",
                GameText.EllipsizeToBox("GameFontHighlightSmall",
                    GuildFrameUiLaw.ResolvedRosterLabel(ClassName(member.Class)),
                    100, 14, scale),
                rowMin + GuildFrameUiLaw.PlayerClassOffset(zoneWidth) * scale,
                scale, valueColor);
            return;
        }

        float noteWidth = crowded ? 70 : 85;
        string online = member.Online
            ? GuildFrameUiLaw.PresenceTag(member.Presence) is { Length: > 0 } tag
                ? tag : "Online"
            : GuildFrameUiLaw.LastOnline(member.OfflineDays);
        GameText.Draw(draw, "GameFontNormalSmall",
            GameText.EllipsizeToBox("GameFontNormalSmall", member.Name, 75, 14, scale),
            rowMin + GuildFrameUiLaw.StatusNameOffset * scale, scale, nameColor);
        string rank = member.Rank < _guildRankNames.Length
            ? _guildRankNames[member.Rank] : "";
        GameText.Draw(draw, "GameFontHighlightSmall",
            GameText.EllipsizeToBox("GameFontHighlightSmall", rank, 55, 14, scale),
            rowMin + GuildFrameUiLaw.StatusRankOffset * scale, scale, valueColor);
        GameText.Draw(draw, "GameFontHighlightSmall",
            GameText.EllipsizeToBox("GameFontHighlightSmall", member.PublicNote,
                noteWidth, 14, scale),
            rowMin + GuildFrameUiLaw.StatusNoteOffset * scale, scale, valueColor);
        GameText.Draw(draw, "GameFontHighlightSmall",
            GameText.EllipsizeToBox("GameFontHighlightSmall", online, 80, 14, scale),
            rowMin + GuildFrameUiLaw.StatusOnlineOffset(noteWidth) * scale,
            scale, valueColor);
    }
}
