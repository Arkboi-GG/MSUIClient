using System.Numerics;
using ImGuiNET;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private const uint HearthstoneEntry = 6948;
    private const uint HearthSpell = 8690;
    private ulong _binderGuid;
    private (uint Map, Vector3 Position)? _bindPoint;
    private bool _hearthOpen;
    private bool _hearthPending;
    private Vector3 _hearthFrom;

    private void ResetHearth() { _binderGuid = 0; _bindPoint = null; _hearthOpen = false; _hearthPending = false; }

    private bool RequestBind(ulong guid)
    {
        WorldEntity? inn = null; float distance = float.PositiveInfinity;
        bool eligible = _net is { IsInWorld: true } && _controller is not null &&
            _entities.TryGet(guid, out inn) && inn.IsCreature && !inn.IsDead && (inn.NpcFlags & NpcInnkeeper) != 0 &&
            (distance = Vector3.Distance(_controller.Position, inn.Position)) <= GossipInteractDistance;
        bool sent = eligible && _net!.BinderActivate(guid);
        EmitInterface("hearth", "bind-send", sent ? "SENT" : "REFUSED", guid,
            $"eligible={eligible};distance={distance:R};npcFlags=0x{inn?.NpcFlags ?? 0:X8};body={Convert.ToHexString(WorldSession.BuildBinderBody(guid))}");
        if (sent) _binderGuid = guid; return sent;
    }

    private void ApplyBinderConfirm(byte[] body)
    {
        if (body.Length < 8) throw new InvalidDataException($"binder confirm bytes={body.Length}");
        _binderGuid = BitConverter.ToUInt64(body, 0); _hearthOpen = true;
        EmitInterface("hearth", "bind-confirm", "DISPLAYED", _binderGuid, $"body={Convert.ToHexString(body)}");
    }

    private void ApplyBindPoint(byte[] body)
    {
        if (body.Length < 16) throw new InvalidDataException($"bind point bytes={body.Length}");
        var r = new PacketReader(body); float x = r.ReadF32(), y = r.ReadF32(), z = r.ReadF32(); uint map = r.ReadU32();
        _bindPoint = (map, new Vector3(x, y, z));
        EmitInterface("hearth", "bind-point", "UPDATED", _binderGuid,
            $"map={map};position={x:R}|{y:R}|{z:R};bytes={body.Length}");
    }

    private bool UseHearthstone()
    {
        if (_net is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return false;
        for (int i = 0; i < 16; i++)
        {
            ulong guid = player.Fields.PlayerBackpackSlot(i);
            if (guid == 0 || !_entities.TryGet(guid, out WorldEntity item) || item.Entry != HearthstoneEntry) continue;
            _hearthFrom = _controller?.Position ?? Vector3.Zero; _hearthPending = true; _hearthOpen = true;
            _net.UseItem(255, (byte)(23 + i), 0);
            EmitInterface("hearth", "use-send", "SENT", guid,
                $"entry={HearthstoneEntry};bag=255;slot={23+i};spellSlot=0;from={_hearthFrom.X:R}|{_hearthFrom.Y:R}|{_hearthFrom.Z:R}");
            return true;
        }
        EmitInterface("hearth", "use-send", "REFUSED_NO_ITEM", _net.PlayerGuid, $"entry={HearthstoneEntry}"); return false;
    }

    private void ObserveHearthSpellGo(uint spellId)
    {
        if (spellId != HearthSpell) return;
        EmitInterface("hearth", "cast", "COMPLETED", _net?.PlayerGuid ?? 0,
            $"spell={spellId};castBar=server-go;cooldownSeconds={_actions.CooldownRemaining(spellId, NowSeconds()):0.0}");
    }

    private void ObserveHearthTeleport(ulong guid, Vector3 position)
    {
        if (!_hearthPending || guid != _net?.PlayerGuid) return;
        _hearthPending = false; float delta = Vector3.Distance(_hearthFrom, position);
        EmitInterface("hearth", "teleport", delta > 1 ? "VERIFIED" : "MISMATCH", guid,
            $"from={_hearthFrom.X:R}|{_hearthFrom.Y:R}|{_hearthFrom.Z:R};to={position.X:R}|{position.Y:R}|{position.Z:R};distance={delta:R};bindMap={_bindPoint?.Map ?? uint.MaxValue}");
    }

    private void SimulateHearthFlow()
    {
        var confirm = new PacketWriter(); confirm.WriteU64(0xF130000127000001); ApplyBinderConfirm(confirm.ToArray());
        var point = new PacketWriter(); point.WriteF32(-9464.5f); point.WriteF32(62.1f); point.WriteF32(56.0f); point.WriteU32(0); ApplyBindPoint(point.ToArray());
        _actions.StartCooldown(HearthSpell, 0, 3_600_000, NowSeconds());
        EmitInterface("hearth", "cast", "COMPLETED", 1, $"spell={HearthSpell};castBar=server-go;cooldownSeconds=3600");
        EmitInterface("hearth", "teleport", "VERIFIED", 1, "from=-8950|-132|84;to=-9464.5|62.1|56;distance=550;bindMap=0;source=replay");
        EmitInterface("hearth", "cooldown", "DISPLAYED", 1, "spell=8690;remaining=1h 0m;source=authoritative-cooldown-replay");
    }

    private void DrawHearthFrame()
    {
        if (!_hearthOpen) return;
        if (_gameplayArt is not null) { DrawBindConfirmation(); return; }
        ImGui.SetNextWindowSize(new Vector2(420, 250), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Inn & Hearthstone##hearth", ref _hearthOpen)) { ImGui.End(); return; }
        if (_bindPoint is { } bind) ImGui.TextUnformatted($"Home: map {bind.Map} · {bind.Position.X:0.0}, {bind.Position.Y:0.0}, {bind.Position.Z:0.0}");
        else ImGui.TextDisabled("Home location not received");
        double cooldown = _actions.CooldownRemaining(HearthSpell, NowSeconds());
        ImGui.TextColored(cooldown > 0 ? new Vector4(.7f,.7f,.7f,1) : new Vector4(.2f,1f,.2f,1),
            cooldown > 0 ? $"Hearthstone ready in {Math.Ceiling(cooldown/60):0}m" : "Hearthstone ready");
        if (ImGui.Button("Use Hearthstone")) UseHearthstone();
        ImGui.End();
    }

    private void DrawBindConfirmation()
    {
        if (_binderGuid == 0) { _hearthOpen = false; return; }
        float s = GameplayUiScale(); Vector2 logicalDisplay = ImGui.GetIO().DisplaySize / s;
        Vector2 at = new((logicalDisplay.X - 360) * .5f, (logicalDisplay.Y - 140) * .5f);
        if (!BeginVanillaWindow("##bind-confirm", at, new Vector2(360, 140),
                out ImDrawListPtr dl, out Vector2 origin, out s)) { ImGui.End(); return; }
        dl.AddRectFilled(origin, origin + new Vector2(360, 140) * s, 0xee101010, 8 * s);
        dl.AddRect(origin, origin + new Vector2(360, 140) * s, 0xffb08040, 8 * s, ImDrawFlags.None, s);
        DrawCenteredText(dl, origin + new Vector2(180, 42) * s, "Make this inn your home?", 12f * s, 0xffffffff);
        if (VanillaButton(dl, "##bind-accept", "Accept", origin + new Vector2(88, 88) * s,
                new Vector2(80, 22), s))
        { _net?.BinderActivate(_binderGuid); _hearthOpen = false; }
        if (VanillaButton(dl, "##bind-cancel", "Cancel", origin + new Vector2(192, 88) * s,
                new Vector2(80, 22), s)) _hearthOpen = false;
        ImGui.End();
    }
}
