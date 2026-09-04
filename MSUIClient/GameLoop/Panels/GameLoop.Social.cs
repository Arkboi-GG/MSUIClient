using System.Numerics;
using System.Text;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private readonly List<SocialPackets.FriendEntry> _friends = [];
    private readonly List<ulong> _ignored = [];
    private readonly List<SocialPackets.FriendStatus> _pendingFriendStatusLines = [];
    private readonly List<FriendsFrameUiLaw.WhoEntry> _who = [];
    // Twelve visible letters plus the terminating zero: the frozen ADD_FRIEND/ADD_IGNORE cap.
    private readonly byte[] _friendNameInput = new byte[FriendsFrameUiLaw.NameMaxLetters + 1];
    private readonly byte[] _whoInput = new byte[64];
    private bool _socialOpen;
    private int _friendSelected;
    private int _ignoreSelected;
    private int _whoSelected = -1;
    private int _socialPage;
    private int _friendScroll;
    private int _ignoreScroll;
    private int _whoScroll;
    private FriendsWhoVariable _whoVariable = FriendsFrameUiLaw.DefaultWhoVariable;
    private bool _whoVariableMenuOpen;
    private bool _showIgnore;
    private uint _whoTotal;
    private bool _socialPopupFocusRequested;
    private bool _socialPopupEditFocused;
    private readonly bool[] _socialTabBindingWasDown = new bool[3];

    private void OpenSocial()
    {
        bool wasClosed = !_socialOpen && !_guildOpen;
        _socialOpen = true;
        _guildOpen = false;
        _socialPage = 0; _showIgnore = false;   // always reopen on the Friends tab, never a stuck page
        _net?.FriendList();
        if (wasClosed)
            PlayUiSound(FriendsFrameUiLaw.OpenSound, FriendsFrameUiLaw.SoundCategory);
    }

    private bool CloseFriendsFrame()
    {
        if (!_socialOpen && !_guildOpen) return false;
        _socialOpen = false;
        _guildOpen = false;
        _guildInfoOpen = false;
        _guildMemberDetailOpen = false;
        _guildControlOpen = false;
        _guildControlDropDownOpen = false;
        _whoVariableMenuOpen = false;
        PlayUiSound(FriendsFrameUiLaw.CloseSound, FriendsFrameUiLaw.SoundCategory);
        return true;
    }

    private void UpdateSocialTabBindings(bool typing)
    {
        GameBinding[] bindings =
        [
            GameBinding.OpenSocialFriends,
            GameBinding.OpenSocialWho,
            GameBinding.OpenSocialGuild,
        ];
        for (int index = 0; index < bindings.Length; index++)
        {
            bool down = BindingDown(bindings[index]);
            if (down && !_socialTabBindingWasDown[index] && !typing &&
                _net is { IsInWorld: true })
                ToggleSocialTab(index);
            _socialTabBindingWasDown[index] = down;
        }
    }

    private void ToggleSocialTab(int index)
    {
        if (index == 2)
        {
            if (CurrentGuildId() == 0) return;
            if (_guildOpen)
            {
                CloseFriendsFrame();
                return;
            }
            _socialOpen = false;
            _socialPage = 0;
            _guildOpen = true;
            RequestGuildRoster();
            PlayUiSound(FriendsFrameUiLaw.OpenSound, FriendsFrameUiLaw.SoundCategory);
            return;
        }

        if (_socialOpen && _socialPage == index)
        {
            CloseFriendsFrame();
            return;
        }
        _guildOpen = false;
        _guildInfoOpen = false;
        _guildMemberDetailOpen = false;
        _socialOpen = true;
        _socialPage = index;
        _showIgnore = false;
        if (index == 1)
        {
            _whoSelected = -1;
            _whoVariable = FriendsFrameUiLaw.DefaultWhoVariable;
        }
        _whoVariableMenuOpen = false;
        if (index == 0) _net?.FriendList();
        else SendWhoFilter(ReadBuffer(_whoInput));
        PlayUiSound(FriendsFrameUiLaw.OpenSound, FriendsFrameUiLaw.SoundCategory);
    }

    private void ApplyFriendList(byte[] body)
    {
        ulong selectedGuid = SelectedFriendGuid();
        var r = new PacketReader(body); byte count = r.ReadU8();
        _friends.Clear();
        for (int i = 0; i < count; i++)
        {
            ulong guid = r.ReadU64(); byte status = r.ReadU8();
            uint area = 0, level = 0, cls = 0;
            if (status != 0) { area = r.ReadU32(); level = r.ReadU32(); cls = r.ReadU32(); }
            _friends.Add(new(guid, status, area, level, cls));
            if (!_playerNames.ContainsKey(guid)) _net?.NameQuery(guid);
        }
        ReorderFriendContacts(selectedGuid);
        _friendScroll = FriendsFrameUiLaw.ClampOffset(_friendScroll, _friends.Count,
            FriendsFrameUiLaw.FriendsVisibleRows);
    }

    private void ApplyFriendStatus(byte[] body)
    {
        ulong selectedFriend = SelectedFriendGuid();
        ulong selectedIgnore = SelectedIgnoreGuid();
        SocialPackets.FriendStatus update = SocialPackets.ParseFriendStatus(body);
        SocialPackets.ApplyStatus(_friends, _ignored, update);
        ReorderFriendContacts(selectedFriend);
        ReorderIgnoreContacts(selectedIgnore);
        _friendScroll = FriendsFrameUiLaw.ClampOffset(_friendScroll, _friends.Count,
            FriendsFrameUiLaw.FriendsVisibleRows);
        _ignoreScroll = FriendsFrameUiLaw.ClampOffset(_ignoreScroll, _ignored.Count,
            FriendsFrameUiLaw.IgnoreVisibleRows);

        string? template = FriendStatusUiLaw.Template(update.Result);
        if (template is null) return;
        if (!FriendStatusUiLaw.NeedsName(template))
        {
            AddChatMessage(template);
            return;
        }
        if (update.Guid != 0 &&
            _playerNames.TryGetValue(update.Guid, out string? name) && name.Length > 0)
        {
            AddChatMessage(FriendStatusUiLaw.Compose(template, name));
            return;
        }
        _pendingFriendStatusLines.Add(update);
        if (update.Guid != 0 && _queriedPlayerNames.Add(update.Guid))
            _net?.NameQuery(update.Guid);
    }

    private void FlushPendingFriendStatus(ulong guid)
    {
        if (!_playerNames.TryGetValue(guid, out string? name) || name.Length == 0)
        {
            _pendingFriendStatusLines.RemoveAll(update => update.Guid == guid);
            return;
        }
        for (int i = _pendingFriendStatusLines.Count - 1; i >= 0; i--)
        {
            SocialPackets.FriendStatus update = _pendingFriendStatusLines[i];
            if (update.Guid != guid) continue;
            _pendingFriendStatusLines.RemoveAt(i);
            string? template = FriendStatusUiLaw.Template(update.Result);
            if (template is not null)
                AddChatMessage(FriendStatusUiLaw.Compose(template, name));
        }
    }

    private void ApplyIgnoreList(byte[] body)
    {
        ulong selectedGuid = SelectedIgnoreGuid();
        var r = new PacketReader(body); int count = r.ReadU8();
        _ignored.Clear();
        for (int i = 0; i < count; i++)
        {
            ulong guid = r.ReadU64(); _ignored.Add(guid);
            if (!_playerNames.ContainsKey(guid)) _net?.NameQuery(guid);
        }
        ReorderIgnoreContacts(selectedGuid);
        _ignoreScroll = 0;
    }

    private ulong SelectedFriendGuid() => _friends.Count == 0 ? 0 :
        _friends[Math.Clamp(_friendSelected, 0, _friends.Count - 1)].Guid;

    private ulong SelectedIgnoreGuid() => _ignored.Count == 0 ? 0 :
        _ignored[Math.Clamp(_ignoreSelected, 0, _ignored.Count - 1)];

    private void ReorderFriendContacts(ulong selectedGuid)
    {
        IReadOnlyList<ulong> order = FriendsFrameUiLaw.ContactOrder(
            _friends.Select(row => row.Guid), guid => _playerNames.GetValueOrDefault(guid));
        Dictionary<ulong, SocialPackets.FriendEntry> byGuid =
            _friends.ToDictionary(row => row.Guid);
        _friends.Clear();
        foreach (ulong guid in order) _friends.Add(byGuid[guid]);
        _friendSelected = FriendsFrameUiLaw.SelectionForGuid(selectedGuid, order);
    }

    private void ReorderIgnoreContacts(ulong selectedGuid)
    {
        IReadOnlyList<ulong> order = FriendsFrameUiLaw.ContactOrder(
            _ignored, guid => _playerNames.GetValueOrDefault(guid));
        _ignored.Clear();
        _ignored.AddRange(order);
        _ignoreSelected = FriendsFrameUiLaw.SelectionForGuid(selectedGuid, order);
    }

    private void ReorderSocialContactsAfterNameResolution()
    {
        ulong selectedFriend = SelectedFriendGuid();
        ulong selectedIgnore = SelectedIgnoreGuid();
        ReorderFriendContacts(selectedFriend);
        ReorderIgnoreContacts(selectedIgnore);
    }

    private void ApplyWhoList(byte[] body)
    {
        var r = new PacketReader(body); uint listed = r.ReadU32(); _whoTotal = r.ReadU32();
        if (listed > 50) throw new InvalidDataException($"SMSG_WHO count {listed}");
        _who.Clear();
        for (uint i = 0; i < listed; i++)
            _who.Add(new(r.ReadCString(), r.ReadCString(), r.ReadU32(), r.ReadU32(),
                r.ReadU32(), r.ReadU32()));
        _whoSelected = -1;
        _whoScroll = 0;
    }

    private string FriendInput()
    {
        int end = Array.IndexOf(_friendNameInput, (byte)0);
        return Encoding.UTF8.GetString(_friendNameInput, 0, end < 0 ? _friendNameInput.Length : end).Trim();
    }

    private void DrawSocialFrame()
    {
        if (!_socialOpen || _gameplayArt is null) return;
        if (!BeginVanillaWindow("##social", UiPanelFrameLogicalOrigin(UiPanelOwnershipRegistry[14]),
                FriendsFrameUiLaw.FrameSize(1f),
                out ImDrawListPtr dl, out Vector2 origin, out float s)) { ImGui.End(); return; }
        // FriendsFrame_Update swaps the complete page shell. Friends and Ignore share the
        // paperdoll top but have different bottoms; Who uses the trainer top and its own bottom.
        // The matrix belongs to FriendsFrameUiLaw so no functional subframe can retain the
        // wrong list recesses just because its controls happen to fit.
        FriendsFrameUiLaw.ShellArt shell = FriendsFrameUiLaw.ShellFor(
            _socialPage, _showIgnore);
        DrawFourPieceShell(dl, origin, s,
            shell.TopLeft, shell.TopRight, shell.BottomLeft, shell.BottomRight);
        DrawArt(dl, @"Interface\FriendsFrame\FriendsFrameScrollIcon",
            origin + FriendsFrameUiLaw.ScrollIcon.Min * s,
            FriendsFrameUiLaw.ScrollIcon.Size, s);
        // FriendsFrameTitleText: GameFontNormal (~12) at TOP centre; text switches to
        // IGNORE_LIST on the ignore subtab (FriendsFrame.xml:586-593, .lua:97).
        string title = _socialPage switch { 1 => "Who List", 2 => "Guild",
            _ => _showIgnore ? "Ignore List" : "Friends List" };
        GameText.DrawCentered(dl, "GameFontNormal", title,
            origin + FriendsFrameUiLaw.TitleCenter * s, s);

        // Guild (page 2) is a separate frame reached via the tab click below; it never renders
        // here, so the dispatch must not close the window from the render path (doing so every
        // frame made the window un-reopenable once the page got stuck on Guild).
        if (_socialPage == 1) DrawWhoPage(dl, origin, s);
        else DrawFriendsOrIgnore(dl, origin, s);

        string[] outerTabs = FriendsFrameUiLaw.OuterTabs;
        float[] tabW = outerTabs.Select(text => VanillaCharacterTabWidth(text, s, 0)).ToArray();
        float tabX = FriendsFrameUiLaw.OuterTabFirst.X;
        for (int i = 0; i < outerTabs.Length; i++)
        {
            if (VanillaTab(dl, $"##social-tab-{i}",
                    origin + FriendsFrameUiLaw.OuterTabMinimum(tabX) * s,
                    outerTabs[i], tabW[i], s, _socialPage == i))
            {
                if (i == 1)
                {
                    _whoSelected = -1;
                    _whoVariable = FriendsFrameUiLaw.DefaultWhoVariable;
                }
                _whoVariableMenuOpen = false;
                PlayUiSound(FriendsFrameUiLaw.OpenSound, FriendsFrameUiLaw.SoundCategory);
                PlayUiSound(FriendsFrameUiLaw.TabSound, FriendsFrameUiLaw.SoundCategory);
                if (i == 2)
                {
                    // Guild opens its own frame; keep the social page on Friends so reopening
                    // the social window later never lands on the self-closing Guild page.
                    _socialOpen = false; _socialPage = 0; _guildOpen = true;
                    RequestGuildRoster();
                }
                else
                {
                    _socialPage = i;
                    if (i == 0) _net?.FriendList();
                    if (i == 1) SendWhoFilter(ReadBuffer(_whoInput));
                }
            }
            tabX += tabW[i] - FriendsFrameUiLaw.OuterTabOverlap;
        }
        DrawImageButton(dl, "##social-close",
            origin + FriendsFrameUiLaw.Close.Min * s, FriendsFrameUiLaw.Close.Size * s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up", @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked()) CloseFriendsFrame();
        ImGui.End();
    }

    private void DrawFriendsOrIgnore(ImDrawListPtr dl, Vector2 origin, float s)
    {
        float friendsWidth=VanillaInsetTabWidth("Friends",s),ignoreWidth=VanillaInsetTabWidth("Ignore",s);
        if (VanillaInsetTab(dl, "##social-friends-list",
                origin + FriendsFrameUiLaw.InsetTabFirst * s,
                "Friends", friendsWidth, s, !_showIgnore))
        {
            _showIgnore = false;
            PlayUiSound(FriendsFrameUiLaw.TabSound, FriendsFrameUiLaw.SoundCategory);
        }
        if (VanillaInsetTab(dl, "##social-ignore-list",
                origin + FriendsFrameUiLaw.InsetTabMinimum(
                    FriendsFrameUiLaw.InsetTabFirst.X + friendsWidth) * s,
                "Ignore", ignoreWidth, s, _showIgnore))
        {
            _showIgnore = true;
            PlayUiSound(FriendsFrameUiLaw.TabSound, FriendsFrameUiLaw.SoundCategory);
        }
        if (!_showIgnore)
        {
            _friendScroll = FriendsFrameUiLaw.ClampOffset(_friendScroll, _friends.Count,
                FriendsFrameUiLaw.FriendsVisibleRows);
            HandleSocialListWheel(origin, s,
                ref _friendScroll, _friends.Count, FriendsFrameUiLaw.FriendsVisibleRows);
            int count = Math.Min(FriendsFrameUiLaw.FriendsVisibleRows,
                _friends.Count - _friendScroll);
            for (int i = 0; i < count; i++)
            {
                int index = _friendScroll + i;
                SocialPackets.FriendEntry row = _friends[index];
                string name = _playerNames.GetValueOrDefault(row.Guid,
                    FriendsFrameUiLaw.UnknownName);
                bool online = row.Status != 0;
                string zone = online
                    ? FriendsFrameUiLaw.ResolvedDisplayLabel(_areas?.ZoneName(row.Area))
                    : "";
                FriendsFrameUiLaw.LogicalRect rowRect = FriendsFrameUiLaw.Row(
                    FriendsFrameUiLaw.FriendsRows, FriendsFrameUiLaw.FriendRowStep, i, 31);
                Vector2 rowMin = origin + rowRect.Min * s;
                bool rowClicked = VanillaListRow(dl, $"##friend-{row.Guid}", rowMin,
                    rowRect.Size, s, "", _friendSelected == index,
                    highlightPath: FriendsFrameUiLaw.RowHighlightPath,
                    additiveHighlight: true);
                bool rowRightClicked = ImGui.IsItemClicked(ImGuiMouseButton.Right);
                if (rowClicked) _friendSelected = index;
                // FRIENDS_LIST_TEMPLATE: gold name, then an inline-white "- zone", then the
                // inherited-gold AFK/DND tag. Offline is one gray "name - Offline" line and
                // UNKNOWN below; putting "Offline" on line two is visibly a different row.
                Vector2 namePos = rowMin + FriendsFrameUiLaw.FriendNameOffset * s;
                if (online)
                {
                    GameText.Draw(dl, "GameFontNormal", name, namePos, s);
                    float nameWidth = GameText.MeasureWidth("GameFontNormal", name, s);
                    string location = $" - {zone}";
                    GameText.Draw(dl, "GameFontNormal", location,
                        namePos + FriendsFrameUiLaw.InlineOffset(nameWidth), s, 0xffffffff);
                    string status = FriendsFrameUiLaw.StatusTag(row.Status);
                    if (status.Length > 0)
                    {
                        float locationWidth = GameText.MeasureWidth(
                            "GameFontNormal", location, s);
                        GameText.Draw(dl, "GameFontNormal", " " + status,
                            namePos + FriendsFrameUiLaw.InlineOffset(
                                nameWidth + locationWidth), s);
                    }
                }
                else
                {
                    GameText.Draw(dl, "GameFontNormal",
                        FriendsFrameUiLaw.OfflineNameLine(name), namePos, s, 0xff999999);
                }
                bool nameKnown = name != FriendsFrameUiLaw.UnknownName;
                if (FriendsFrameUiLaw.CanOpenFriendMenu(online, nameKnown) && rowRightClicked)
                    OpenFriendPopup(name, ImGui.GetIO().MousePos);
                if (rowClicked || rowRightClicked)
                    PlayUiSound(FriendsFrameUiLaw.RowSound, FriendsFrameUiLaw.SoundCategory);
                string infoLine = FriendsFrameUiLaw.FriendInfoLine(online, row.Level,
                    FriendsFrameUiLaw.ResolvedDisplayLabel(ClassName((byte)row.Class)));
                GameText.Draw(dl, "GameFontHighlightSmall", infoLine,
                    rowMin + FriendsFrameUiLaw.FriendInfoOffset * s, s);
            }
            DrawSocialFauxScrollBar(dl, "##friends-scroll", origin, s,
                FriendsFrameUiLaw.FriendsScrollFrame, _friendScroll,
                FriendsFrameUiLaw.MaximumOffset(_friends.Count,
                    FriendsFrameUiLaw.FriendsVisibleRows),
                value => _friendScroll = value);
        }
        else
        {
            _ignoreScroll = FriendsFrameUiLaw.ClampOffset(_ignoreScroll, _ignored.Count,
                FriendsFrameUiLaw.IgnoreVisibleRows);
            HandleSocialListWheel(origin, s,
                ref _ignoreScroll, _ignored.Count, FriendsFrameUiLaw.IgnoreVisibleRows);
            int count = Math.Min(FriendsFrameUiLaw.IgnoreVisibleRows,
                _ignored.Count - _ignoreScroll);
            for (int i = 0; i < count; i++)
            {
                int index = _ignoreScroll + i;
                ulong guid = _ignored[index];
                string name = FriendsFrameUiLaw.IgnoreNameLine(
                    _playerNames.GetValueOrDefault(guid));
                FriendsFrameUiLaw.LogicalRect rowRect = FriendsFrameUiLaw.Row(
                    FriendsFrameUiLaw.IgnoreRows, FriendsFrameUiLaw.IgnoreRowStep, i, 16);
                // FriendsFrameIgnoreButtonTemplate name inherits GameFontNormal (gold), not white.
                Vector2 rowMin = origin + rowRect.Min * s;
                if (VanillaListRow(dl, $"##ignore-{guid}", rowMin,
                        rowRect.Size, s, "", _ignoreSelected == index,
                        highlightPath: FriendsFrameUiLaw.RowHighlightPath,
                        additiveHighlight: true))
                {
                    _ignoreSelected = index;
                    PlayUiSound(FriendsFrameUiLaw.RowSound,
                        FriendsFrameUiLaw.SoundCategory);
                }
                GameText.Draw(dl, "GameFontNormal", name,
                    rowMin + FriendsFrameUiLaw.IgnoreNameOffset * s, s);
            }
            DrawSocialFauxScrollBar(dl, "##ignore-scroll", origin, s,
                FriendsFrameUiLaw.IgnoreScrollFrame, _ignoreScroll,
                FriendsFrameUiLaw.MaximumOffset(_ignored.Count,
                    FriendsFrameUiLaw.IgnoreVisibleRows),
                value => _ignoreScroll = value);
        }
        // FriendsListFrame has NO inline name box (FriendsFrame.xml) — Add Friend opens the
        // ADD_FRIEND popup. Buttons are a 2x2 grid: Add/Send top (y384), Remove/Invite bottom (y410).
        if (!_showIgnore)
        {
            bool haveSel = _friends.Count > 0;
            SocialPackets.FriendEntry? selectedFriend = haveSel
                ? _friends[Math.Clamp(_friendSelected, 0, _friends.Count - 1)] : null;
            bool nameKnown = selectedFriend is { } selected &&
                _playerNames.TryGetValue(selected.Guid, out string? selectedName) &&
                selectedName.Length > 0;
            bool selectedOnline = FriendsFrameUiLaw.CanContact(haveSel,
                selectedFriend is { Status: not 0 }, nameKnown);
            if (VanillaButton(dl, "##social-add", "Add Friend",
                    origin + FriendsFrameUiLaw.AddFriend.Min * s,
                    FriendsFrameUiLaw.AddFriend.Size, s))
                ShowSocialNamePopup(ignore: false);
            OfferVanillaNewbieTooltip(new("social-action", 1), "Add Friend",
                FriendsFrameUiLaw.AddFriendTooltip);
            if (VanillaButton(dl, "##social-send", "Send Message",
                    origin + FriendsFrameUiLaw.SendMessage.Min * s,
                    FriendsFrameUiLaw.SendMessage.Size, s, selectedOnline))
            {
                SocialPackets.FriendEntry sel = _friends[Math.Clamp(_friendSelected, 0, _friends.Count - 1)];
                OpenChatEditWith($"/w {_playerNames.GetValueOrDefault(sel.Guid, "")} ");
            }
            OfferVanillaNewbieTooltip(new("social-action", 2), "Send Message",
                FriendsFrameUiLaw.SendMessageTooltip);
            if (VanillaButton(dl, "##social-remove", "Remove Friend",
                    origin + FriendsFrameUiLaw.RemoveFriend.Min * s,
                    FriendsFrameUiLaw.RemoveFriend.Size, s,
                    FriendsFrameUiLaw.CanRemove(haveSel)))
                _net?.DeleteFriend(_friends[Math.Clamp(_friendSelected, 0, _friends.Count - 1)].Guid);
            OfferVanillaNewbieTooltip(new("social-action", 3), "Remove Friend",
                FriendsFrameUiLaw.RemoveFriendTooltip);
            if (VanillaButton(dl, "##social-invite", "Group Invite",
                    origin + FriendsFrameUiLaw.GroupInvite.Min * s,
                    FriendsFrameUiLaw.GroupInvite.Size, s, selectedOnline))
            {
                SocialPackets.FriendEntry sel = _friends[Math.Clamp(_friendSelected, 0, _friends.Count - 1)];
                if (!RefuseTacticalFreezeLiveCommand("inviting a party member") &&
                    !RefuseTacticalFrozenActor(sel.Guid, "invite them to a party"))
                    _net?.GroupInvite(_playerNames.GetValueOrDefault(sel.Guid, ""));
            }
            OfferVanillaNewbieTooltip(new("social-action", 4), "Group Invite",
                FriendsFrameUiLaw.GroupInviteTooltip);
        }
        else
        {
            if (VanillaButton(dl, "##social-ignore-add", "Ignore Player",
                    origin + FriendsFrameUiLaw.IgnorePlayer.Min * s,
                    FriendsFrameUiLaw.IgnorePlayer.Size, s))
                ShowSocialNamePopup(ignore: true);
            if (VanillaButton(dl, "##social-ignore-remove", "Remove Player",
                    origin + FriendsFrameUiLaw.StopIgnore.Min * s,
                    FriendsFrameUiLaw.StopIgnore.Size, s, _ignored.Count > 0))
                _net?.DeleteIgnore(_ignored[Math.Clamp(_ignoreSelected, 0, _ignored.Count - 1)]);
        }
    }

    private void ShowSocialNamePopup(bool ignore)
    {
        bool playerDeadOrGhost = _net is not null &&
            _entities.TryGet(_net.PlayerGuid, out WorldEntity player) && player.IsDead;
        ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Show(
            _staticPopupSlots,
            ignore ? FriendsFrameUiLaw.AddIgnorePopupDefinition
                : FriendsFrameUiLaw.AddFriendPopupDefinition,
            playerDeadOrGhost));
    }

    private void SubmitSocialNamePopup(string type)
    {
        string input = FriendInput();
        if (type == FriendsFrameUiLaw.AddIgnorePopupType) _net?.AddIgnore(input);
        else _net?.AddFriend(input);
    }

    // Shared StaticPopup's narrow edit-box branch. ImGui owns only the invisible input/hit shell;
    // every modal and child rectangle below is resolved by StaticPopupCoordinatorLaw.
    private void DrawSocialNamePopup()
    {
        (int Slot, StaticPopupCoordinatorLaw.Instance Instance)? popup =
            FriendsFrameUiLaw.NamePopup(_staticPopupSlots);
        if (popup is not { } visible || _skin is null) return;

        float s = GameplayUiScale();
        string type = visible.Instance.Definition.Type;
        string text = FriendsFrameUiLaw.PopupText(type);
        float textHeight = GameText.LinePitch("GameFontHighlight", 1);
        StaticPopupCoordinatorLaw.NarrowEditBoxLayout layout =
            StaticPopupCoordinatorLaw.NarrowEditLayout(textHeight);
        Vector2 origin = StaticPopupOrigin(visible.Slot, layout.Width, s);
        Vector2 size = layout.Size * s;

        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoNav;
        bool begun = ImGui.Begin($"##social-name-popup-{visible.Slot}", flags);
        ImGui.PopStyleVar(2);
        if (!begun) { ImGui.End(); return; }

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        dl.PushClipRectFullScreen();
        _skin.DrawBackdrop(dl, origin, origin + size, WowSkin.Dialog);
        GameText.DrawCentered(dl, "GameFontHighlight", text,
            origin + layout.Text.Center * s, s);

        Vector2 editMin = origin + layout.EditBox.Min * s;
        DrawStaticPopupEditBoxBorder(dl, editMin, s);
        ImGui.SetCursorScreenPos(editMin + StaticPopupCoordinatorLaw.EditTextOffset * s);
        ImGui.SetNextItemWidth(layout.EditBox.Width * s);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Border, Vector4.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
        if (_socialPopupFocusRequested)
        {
            ImGui.SetKeyboardFocusHere();
            _socialPopupFocusRequested = false;
        }
        bool entered = ImGui.InputText("##static-popup-edit", _friendNameInput,
            (uint)_friendNameInput.Length, ImGuiInputTextFlags.EnterReturnsTrue);
        _socialPopupEditFocused = ImGui.IsItemActive();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(4);

        bool accepted = DrawPartyInviteButton(dl, "StaticPopup1Button1", "Accept",
            origin + layout.Button1.Min * s,
            s, capture: false, default);
        bool cancelled = DrawPartyInviteButton(dl, "StaticPopup1Button2", "Cancel",
            origin + layout.Button2.Min * s,
            s, capture: false, default);
        dl.PopClipRect();
        ImGui.End();

        if (entered)
        {
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.EditBoxEnter(
                _staticPopupSlots, visible.Slot));
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.HideByType(
                _staticPopupSlots, type));
        }
        else if (accepted)
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
                _staticPopupSlots, visible.Slot, buttonIndex: 1));
        else if (cancelled)
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
                _staticPopupSlots, visible.Slot, buttonIndex: 2));
    }

    private void DrawStaticPopupEditBoxBorder(ImDrawListPtr dl, Vector2 editMin, float s)
    {
        uint left = _gameplayArt?.Handle(@"Interface\ChatFrame\UI-ChatInputBorder-Left") ?? 0;
        uint right = _gameplayArt?.Handle(@"Interface\ChatFrame\UI-ChatInputBorder-Right") ?? 0;
        uint[] textures = [left, right];
        IReadOnlyList<StaticPopupCoordinatorLaw.TextureSlice> slices =
            StaticPopupCoordinatorLaw.NarrowEditBorderSlices;
        for (int i = 0; i < textures.Length; i++)
        {
            if (textures[i] == 0) continue;
            StaticPopupCoordinatorLaw.TextureSlice slice = slices[i];
            Vector2 min = editMin + slice.Rect.Min * s;
            dl.AddImage((nint)textures[i], min, min + slice.Rect.Size * s,
                slice.UvMin, slice.UvMax);
        }
    }

    // WhoFrameColumnHeaderTemplate: 3-slice WhoFrame-ColumnTabs (Left 5px, Middle stretched,
    // Right 4px; all V 0-0.75), 24 tall, GameFontHighlightSmall label +8 (FriendsFrame.xml:166-233).
    private void DrawWhoColumnHeader(ImDrawListPtr dl, Vector2 min, float width, float s, string label)
    {
        uint tex = _gameplayArt?.Handle(@"Interface\FriendsFrame\WhoFrame-ColumnTabs") ?? 0;
        if (tex != 0)
            foreach (FriendsFrameUiLaw.TextureSlice slice in
                     FriendsFrameUiLaw.WhoColumnHeaderSlices(width))
            {
                Vector2 at = min + slice.Rect.Min * s;
                dl.AddImage((nint)tex, at, at + slice.Rect.Size * s,
                    slice.UvMin, slice.UvMax);
            }
        GameText.Draw(dl, "GameFontHighlightSmall", label,
            min + FriendsFrameUiLaw.WhoHeaderTextOffset * s, s);
    }

    private void DrawWhoPage(ImDrawListPtr dl, Vector2 origin, float s)
    {
        // WhoFrame column headers (FriendsFrame.xml:1302-1398): Name x20 w83, then the sort
        // dropdown (default ZONE) x101 w105, Lvl x204 w32, Class x234 w92.
        (string Label, FriendsFrameUiLaw.LogicalRect Rect, FriendsWhoSort? Sort)[] cols =
            [("Name", FriendsFrameUiLaw.WhoHeaderName, FriendsWhoSort.Name),
             ("", FriendsFrameUiLaw.WhoVariableHeader(_who.Count), null),
             ("Lvl", FriendsFrameUiLaw.WhoLevelHeader(_who.Count), FriendsWhoSort.Level),
             ("Class", FriendsFrameUiLaw.WhoClassHeader(_who.Count), FriendsWhoSort.Class)];
        for (int column = 0; column < cols.Length; column++)
        {
            var c = cols[column];
            DrawWhoColumnHeader(dl, origin + c.Rect.Min * s, c.Rect.Width, s, c.Label);
            if (c.Sort is not { } sort) continue;
            Vector2 headerMin = origin + c.Rect.Min * s;
            ImGui.SetCursorScreenPos(headerMin);
            bool clicked = ImGui.InvisibleButton($"##who-header-{column}",
                FriendsFrameUiLaw.WhoHeaderHit(c.Rect.Width).Size * s);
            if (ImGui.IsItemHovered())
            {
                uint highlight = _gameplayArt?.AdditiveHandle(
                    FriendsFrameUiLaw.WhoHeaderHighlightPath) ?? 0;
                FriendsFrameUiLaw.LogicalRect highlightRect =
                    FriendsFrameUiLaw.WhoHeaderHighlight(c.Rect.Width);
                Vector2 highlightMin = headerMin + highlightRect.Min * s;
                if (highlight != 0)
                    dl.AddImage((nint)highlight, highlightMin,
                        highlightMin + highlightRect.Size * s);
            }
            if (clicked)
            {
                SortWhoResults(sort);
                PlayUiSound("igMainMenuOptionCheckBoxOn", "ui.social");
            }
        }

        _whoScroll = FriendsFrameUiLaw.ClampOffset(_whoScroll, _who.Count,
            FriendsFrameUiLaw.WhoVisibleRows);
        HandleSocialListWheel(origin, s,
            ref _whoScroll, _who.Count, FriendsFrameUiLaw.WhoVisibleRows);
        int count = Math.Min(FriendsFrameUiLaw.WhoVisibleRows, _who.Count - _whoScroll);
        for (int i = 0; i < count; i++)
        {
            int index = _whoScroll + i;
            FriendsFrameUiLaw.WhoEntry row = _who[index];
            FriendsFrameUiLaw.LogicalRect rowRect = FriendsFrameUiLaw.Row(
                FriendsFrameUiLaw.WhoRows, FriendsFrameUiLaw.WhoRowStep, i, 16);
            Vector2 rowMin = origin + rowRect.Min * s;
            bool rowClicked = VanillaListRow(dl, $"##who-{index}", rowMin,
                rowRect.Size, s, "", _whoSelected == index,
                highlightPath: FriendsFrameUiLaw.RowHighlightPath,
                additiveHighlight: true);
            bool rowRightClicked = ImGui.IsItemClicked(ImGuiMouseButton.Right);
            if (rowClicked) _whoSelected = index;
            if (rowRightClicked)
                OpenFriendPopup(row.Name, ImGui.GetIO().MousePos);
            if (rowClicked || rowRightClicked)
                PlayUiSound(FriendsFrameUiLaw.RowSound, FriendsFrameUiLaw.SoundCategory);
            // BenillaFriendsFrameWhoButtonTemplate: name x10/w88; variable immediately after it;
            // level +2 and class +12. The variable widens from 95 to 110 when no bar is present.
            float variableWidth = FriendsFrameUiLaw.WhoVariableTextWidth(_who.Count);
            string variable = _whoVariable switch
            {
                FriendsWhoVariable.Guild => row.Guild,
                FriendsWhoVariable.Race => FriendsFrameUiLaw.ResolvedDisplayLabel(
                    RaceName((byte)row.Race)),
                _ => FriendsFrameUiLaw.ResolvedDisplayLabel(_areas?.ZoneName(row.Area)),
            };
            GameText.Draw(dl, "GameFontNormalSmall",
                GameText.EllipsizeToBox("GameFontNormalSmall", row.Name, 88, 14, s),
                rowMin + FriendsFrameUiLaw.WhoNameTextOffset * s, s, VanillaGold);
            GameText.Draw(dl, "GameFontHighlightSmall",
                GameText.EllipsizeToBox("GameFontHighlightSmall", variable,
                    variableWidth, 14, s),
                rowMin + FriendsFrameUiLaw.WhoVariableTextOffset * s, s);
            GameText.DrawCentered(dl, "GameFontHighlightSmall", row.Level.ToString(),
                rowMin + FriendsFrameUiLaw.WhoLevelCenter(variableWidth) * s, s);
            GameText.Draw(dl, "GameFontHighlightSmall",
                GameText.EllipsizeToBox("GameFontHighlightSmall",
                    FriendsFrameUiLaw.ResolvedDisplayLabel(
                        ClassName((byte)row.Class)), 100, 8, s),
                rowMin + FriendsFrameUiLaw.WhoClassTextOffset(variableWidth) * s, s);
        }
        DrawSocialFauxScrollBar(dl, "##who-scroll", origin, s,
            FriendsFrameUiLaw.WhoScrollFrame, _whoScroll,
            FriendsFrameUiLaw.MaximumOffset(_who.Count, FriendsFrameUiLaw.WhoVisibleRows),
            value => _whoScroll = value);
        GameText.DrawCentered(dl, "GameFontNormalSmall",
            FriendsFrameUiLaw.WhoTotals(_whoTotal),
            origin + FriendsFrameUiLaw.WhoTotalsBox.Center * s, s);
        // WhoFrameEditBox is a bare 296x32 ChatFontNormal field: no backdrop, zero text insets.
        VanillaBareInputText("##who-search", _whoInput,
            origin + FriendsFrameUiLaw.WhoSearch.Min * s, FriendsFrameUiLaw.WhoSearch.Size,
            FriendsFrameUiLaw.WhoSearchTextInset, s);
        bool whoInputActive = ImGui.IsItemActive();
        if (FriendsFrameUiLaw.ShouldSubmitWhoFilter(whoInputActive,
                ImGui.IsKeyPressed(ImGuiKey.Enter, false),
                ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false)))
            SendWhoFilter(ReadBuffer(_whoInput));
        // Refresh | Add Friend | Group Invite row at y=408 (FriendsFrame.xml:1583-1634).
        if (VanillaButton(dl, "##who-refresh", "Refresh",
                origin + FriendsFrameUiLaw.WhoRefresh.Min * s,
                FriendsFrameUiLaw.WhoRefresh.Size, s))
        {
            SendWhoFilter(ReadBuffer(_whoInput));
            _whoSelected = -1;
        }
        bool selected = FriendsFrameUiLaw.WhoSelectionValid(_whoSelected, _who.Count);
        int selectedIndex = Math.Clamp(_whoSelected, 0, Math.Max(0, _who.Count - 1));
        if (VanillaButton(dl, "##who-friend", "Add Friend",
                origin + FriendsFrameUiLaw.WhoAddFriend.Min * s,
                FriendsFrameUiLaw.WhoAddFriend.Size, s, selected))
            _net?.AddFriend(_who[selectedIndex].Name);
        if (VanillaButton(dl, "##who-invite", "Group Invite",
                origin + FriendsFrameUiLaw.WhoGroupInvite.Min * s,
                FriendsFrameUiLaw.WhoGroupInvite.Size, s, selected))
        {
            string name = _who[selectedIndex].Name;
            if (!RefuseTacticalFreezeLiveCommand("inviting a party member") &&
                !RefuseTacticalFrozenActor(KnownPlayerGuid(name),
                    "invite them to a party"))
                _net?.GroupInvite(name);
        }
        DrawWhoVariableDropdown(dl, origin, s);
    }

    private void DrawWhoVariableDropdown(ImDrawListPtr draw, Vector2 origin, float scale)
    {
        DropdownCapsuleUiLaw.Layout dropdown = FriendsFrameUiLaw.WhoDropdown(_who.Count);
        if (VanillaDropdownCapsule(draw, "##who-variable-dropdown", origin, scale,
                dropdown, FriendsFrameUiLaw.WhoVariableLabels[(int)_whoVariable]))
        {
            _whoVariableMenuOpen = !_whoVariableMenuOpen;
            PlayUiSound(DropdownCapsuleUiLaw.ToggleSound, "ui.social");
        }

        if (!_whoVariableMenuOpen) return;
        DropdownCapsuleUiLaw.LogicalRect list = DropdownCapsuleUiLaw.List(dropdown,
            FriendsFrameUiLaw.WhoVariableLabels.Length);
        Vector2 listMin = origin + list.Min * scale;
        Vector2 listMax = listMin + list.Size * scale;
        _skin?.DrawBackdrop(draw, listMin, listMax, WowSkin.Dialog);
        for (int i = 0; i < FriendsFrameUiLaw.WhoVariableLabels.Length; i++)
        {
            DropdownCapsuleUiLaw.LogicalRect row = DropdownCapsuleUiLaw.Row(dropdown, i);
            Vector2 rowMin = origin + row.Min * scale;
            Vector2 rowSize = row.Size * scale;
            ImGui.SetCursorScreenPos(rowMin);
            bool clicked = ImGui.InvisibleButton($"##who-variable-{i}", rowSize);
            bool selected = i == (int)_whoVariable;
            if (selected || ImGui.IsItemHovered())
            {
                uint highlight = _gameplayArt?.AdditiveHandle(
                    DropdownCapsuleUiLaw.RowHighlight) ?? 0;
                if (highlight != 0)
                    draw.AddImage((nint)highlight, rowMin, rowMin + rowSize);
            }
            if (selected)
            {
                uint check = _gameplayArt?.Handle(DropdownCapsuleUiLaw.RowCheck) ?? 0;
                if (check != 0)
                {
                    Vector2 checkMin = rowMin + DropdownCapsuleUiLaw.Check.Min * scale;
                    draw.AddImage((nint)check, checkMin,
                        checkMin + DropdownCapsuleUiLaw.Check.Size * scale);
                }
            }
            GameText.Draw(draw, DropdownCapsuleUiLaw.SelectionFont,
                FriendsFrameUiLaw.WhoVariableLabels[i],
                rowMin + DropdownCapsuleUiLaw.RowTextOffset * scale, scale);
            if (clicked)
            {
                SelectWhoVariable((FriendsWhoVariable)i);
                PlayUiSound(DropdownCapsuleUiLaw.RowSound, "ui.social");
            }
        }

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            Vector2 mouse = ImGui.GetIO().MousePos;
            Vector2 frameMin = origin + dropdown.Frame.Min * scale;
            bool inFrame = mouse.X >= frameMin.X && mouse.Y >= frameMin.Y &&
                mouse.X <= frameMin.X + dropdown.Frame.Size.X * scale &&
                mouse.Y <= frameMin.Y + dropdown.Frame.Size.Y * scale;
            bool inList = mouse.X >= listMin.X && mouse.Y >= listMin.Y &&
                mouse.X <= listMax.X && mouse.Y <= listMax.Y;
            if (!inFrame && !inList) _whoVariableMenuOpen = false;
        }
    }

    private void SelectWhoVariable(FriendsWhoVariable variable)
    {
        _whoVariable = variable;
        _whoVariableMenuOpen = false;
        SortWhoResults(FriendsFrameUiLaw.SortForVariable(variable));
        if (!FriendsFrameUiLaw.WhoSelectionValid(_whoSelected, _who.Count))
            _whoSelected = -1;
    }

    private void SortWhoResults(FriendsWhoSort sort)
    {
        IReadOnlyList<FriendsFrameUiLaw.WhoEntry> sorted =
            FriendsFrameUiLaw.SortWho(_who, sort);
        _who.Clear();
        _who.AddRange(sorted);
    }

    private static void HandleSocialListWheel(Vector2 origin, float scale,
        ref int offset, int itemCount, int visibleRows)
    {
        Vector2 min = origin + FriendsFrameUiLaw.ListWheelRegion.Min * scale;
        Vector2 max = min + FriendsFrameUiLaw.ListWheelRegion.Size * scale;
        float wheel = ImGui.GetIO().MouseWheel;
        if (wheel != 0 && ImGui.IsMouseHoveringRect(min, max, false))
            offset = FriendsFrameUiLaw.WheelOffset(offset, itemCount, visibleRows, wheel);
    }

    private void DrawSocialFauxScrollBar(ImDrawListPtr draw, string id, Vector2 origin,
        float scale, FriendsFrameUiLaw.LogicalRect scrollFrame, int value, int maximum,
        Action<int> changed)
    {
        if (maximum <= 0 || _gameplayArt is null) return;

        FriendsFrameUiLaw.ScrollBarLayout layout =
            FriendsFrameUiLaw.ScrollBar(scrollFrame, value, maximum);
        Vector2 upMin = origin + layout.UpButton.Min * scale;
        Vector2 buttonSize = layout.UpButton.Size * scale;
        Vector2 downMin = origin + layout.DownButton.Min * scale;

        void Arrow(string suffix, Vector2 min, bool enabled, int next)
        {
            ImGui.SetCursorScreenPos(min);
            if (!enabled) ImGui.BeginDisabled();
            bool releasedInside = ImGui.InvisibleButton(id + suffix, buttonSize);
            bool held = enabled && ImGui.IsItemActive();
            bool hovered = enabled && ImGui.IsItemHovered();
            if (!enabled) ImGui.EndDisabled();

            ButtonInteractionLaw.Visual visual = ButtonInteractionLaw.ResolveVisual(
                enabled, hovered, held, scriptedPushed: false, isChecked: false,
                lockedHighlight: false);
            string stem = suffix == "-up"
                ? "UI-ScrollBar-ScrollUpButton"
                : "UI-ScrollBar-ScrollDownButton";
            string state = visual.PrimaryTexture switch
            {
                ButtonInteractionLaw.TextureSlot.Disabled => "Disabled",
                ButtonInteractionLaw.TextureSlot.Pushed => "Down",
                _ => "Up",
            };
            uint texture = _gameplayArt.Handle($@"Interface\Buttons\{stem}-{state}");
            if (texture != 0)
                draw.AddImage((nint)texture, min, min + buttonSize,
                    FriendsFrameUiLaw.ScrollUvMin, FriendsFrameUiLaw.ScrollUvMax);
            if (visual.HighlightVisible)
            {
                uint highlight = _gameplayArt.AdditiveHandle(
                    $@"Interface\Buttons\{stem}-Highlight");
                if (highlight != 0)
                    draw.AddImage((nint)highlight, min, min + buttonSize,
                        FriendsFrameUiLaw.ScrollUvMin, FriendsFrameUiLaw.ScrollUvMax);
            }
            if (enabled && releasedInside)
            {
                changed(next);
                PlayUiSound("UChatScrollButton", "ui.social");
            }
        }

        Arrow("-up", upMin, value > 0, value - 1);
        Arrow("-down", downMin, value < maximum, value + 1);

        Vector2 sliderMin = origin + layout.Track.Min * scale;
        Vector2 sliderSize = layout.Track.Size * scale;
        Vector2 knobSize = layout.Knob.Size * scale;
        Vector2 knobMin = origin + layout.Knob.Min * scale;
        uint knob = _gameplayArt.Handle(@"Interface\Buttons\UI-ScrollBar-Knob");
        if (knob != 0)
            draw.AddImage((nint)knob, knobMin, knobMin + knobSize,
                FriendsFrameUiLaw.ScrollUvMin, FriendsFrameUiLaw.ScrollUvMax);

        ImGui.SetCursorScreenPos(sliderMin);
        ImGui.InvisibleButton(id + "-track", sliderSize);
        if (ImGui.IsItemActive())
        {
            float y = ImGui.GetIO().MousePos.Y - sliderMin.Y - knobSize.Y * .5f;
            int next = (int)MathF.Round(Math.Clamp(y /
                MathF.Max(1, sliderSize.Y - knobSize.Y), 0, 1) * maximum);
            if (next != value) changed(next);
        }
    }

    private void SendWhoFilter(string filter) => _net?.Who(
        SocialPackets.ParseWhoFilter(filter, name => _areas?.IdForName(name)));
}
