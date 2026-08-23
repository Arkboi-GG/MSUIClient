using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

/// <summary>DeathFrame.xml's death, corpse recovery, resurrection, and spirit-healer arc.</summary>
public sealed partial class GameLoop
{
    private sealed record ResurrectOffer(
        ulong Caster, string Name, bool Sickness, bool HasTimer, double ExpiresAt);

    private bool? _deathWasDead;
    private bool? _deathWasGhost;
    private bool _releaseTimerRunning;
    private double _diedAt;
    private ulong _corpseGuid;
    private CorpseLocation? _corpseLocation;
    private uint _corpseReclaimDelayMs;
    private double _corpseReclaimReadyAt;
    private ResurrectOffer? _resurrectOffer;
    private bool _releaseDialogOpen;
    private DeathDialogKind _recoverDialog;
    private ulong _spiritHealerGuid;
    private int _xpLossStage;
    private DeathDialogKind _deathPresentedKind;
    private bool _deathRezOpen;
    private Dictionary<ulong, uint> _deathDurability = [];

    private void ResetDeathRez()
    {
        _deathWasDead = null;
        _deathWasGhost = null;
        _releaseTimerRunning = false;
        _diedAt = 0;
        _corpseGuid = 0;
        _corpseLocation = null;
        _corpseReclaimDelayMs = 0;
        _corpseReclaimReadyAt = 0;
        _resurrectOffer = null;
        _releaseDialogOpen = false;
        _recoverDialog = DeathDialogKind.None;
        _spiritHealerGuid = 0;
        _xpLossStage = 0;
        _deathPresentedKind = DeathDialogKind.None;
        _deathRezOpen = false;
        _deathDurability.Clear();
    }

    private void ObserveDeathRez()
    {
        if (_net is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return;
        bool dead = player.IsDead;
        bool ghost = (player.Fields.PlayerFlags & 0x10) != 0;
        bool first = _deathWasDead is null;

        if ((first && dead && !ghost) || (_deathWasDead == false && dead && !ghost))
        {
            _diedAt = NowSeconds();
            _releaseTimerRunning = (player.Fields.PlayerFieldBytes & 0x08) != 0;
            _releaseDialogOpen = true;
            EmitInterface("death-rez", "life-state", "PLAYER_DEAD", player.Guid,
                $"releaseTimer={_releaseTimerRunning};health={player.Fields.Health};" +
                $"playerFlags=0x{player.Fields.PlayerFlags:X8}");
        }
        if ((first && ghost) || (_deathWasGhost != true && ghost))
        {
            _releaseDialogOpen = false;
            _net.CorpseQuery();
            EmitInterface("death-rez", "life-state", "PLAYER_ALIVE_GHOST", player.Guid,
                "wire=MSG_CORPSE_QUERY;body=<empty>");
        }
        if (_deathWasGhost == true && !ghost && !dead)
        {
            _recoverDialog = DeathDialogKind.None;
            _spiritHealerGuid = 0;
            _xpLossStage = 0;
            _resurrectOffer = null;
            _net.CorpseQuery();
            EmitInterface("death-rez", "life-state", "PLAYER_UNGHOST", player.Guid,
                "wire=MSG_CORPSE_QUERY;body=<empty>");
        }
        else if (_deathWasDead == true && !dead && !ghost)
        {
            _releaseDialogOpen = false;
            EmitInterface("death-rez", "life-state", "PLAYER_ALIVE", player.Guid,
                $"health={player.Fields.Health};playerFlags=0x{player.Fields.PlayerFlags:X8}");
        }

        _deathWasDead = dead;
        _deathWasGhost = ghost;
        ObserveEquippedDurability(player);
        UpdateDeathDialogs(player, ghost);
    }

    private void ObserveEquippedDurability(WorldEntity player)
    {
        Dictionary<ulong, uint> now = EnumerateEquippedDurability(player);
        if (_deathDurability.Count > 0)
            foreach ((ulong guid, uint durability) in now)
                if (_deathDurability.TryGetValue(guid, out uint before) && durability < before)
                    EmitInterface("death-rez", "durability", "DAMAGED", guid,
                        $"before={before};after={durability};loss={before - durability}");
        _deathDurability = now;
    }

    private Dictionary<ulong, uint> EnumerateEquippedDurability(WorldEntity player)
    {
        var values = new Dictionary<ulong, uint>();
        for (int i = 0; i < 19; i++)
        {
            ulong guid = player.Fields.PlayerInventorySlot(i);
            if (guid != 0 && _entities.TryGet(guid, out WorldEntity item))
                values[guid] = item.Fields.GetU32(ObjectFields.ITEM_DURABILITY) ?? 0;
        }
        return values;
    }

    private void ObserveCorpseStore()
    {
        ulong owner = LocalPlayerGuid;
        WorldEntity? corpse = _entities.Entities.Values.FirstOrDefault(x =>
            x.Type == ObjectTypeId.Corpse && x.Fields.GetGuid(6) == owner);
        if (corpse is null || corpse.Guid == _corpseGuid) return;
        _corpseGuid = corpse.Guid;
        float distance = _controller is null
            ? float.PositiveInfinity
            : Vector3.Distance(_controller.Position, corpse.Position);
        EmitInterface("death-rez", "corpse", "CREATED", corpse.Guid,
            $"owner={owner:X16};distance={distance:R};" +
            $"position={corpse.Position.X:R}|{corpse.Position.Y:R}|{corpse.Position.Z:R}");
    }

    private void ApplyCorpseQuery(byte[] body)
    {
        CorpseLocation location = DeathPackets.ParseCorpseQuery(body);
        _corpseLocation = location.Found ? location : null;
        if (!location.Found) _corpseGuid = 0;
        EmitInterface("death-rez", "corpse-query", location.Found ? "FOUND" : "NOT_FOUND",
            _corpseGuid, location.Found
                ? $"displayMap={location.DisplayMap};position={location.Position.X:R}|" +
                  $"{location.Position.Y:R}|{location.Position.Z:R};corpseMap={location.CorpseMap}"
                : "marker=cleared");
    }

    private void ApplyCorpseReclaimDelay(byte[] body)
    {
        _corpseReclaimDelayMs = DeathPackets.ParseReclaimDelay(body);
        _corpseReclaimReadyAt = NowSeconds() + _corpseReclaimDelayMs / 1000.0;
        _recoverDialog = DeathDialogKind.None; // re-arm the range edge from this new delay
        EmitInterface("death-rez", "reclaim-delay", "DISPLAYED", _corpseGuid,
            $"delayMs={_corpseReclaimDelayMs}");
    }

    private void ApplyResurrectRequest(byte[] body)
    {
        ResurrectRequestPacket request = DeathPackets.ParseResurrectRequest(body);
        double gate = request.HasTimer
            ? Math.Max(0, _corpseReclaimReadyAt - NowSeconds())
            : 0;
        _resurrectOffer = new(request.Caster, request.Name, request.Sickness,
            request.HasTimer, NowSeconds() + gate + DeathFrameUiLaw.ResurrectOfferSeconds);
        _releaseDialogOpen = false;
        EmitInterface("death-rez", "resurrect-request", "RECEIVED", request.Caster,
            $"name={SanitizeEvidence(request.Name)};sickness={request.Sickness};" +
            $"hasTimer={request.HasTimer};expiresAfter={gate + DeathFrameUiLaw.ResurrectOfferSeconds:R}");
    }

    private void ApplySpiritHealerConfirm(byte[] body)
    {
        _spiritHealerGuid = DeathPackets.ParseSpiritHealerConfirm(body);
        _xpLossStage = 1;
        ResetGossip();
        EmitInterface("death-rez", "spirit-healer-confirm", "DISPLAYED", _spiritHealerGuid,
            "stage=1;wire=none");
    }

    private void ApplyDurabilityDamageDeath(byte[] body)
    {
        if (body.Length != 0)
            throw new InvalidDataException(
                $"SMSG_DURABILITY_DAMAGE_DEATH expected empty body, got {body.Length}");
        ShowUiError("Your equipped items suffer a 10% durability loss.");
        EmitInterface("death-rez", "durability", "DEATH_WARNING", LocalPlayerGuid,
            "lossPercent=10;body=<empty>");
    }

    private void UpdateDeathDialogs(WorldEntity player, bool ghost)
    {
        double now = NowSeconds();
        if (_resurrectOffer is { } offer)
        {
            if (now >= offer.ExpiresAt)
            {
                AnswerResurrect(false, "TIMEOUT");
                _releaseDialogOpen = player.IsDead && !ghost;
            }
            else if (offer.Name.Length == 0 && TryResolveGroupLootName(offer.Caster) is { } name)
                _resurrectOffer = offer with { Name = name };
        }

        if (_spiritHealerGuid != 0)
        {
            bool inRange = _controller is not null &&
                _entities.TryGet(_spiritHealerGuid, out WorldEntity healer) &&
                Vector3.DistanceSquared(_controller.Position, healer.Position) <=
                    DeathFrameUiLaw.SpiritHealerRangeSquared;
            if (!inRange)
            {
                EmitInterface("death-rez", "spirit-healer-confirm", "CLOSED_RANGE",
                    _spiritHealerGuid, $"limit={DeathFrameUiLaw.SpiritHealerRange:R}");
                _spiritHealerGuid = 0;
                _xpLossStage = 0;
            }
        }

        _recoverDialog = DeathDialogKind.None;
        if (ghost && _corpseLocation is { } corpse && _controller is not null &&
            corpse.DisplayMap == _config.Start.Map &&
            Vector3.DistanceSquared(_controller.Position, corpse.Position) <=
                DeathFrameUiLaw.CorpseRangeSquared)
            _recoverDialog = corpse.DisplayMap == corpse.CorpseMap
                ? DeathDialogKind.RecoverCorpse
                : DeathDialogKind.RecoverCorpseInInstance;

        _deathRezOpen = CurrentDeathDialog(player.Level).Kind != DeathDialogKind.None;
    }

    private (DeathDialogKind Kind, string Text, bool AcceptEnabled) CurrentDeathDialog(uint level)
    {
        double recovery = Math.Max(0, _corpseReclaimReadyAt - NowSeconds());
        if (_spiritHealerGuid != 0 && _xpLossStage > 0)
        {
            string? sickness = DeathFrameUiLaw.SicknessDuration(level);
            DeathDialogKind kind = sickness is null
                ? DeathDialogKind.XpLossNoSickness
                : DeathDialogKind.XpLoss;
            return (kind, DeathFrameUiLaw.XpLossText(sickness, _xpLossStage == 2), true);
        }
        if (_resurrectOffer is { } offer && offer.Name.Length > 0)
        {
            DeathDialogKind kind = offer.Sickness
                ? DeathDialogKind.Resurrect
                : offer.HasTimer
                    ? DeathDialogKind.ResurrectNoSickness
                    : DeathDialogKind.ResurrectNoTimer;
            double gate = offer.HasTimer ? recovery : 0;
            return (kind, DeathFrameUiLaw.ResurrectText(kind, offer.Name, gate),
                DeathFrameUiLaw.AcceptEnabled(kind, gate));
        }
        if (_recoverDialog == DeathDialogKind.RecoverCorpse)
            return (_recoverDialog, DeathFrameUiLaw.RecoverText(recovery), recovery <= 0);
        if (_recoverDialog == DeathDialogKind.RecoverCorpseInInstance)
            return (_recoverDialog, "You must enter the instance to recover your corpse", false);
        if (_releaseDialogOpen)
        {
            double left = Math.Max(0,
                DeathFrameUiLaw.ReleaseWindowSeconds - (NowSeconds() - _diedAt));
            return (DeathDialogKind.Release,
                DeathFrameUiLaw.ReleaseText(_releaseTimerRunning, left), true);
        }
        return (DeathDialogKind.None, "", false);
    }

    private bool RequestRepop()
    {
        bool eligible = _net is { IsInWorld: true } &&
            _entities.TryGet(_net.PlayerGuid, out WorldEntity player) &&
            player.IsDead && (player.Fields.PlayerFlags & 0x10) == 0;
        bool sent = eligible && _net!.RepopRequest();
        if (sent) _releaseDialogOpen = false;
        EmitInterface("death-rez", "repop", sent ? "SENT" : "REFUSED_NOT_DEAD",
            _net?.PlayerGuid ?? 0, $"eligible={eligible};body=<empty>");
        return sent;
    }

    private bool ReclaimCorpse()
    {
        bool timerReady = NowSeconds() >= _corpseReclaimReadyAt;
        bool sent = timerReady && _net?.ReclaimCorpse(_corpseGuid) == true;
        EmitInterface("death-rez", "reclaim",
            sent ? "SENT" : timerReady ? "SEND_FAILED" : "REFUSED_DELAY", _corpseGuid,
            $"delayMs={_corpseReclaimDelayMs};remainingMs=" +
            $"{(uint)Math.Max(0, (_corpseReclaimReadyAt - NowSeconds()) * 1000)};" +
            $"body={Convert.ToHexString(WorldSession.BuildReclaimCorpseBody(_corpseGuid))}");
        return sent;
    }

    private bool AnswerResurrect(bool accept, string source = "CLICK")
    {
        if (_resurrectOffer is not { } offer || _net is null) return false;
        bool sent = _net.ResurrectResponse(offer.Caster, accept);
        EmitInterface("death-rez", "resurrect-response",
            sent ? accept ? "ACCEPT_SENT" : "DECLINE_SENT" : "SEND_FAILED", offer.Caster,
            $"source={source};body={Convert.ToHexString(
                WorldSession.BuildResurrectResponseBody(offer.Caster, accept))}");
        if (sent)
        {
            _resurrectOffer = null;
            if (!accept && _deathWasDead == true && _deathWasGhost != true)
                _releaseDialogOpen = true;
        }
        return sent;
    }

    private void AcceptXpLoss()
    {
        if (_spiritHealerGuid == 0 || _net is null) return;
        ulong healer = _spiritHealerGuid;
        bool sent = _net.SpiritHealerActivate(healer);
        EmitInterface("death-rez", "spirit-healer", sent ? "ACCEPT_SENT" : "SEND_FAILED",
            healer, $"body={Convert.ToHexString(WorldSession.BuildSpiritHealerBody(healer))}");
        if (sent)
        {
            _spiritHealerGuid = 0;
            _xpLossStage = 0;
        }
    }

    private bool TryDismissDeathConfirmationOnEscape()
    {
        uint level = _entities.TryGet(LocalPlayerGuid, out WorldEntity player) ? player.Level : 0;
        DeathDialogKind kind = CurrentDeathDialog(level).Kind;
        if (kind == DeathDialogKind.None || !DeathFrameUiLaw.HideOnEscape(kind)) return false;
        if (kind is DeathDialogKind.Resurrect or DeathDialogKind.ResurrectNoSickness or
            DeathDialogKind.ResurrectNoTimer)
            AnswerResurrect(false, "ESCAPE");
        else
        {
            _spiritHealerGuid = 0;
            _xpLossStage = 0;
            EmitInterface("death-rez", "spirit-healer-confirm", "CANCELLED_ESCAPE", 0,
                "wire=none");
        }
        return true;
    }

    private void SimulateDeathRezFlow()
    {
        _corpseGuid = 0xF101000000001234;
        _corpseLocation = new(true, _config.Start.Map, _controller?.Position ?? Vector3.Zero,
            checked((uint)Math.Max(0, _config.Start.Map)));
        ApplyCorpseReclaimDelay(BitConverter.GetBytes(30_000u));
        _corpseReclaimReadyAt = NowSeconds();
        var writer = new PacketWriter();
        writer.WriteU64(0x1234);
        byte[] name = System.Text.Encoding.UTF8.GetBytes("Nighthealer");
        writer.WriteU32((uint)name.Length + 1);
        writer.WriteBytes(name);
        writer.WriteU8(0);
        writer.WriteU8(0);
        writer.WriteU8(1);
        ApplyResurrectRequest(writer.ToArray());
        EmitInterface("death-rez", "replay", "READY", _corpseGuid,
            "corpseQuery=true;reclaimDelay=true;resurrect=true");
    }

    private void DrawDeathRezFrame()
    {
        if (_skin is null || !_entities.TryGet(LocalPlayerGuid, out WorldEntity player))
        {
            _deathRezOpen = false;
            return;
        }
        UpdateDeathDialogs(player, (player.Fields.PlayerFlags & 0x10) != 0);
        var dialog = CurrentDeathDialog(player.Level);
        _deathRezOpen = dialog.Kind != DeathDialogKind.None;
        UpdateDeathDialogSound(dialog.Kind);
        if (!_deathRezOpen) return;

        float scale = GameplayUiScale();
        bool alert = dialog.Kind is DeathDialogKind.XpLoss or DeathDialogKind.XpLossNoSickness;
        string[] lines = WrapTooltipText(dialog.Text, "GameFontHighlight", scale,
            DeathFrameUiLaw.TextWidth * scale).ToArray();
        float pitch = GameText.LinePitch("GameFontHighlight", 1f);
        float textHeight = Math.Max(pitch, lines.Length * pitch);
        DeathFrameUiLaw.ScreenRect frame = DeathFrameUiLaw.PopupRect(
            ImGui.GetIO().DisplaySize, scale, textHeight, alert);

        ImGui.SetNextWindowPos(frame.Min, ImGuiCond.Always);
        ImGui.SetNextWindowSize(frame.Size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav;
        bool begun = ImGui.Begin($"##death-dialog-{dialog.Kind}", flags);
        ImGui.PopStyleVar(2);
        if (!begun) { ImGui.End(); return; }

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        _skin.DrawBackdrop(draw, frame.Min, frame.Min + frame.Size, WowSkin.Dialog);
        float logicalWidth = alert ? DeathFrameUiLaw.AlertPopupWidth : DeathFrameUiLaw.PopupWidth;
        for (int i = 0; i < lines.Length; i++)
            GameText.DrawCentered(draw, "GameFontHighlight", lines[i],
                frame.Min + DeathFrameUiLaw.TextCenter(logicalWidth,
                    (i + .5f) * pitch) * scale, scale);
        if (alert)
            DrawArt(draw, DeathFrameUiLaw.AlertIconPath,
                frame.Min + DeathFrameUiLaw.AlertIconMin(frame.Size.Y / scale) * scale,
                DeathFrameUiLaw.AlertIconDimensions, scale);

        bool accept = false;
        bool cancel = false;
        int buttons = DeathButtonCount(dialog.Kind);
        if (buttons >= 1)
            accept = DrawDeathDialogButton(draw, 1, buttons, DeathPrimaryButton(dialog.Kind),
                frame.Min + DeathFrameUiLaw.ButtonMin(1, buttons, logicalWidth, textHeight) * scale,
                scale, dialog.AcceptEnabled);
        if (buttons == 2)
            cancel = DrawDeathDialogButton(draw, 2, buttons, DeathSecondaryButton(dialog.Kind),
                frame.Min + DeathFrameUiLaw.ButtonMin(2, buttons, logicalWidth, textHeight) * scale,
                scale, true);
        ImGui.End();

        if (accept) AcceptDeathDialog(dialog.Kind);
        else if (cancel) CancelDeathDialog(dialog.Kind);
    }

    private void UpdateDeathDialogSound(DeathDialogKind next)
    {
        if (next == _deathPresentedKind) return;
        if (_deathPresentedKind != DeathDialogKind.None)
            PlayUiSound("igMainMenuClose", "ui.death");
        if (next != DeathDialogKind.None)
            PlayUiSound("igMainMenuOpen", "ui.death");
        _deathPresentedKind = next;
    }

    private static int DeathButtonCount(DeathDialogKind kind) => kind switch
    {
        DeathDialogKind.None or DeathDialogKind.RecoverCorpseInInstance => 0,
        DeathDialogKind.Release or DeathDialogKind.RecoverCorpse => 1,
        _ => 2,
    };

    private static string DeathPrimaryButton(DeathDialogKind kind) =>
        kind == DeathDialogKind.Release ? DeathFrameUiLaw.ReleaseButton : DeathFrameUiLaw.AcceptButton;

    private static string DeathSecondaryButton(DeathDialogKind kind) =>
        kind is DeathDialogKind.XpLoss or DeathDialogKind.XpLossNoSickness
            ? DeathFrameUiLaw.CancelButton
            : DeathFrameUiLaw.DeclineButton;

    private void AcceptDeathDialog(DeathDialogKind kind)
    {
        switch (kind)
        {
            case DeathDialogKind.Release:
                RequestRepop();
                break;
            case DeathDialogKind.RecoverCorpse:
                ReclaimCorpse(); // stays visible until descriptor deltas confirm resurrection
                break;
            case DeathDialogKind.Resurrect:
            case DeathDialogKind.ResurrectNoSickness:
            case DeathDialogKind.ResurrectNoTimer:
                AnswerResurrect(true);
                break;
            case DeathDialogKind.XpLoss:
            case DeathDialogKind.XpLossNoSickness:
                if (_xpLossStage == 1)
                {
                    _xpLossStage = 2;
                    EmitInterface("death-rez", "spirit-healer-confirm", "SECOND_ASK",
                        _spiritHealerGuid, "wire=none");
                }
                else AcceptXpLoss();
                break;
        }
    }

    private void CancelDeathDialog(DeathDialogKind kind)
    {
        if (kind is DeathDialogKind.Resurrect or DeathDialogKind.ResurrectNoSickness or
            DeathDialogKind.ResurrectNoTimer)
            AnswerResurrect(false);
        else
        {
            ulong healer = _spiritHealerGuid;
            _spiritHealerGuid = 0;
            _xpLossStage = 0;
            EmitInterface("death-rez", "spirit-healer-confirm", "CANCELLED", healer,
                "wire=none");
        }
    }

    private bool DrawDeathDialogButton(ImDrawListPtr draw, int index, int buttonCount,
        string caption, Vector2 min, float scale, bool enabled)
    {
        Vector2 size = DeathFrameUiLaw.ButtonSize(scale);
        ImGui.SetCursorScreenPos(min);
        bool clicked = ImGui.InvisibleButton($"##death-dialog-{index}-{buttonCount}", size) && enabled;
        bool active = enabled && ImGui.IsItemActive();
        bool hovered = enabled && ImGui.IsItemHovered();
        uint art = _skin!.TextureHandle(active ? "dialog.button.down" : "dialog.button.up");
        if (art != 0)
            draw.AddImage((nint)art, min, min + size, Vector2.Zero,
                DeathFrameUiLaw.DialogButtonUvMax,
                enabled ? 0xffffffff : 0xff777777);
        if (hovered)
        {
            uint highlight = _gameplayArt?.BrightHighlightHandle(
                @"Interface\Buttons\UI-DialogBox-Button-Highlight") ?? 0;
            if (highlight != 0)
                draw.AddImage((nint)highlight, min, min + size, Vector2.Zero,
                    DeathFrameUiLaw.DialogButtonUvMax);
        }
        GameText.DrawCentered(draw,
            enabled ? hovered ? "GameFontHighlight" : "GameFontNormal" : "GameFontDisable",
            caption, min + size * .5f, scale);
        return clicked;
    }
}
