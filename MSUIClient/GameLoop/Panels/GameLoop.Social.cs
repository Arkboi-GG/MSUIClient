using System.Numerics;
using System.Text;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private sealed record WhoRow(string Name, string Guild, uint Level, uint Class, uint Race, uint Area);
    private readonly List<SocialPackets.FriendEntry> _friends = [];
    private readonly List<ulong> _ignored = [];
    private readonly List<SocialPackets.FriendStatus> _pendingFriendStatusLines = [];
    private readonly List<WhoRow> _who = [];
    // Twelve visible letters plus the terminating zero: the frozen ADD_FRIEND/ADD_IGNORE cap.
    private readonly byte[] _friendNameInput = new byte[FriendsFrameUiLaw.NameMaxLetters + 1];
    private readonly byte[] _whoInput = new byte[64];
    private bool _socialOpen;
    private int _socialSelected;
    private int _socialPage;
    private int _friendScroll;
    private int _ignoreScroll;
    private int _whoScroll;
    private FriendsWhoVariable _whoVariable = FriendsWhoVariable.Zone;
    private bool _whoVariableMenuOpen;
    private bool _showIgnore;
    private uint _whoTotal;
    private bool _socialPopupFocusRequested;
    private bool _socialPopupEditFocused;

    private void OpenSocial()
    {
        _socialOpen = true;
        _socialPage = 0; _showIgnore = false;   // always reopen on the Friends tab, never a stuck page
        _net?.FriendList();
    }

    private void ApplyFriendList(byte[] body)
    {
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
        _socialSelected = Math.Clamp(_socialSelected, 0, Math.Max(0, _friends.Count - 1));
        _friendScroll = FriendsFrameUiLaw.ClampOffset(_friendScroll, _friends.Count,
            FriendsFrameUiLaw.FriendsVisibleRows);
    }

    private void ApplyFriendStatus(byte[] body)
    {
        SocialPackets.FriendStatus update = SocialPackets.ParseFriendStatus(body);
        SocialPackets.ApplyStatus(_friends, _ignored, update);
        _socialSelected = Math.Clamp(_socialSelected, 0,
            Math.Max(0, (_showIgnore ? _ignored.Count : _friends.Count) - 1));
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
        var r = new PacketReader(body); int count = r.ReadU8();
        _ignored.Clear();
        for (int i = 0; i < count; i++)
        {
            ulong guid = r.ReadU64(); _ignored.Add(guid);
            if (!_playerNames.ContainsKey(guid)) _net?.NameQuery(guid);
        }
        _socialSelected = 0;
        _ignoreScroll = 0;
    }

    private void ApplyWhoList(byte[] body)
    {
        var r = new PacketReader(body); uint listed = r.ReadU32(); _whoTotal = r.ReadU32();
        if (listed > 50) throw new InvalidDataException($"SMSG_WHO count {listed}");
        _who.Clear();
        for (uint i = 0; i < listed; i++)
            _who.Add(new(r.ReadCString(), r.ReadCString(), r.ReadU32(), r.ReadU32(),
                r.ReadU32(), r.ReadU32()));
        _socialSelected = 0;
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
        // FriendsFrame's top half is the plain paperdoll top (UI-Character-General-Top*); only
        // the BOTTOM half carries the list inset + button recesses (UI-FriendsFrame-Bot*).
        // Using FriendsFrame-Top* for the top drew a second inset/scrollbar - the "two halves"
        // seam (FriendsFrame.xml:536-585).
        DrawFourPieceShell(dl, origin, s,
            @"Interface\PaperDollInfoFrame\UI-Character-General-TopLeft",
            @"Interface\PaperDollInfoFrame\UI-Character-General-TopRight",
            @"Interface\FriendsFrame\UI-FriendsFrame-BotLeft",
            @"Interface\FriendsFrame\UI-FriendsFrame-BotRight");
        DrawArt(dl, @"Interface\FriendsFrame\FriendsFrameScrollIcon",
            origin + FriendsFrameUiLaw.ScrollIcon.Min * s,
            FriendsFrameUiLaw.ScrollIcon.Size, s);
        // FriendsFrameTitleText: GameFontNormal (~12) at TOP centre; text switches to
        // IGNORE_LIST on the ignore subtab (FriendsFrame.xml:586-593, .lua:97).
        string title = _socialPage switch { 1 => "Who List", 2 => "Guild",
            _ => _showIgnore ? "Ignore List" : "Friends List" };
        DrawCenteredText(dl, origin + new Vector2(192, 18) * s, title, 12f * s, VanillaGold);

        // Guild (page 2) is a separate frame reached via the tab click below; it never renders
        // here, so the dispatch must not close the window from the render path (doing so every
        // frame made the window un-reopenable once the page got stuck on Guild).
        if (_socialPage == 1) DrawWhoPage(dl, origin, s);
        else DrawFriendsOrIgnore(dl, origin, s);

        string[] outerTabs = FriendsFrameUiLaw.OuterTabs;
        float[] tabW = outerTabs.Select(text => VanillaCharacterTabWidth(text, s, 0)).ToArray();
        float tabX = 11;
        for (int i = 0; i < outerTabs.Length; i++)
        {
            if (VanillaTab(dl, $"##social-tab-{i}", origin + new Vector2(tabX, 433) * s,
                    outerTabs[i], tabW[i], s, _socialPage == i))
            {
                _socialSelected = 0;
                _whoVariableMenuOpen = false;
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
            tabX += tabW[i] - 14;
        }
        DrawImageButton(dl, "##social-close", origin + new Vector2(322, 8) * s, new Vector2(32) * s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up", @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked()) _socialOpen = false;
        ImGui.End();
    }

    private void DrawFriendsOrIgnore(ImDrawListPtr dl, Vector2 origin, float s)
    {
        float friendsWidth=VanillaInsetTabWidth("Friends",s),ignoreWidth=VanillaInsetTabWidth("Ignore",s);
        if (VanillaInsetTab(dl, "##social-friends-list", origin + new Vector2(70, 39) * s,
                "Friends", friendsWidth, s, !_showIgnore)) { _showIgnore = false; _socialSelected = 0; }
        if (VanillaInsetTab(dl, "##social-ignore-list", origin + new Vector2(70+friendsWidth, 39) * s,
                "Ignore", ignoreWidth, s, _showIgnore)) { _showIgnore = true; _socialSelected = 0; }
        if (!_showIgnore)
        {
            _friendScroll = FriendsFrameUiLaw.ClampOffset(_friendScroll, _friends.Count,
                FriendsFrameUiLaw.FriendsVisibleRows);
            HandleSocialListWheel(origin, s, FriendsFrameUiLaw.FriendsRows,
                ref _friendScroll, _friends.Count, FriendsFrameUiLaw.FriendsVisibleRows);
            int count = Math.Min(FriendsFrameUiLaw.FriendsVisibleRows,
                _friends.Count - _friendScroll);
            for (int i = 0; i < count; i++)
            {
                int index = _friendScroll + i;
                SocialPackets.FriendEntry row = _friends[index];
                string name = _playerNames.GetValueOrDefault(row.Guid, $"Player {row.Guid & 0xffff:X4}");
                bool online = row.Status != 0;
                string zone = online ? _areas?.ZoneName(row.Area) ?? $"Area {row.Area}" : "";
                FriendsFrameUiLaw.LogicalRect rowRect = FriendsFrameUiLaw.Row(
                    FriendsFrameUiLaw.FriendsRows, FriendsFrameUiLaw.FriendRowStep, i, 31);
                Vector2 rowMin = origin + rowRect.Min * s;
                if (VanillaListRow(dl, $"##friend-{row.Guid}", rowMin,
                        rowRect.Size, s, "", _socialSelected == index)) _socialSelected = index;
                // FriendsFrameButtonTemplate: gold GameFontNormal name/location at (10,-3),
                // white GameFontHighlightSmall info line below (FriendsFrame.xml:4-44, .lua:185-186).
                string nameLine = online ? $"{name}  ({zone})" : name;
                string infoLine = online ? $"Level {row.Level} {ClassName((byte)row.Class)}" : "Offline";
                GameText.Draw(dl, "GameFontNormal", nameLine, rowMin + new Vector2(10, 4) * s, s,
                    online ? VanillaGold : 0xff808080);
                GameText.Draw(dl, "GameFontHighlightSmall", infoLine, rowMin + new Vector2(10, 17) * s, s);
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
            HandleSocialListWheel(origin, s, FriendsFrameUiLaw.IgnoreRows,
                ref _ignoreScroll, _ignored.Count, FriendsFrameUiLaw.IgnoreVisibleRows);
            int count = Math.Min(FriendsFrameUiLaw.IgnoreVisibleRows,
                _ignored.Count - _ignoreScroll);
            for (int i = 0; i < count; i++)
            {
                int index = _ignoreScroll + i;
                ulong guid = _ignored[index];
                string name = _playerNames.GetValueOrDefault(guid, $"Player {guid & 0xffff:X4}");
                FriendsFrameUiLaw.LogicalRect rowRect = FriendsFrameUiLaw.Row(
                    FriendsFrameUiLaw.IgnoreRows, FriendsFrameUiLaw.IgnoreRowStep, i, 16);
                // FriendsFrameIgnoreButtonTemplate name inherits GameFontNormal (gold), not white.
                if (VanillaListRow(dl, $"##ignore-{guid}", origin + rowRect.Min * s,
                        rowRect.Size, s, name, _socialSelected == index, VanillaGold))
                    _socialSelected = index;
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
                ? _friends[Math.Clamp(_socialSelected, 0, _friends.Count - 1)] : null;
            bool nameKnown = selectedFriend is { } selected &&
                _playerNames.TryGetValue(selected.Guid, out string? selectedName) &&
                selectedName.Length > 0;
            bool selectedOnline = FriendsFrameUiLaw.CanContact(haveSel,
                selectedFriend is { Status: not 0 }, nameKnown);
            if (VanillaButton(dl, "##social-add", "Add Friend",
                    origin + FriendsFrameUiLaw.AddFriend.Min * s,
                    FriendsFrameUiLaw.AddFriend.Size, s))
                ShowSocialNamePopup(ignore: false);
            if (VanillaButton(dl, "##social-send", "Send Message",
                    origin + FriendsFrameUiLaw.SendMessage.Min * s,
                    FriendsFrameUiLaw.SendMessage.Size, s, selectedOnline))
            {
                SocialPackets.FriendEntry sel = _friends[Math.Clamp(_socialSelected, 0, _friends.Count - 1)];
                OpenChatEditWith($"/w {_playerNames.GetValueOrDefault(sel.Guid, "")} ");
            }
            if (VanillaButton(dl, "##social-remove", "Remove Friend",
                    origin + FriendsFrameUiLaw.RemoveFriend.Min * s,
                    FriendsFrameUiLaw.RemoveFriend.Size, s,
                    FriendsFrameUiLaw.CanRemove(haveSel)))
                _net?.DeleteFriend(_friends[Math.Clamp(_socialSelected, 0, _friends.Count - 1)].Guid);
            if (VanillaButton(dl, "##social-invite", "Group Invite",
                    origin + FriendsFrameUiLaw.GroupInvite.Min * s,
                    FriendsFrameUiLaw.GroupInvite.Size, s, selectedOnline))
            {
                SocialPackets.FriendEntry sel = _friends[Math.Clamp(_socialSelected, 0, _friends.Count - 1)];
                _net?.GroupInvite(_playerNames.GetValueOrDefault(sel.Guid, ""));
            }
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
                _net?.DeleteIgnore(_ignored[Math.Clamp(_socialSelected, 0, _ignored.Count - 1)]);
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
        Vector2 size = new(layout.Width * s, layout.Height * s);

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
            origin + new Vector2(layout.Width * .5f,
                layout.Text.Y + layout.Text.Height * .5f) * s, s);

        Vector2 editMin = origin + new Vector2(layout.EditBox.X, layout.EditBox.Y) * s;
        DrawStaticPopupEditBoxBorder(dl, editMin, s);
        ImGui.SetCursorScreenPos(editMin + new Vector2(0, 7) * s);
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
            origin + new Vector2(layout.Button1.X, layout.Button1.Y) * s,
            s, capture: false, default);
        bool cancelled = DrawPartyInviteButton(dl, "StaticPopup1Button2", "Cancel",
            origin + new Vector2(layout.Button2.X, layout.Button2.Y) * s,
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
        float cap = StaticPopupCoordinatorLaw.EditBoxBorderCapWidth;
        float outer = StaticPopupCoordinatorLaw.EditBoxBorderOuterOffset;
        float width = StaticPopupCoordinatorLaw.NarrowEditBoxWidth;
        float height = StaticPopupCoordinatorLaw.NarrowEditBoxHeight;
        if (left != 0)
            dl.AddImage((nint)left, editMin + new Vector2(-outer, 0) * s,
                editMin + new Vector2(cap - outer, height) * s,
                Vector2.Zero, new Vector2(.29296875f, 1));
        if (right != 0)
            dl.AddImage((nint)right, editMin + new Vector2(width + outer - cap, 0) * s,
                editMin + new Vector2(width + outer, height) * s,
                new Vector2(.70703125f, 0), Vector2.One);
    }

    // WhoFrameColumnHeaderTemplate: 3-slice WhoFrame-ColumnTabs (Left 5px, Middle stretched,
    // Right 4px; all V 0-0.75), 24 tall, GameFontHighlightSmall label +8 (FriendsFrame.xml:166-233).
    private void DrawWhoColumnHeader(ImDrawListPtr dl, Vector2 min, float width, float s, string label)
    {
        uint tex = _gameplayArt?.Handle(@"Interface\FriendsFrame\WhoFrame-ColumnTabs") ?? 0;
        if (tex != 0)
        {
            const float leftW = 5f, rightW = 4f, h = 24f;
            float midW = MathF.Max(0f, width - leftW - rightW);
            Vector2 p = min;
            dl.AddImage((nint)tex, p, p + new Vector2(leftW, h) * s, new Vector2(0f, 0f), new Vector2(0.078125f, 0.75f));
            p += new Vector2(leftW, 0f) * s;
            dl.AddImage((nint)tex, p, p + new Vector2(midW, h) * s, new Vector2(0.078125f, 0f), new Vector2(0.90625f, 0.75f));
            p += new Vector2(midW, 0f) * s;
            dl.AddImage((nint)tex, p, p + new Vector2(rightW, h) * s, new Vector2(0.90625f, 0f), new Vector2(0.96875f, 0.75f));
        }
        GameText.Draw(dl, "GameFontHighlightSmall", label, min + new Vector2(8, 7) * s, s);
    }

    private void DrawWhoPage(ImDrawListPtr dl, Vector2 origin, float s)
    {
        // WhoFrame column headers (FriendsFrame.xml:1302-1398): Name x20 w83, then the sort
        // dropdown (default ZONE) x101 w105, Lvl x204 w32, Class x234 w92. The 3-slice
        // WhoFrame-ColumnTabs art and the Zone/Guild/Race sort dropdown are still TODO —
        // headers render as flat labels for now, but with the correct identity/positions.
        (string Label, FriendsFrameUiLaw.LogicalRect Rect)[] cols =
            [("Name", FriendsFrameUiLaw.WhoHeaderName),
             ("", FriendsFrameUiLaw.WhoVariableHeader(_who.Count)),
             ("Lvl", FriendsFrameUiLaw.WhoLevelHeader(_who.Count)),
             ("Class", FriendsFrameUiLaw.WhoClassHeader(_who.Count))];
        foreach (var c in cols)
            DrawWhoColumnHeader(dl, origin + c.Rect.Min * s, c.Rect.Width, s, c.Label);

        _whoScroll = FriendsFrameUiLaw.ClampOffset(_whoScroll, _who.Count,
            FriendsFrameUiLaw.WhoVisibleRows);
        HandleSocialListWheel(origin, s, FriendsFrameUiLaw.WhoRows,
            ref _whoScroll, _who.Count, FriendsFrameUiLaw.WhoVisibleRows);
        int count = Math.Min(FriendsFrameUiLaw.WhoVisibleRows, _who.Count - _whoScroll);
        for (int i = 0; i < count; i++)
        {
            int index = _whoScroll + i;
            WhoRow row = _who[index];
            FriendsFrameUiLaw.LogicalRect rowRect = FriendsFrameUiLaw.Row(
                FriendsFrameUiLaw.WhoRows, FriendsFrameUiLaw.WhoRowStep, i, 16);
            Vector2 rowMin = origin + rowRect.Min * s;
            if (VanillaListRow(dl, $"##who-{index}", rowMin, rowRect.Size, s, "",
                    _socialSelected == index)) _socialSelected = index;
            // BenillaFriendsFrameWhoButtonTemplate: name x10/w88; variable immediately after it;
            // level +2 and class +12. The variable widens from 95 to 110 when no bar is present.
            float variableWidth = FriendsFrameUiLaw.WhoVariableTextWidth(_who.Count);
            string variable = _whoVariable switch
            {
                FriendsWhoVariable.Guild => row.Guild,
                FriendsWhoVariable.Race => RaceName((byte)row.Race),
                _ => _areas?.ZoneName(row.Area) ?? "",
            };
            GameText.Draw(dl, "GameFontNormalSmall",
                GameText.EllipsizeToBox("GameFontNormalSmall", row.Name, 88, 14, s),
                rowMin + new Vector2(10, 3) * s, s, VanillaGold);
            GameText.Draw(dl, "GameFontHighlightSmall",
                GameText.EllipsizeToBox("GameFontHighlightSmall", variable,
                    variableWidth, 14, s),
                rowMin + new Vector2(98, 3) * s, s);
            GameText.DrawCentered(dl, "GameFontHighlightSmall", row.Level.ToString(),
                rowMin + new Vector2(98 + variableWidth + 12, 8) * s, s);
            GameText.Draw(dl, "GameFontHighlightSmall",
                GameText.EllipsizeToBox("GameFontHighlightSmall",
                    ClassName((byte)row.Class), 100, 8, s),
                rowMin + new Vector2(132 + variableWidth, 3) * s, s);
        }
        DrawSocialFauxScrollBar(dl, "##who-scroll", origin, s,
            FriendsFrameUiLaw.WhoScrollFrame, _whoScroll,
            FriendsFrameUiLaw.MaximumOffset(_who.Count, FriendsFrameUiLaw.WhoVisibleRows),
            value => _whoScroll = value);
        dl.AddText(ImGui.GetFont(), 10f * s, origin + new Vector2(25, 371) * s, VanillaGold,
            $"Players: {_who.Count} of {_whoTotal}");
        // WhoFrameEditBox is 296x32 at top-origin (24,380) (FriendsFrame.xml:1635-1645).
        VanillaInputText(dl, "##who-search", _whoInput,
            origin + FriendsFrameUiLaw.WhoSearch.Min * s, FriendsFrameUiLaw.WhoSearch.Size, s);
        // Refresh | Add Friend | Group Invite row at y=408 (FriendsFrame.xml:1583-1634).
        if (VanillaButton(dl, "##who-refresh", "Refresh",
                origin + FriendsFrameUiLaw.WhoRefresh.Min * s,
                FriendsFrameUiLaw.WhoRefresh.Size, s)) SendWhoFilter(ReadBuffer(_whoInput));
        bool selected = _who.Count > 0;
        int selectedIndex = Math.Clamp(_socialSelected, 0, Math.Max(0, _who.Count - 1));
        if (VanillaButton(dl, "##who-friend", "Add Friend",
                origin + FriendsFrameUiLaw.WhoAddFriend.Min * s,
                FriendsFrameUiLaw.WhoAddFriend.Size, s, selected))
            _net?.AddFriend(_who[selectedIndex].Name);
        if (VanillaButton(dl, "##who-invite", "Group Invite",
                origin + FriendsFrameUiLaw.WhoGroupInvite.Min * s,
                FriendsFrameUiLaw.WhoGroupInvite.Size, s, selected))
            _net?.GroupInvite(_who[selectedIndex].Name);
        DrawWhoVariableDropdown(dl, origin, s);
    }

    private void DrawWhoVariableDropdown(ImDrawListPtr draw, Vector2 origin, float scale)
    {
        if (_gameplayArt is null) return;
        FriendsFrameUiLaw.LogicalRect frame = FriendsFrameUiLaw.WhoDropdownFrame(_who.Count);
        float dropWidth = FriendsFrameUiLaw.WhoDropdownWidth(_who.Count);
        Vector2 frameMin = origin + frame.Min * scale;
        Vector2 artMin = frameMin + new Vector2(0, -17) * scale;
        uint art = _gameplayArt.Handle(
            @"Interface\Glues\CharacterCreate\CharacterCreate-LabelFrame");
        if (art != 0)
        {
            draw.AddImage((nint)art, artMin, artMin + new Vector2(25, 64) * scale,
                Vector2.Zero, new Vector2(.1953125f, 1));
            draw.AddImage((nint)art, artMin + new Vector2(25, 0) * scale,
                artMin + new Vector2(25 + dropWidth, 64) * scale,
                new Vector2(.1953125f, 0), new Vector2(.8046875f, 1));
            draw.AddImage((nint)art, artMin + new Vector2(25 + dropWidth, 0) * scale,
                artMin + new Vector2(50 + dropWidth, 64) * scale,
                new Vector2(.8046875f, 0), Vector2.One);
        }
        GameText.Draw(draw, "GameFontHighlightSmall",
            FriendsFrameUiLaw.WhoVariableLabels[(int)_whoVariable],
            frameMin + new Vector2(27, 11) * scale, scale);

        Vector2 buttonMin = frameMin + new Vector2(frame.Width - 40, 18) * scale;
        ImGui.SetCursorScreenPos(buttonMin);
        bool toggled = ImGui.InvisibleButton("##who-variable-dropdown", new Vector2(24) * scale);
        bool held = ImGui.IsItemActive();
        bool hovered = ImGui.IsItemHovered();
        string state = held ? "Down" : "Up";
        uint button = _gameplayArt.Handle($@"Interface\ChatFrame\UI-ChatIcon-ScrollDown-{state}");
        if (button != 0)
            draw.AddImage((nint)button, buttonMin, buttonMin + new Vector2(24) * scale);
        if (hovered)
        {
            uint highlight = _gameplayArt.AdditiveHandle(
                @"Interface\Buttons\UI-Common-MouseHilight");
            if (highlight != 0)
                draw.AddImage((nint)highlight, buttonMin, buttonMin + new Vector2(24) * scale);
        }
        if (toggled)
        {
            _whoVariableMenuOpen = !_whoVariableMenuOpen;
            PlayUiSound("igMainMenuOptionCheckBoxOn", "ui.social");
        }

        if (!_whoVariableMenuOpen) return;
        FriendsFrameUiLaw.LogicalRect list = FriendsFrameUiLaw.WhoDropdownList(_who.Count);
        Vector2 listMin = origin + list.Min * scale;
        Vector2 listMax = listMin + list.Size * scale;
        _skin?.DrawBackdrop(draw, listMin, listMax, WowSkin.Tooltip,
            UnitPopupUiLaw.MenuBackdropFillTint, UnitPopupUiLaw.MenuBackdropEdgeTint);
        for (int i = 0; i < FriendsFrameUiLaw.WhoVariableLabels.Length; i++)
        {
            FriendsFrameUiLaw.LogicalRect row = FriendsFrameUiLaw.WhoDropdownRow(_who.Count, i);
            Vector2 rowMin = origin + row.Min * scale;
            Vector2 rowSize = row.Size * scale;
            ImGui.SetCursorScreenPos(rowMin);
            bool clicked = ImGui.InvisibleButton($"##who-variable-{i}", rowSize);
            if (ImGui.IsItemHovered())
            {
                uint highlight = _gameplayArt.AdditiveHandle(
                    @"Interface\QuestFrame\UI-QuestTitleHighlight");
                if (highlight != 0)
                    draw.AddImage((nint)highlight, rowMin, rowMin + rowSize);
            }
            if (i == (int)_whoVariable)
            {
                uint check = _gameplayArt.Handle(@"Interface\Buttons\UI-CheckBox-Check");
                if (check != 0)
                    draw.AddImage((nint)check, rowMin + new Vector2(0, -4) * scale,
                        rowMin + new Vector2(24, 20) * scale);
            }
            GameText.Draw(draw, "GameFontHighlightSmall",
                FriendsFrameUiLaw.WhoVariableLabels[i], rowMin + new Vector2(27, 2) * scale,
                scale);
            if (clicked) SelectWhoVariable((FriendsWhoVariable)i);
        }

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            Vector2 mouse = ImGui.GetIO().MousePos;
            bool inFrame = mouse.X >= frameMin.X && mouse.Y >= frameMin.Y &&
                mouse.X <= frameMin.X + frame.Size.X * scale &&
                mouse.Y <= frameMin.Y + frame.Size.Y * scale;
            bool inList = mouse.X >= listMin.X && mouse.Y >= listMin.Y &&
                mouse.X <= listMax.X && mouse.Y <= listMax.Y;
            if (!inFrame && !inList) _whoVariableMenuOpen = false;
        }
    }

    private void SelectWhoVariable(FriendsWhoVariable variable)
    {
        _whoVariable = variable;
        _whoVariableMenuOpen = false;
        string Value(WhoRow row) => variable switch
        {
            FriendsWhoVariable.Guild => row.Guild,
            FriendsWhoVariable.Race => RaceName((byte)row.Race),
            _ => _areas?.ZoneName(row.Area) ?? "",
        };
        _who.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(
            Value(left), Value(right)));
        _socialSelected = _who.Count == 0 ? 0 : Math.Clamp(_socialSelected, 0, _who.Count - 1);
    }

    private static void HandleSocialListWheel(Vector2 origin, float scale,
        FriendsFrameUiLaw.LogicalRect rows, ref int offset, int itemCount, int visibleRows)
    {
        Vector2 min = origin + rows.Min * scale;
        Vector2 max = min + rows.Size * scale;
        float wheel = ImGui.GetIO().MouseWheel;
        if (wheel != 0 && ImGui.IsMouseHoveringRect(min, max, false))
            offset = FriendsFrameUiLaw.WheelOffset(offset, itemCount, visibleRows, wheel);
    }

    private void DrawSocialFauxScrollBar(ImDrawListPtr draw, string id, Vector2 origin,
        float scale, FriendsFrameUiLaw.LogicalRect scrollFrame, int value, int maximum,
        Action<int> changed)
    {
        if (maximum <= 0 || _gameplayArt is null) return;

        FriendsFrameUiLaw.LogicalRect furniture =
            FriendsFrameUiLaw.ScrollFurniture(scrollFrame);
        Vector2 upMin = origin + furniture.Min * scale;
        Vector2 buttonSize = new Vector2(16) * scale;
        Vector2 downMin = origin + new Vector2(furniture.X,
            furniture.Y + furniture.Height - 16) * scale;

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
                    new Vector2(.25f), new Vector2(.75f));
            if (visual.HighlightVisible)
            {
                uint highlight = _gameplayArt.AdditiveHandle(
                    $@"Interface\Buttons\{stem}-Highlight");
                if (highlight != 0)
                    draw.AddImage((nint)highlight, min, min + buttonSize,
                        new Vector2(.25f), new Vector2(.75f));
            }
            if (enabled && releasedInside)
            {
                changed(next);
                PlayUiSound("UChatScrollButton", "ui.social");
            }
        }

        Arrow("-up", upMin, value > 0, value - 1);
        Arrow("-down", downMin, value < maximum, value + 1);

        Vector2 sliderMin = upMin + new Vector2(0, 16) * scale;
        float sliderHeight = MathF.Max(16, furniture.Height - 32) * scale;
        float fraction = Math.Clamp((float)value / maximum, 0, 1);
        Vector2 knobSize = new Vector2(16) * scale;
        Vector2 knobMin = sliderMin + new Vector2(0,
            fraction * MathF.Max(0, sliderHeight - knobSize.Y));
        uint knob = _gameplayArt.Handle(@"Interface\Buttons\UI-ScrollBar-Knob");
        if (knob != 0)
            draw.AddImage((nint)knob, knobMin, knobMin + knobSize,
                new Vector2(.25f), new Vector2(.75f));

        ImGui.SetCursorScreenPos(sliderMin);
        ImGui.InvisibleButton(id + "-track", new Vector2(16 * scale, sliderHeight));
        if (ImGui.IsItemActive())
        {
            float y = ImGui.GetIO().MousePos.Y - sliderMin.Y - knobSize.Y * .5f;
            int next = (int)MathF.Round(Math.Clamp(y /
                MathF.Max(1, sliderHeight - knobSize.Y), 0, 1) * maximum);
            if (next != value) changed(next);
        }
    }

    private void SendWhoFilter(string filter) => _net?.Who(
        SocialPackets.ParseWhoFilter(filter, name => _areas?.IdForName(name)));
}
