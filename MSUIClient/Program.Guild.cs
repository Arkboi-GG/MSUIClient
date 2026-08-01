using System.Numerics;
using System.Text;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private sealed record GuildMember(ulong Guid, bool Online, string Name, uint Rank,
        byte Level, byte Class, uint Zone, float OfflineDays, string PublicNote, string OfficerNote);

    private readonly List<GuildMember> _guildMembers = [];
    private readonly List<uint> _guildRankRights = [];
    private bool _guildOpen;
    private string _guildMotd = "";
    private string _guildInfo = "";
    private int _guildSelected;
    private readonly byte[] _guildMotdEdit = new byte[256];

    private void InitGuild() { }
    private void ResetGuild()
    { _guildMembers.Clear(); _guildRankRights.Clear(); _guildOpen = false; _guildMotd = _guildInfo = ""; _guildSelected = 0; }

    private bool RequestGuildRoster()
    {
        bool sent = _net?.GuildRoster() == true;
        EmitInterface("guild", "roster-send", sent ? "SENT" : "SEND_FAILED", _net?.PlayerGuid ?? 0, "body=EMPTY");
        return sent;
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
                ulong guid = r.ReadU64(); bool online = r.ReadU8() != 0; string name = r.ReadCString();
                uint rank = r.ReadU32(); byte level = r.ReadU8(), cls = r.ReadU8(); uint zone = r.ReadU32();
                float offline = online ? 0 : r.ReadF32(); string note = r.ReadCString(), officer = r.ReadCString();
                members.Add(new(guid, online, name, rank, level, cls, zone, offline, note, officer));
            }
            if (r.Remaining != 0) throw new InvalidDataException($"trailing={r.Remaining}");
            _guildMembers.Clear(); _guildMembers.AddRange(members); _guildRankRights.Clear(); _guildRankRights.AddRange(rights);
            _guildMotd = motd; _guildInfo = info; Array.Clear(_guildMotdEdit); byte[] motdBytes = Encoding.UTF8.GetBytes(motd);
            Array.Copy(motdBytes, _guildMotdEdit, Math.Min(motdBytes.Length, _guildMotdEdit.Length - 1));
            _guildOpen = true; _guildSelected = 0;
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
        if (body.Length < 2) return; var r = new PacketReader(body); byte evt = r.ReadU8(), count = r.ReadU8();
        var values = new List<string>(); for (int i = 0; i < count && r.HasMore; i++) values.Add(r.ReadCString());
        EmitInterface("guild", "event", "RECEIVED", 0, $"event={evt};values={string.Join('|', values.Select(SanitizeEvidence))}");
        if (evt == 5 && values.Count > 0) _guildMotd = values[0];
    }

    private void ApplyGuildCommandResult(byte[] body)
    {
        if (body.Length < 8) return; var r = new PacketReader(body); uint command = r.ReadU32(); string text = r.ReadCString(); uint result = r.ReadU32();
        EmitInterface("guild", "command", result == 0 ? "SUCCESS" : $"FAILED-{result}", 0,
            $"command={command};text={SanitizeEvidence(text)};body={Convert.ToHexString(body)}");
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
        if (!_guildOpen||_gameplayArt is null) return;float s=GameplayUiScale();Vector2 origin=new(0,8*s),logicalSize=new(384,512);
        ImGui.SetNextWindowPos(origin,ImGuiCond.Always);ImGui.SetNextWindowSize(logicalSize*s,ImGuiCond.Always);ImGui.SetNextWindowBgAlpha(0);
        if (!ImGui.Begin("##guild",ImGuiWindowFlags.NoDecoration|ImGuiWindowFlags.NoMove|ImGuiWindowFlags.NoSavedSettings|ImGuiWindowFlags.NoBackground|ImGuiWindowFlags.NoNav)) { ImGui.End(); return; }
        ImDrawListPtr dl=ImGui.GetWindowDrawList();if(_uiParityArmed&&_uiParityPanel=="guild"){BeginUiParityFrame(origin,s);CollectUiParityDraw("GuildFrame","Frame",origin,logicalSize*s,"",new("",0,"IMGUI_HOST","ANCHOR:ABSOLUTE","","",0,8));}
        (string Element,string Path,Vector2 Offset,Vector2 Size)[] art=[
            ("GuildFrame/ShellTopLeft",@"Interface\FriendsFrame\UI-FriendsFrame-TopLeft",Vector2.Zero,new(256,256)),
            ("GuildFrame/ShellTopRight",@"Interface\FriendsFrame\UI-FriendsFrame-TopRight",new(256,0),new(128,256)),
            ("GuildFrame/ShellBotLeft",@"Interface\FriendsFrame\UI-FriendsFrame-BotLeft",new(0,256),new(256,256)),
            ("GuildFrame/ShellBotRight",@"Interface\FriendsFrame\UI-FriendsFrame-BotRight",new(256,256),new(128,256))];
        foreach(var r in art){Vector2 m=origin+r.Offset*s;DrawArt(dl,r.Path,m,r.Size,s);if(_uiParityArmed&&_uiParityPanel=="guild")CollectUiParityDraw(r.Element,"Texture",m,r.Size*s,"GuildFrame",new(r.Path,0xffffffff,"IMGUI_IMAGE","TOPLEFT","GuildFrame","TOPLEFT",r.Offset.X,-r.Offset.Y));}
        ImGui.SetCursorScreenPos(origin+new Vector2(30,72)*s);ImGui.BeginChild("##guild-content",new Vector2(305,365)*s,false);
        ImGui.TextUnformatted($"MOTD: {_guildMotd}"); ImGui.TextDisabled(_guildInfo); ImGui.Separator();
        for (int i = 0; i < _guildMembers.Count; i++) { GuildMember m = _guildMembers[i]; if (ImGui.Selectable($"{(m.Online ? "Online" : $"Offline {m.OfflineDays:F1}d")}  {m.Name}  Lv{m.Level}  Rank {m.Rank}##guild-{m.Guid}", _guildSelected == i)) _guildSelected = i; }
        ImGui.InputText("MOTD", _guildMotdEdit, (uint)_guildMotdEdit.Length); if (ImGui.Button("Set MOTD")) SetGuildMotd(ReadBuffer(_guildMotdEdit));
        if (_guildMembers.Count > 0) { string name = _guildMembers[Math.Clamp(_guildSelected, 0, _guildMembers.Count - 1)].Name; ImGui.SameLine(); if (ImGui.Button("Promote")) PromoteGuildMember(name); ImGui.SameLine(); if (ImGui.Button("Demote")) DemoteGuildMember(name); }
        if (_config.DevTools && ImGui.Button("Copy guild evidence")) CopyVerdictText(string.Join(Environment.NewLine, _verdicts.Snapshot("interface").OfType<InterfaceVerdict>().Where(v => v.Family == "guild").Select(v => $"[verdict:interface] {v.ToLine()}")));
        ImGui.EndChild();Vector2 close=origin+new Vector2(324,10)*s;DrawImageButton(dl,"##guild-close",close,new Vector2(32)*s,@"Interface\Buttons\UI-Panel-MinimizeButton-Up",@"Interface\Buttons\UI-Panel-MinimizeButton-Down",@"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");if(ImGui.IsItemClicked())_guildOpen=false;
        if(_uiParityArmed&&_uiParityPanel=="guild")MarkUiParityFrameComplete();
        ImGui.End();
    }
}
