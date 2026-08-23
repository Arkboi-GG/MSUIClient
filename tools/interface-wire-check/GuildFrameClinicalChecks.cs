using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

internal static class GuildFrameClinicalChecks
{
    public static void Run()
    {
        GuildRosterSortProjection[] rows =
        [
            new("Zed", 0, 60, "Ironforge", "Warrior", true, "leader", 0),
            new("Alice", 4, 10, "Elwynn", "Mage", false, "initiate", .5f),
            new("Bob", 2, 30, "Durotar", "Rogue", true, "officer", 0),
        ];
        var sort = new GuildRosterSortLaw();
        Check(sort.Primary == (GuildRosterSortField.Rank, false) &&
              sort.Order(rows, showOffline: true).SequenceEqual(new[] { 1, 2, 0 }),
            "guild default eight-key rank order drift");
        sort.Select(GuildRosterSortField.Name);
        Check(sort.Primary == (GuildRosterSortField.Name, false) &&
              sort.Order(rows, showOffline: true).SequenceEqual(new[] { 1, 2, 0 }),
            "guild first name sort must be ascending");
        sort.Select(GuildRosterSortField.Name);
        Check(sort.Primary == (GuildRosterSortField.Name, true) &&
              sort.Order(rows, showOffline: true).SequenceEqual(new[] { 0, 2, 1 }),
            "guild repeated primary sort must reverse");
        sort.Select(GuildRosterSortField.Level);
        sort.Select(GuildRosterSortField.Name);
        Check(sort.Primary == (GuildRosterSortField.Name, true),
            "guild sort stack forgot a column's remembered direction");

        var offlineFirstByRank = new GuildRosterSortLaw();
        Check(offlineFirstByRank.Order(rows, showOffline: false)
                  .SequenceEqual(new[] { 2, 0, 1 }) &&
              GuildRosterSortLaw.DisplayedCount(rows, showOffline: false) == 2 &&
              GuildRosterSortLaw.DisplayedCount(rows, showOffline: true) == 3,
            "Show Offline must sink rows and expose the online prefix, not delete backing rows");
        Check(GuildFrameUiLaw.PresenceTag(0) == "" &&
              GuildFrameUiLaw.PresenceTag(0x02) == "<AFK>" &&
              GuildFrameUiLaw.PresenceTag(0x04) == "<DND>" &&
              GuildFrameUiLaw.PresenceTag(0x06) == "<DND>",
            "guild DND-before-AFK presence tag drift");

        const uint guildId = 0x1122_3344;
        Check((ushort)Op.CMSG_GUILD_QUERY == 0x0054 &&
              (ushort)Op.SMSG_GUILD_QUERY_RESPONSE == 0x0055,
            "guild query opcode drift");
        byte[] body = WorldSession.BuildGuildQueryBody(guildId);
        Check(body.Length == 4 && BitConverter.ToUInt32(body) == guildId,
            "CMSG_GUILD_QUERY must carry exactly one u32 guild id");
        byte[] invite = WorldSession.BuildCStringBody("Rexxar");
        Check((ushort)Op.CMSG_GUILD_INVITE == 0x0082 && invite.Length == 7 &&
              System.Text.Encoding.UTF8.GetString(invite, 0, 6) == "Rexxar" &&
              invite[^1] == 0 &&
              GuildFrameUiLaw.AddMemberDefinition.WhileDead &&
              GuildFrameUiLaw.AddMemberDefinition.HasEditBox &&
              GuildFrameUiLaw.AddMemberDefinition.HasEditBoxEnter &&
              GuildFrameUiLaw.AddMemberDefinition.HasOnShow &&
              GuildFrameUiLaw.AddMemberDefinition.HasOnHide &&
              GuildFrameUiLaw.AddMemberDefinition.MaxLetters == 12,
            "ADD_GUILDMEMBER popup or guild-invite cstring contract drift");
        Check((ushort)Op.CMSG_GUILD_INFO_TEXT == 0x02FC &&
              WorldSession.BuildCStringBody("We raid.").SequenceEqual(
                  System.Text.Encoding.UTF8.GetBytes("We raid.\0")) &&
              GuildFrameUiLaw.InfoFrame == new GuildFrameUiLaw.LogicalRect(0, 0, 297, 298) &&
              GuildFrameUiLaw.InfoFrameOffset == new System.Numerics.Vector2(349, 65) &&
              GuildFrameUiLaw.InfoTextBackground ==
                  new GuildFrameUiLaw.LogicalRect(11, 32, 276, 230) &&
              GuildFrameUiLaw.InfoEditBox ==
                  new GuildFrameUiLaw.LogicalRect(16, 39, 240, 218) &&
              GuildFrameUiLaw.InfoSaveButton ==
                  new GuildFrameUiLaw.LogicalRect(10, 264, 139, 22) &&
              GuildFrameUiLaw.InfoCancelButton ==
                  new GuildFrameUiLaw.LogicalRect(149, 264, 139, 22) &&
              GuildFrameUiLaw.InfoMaxLetters == 500 &&
              GuildFrameUiLaw.InfoPlaceholder == "Click here to set message" &&
              GuildFrameUiLaw.CanEditInfo(1,
                  new uint[] { 0, GuildFrameUiLaw.ModifyGuildInfoRight }) &&
              !GuildFrameUiLaw.CanEditInfo(0,
                  new uint[] { 0, GuildFrameUiLaw.ModifyGuildInfoRight }),
            "GuildInfoFrame geometry, permission, placeholder, or wire contract drift");
        StaticPopupCoordinatorLaw.WideEditBoxLayout wide =
            StaticPopupCoordinatorLaw.WideEditLayout(12);
        uint[] rankRights =
        [
            GuildFrameUiLaw.RemoveRight | GuildFrameUiLaw.PromoteRight |
                GuildFrameUiLaw.DemoteRight | GuildFrameUiLaw.EditPublicNoteRight |
                GuildFrameUiLaw.ViewOfficerNoteRight | GuildFrameUiLaw.EditOfficerNoteRight,
            0,
            0,
            0,
        ];
        Check(GuildFrameUiLaw.MemberDetailOffset == new System.Numerics.Vector2(351, 28) &&
              GuildFrameUiLaw.MemberDetailHeight(false) == 195 &&
              GuildFrameUiLaw.MemberDetailHeight(true) == 255 &&
              GuildFrameUiLaw.MemberRemoveButton(195) ==
                  new GuildFrameUiLaw.LogicalRect(10, 161, 96, 22) &&
              GuildFrameUiLaw.MemberInviteButton(255) ==
                  new GuildFrameUiLaw.LogicalRect(107, 221, 96, 22) &&
              GuildFrameUiLaw.CanPromote(0, 2, rankRights) &&
              !GuildFrameUiLaw.CanPromote(0, 1, rankRights) &&
              GuildFrameUiLaw.CanDemote(0, 2, 3, rankRights) &&
              !GuildFrameUiLaw.CanDemote(0, 3, 3, rankRights) &&
              GuildFrameUiLaw.CanRemove(0, 1, rankRights) &&
              GuildFrameUiLaw.DisplayNote("", true,
                  GuildFrameUiLaw.PublicNotePlaceholder) ==
                  "Click here to set a Public Note." &&
              wide.Width == 420 && wide.Height == 112 &&
              wide.EditBox == new StaticPopupCoordinatorLaw.Rect(35, 24, 350, 64) &&
              wide.Button1.Y == 75 &&
              GuildFrameUiLaw.NoteMaxLetters == 31,
            "GuildMemberDetail geometry, rights gates, note placeholders, or wide popup drift");
        byte[] notes = WorldSession.BuildTwoCStringBody("Bob", "hi");
        Check((ushort)Op.CMSG_GUILD_REMOVE == 0x008E &&
              (ushort)Op.CMSG_GUILD_SET_PUBLIC_NOTE == 0x0234 &&
              (ushort)Op.CMSG_GUILD_SET_OFFICER_NOTE == 0x0235 &&
              notes.SequenceEqual(System.Text.Encoding.UTF8.GetBytes("Bob\0hi\0")) &&
              GuildFrameUiLaw.RemoveMemberDefinition.WhileDead &&
              GuildFrameUiLaw.RemoveMemberDefinition.HideOnEscape &&
              GuildFrameUiLaw.SetPublicNoteDefinition.MaxLetters == 31 &&
              GuildFrameUiLaw.SetOfficerNoteDefinition.MaxLetters == 31,
            "guild member action opcode/body or StaticPopup definition drift");
        byte[] rankBody = WorldSession.BuildGuildRankBody(3, 0x0001_0040, "Veteran");
        Check((ushort)Op.CMSG_GUILD_RANK == 0x0231 &&
              (ushort)Op.CMSG_GUILD_ADD_RANK == 0x0232 &&
              (ushort)Op.CMSG_GUILD_DEL_RANK == 0x0233 &&
              rankBody.Length == 16 && BitConverter.ToUInt32(rankBody, 0) == 3 &&
              BitConverter.ToUInt32(rankBody, 4) == 0x0001_0040 &&
              System.Text.Encoding.UTF8.GetString(rankBody, 8, 7) == "Veteran" &&
              rankBody[^1] == 0 &&
              GuildFrameUiLaw.ControlFrameOffset ==
                  new System.Numerics.Vector2(349, 65) &&
              GuildFrameUiLaw.ControlFrame ==
                  new GuildFrameUiLaw.LogicalRect(0, 0, 297, 298) &&
              GuildFrameUiLaw.ControlCheckbox(1) ==
                  new GuildFrameUiLaw.LogicalRect(25, 123, 20, 20) &&
              GuildFrameUiLaw.ControlCheckbox(2) ==
                  new GuildFrameUiLaw.LogicalRect(160, 123, 20, 20) &&
              GuildFrameUiLaw.ControlCheckbox(13) ==
                  new GuildFrameUiLaw.LogicalRect(25, 243, 20, 20) &&
              GuildFrameUiLaw.RankRightOrder.SequenceEqual(new uint[]
              {
                  1, 2, 4, 8, 0x80, 0x100, 0x10, 0x20, 0x1000,
                  0x2000, 0x4000, 0x8000, 0x10000,
              }) &&
              GuildFrameUiLaw.ShowRemoveRank(5, 6) &&
              GuildFrameUiLaw.CanRemoveRank(5, 6, 0) &&
              !GuildFrameUiLaw.CanRemoveRank(5, 6, 1) &&
              GuildFrameUiLaw.AddRankDefinition.MaxLetters == 15,
            "GuildControl geometry, rights order, rank bounds, or wire contract drift");

        var promotionBody = new PacketWriter();
        promotionBody.WriteU8(GuildFramePacketLaw.Promotion);
        promotionBody.WriteU8(3);
        promotionBody.WriteCString("Tigole");
        promotionBody.WriteCString("Furor");
        promotionBody.WriteCString("Officer");
        GuildEventWire promotion = GuildFramePacketLaw.ParseEvent(promotionBody.ToArray());
        var presenceBody = new PacketWriter();
        presenceBody.WriteU8(GuildFramePacketLaw.SignedOn);
        presenceBody.WriteU8(1);
        presenceBody.WriteCString("Kaplan");
        presenceBody.WriteU64(9);
        GuildEventWire presence = GuildFramePacketLaw.ParseEvent(presenceBody.ToArray());
        Check(GuildFramePacketLaw.EventLine(promotion, ignored: false) ==
                  "Tigole has promoted Furor to Officer." &&
              GuildFramePacketLaw.EventLine(presence, ignored: false) ==
                  "|Hplayer:Kaplan|h[Kaplan]|h has come online." &&
              GuildFramePacketLaw.EventLine(presence, ignored: true) is null &&
              GuildFramePacketLaw.MakesRosterStale(GuildFramePacketLaw.UpdateRoster) &&
              !GuildFramePacketLaw.MakesRosterStale(GuildFramePacketLaw.SignedOn) &&
              GuildFramePacketLaw.MotdLine("Raid at eight") ==
                  "Guild Message of the Day: Raid at eight",
            "guild event decoder, system lines, ignore gate, or stale-roster edge drift");

        var commandBody = new PacketWriter();
        commandBody.WriteU32(1);
        commandBody.WriteCString("Thrall");
        commandBody.WriteU32(0);
        GuildCommandResultWire command =
            GuildFramePacketLaw.ParseCommandResult(commandBody.ToArray());
        Check(GuildFramePacketLaw.CommandLine(command) ==
                  "You have invited Thrall to join your guild." &&
              GuildFramePacketLaw.CommandLine(new(3, "", 8)) ==
                  "You must promote a new Guild Master using /gleader before leaving the guild." &&
              GuildFramePacketLaw.CommandLine(new(1, "", 8)) ==
                  "You don't have permission to do that." &&
              GuildFramePacketLaw.CommandMakesRosterStale(new(0x13, "", 0)) &&
              !GuildFramePacketLaw.CommandMakesRosterStale(command),
            "guild command-result line or stale-roster result edge drift");

        var inviteBody = new PacketWriter();
        inviteBody.WriteCString("Tigole");
        inviteBody.WriteCString("Legacy of Steel");
        GuildInviteWire guildInvite = GuildFramePacketLaw.ParseInvite(inviteBody.ToArray());
        string inviteToken = GuildFrameUiLaw.InvitePopupToken(
            guildInvite.Inviter, guildInvite.Guild);
        (string decodedInviter, string decodedGuild) =
            GuildFrameUiLaw.InvitePopupData(inviteToken);
        StaticPopupCoordinatorLaw.Plan guildInviteShow = StaticPopupCoordinatorLaw.Show(
            StaticPopupCoordinatorLaw.Slots.Empty, GuildFrameUiLaw.InviteDefinition,
            playerDeadOrGhost: true, dataToken: inviteToken);
        StaticPopupCoordinatorLaw.Plan guildInviteTimeout = StaticPopupCoordinatorLaw.Advance(
            guildInviteShow.Slots, guildInviteShow.Slot!.Value, 60);
        Check((ushort)Op.SMSG_GUILD_INVITE == 0x0083 &&
              (ushort)Op.CMSG_GUILD_ACCEPT == 0x0084 &&
              (ushort)Op.CMSG_GUILD_DECLINE == 0x0085 &&
              (ushort)Op.SMSG_GUILD_DECLINE == 0x0086 &&
              (ushort)Op.CMSG_GUILD_INFO == 0x0087 &&
              (ushort)Op.SMSG_GUILD_INFO == 0x0088 &&
              decodedInviter == "Tigole" && decodedGuild == "Legacy of Steel" &&
              GuildFrameUiLaw.InvitePopupText(decodedInviter, decodedGuild) ==
                  "Tigole invites you to join Legacy of Steel." &&
              GuildFramePacketLaw.InviteLine(guildInvite) ==
                  "Tigole invites you join Legacy of Steel." &&
              GuildFrameUiLaw.InviteDefinition.WhileDead &&
              GuildFrameUiLaw.InviteDefinition.HideOnEscape &&
              GuildFrameUiLaw.InviteDefinition.TimeoutSeconds == 60 &&
              guildInviteShow.Outcome == StaticPopupCoordinatorLaw.Outcome.Shown &&
              guildInviteTimeout.Outcome == StaticPopupCoordinatorLaw.Outcome.TimedOut &&
              guildInviteTimeout.Effects.Count(effect =>
                  effect.Kind == StaticPopupCoordinatorLaw.EffectKind.CancelTimeout) == 1,
            "GUILD_INVITE packet, shared popup geometry/lifecycle, or wire opcodes drift");

        var infoBody = new PacketWriter();
        infoBody.WriteCString("Legacy of Steel");
        infoBody.WriteU32(14);
        infoBody.WriteU32(3);
        infoBody.WriteU32(2005);
        infoBody.WriteU32(42);
        infoBody.WriteU32(30);
        GuildInfoWire guildCounts = GuildFramePacketLaw.ParseInfo(infoBody.ToArray());
        Check(GuildFramePacketLaw.InfoLines(guildCounts).SequenceEqual(new[]
              {
                  "Guild: Legacy of Steel",
                  "Guild created 3-14-2005, 42 players, 30 accounts",
              }) &&
              GuildFramePacketLaw.DeclineLine("Kaplan") ==
                  "Kaplan declines your guild invitation.",
            "guild decline or /ginfo line composition drift");

        string root = ClientConfig.FindRepoRoot();
        string guild = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Guild.cs"));
        string net = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        string modal = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.GuildPopups.cs"));
        string info = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.GuildInfo.cs"));
        string member = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.GuildMemberDetail.cs"));
        string control = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.GuildControl.cs"));
        string chat = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Chat.cs"));
        Check(guild.Contains("_guildSort.Order(projections, _guildShowOffline)",
                  StringComparison.Ordinal) &&
              guild.Contains("GuildRosterSortLaw.DisplayedCount", StringComparison.Ordinal) &&
              guild.Contains("_guildSort.Select(headerFields[i])", StringComparison.Ordinal) &&
              guild.Contains("DrawGuildColumnHeader", StringComparison.Ordinal) &&
              guild.Contains("GuildFrameUiLaw.PresenceTag(member.Presence)",
                  StringComparison.Ordinal) &&
              guild.Contains("_guildRankNames[member.Rank]", StringComparison.Ordinal) &&
              guild.Contains("GameText.DrawCentered(dl, \"GameFontNormal\", _guildName",
                  StringComparison.Ordinal) &&
              guild.Contains("RequestOwnGuildIdentity()", StringComparison.Ordinal) &&
              guild.Contains("ShowGuildAddMemberPopup();", StringComparison.Ordinal) &&
              net.Contains("case Op.SMSG_GUILD_QUERY_RESPONSE:", StringComparison.Ordinal) &&
              net.Contains("ApplyGuildQueryResponse(body);", StringComparison.Ordinal) &&
              net.Contains("case Op.SMSG_GUILD_INVITE:", StringComparison.Ordinal) &&
              net.Contains("case Op.SMSG_GUILD_DECLINE:", StringComparison.Ordinal) &&
              net.Contains("case Op.SMSG_GUILD_INFO:", StringComparison.Ordinal),
            "guild header sorting, rank identity, title, presence, or dispatch integration drift");
        Check(modal.Contains("StaticPopupCoordinatorLaw.NarrowEditLayout", StringComparison.Ordinal) &&
              modal.Contains("StaticPopupOrigin(visible.Slot", StringComparison.Ordinal) &&
              modal.Contains("_net?.GuildInvite(GuildAddMemberInput())",
                  StringComparison.Ordinal) &&
              modal.Contains("GuildFrameUiLaw.AddMemberMaxLetters + 1",
                  StringComparison.Ordinal),
            "ADD_GUILDMEMBER regressed from shared rule-owned StaticPopup edit geometry");
        Check(guild.Contains("ToggleGuildInfoFrame();", StringComparison.Ordinal) &&
              info.Contains("GuildFrameUiLaw.InfoFrameOrigin", StringComparison.Ordinal) &&
              info.Contains("GuildFrameUiLaw.InfoEditBox", StringComparison.Ordinal) &&
              info.Contains("_skin.DrawBackdrop(draw, origin, origin + size, WowSkin.Dialog)",
                  StringComparison.Ordinal) &&
              info.Contains("_net?.GuildInfoText(text);", StringComparison.Ordinal) &&
              info.Contains("RequestGuildRoster();", StringComparison.Ordinal),
            "GuildInfoFrame regressed from rule-owned satellite geometry or save flow");
        Check(guild.Contains("_guildMemberDetailOpen && _guildSelected == originalIndex",
                  StringComparison.Ordinal) &&
              member.Contains("GuildFrameUiLaw.MemberDetailOrigin", StringComparison.Ordinal) &&
              member.Contains("GuildFrameUiLaw.MemberDetailHeight", StringComparison.Ordinal) &&
              member.Contains("ShowGuildMemberPopup(GuildFrameUiLaw.RemoveMemberDefinition)",
                  StringComparison.Ordinal) &&
              member.Contains("_net?.GroupInvite(member.Name);", StringComparison.Ordinal) &&
              modal.Contains("StaticPopupCoordinatorLaw.WideEditLayout", StringComparison.Ordinal) &&
              modal.Contains("_net?.GuildSetPublicNote", StringComparison.Ordinal) &&
              modal.Contains("_net?.GuildSetOfficerNote", StringComparison.Ordinal) &&
              modal.Contains("_net?.GuildRemove", StringComparison.Ordinal),
            "GuildMemberDetail or its rule-owned shared-popup action surfaces regressed");
        Check(guild.Contains("ToggleGuildControlFrame();", StringComparison.Ordinal) &&
              control.Contains("GuildFrameUiLaw.ControlFrameOrigin", StringComparison.Ordinal) &&
              control.Contains("DrawGuildControlShell", StringComparison.Ordinal) &&
              control.Contains("GuildFrameUiLaw.ControlCheckbox", StringComparison.Ordinal) &&
              control.Contains("_net?.GuildRank", StringComparison.Ordinal) &&
              control.Contains("_net?.GuildDeleteRank", StringComparison.Ordinal) &&
              control.Contains("ShowGuildAddRankPopup();", StringComparison.Ordinal) &&
              modal.Contains("GuildFrameUiLaw.AddRankPopupType", StringComparison.Ordinal) &&
              modal.Contains("_net?.GuildAddRank", StringComparison.Ordinal),
            "GuildControl regressed from rule-owned geometry or exact rank wire flows");
        Check(guild.Contains("GuildFramePacketLaw.ParseEvent(body)", StringComparison.Ordinal) &&
              guild.Contains("GuildFramePacketLaw.EventLine(notice, ignored)",
                  StringComparison.Ordinal) &&
              guild.Contains("_ignored.Contains(guid)", StringComparison.Ordinal) &&
              guild.Contains("GuildFramePacketLaw.MakesRosterStale", StringComparison.Ordinal) &&
              guild.Contains("ShowGuildInvitePopup(invite);", StringComparison.Ordinal) &&
              modal.Contains("GuildFrameUiLaw.InvitePopupHeight", StringComparison.Ordinal) &&
              modal.Contains("StaticPopupOrigin(visible.Slot", StringComparison.Ordinal) &&
              modal.Contains("_net?.GuildAccept();", StringComparison.Ordinal) &&
              modal.Contains("_net?.GuildDecline();", StringComparison.Ordinal) &&
              chat.Contains("case \"/ginfo\":", StringComparison.Ordinal) &&
              chat.Contains("_net?.GuildInfo();", StringComparison.Ordinal),
            "guild event family or rule-owned GUILD_INVITE popup integration drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
