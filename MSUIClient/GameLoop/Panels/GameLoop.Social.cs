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
    }

    private void ApplyFriendStatus(byte[] body)
    {
        SocialPackets.FriendStatus update = SocialPackets.ParseFriendStatus(body);
        SocialPackets.ApplyStatus(_friends, _ignored, update);
        _socialSelected = Math.Clamp(_socialSelected, 0,
            Math.Max(0, (_showIgnore ? _ignored.Count : _friends.Count) - 1));

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
    }

    private string FriendInput()
    {
        int end = Array.IndexOf(_friendNameInput, (byte)0);
        return Encoding.UTF8.GetString(_friendNameInput, 0, end < 0 ? _friendNameInput.Length : end).Trim();
    }

    private void DrawSocialFrame()
    {
        if (!_socialOpen || _gameplayArt is null) return;
        if (!BeginVanillaWindow("##social", FriendsFrameUiLaw.FrameOrigin(1f),
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
        // FriendsFrameTitleText: GameFontNormal (~12) at TOP centre; text switches to
        // IGNORE_LIST on the ignore subtab (FriendsFrame.xml:586-593, .lua:97).
        string title = _socialPage switch { 1 => "Who List", 2 => "Guild", 3 => "Raid",
            _ => _showIgnore ? "Ignore List" : "Friends List" };
        DrawCenteredText(dl, origin + new Vector2(192, 18) * s, title, 12f * s, VanillaGold);

        // Guild (page 2) is a separate frame reached via the tab click below; it never renders
        // here, so the dispatch must not close the window from the render path (doing so every
        // frame made the window un-reopenable once the page got stuck on Guild).
        if (_socialPage == 1) DrawWhoPage(dl, origin, s);
        else if (_socialPage == 3) DrawRaidPage(dl, origin, s);
        else DrawFriendsOrIgnore(dl, origin, s);

        string[] outerTabs = ["Friends", "Who", "Guild", "Raid"];
        float[] tabW = outerTabs.Select(text => VanillaCharacterTabWidth(text, s, 0)).ToArray();
        float tabX = 11;
        for (int i = 0; i < outerTabs.Length; i++)
        {
            if (VanillaTab(dl, $"##social-tab-{i}", origin + new Vector2(tabX, 433) * s,
                    outerTabs[i], tabW[i], s, _socialPage == i))
            {
                _socialSelected = 0;
                if (i == 2)
                {
                    // Guild opens its own frame; keep the social page on Friends so reopening
                    // the social window later never lands on the self-closing Guild page.
                    _socialOpen = false; _socialPage = 0; RequestGuildRoster();
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
            for (int i = 0; i < _friends.Count && i < 10; i++)
            {
                SocialPackets.FriendEntry row = _friends[i];
                string name = _playerNames.GetValueOrDefault(row.Guid, $"Player {row.Guid & 0xffff:X4}");
                bool online = row.Status != 0;
                string zone = online ? _areas?.ZoneName(row.Area) ?? $"Area {row.Area}" : "";
                Vector2 rowMin = origin + new Vector2(23, 76 + i * 31) * s;
                if (VanillaListRow(dl, $"##friend-{row.Guid}", rowMin,
                        new Vector2(298, 31), s, "", _socialSelected == i)) _socialSelected = i;
                // FriendsFrameButtonTemplate: gold GameFontNormal name/location at (10,-3),
                // white GameFontHighlightSmall info line below (FriendsFrame.xml:4-44, .lua:185-186).
                string nameLine = online ? $"{name}  ({zone})" : name;
                string infoLine = online ? $"Level {row.Level} {ClassName((byte)row.Class)}" : "Offline";
                GameText.Draw(dl, "GameFontNormal", nameLine, rowMin + new Vector2(10, 4) * s, s,
                    online ? VanillaGold : 0xff808080);
                GameText.Draw(dl, "GameFontHighlightSmall", infoLine, rowMin + new Vector2(10, 17) * s, s);
            }
        }
        else for (int i = 0; i < _ignored.Count && i < 20; i++)
        {
            ulong guid = _ignored[i]; string name = _playerNames.GetValueOrDefault(guid, $"Player {guid & 0xffff:X4}");
            // FriendsFrameIgnoreButtonTemplate name inherits GameFontNormal (gold), not white.
            if (VanillaListRow(dl, $"##ignore-{guid}", origin + new Vector2(23, 76 + i * 16) * s,
                    new Vector2(298, 16), s, name, _socialSelected == i, VanillaGold)) _socialSelected = i;
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
                    origin + new Vector2(17, 384) * s, new Vector2(131, 21), s))
                ShowSocialNamePopup(ignore: true);
            if (VanillaButton(dl, "##social-ignore-remove", "Stop Ignore",
                    origin + new Vector2(17, 410) * s, new Vector2(131, 21), s, _ignored.Count > 0))
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
        (string Label, float X, float W)[] cols =
            [("Name", 20, 83), ("Zone", 101, 105), ("Lvl", 204, 32), ("Class", 234, 92)];
        foreach (var c in cols)
            DrawWhoColumnHeader(dl, origin + new Vector2(c.X, 68) * s, c.W, s, c.Label);
        for (int i = 0; i < _who.Count && i < 17; i++)
        {
            WhoRow row = _who[i];
            Vector2 rowMin = origin + new Vector2(15, 95 + i * 16) * s;
            if (VanillaListRow(dl, $"##who-{i}", rowMin, new Vector2(343, 16), s, "",
                    _socialSelected == i)) _socialSelected = i;
            // Columns anchored to the header x-positions (row starts at frame x=15).
            Vector2 ty = new(0, 3);
            GameText.Draw(dl, "GameFontHighlightSmall", row.Name, rowMin + (new Vector2(5, 0) + ty) * s, s);
            GameText.Draw(dl, "GameFontHighlightSmall", _areas?.ZoneName(row.Area) ?? "",
                rowMin + (new Vector2(86, 0) + ty) * s, s);
            GameText.Draw(dl, "GameFontHighlightSmall", row.Level.ToString(),
                rowMin + (new Vector2(189, 0) + ty) * s, s);
            GameText.Draw(dl, "GameFontHighlightSmall", ClassName((byte)row.Class),
                rowMin + (new Vector2(219, 0) + ty) * s, s);
        }
        dl.AddText(ImGui.GetFont(), 10f * s, origin + new Vector2(25, 371) * s, VanillaGold,
            $"Players: {_who.Count} of {_whoTotal}");
        // WhoFrameEditBox is 296x32 at top-origin (24,380) (FriendsFrame.xml:1635-1645).
        VanillaInputText(dl, "##who-search", _whoInput,
            origin + new Vector2(24, 380) * s, new Vector2(296, 32), s);
        // Refresh | Add Friend | Group Invite row at y=408 (FriendsFrame.xml:1583-1634).
        if (VanillaButton(dl, "##who-refresh", "Refresh", origin + new Vector2(19, 408) * s,
                new Vector2(85, 22), s)) SendWhoFilter(ReadBuffer(_whoInput));
        bool selected = _who.Count > 0;
        if (VanillaButton(dl, "##who-friend", "Add Friend", origin + new Vector2(104, 408) * s,
                new Vector2(120, 22), s, selected)) _net?.AddFriend(_who[_socialSelected].Name);
        if (VanillaButton(dl, "##who-invite", "Group Invite", origin + new Vector2(224, 408) * s,
                new Vector2(120, 22), s, selected)) _net?.GroupInvite(_who[_socialSelected].Name);
    }

    private void DrawRaidPage(ImDrawListPtr dl, Vector2 origin, float s)
    {
        if (_partyMembers.Count == 0)
            DrawCenteredText(dl, origin + new Vector2(192, 220) * s, "You are not in a raid group", 11f * s, 0xffffffff);
        for (int i = 0; i < _partyMembers.Count && i < 20; i++)
        {
            PartyMember m = _partyMembers[i];
            VanillaListRow(dl, $"##raid-{m.Guid}", origin + new Vector2(24, 76 + i * 16) * s,
                new Vector2(320, 16), s, $"Group {m.Subgroup + 1}    {m.Name}", false,
                PartyFrameUiLaw.Has(m.Status, PartyFrameUiLaw.Online)
                    ? 0xffffffff : 0xff808080);
        }
    }

    private void SendWhoFilter(string filter) => _net?.Who(
        SocialPackets.ParseWhoFilter(filter, name => _areas?.IdForName(name)));
}
