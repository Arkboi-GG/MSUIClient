using System.Numerics;
using ImGuiNET;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private sealed record ResurrectOffer(ulong Caster, string Name, byte Type, uint Health, uint Mana);
    private bool? _deathWasDead;
    private ulong _corpseGuid;
    private uint _corpseReclaimDelayMs;
    private double _corpseReclaimReadyAt;
    private ResurrectOffer? _resurrectOffer;
    private bool _deathRezOpen;
    private Dictionary<ulong, uint> _deathDurability = [];

    private void ResetDeathRez()
    {
        _deathWasDead = null; _corpseGuid = 0; _corpseReclaimDelayMs = 0;
        _corpseReclaimReadyAt = 0; _resurrectOffer = null; _deathRezOpen = false; _deathDurability.Clear();
    }

    private void ObserveDeathRez()
    {
        if (_net is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity p)) return;
        bool dead = p.IsDead;
        if (_deathWasDead is null || _deathWasDead != dead)
        {
            EmitInterface("death-rez", "life-state", dead ? "DEAD" : "ALIVE", p.Guid,
                $"health={p.Fields.Health};flags=0x{p.Fields.UnitFlags:X8};corpse=0x{_corpseGuid:X16}");
            _deathRezOpen = dead || _deathWasDead == true;
        }
        _deathWasDead = dead;
        var now = EnumerateEquippedDurability(p);
        if (_deathDurability.Count > 0)
            foreach ((ulong guid, uint durability) in now)
                if (_deathDurability.TryGetValue(guid, out uint before) && durability < before)
                    EmitInterface("death-rez", "durability", "DAMAGED", guid,
                        $"before={before};after={durability};loss={before-durability}");
        _deathDurability = now;
    }

    private Dictionary<ulong, uint> EnumerateEquippedDurability(WorldEntity player)
    {
        var values = new Dictionary<ulong, uint>();
        for (int i = 0; i < 19; i++)
        {
            ulong guid = player.Fields.PlayerInventorySlot(i);
            if (guid != 0 && _entities.TryGet(guid, out WorldEntity item)) values[guid] = item.Fields.GetU32(ObjectFields.ITEM_DURABILITY) ?? 0;
        }
        return values;
    }

    private void ObserveCorpseStore()
    {
        WorldEntity? corpse = _entities.Entities.Values.FirstOrDefault(x => x.Type == ObjectTypeId.Corpse);
        if (corpse is null || corpse.Guid == _corpseGuid) return;
        _corpseGuid = corpse.Guid;
        float distance = _controller is null ? float.PositiveInfinity : Vector3.Distance(_controller.Position, corpse.Position);
        EmitInterface("death-rez", "corpse", "CREATED", corpse.Guid,
            $"distance={distance:R};position={corpse.Position.X:R}|{corpse.Position.Y:R}|{corpse.Position.Z:R}");
    }

    private bool RequestRepop()
    {
        bool eligible = _net is { IsInWorld: true } && _entities.TryGet(_net.PlayerGuid, out WorldEntity p) && p.IsDead;
        bool sent = eligible && _net!.RepopRequest();
        EmitInterface("death-rez", "repop", sent ? "SENT" : "REFUSED_NOT_DEAD", _net?.PlayerGuid ?? 0,
            $"eligible={eligible};body=<empty>"); return sent;
    }

    private bool ReclaimCorpse()
    {
        bool timer = NowSeconds() >= _corpseReclaimReadyAt;
        bool sent = _corpseGuid != 0 && timer && _net?.ReclaimCorpse(_corpseGuid) == true;
        EmitInterface("death-rez", "reclaim", sent ? "SENT" : _corpseGuid == 0 ? "REFUSED_NO_CORPSE" : "REFUSED_DELAY", _corpseGuid,
            $"delayMs={_corpseReclaimDelayMs};remainingMs={(uint)Math.Max(0, (_corpseReclaimReadyAt-NowSeconds())*1000)};body={Convert.ToHexString(WorldSession.BuildReclaimCorpseBody(_corpseGuid))}");
        return sent;
    }

    private void ApplyCorpseReclaimDelay(byte[] body)
    {
        if (body.Length < 4) throw new InvalidDataException($"reclaim delay bytes={body.Length}");
        _corpseReclaimDelayMs = BitConverter.ToUInt32(body, 0); _corpseReclaimReadyAt = NowSeconds() + _corpseReclaimDelayMs / 1000.0;
        EmitInterface("death-rez", "reclaim-delay", "DISPLAYED", _corpseGuid, $"delayMs={_corpseReclaimDelayMs}");
    }

    private void ApplyResurrectRequest(byte[] body)
    {
        var r = new PacketReader(body); ulong caster = r.ReadU64(); string name = r.ReadCString();
        byte type = r.Remaining >= 1 ? r.ReadU8() : (byte)0; uint health = r.Remaining >= 4 ? r.ReadU32() : 0;
        uint mana = r.Remaining >= 4 ? r.ReadU32() : 0;
        _resurrectOffer = new(caster, name, type, health, mana); _deathRezOpen = true;
        EmitInterface("death-rez", "resurrect-request", "DISPLAYED", caster,
            $"name={SanitizeEvidence(name)};type={type};health={health};mana={mana};bytes={body.Length}");
    }

    private bool AnswerResurrect(bool accept)
    {
        if (_resurrectOffer is null || _net is null) return false;
        bool sent = _net.ResurrectResponse(_resurrectOffer.Caster, accept);
        EmitInterface("death-rez", "resurrect-response", sent ? (accept ? "ACCEPT_SENT" : "DECLINE_SENT") : "SEND_FAILED", _resurrectOffer.Caster,
            $"body={Convert.ToHexString(WorldSession.BuildResurrectResponseBody(_resurrectOffer.Caster, accept))}");
        if (sent) _resurrectOffer = null; return sent;
    }

    private void SimulateDeathRezFlow()
    {
        _corpseGuid = 0xF101000000001234; _deathRezOpen = true;
        EmitInterface("death-rez", "corpse", "CREATED", _corpseGuid, "distance=22;position=1|2|3;source=replay");
        ApplyCorpseReclaimDelay(BitConverter.GetBytes(30000u)); _corpseReclaimReadyAt = NowSeconds();
        EmitInterface("death-rez", "reclaim", "SERVER_ACCEPTED", _corpseGuid, $"body={Convert.ToHexString(WorldSession.BuildReclaimCorpseBody(_corpseGuid))}");
        var w = new PacketWriter(); w.WriteU64(0x1234); w.WriteCString("Nighthealer"); w.WriteU8(0); w.WriteU32(1304); w.WriteU32(0); ApplyResurrectRequest(w.ToArray());
        EmitInterface("death-rez", "spirit-healer", "SERVER_ACCEPTED", 0xF13000195B015407,
            $"sicknessSpell=15007;durabilityLossPercent=25;body={Convert.ToHexString(WorldSession.BuildSpiritHealerBody(0xF13000195B015407))}");
        EmitInterface("death-rez", "durability", "DAMAGED", 0x4000001, "before=100;after=75;loss=25;source=spirit-healer-replay");
    }

    private void DrawDeathRezFrame()
    {
        if (!_deathRezOpen) return;
        if (_gameplayArt is not null) { DrawDeathRezPopup(); return; }
        ImGui.SetNextWindowSize(new Vector2(430, 270), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Death & Resurrection##death-rez", ref _deathRezOpen)) { ImGui.End(); return; }
        ImGui.TextColored(new Vector4(.8f, .8f, .8f, 1f), _deathWasDead == true ? "You are dead" : "Resurrection flow");
        if (_corpseGuid != 0)
        {
            uint left = (uint)Math.Max(0, (_corpseReclaimReadyAt - NowSeconds()) * 1000);
            ImGui.TextUnformatted($"Corpse 0x{_corpseGuid:X16}"); ImGui.TextDisabled($"Reclaim in {left/1000f:0.0}s");
            if (ImGui.Button("Reclaim Corpse")) ReclaimCorpse();
        }
        if (_resurrectOffer is not null)
        {
            ImGui.Separator(); ImGui.TextUnformatted($"{_resurrectOffer.Name} offers resurrection");
            ImGui.TextDisabled($"Health {_resurrectOffer.Health} · Mana {_resurrectOffer.Mana}");
            if (ImGui.Button("Accept")) AnswerResurrect(true); ImGui.SameLine(); if (ImGui.Button("Decline")) AnswerResurrect(false);
        }
        ImGui.End();
    }

    private void DrawDeathRezPopup()
    {
        float s = GameplayUiScale(); Vector2 logicalDisplay = ImGui.GetIO().DisplaySize / s;
        Vector2 at = new((logicalDisplay.X - 384) * .5f, (logicalDisplay.Y - 170) * .5f);
        if (!BeginVanillaWindow("##death-rez-popup", at, new Vector2(384, 170),
                out ImDrawListPtr dl, out Vector2 origin, out s)) { ImGui.End(); return; }
        dl.AddRectFilled(origin, origin + new Vector2(384, 170) * s, 0xee101010, 8 * s);
        dl.AddRect(origin, origin + new Vector2(384, 170) * s, 0xffb08040, 8 * s, ImDrawFlags.None, s);
        DrawCenteredText(dl, origin + new Vector2(192, 28) * s,
            _resurrectOffer is not null ? $"{_resurrectOffer.Name} offers resurrection" : "You are dead",
            12f * s, 0xffffffff);
        if (_resurrectOffer is not null)
        {
            DrawCenteredText(dl, origin + new Vector2(192, 58) * s,
                $"Health {_resurrectOffer.Health}  Mana {_resurrectOffer.Mana}", 10f * s, 0xffaaaaaa);
            if (VanillaButton(dl, "##rez-accept", "Accept", origin + new Vector2(94, 105) * s,
                    new Vector2(80, 22), s)) AnswerResurrect(true);
            if (VanillaButton(dl, "##rez-decline", "Decline", origin + new Vector2(210, 105) * s,
                    new Vector2(80, 22), s)) AnswerResurrect(false);
        }
        else if (_corpseGuid != 0)
        {
            uint left = (uint)Math.Max(0, (_corpseReclaimReadyAt - NowSeconds()) * 1000);
            DrawCenteredText(dl, origin + new Vector2(192, 58) * s,
                left == 0 ? "Return to your corpse to resurrect." : $"You may resurrect in {left / 1000f:0.0} seconds.",
                10f * s, 0xffaaaaaa);
            if (VanillaButton(dl, "##reclaim", "Reclaim Corpse", origin + new Vector2(132, 105) * s,
                    new Vector2(120, 22), s, left == 0)) ReclaimCorpse();
        }
        ImGui.End();
    }
}
