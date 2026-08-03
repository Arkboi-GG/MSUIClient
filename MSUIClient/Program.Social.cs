using System.Numerics;
using System.Text;
using ImGuiNET;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private sealed record FriendRow(ulong Guid, byte Status, uint Area, uint Level, uint Class);
    private sealed record WhoRow(string Name, string Guild, uint Level, uint Class, uint Race, uint Area);
    private readonly List<FriendRow> _friends = [];
    private readonly List<ulong> _ignored = [];
    private readonly List<WhoRow> _who = [];
    private readonly byte[] _friendNameInput = new byte[64];
    private readonly byte[] _whoInput = new byte[64];
    private bool _socialOpen;
    private int _socialSelected;
    private int _socialPage;
    private bool _showIgnore;
    private uint _whoTotal;

    private void OpenSocial()
    {
        _socialOpen = true;
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
        // The status result has result-dependent trailing data. A fresh list is the
        // authoritative compact state and also handles add/remove/online transitions.
        if (body.Length >= 9) _net?.FriendList();
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
        if (!BeginVanillaWindow("##social", new Vector2(0, 104), new Vector2(384, 512),
                out ImDrawListPtr dl, out Vector2 origin, out float s)) { ImGui.End(); return; }
        DrawFourPieceShell(dl, origin, s,
            @"Interface\FriendsFrame\UI-FriendsFrame-TopLeft",
            @"Interface\FriendsFrame\UI-FriendsFrame-TopRight",
            @"Interface\FriendsFrame\UI-FriendsFrame-BotLeft",
            @"Interface\FriendsFrame\UI-FriendsFrame-BotRight");
        string title = _socialPage switch { 1 => "Who List", 2 => "Guild", 3 => "Raid", _ => "Friends List" };
        DrawCenteredText(dl, origin + new Vector2(192, 18) * s, title, 14f * s, VanillaGold);

        if (_socialPage == 0) DrawFriendsOrIgnore(dl, origin, s);
        else if (_socialPage == 1) DrawWhoPage(dl, origin, s);
        else if (_socialPage == 2)
        {
            dl.AddText(ImGui.GetFont(), 11f * s, origin + new Vector2(28, 82) * s, 0xffffffff,
                "Opening the guild roster...");
            _socialOpen = false; RequestGuildRoster();
        }
        else DrawRaidPage(dl, origin, s);

        string[] outerTabs = ["Friends", "Who", "Guild", "Raid"];
        float[] tabW = outerTabs.Select(text => VanillaCharacterTabWidth(text, s, 0)).ToArray();
        float tabX = 11;
        for (int i = 0; i < outerTabs.Length; i++)
        {
            if (VanillaTab(dl, $"##social-tab-{i}", origin + new Vector2(tabX, 433) * s,
                    outerTabs[i], tabW[i], s, _socialPage == i))
            {
                _socialPage = i; _socialSelected = 0;
                if (i == 0) _net?.FriendList();
                if (i == 1) _net?.Who(ReadBuffer(_whoInput));
                if (i == 2) { _socialOpen = false; RequestGuildRoster(); }
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
                FriendRow row = _friends[i];
                string name = _playerNames.GetValueOrDefault(row.Guid, $"Player {row.Guid & 0xffff:X4}");
                string zone = row.Status == 0 ? "Offline" : _areas?.ZoneName(row.Area) ?? $"Area {row.Area}";
                string info = row.Status == 0 ? "Offline" : $"Level {row.Level} {ClassName((byte)row.Class)} - {zone}";
                if (VanillaListRow(dl, $"##friend-{row.Guid}", origin + new Vector2(23, 76 + i * 31) * s,
                        new Vector2(298, 31), s, $"{name}    {info}", _socialSelected == i,
                        row.Status == 0 ? 0xff808080 : 0xffffffff)) _socialSelected = i;
            }
        }
        else for (int i = 0; i < _ignored.Count && i < 20; i++)
        {
            ulong guid = _ignored[i]; string name = _playerNames.GetValueOrDefault(guid, $"Player {guid & 0xffff:X4}");
            if (VanillaListRow(dl, $"##ignore-{guid}", origin + new Vector2(23, 76 + i * 16) * s,
                    new Vector2(298, 16), s, name, _socialSelected == i, 0xffffffff)) _socialSelected = i;
        }
        VanillaInputText(dl, "##social-name", _friendNameInput,
            origin + new Vector2(17, 378) * s, new Vector2(302, 22), s);
        string input = FriendInput();
        if (VanillaButton(dl, "##social-add", _showIgnore ? "Ignore Player" : "Add Friend",
                origin + new Vector2(17, 405) * s, new Vector2(131, 21), s, input.Length > 0))
        {
            if (_showIgnore) _net?.AddIgnore(input); else _net?.AddFriend(input);
            Array.Clear(_friendNameInput);
        }
        int count = _showIgnore ? _ignored.Count : _friends.Count;
        if (VanillaButton(dl, "##social-remove", _showIgnore ? "Stop Ignore" : "Remove Friend",
                origin + new Vector2(17, 431) * s, new Vector2(131, 21), s, count > 0))
        {
            if (_showIgnore) _net?.DeleteIgnore(_ignored[Math.Clamp(_socialSelected, 0, _ignored.Count - 1)]);
            else _net?.DeleteFriend(_friends[Math.Clamp(_socialSelected, 0, _friends.Count - 1)].Guid);
        }
        if (!_showIgnore && VanillaButton(dl, "##social-invite", "Group Invite",
                origin + new Vector2(214, 431) * s, new Vector2(131, 21), s, _friends.Count > 0))
        {
            FriendRow row = _friends[Math.Clamp(_socialSelected, 0, _friends.Count - 1)];
            _net?.GroupInvite(_playerNames.GetValueOrDefault(row.Guid, ""));
        }
    }

    private void DrawWhoPage(ImDrawListPtr dl, Vector2 origin, float s)
    {
        string[] headers = ["Name", "Guild", "Lvl", "Class"];
        float[] x = [20, 152, 247, 280]; float[] w = [134, 97, 35, 78];
        for (int i = 0; i < headers.Length; i++)
        {
            dl.AddRectFilled(origin + new Vector2(x[i], 70) * s,
                origin + new Vector2(x[i] + w[i], 91) * s, 0xff342517);
            dl.AddText(ImGui.GetFont(), 10f * s, origin + new Vector2(x[i] + 5, 75) * s, 0xffffffff, headers[i]);
        }
        for (int i = 0; i < _who.Count && i < 17; i++)
        {
            WhoRow row = _who[i]; string guild = row.Guild.Length == 0 ? "" : $"<{row.Guild}>";
            string line = $"{row.Name,-18}{guild,-14}{row.Level,3}  {ClassName((byte)row.Class)}";
            if (VanillaListRow(dl, $"##who-{i}", origin + new Vector2(15, 95 + i * 16) * s,
                    new Vector2(343, 16), s, line, _socialSelected == i, 0xffffffff)) _socialSelected = i;
        }
        dl.AddText(ImGui.GetFont(), 10f * s, origin + new Vector2(25, 371) * s, VanillaGold,
            $"Players: {_who.Count} of {_whoTotal}");
        VanillaInputText(dl, "##who-search", _whoInput,
            origin + new Vector2(24, 385) * s, new Vector2(296, 22), s);
        if (VanillaButton(dl, "##who-refresh", "Refresh", origin + new Vector2(19, 423) * s,
                new Vector2(85, 22), s)) _net?.Who(ReadBuffer(_whoInput));
        bool selected = _who.Count > 0;
        if (VanillaButton(dl, "##who-friend", "Add Friend", origin + new Vector2(104, 423) * s,
                new Vector2(120, 22), s, selected)) _net?.AddFriend(_who[_socialSelected].Name);
        if (VanillaButton(dl, "##who-invite", "Group Invite", origin + new Vector2(224, 423) * s,
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
                m.Online != 0 ? 0xffffffff : 0xff808080);
        }
    }
}
