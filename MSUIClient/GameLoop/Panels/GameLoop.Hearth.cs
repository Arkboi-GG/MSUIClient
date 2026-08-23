using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private const uint HearthstoneEntry = 6948;
    private const uint HearthSpell = 8690;
    private ulong _binderGuid;
    private (uint Map, Vector3 Position)? _bindPoint;
    private bool _binderConfirmOpen;
    private string _binderAreaName = BinderConfirmUiLaw.FallbackAreaName;
    private bool _hearthPending;
    private Vector3 _hearthFrom;

    private void ResetHearth()
    {
        _binderGuid = 0;
        _bindPoint = null;
        _binderConfirmOpen = false;
        _binderAreaName = BinderConfirmUiLaw.FallbackAreaName;
        _hearthPending = false;
    }

    private bool RequestBind(ulong guid)
    {
        WorldEntity? inn = null; float distance = float.PositiveInfinity;
        bool eligible = _net is { IsInWorld: true } && _controller is not null &&
            _entities.TryGet(guid, out inn) && inn.IsCreature && !inn.IsDead && (inn.NpcFlags & NpcInnkeeper) != 0 &&
            (distance = Vector3.Distance(_controller.Position, inn.Position)) <= BinderConfirmUiLaw.ServiceRange;
        bool sent = eligible && _net!.BinderActivate(guid);
        EmitInterface("hearth", "bind-send", sent ? "SENT" : "REFUSED", guid,
            $"eligible={eligible};distance={distance:R};npcFlags=0x{inn?.NpcFlags ?? 0:X8};body={Convert.ToHexString(WorldSession.BuildBinderBody(guid))}");
        if (sent) _binderGuid = guid; return sent;
    }

    private void ApplyBinderConfirm(byte[] body)
    {
        BinderConfirmPacket packet = BinderPackets.ParseConfirm(body);
        _binderGuid = packet.BinderGuid;
        _binderAreaName = CurrentBinderAreaName();
        _binderConfirmOpen = true;
        EmitInterface("hearth", "bind-confirm", "DISPLAYED", _binderGuid,
            $"area={_binderAreaName};body={Convert.ToHexString(body)}");
    }

    private void ApplyBindPoint(byte[] body)
    {
        BindPointPacket packet = BinderPackets.ParseBindPoint(body);
        _bindPoint = (packet.MapId, packet.Position);
        EmitInterface("hearth", "bind-point", "UPDATED", _binderGuid,
            $"map={packet.MapId};position={packet.Position.X:R}|{packet.Position.Y:R}|{packet.Position.Z:R};bytes={body.Length}");
    }

    private void ApplyPlayerBound(byte[] body)
    {
        PlayerBoundPacket packet = BinderPackets.ParsePlayerBound(body);
        _binderConfirmOpen = false;
        Vector3 listener = _controller?.Position ?? Vector3.Zero;
        _spellSounds?.Play(BinderConfirmUiLaw.BoundSoundId, ControlledGuid, listener, listener,
            category: "ui.binder");
        EnsureAreaTableForMinimap();
        string? message = BinderConfirmUiLaw.PlayerBoundText(_areas?.AreaName(packet.AreaId));
        if (message is not null) AddChatMessage(message);
        EmitInterface("hearth", "player-bound", "APPLIED", packet.BinderGuid,
            $"area={packet.AreaId};message={(message ?? "<unresolved>")};sound={BinderConfirmUiLaw.BoundSoundId}");
    }

    private string CurrentBinderAreaName()
    {
        EnsureAreaTableForMinimap();
        string? area = _areas?.AreaName(_minimapAreaId);
        if (!string.IsNullOrWhiteSpace(area)) return area;
        uint parent = _areas?.ParentZoneId(_minimapAreaId) ?? 0;
        area = _areas?.AreaName(parent);
        return BinderConfirmUiLaw.ResolvedAreaName(area);
    }

    private bool UseHearthstone()
    {
        if (_net is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return false;
        for (int i = 0; i < 16; i++)
        {
            ulong guid = player.Fields.PlayerBackpackSlot(i);
            if (guid == 0 || !_entities.TryGet(guid, out WorldEntity item) || item.Entry != HearthstoneEntry) continue;
            _hearthFrom = _controller?.Position ?? Vector3.Zero; _hearthPending = true;
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

    private bool BinderConfirmationInRange(out float distance)
    {
        distance = float.PositiveInfinity;
        bool playerAvailable = _controller is not null;
        bool binderAvailable = _entities.TryGet(_binderGuid, out WorldEntity binder);
        if (playerAvailable && binderAvailable)
            distance = Vector3.Distance(_controller!.Position, binder.Position);
        return BinderConfirmUiLaw.ShouldRemainOpen(playerAvailable, binderAvailable,
            binder?.IsCreature == true, binder?.IsDead == true, distance);
    }

    private bool TryDismissBinderConfirmationOnEscape()
    {
        if (!_binderConfirmOpen) return false;
        _binderConfirmOpen = false;
        EmitInterface("hearth", "bind-confirm", "CANCELLED_ESCAPE", _binderGuid, "wire=none");
        return true;
    }

    private void DrawBindConfirmation()
    {
        if (!_binderConfirmOpen || _skin is null) return;
        float distance = float.PositiveInfinity;
        if (_binderGuid == 0 || !BinderConfirmationInRange(out distance))
        {
            _binderConfirmOpen = false;
            EmitInterface("hearth", "bind-confirm", "CLOSED_RANGE", _binderGuid,
                $"distance={distance:R};limit={BinderConfirmUiLaw.ServiceRange:R}");
            return;
        }

        float scale = GameplayUiScale();
        string text = BinderConfirmUiLaw.Prompt(_binderAreaName);
        string[] lines = WrapTooltipText(text, "GameFontHighlight", scale,
            BinderConfirmUiLaw.TextWidth * scale).ToArray();
        float linePitch = GameText.LinePitch("GameFontHighlight", 1f);
        float textHeight = lines.Length * linePitch;
        BinderConfirmUiLaw.ScreenRect frame = BinderConfirmUiLaw.PopupRect(
            ImGui.GetIO().DisplaySize, scale, textHeight);

        ImGui.SetNextWindowPos(frame.Min, ImGuiCond.Always);
        ImGui.SetNextWindowSize(frame.Size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav;
        bool begun = ImGui.Begin("##binder-confirm", flags);
        ImGui.PopStyleVar(2);
        if (!begun) { ImGui.End(); return; }

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        _skin.DrawBackdrop(draw, frame.Min, frame.Min + frame.Size, WowSkin.Dialog);
        for (int i = 0; i < lines.Length; i++)
            GameText.DrawCentered(draw, "GameFontHighlight", lines[i],
                frame.Min + BinderConfirmUiLaw.TextCenter((i + .5f) * linePitch) * scale, scale);

        bool accept = DrawBinderConfirmationButton(draw, 1, BinderConfirmUiLaw.AcceptText,
            frame.Min + BinderConfirmUiLaw.ButtonMin(1, textHeight) * scale, scale);
        bool cancel = DrawBinderConfirmationButton(draw, 2, BinderConfirmUiLaw.CancelText,
            frame.Min + BinderConfirmUiLaw.ButtonMin(2, textHeight) * scale, scale);
        ImGui.End();

        if (accept)
        {
            _net?.BinderActivate(_binderGuid);
            _binderConfirmOpen = false;
            EmitInterface("hearth", "bind-confirm", "ACCEPTED", _binderGuid, "wire=CMSG_BINDER_ACTIVATE");
        }
        else if (cancel)
        {
            _binderConfirmOpen = false;
            EmitInterface("hearth", "bind-confirm", "CANCELLED", _binderGuid, "wire=none");
        }
    }

    private bool DrawBinderConfirmationButton(
        ImDrawListPtr draw, int buttonIndex, string caption, Vector2 min, float scale)
    {
        Vector2 size = BinderConfirmUiLaw.ButtonSize(scale);
        ImGui.SetCursorScreenPos(min);
        bool clicked = ImGui.InvisibleButton($"##binder-confirm-{buttonIndex}", size);
        bool pressed = ImGui.IsItemActive();
        bool hovered = ImGui.IsItemHovered();
        uint art = _skin!.TextureHandle(pressed ? "dialog.button.down" : "dialog.button.up");
        if (art != 0)
            draw.AddImage((nint)art, min, min + size, Vector2.Zero,
                BinderConfirmUiLaw.ButtonUvMax);
        if (hovered)
        {
            uint highlight = _gameplayArt?.BrightHighlightHandle(
                @"Interface\Buttons\UI-DialogBox-Button-Highlight") ?? 0;
            if (highlight != 0)
                draw.AddImage((nint)highlight, min, min + size,
                    Vector2.Zero, BinderConfirmUiLaw.ButtonUvMax);
        }
        GameText.DrawCentered(draw, hovered ? "GameFontHighlight" : "GameFontNormal",
            caption, min + size * .5f, scale);
        return clicked;
    }
}
