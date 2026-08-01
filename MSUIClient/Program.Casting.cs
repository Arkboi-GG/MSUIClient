using System.Numerics;
using ImGuiNET;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private enum CastBarPhase { Hidden, Casting, Channel, Success, Failed }
    private CastBarPhase _castBarPhase;
    private uint _castBarSpell;
    private string _castBarText = "";
    private double _castBarStarted;
    private double _castBarEnds;
    private double _castBarDisplayUntil;
    private double _castBarFinishedAt;

    private readonly List<(double At, ulong Target, uint Spell)> _pendingSpellImpacts = [];
    private bool _castMovementWasActive;

    private void ApplySpellStart(SpellStartPacket packet)
    {
        if (_net is not null && packet.Caster == _net.PlayerGuid)
            EmitSpellServerResult(packet.SpellId, "SMSG_SPELL_START");
        SpellInfo? info = _spellCatalog?.TryGet(packet.SpellId, out SpellInfo found) == true ? found : null;
        SpellVisualKitInfo? kit = ResolveSpellKit(info?.VisualId ?? 0, static s => s.Precast);
        ushort? anim = kit?.AnimationId;
        if (kit is { } precastKit)
            _spellEffects?.SpawnKit(packet.Caster, packet.SpellId, precastKit, persistent: true, NowSeconds());
        if (_net is not null && packet.Caster == _net.PlayerGuid)
        {
            _character?.BeginSpellVisual(anim);
            if (info is { } startedInfo)
                EmitSpellAnimation(startedInfo, "PRECAST", SpellStageKitId(startedInfo.VisualId, "precast"), anim, "SERVER_START");
            if (info?.Ranged == true) SetVisualSheath(2);
            if (packet.CastTimeMs > 0 && info?.Ranged != true)
                BeginCastBar(packet.SpellId, packet.CastTimeMs, channel: false);
        }
        else _creatures?.BeginSpellVisual(packet.Caster, anim);
    }

    private void ApplySpellGo(SpellGoPacket packet)
    {
        double now = NowSeconds();
        SpellInfo? info = _spellCatalog?.TryGet(packet.SpellId, out SpellInfo found) == true ? found : null;
        SpellVisualKitInfo? kit = ResolveSpellKit(info?.VisualId ?? 0, static s => s.Cast);
        ushort? anim = kit?.AnimationId;
        _spellEffects?.Reap(packet.Caster, packet.SpellId);
        if (kit is { } castKit)
            _spellEffects?.SpawnKit(packet.Caster, packet.SpellId, castKit, persistent: false, NowSeconds());
        if (_net is not null && packet.Caster == _net.PlayerGuid)
        {
            EmitSpellServerResult(packet.SpellId, "SMSG_SPELL_GO");
            if (_pendingCastSpell == packet.SpellId) _pendingCastSpell = 0;
            if (_queuedMeleeSpell == packet.SpellId) _queuedMeleeSpell = 0;
            _character?.ReleaseSpellVisual(anim);
            if (info is { } completedInfo)
                EmitSpellAnimation(completedInfo, "CAST", SpellStageKitId(completedInfo.VisualId, "cast"), anim, "SERVER_GO");
            if (info?.Ranged == true) SetVisualSheath(2);
            CompleteCastBar(packet.SpellId);
            if (info is { } completed)
            {
                uint cooldown = Math.Max(completed.RecoveryMs, completed.CategoryRecoveryMs);
                if (cooldown > 0) _actions.StartCooldown(packet.SpellId, 0, cooldown, now);
            }
        }
        else _creatures?.ReleaseSpellVisual(packet.Caster, anim);

        foreach (ulong target in packet.Hits)
        {
            double travel = 0;
            if (info is { Speed: > 0 } && TryUnitPosition(packet.Caster, out Vector3 from) &&
                TryUnitPosition(target, out Vector3 to))
                travel = Vector3.Distance(from, to) / info.Value.Speed;
            if (travel > .01 && info is { } missileInfo && _spellVisualCatalog is not null &&
                _spellVisualCatalog.TryGetStages(missileInfo.VisualId, out SpellVisualStages stages) &&
                _spellVisualCatalog.MissilePath(stages) is { } missilePath &&
                TryUnitPosition(packet.Caster, out Vector3 missileFrom) && TryUnitPosition(target, out Vector3 missileTo))
                _spellEffects?.SpawnMissile(packet.Caster, packet.SpellId, missilePath,
                    missileFrom + new Vector3(0, 0, 1.2f), missileTo + new Vector3(0, 0, 1.1f), now, travel);
            if (travel <= 0.01) ApplySpellImpact(target, packet.SpellId);
            else _pendingSpellImpacts.Add((now + travel, target, packet.SpellId));
        }
    }

    private void ApplySpellImpact(ulong target, uint spellId)
    {
        uint visual = _spellCatalog?.TryGet(spellId, out SpellInfo info) == true ? info.VisualId : 0;
        SpellVisualKitInfo? kit = ResolveSpellKit(visual, static s => s.Impact);
        if (kit is { } impactKit)
            _spellEffects?.SpawnKit(target, spellId, impactKit, persistent: false, NowSeconds());

        // Reference law (benilla creature_anim/driver/wound.rs:14-15 + net/apply/combat_log.rs):
        // a spell impact animates its victim ONLY through the kit's own authored animation id,
        // and only the CombatWound family (8 StandWound / 9 CombatWound / 10 CombatCritical)
        // routes through the wound reaction. A kit with no animation plays NOTHING - the old
        // "no anim => synthesize a wound" fallback here is what made the server's login visual
        // (LOGINEFFECT, spell 836) flinch the local player the moment the world became visible.
        if (kit?.AnimationId is not { } anim || anim == 0) return;
        bool wound = anim is 8 or 9 or 10;
        if (_net is not null && target == _net.PlayerGuid)
        {
            if (wound) _character?.TriggerCombatReaction(0, landedHit: true);
            else _character?.TriggerOneShot(anim);
        }
        else if (wound)
            _creatures?.TriggerCombatReaction(target, 0, landedHit: true);
        else
            _creatures?.ReleaseSpellVisual(target, anim);
    }

    private SpellVisualKitInfo? ResolveSpellKit(uint visualId,
        Func<SpellVisualStages, uint> stage)
    {
        if (_spellVisualCatalog is null ||
            !_spellVisualCatalog.TryGetStages(visualId, out SpellVisualStages stages)) return null;
        uint kitId = stage(stages);
        return kitId != 0 && _spellVisualCatalog.TryGetKit(kitId, out SpellVisualKitInfo kit) ? kit : null;
    }

    private uint SpellStageKitId(uint visualId, string stage)
    {
        if (_spellVisualCatalog?.TryGetStages(visualId, out SpellVisualStages stages) != true) return 0;
        return stage == "precast" ? stages.Precast : stage == "channel" ? stages.Channel : stages.Cast;
    }

    private void ApplySpellFailure(ulong caster, uint spellId, string text)
    {
        if (_net is not null && caster == _net.PlayerGuid)
        {
            if (_pendingCastSpell == spellId) _pendingCastSpell = 0;
            if (_queuedMeleeSpell == spellId) _queuedMeleeSpell = 0;
            _globalCooldownUntil = 0;
            _character?.CancelSpellVisual();
            FailCastBar(spellId, text);
        }
        else _creatures?.CancelSpellVisual(caster);
        _spellEffects?.Reap(caster, spellId);
    }

    /// <summary>
    /// Escape follows the 1.12 stop-casting order: stop a ranged auto-repeat
    /// first; otherwise cancel an ordinary in-flight cast. A channel is not an
    /// Escape target in the original client (movement cancels it separately).
    /// Returning true keeps the same key press from opening the game menu.
    /// </summary>
    private bool TryCancelSpellOnEscape()
    {
        if (_net is not { IsInWorld: true }) return false;
        if (_autoRepeatSpell != 0)
        {
            _net.CancelAutoRepeat();
            _autoRepeatSpell = 0;
            SetVisualSheath(0);
            _character?.CancelSpellVisual();
            return true;
        }
        uint spell = _pendingCastSpell;
        if (spell == 0 && _castBarPhase == CastBarPhase.Casting) spell = _castBarSpell;
        if (spell == 0) return false;
        EmitCastBarVerdict("CANCEL_SEND", spell, cancelSource: "ESCAPE");
        _net.CancelCast(spell);
        ApplySpellFailure(_net.PlayerGuid, spell, "INTERRUPTED");
        return true;
    }

    /// <summary>Send the movement-only cancel exactly once on the stopped-to-moving edge.</summary>
    private void UpdateCastMovementInput(bool active)
    {
        bool edge = active && !_castMovementWasActive;
        _castMovementWasActive = active;
        if (!edge || _net is not { IsInWorld: true }) return;

        if (_castBarPhase == CastBarPhase.Channel &&
            (_spellCatalog?.TryGet(_castBarSpell, out SpellInfo channel) != true ||
             channel.MovementInterruptsChannel))
        {
            EmitCastBarVerdict("CANCEL_SEND", _castBarSpell, cancelSource: "MOVEMENT_CHANNEL");
            _net.CancelChannelling(_castBarSpell);
            return; // channel owns the one movement-cancel edge
        }

        uint spell = _pendingCastSpell;
        if (spell == 0 && _castBarPhase == CastBarPhase.Casting) spell = _castBarSpell;
        if (spell == 0) return;
        if (_spellCatalog?.TryGet(spell, out SpellInfo info) == true && !info.MovementInterrupts)
            return;
        EmitCastBarVerdict("CANCEL_SEND", spell, cancelSource: "MOVEMENT_CAST");
        _net.CancelCast(spell);
        ApplySpellFailure(_net.PlayerGuid, spell, "INTERRUPTED");
    }

    private void ApplyAutoRepeatCancelled()
    {
        _autoRepeatSpell = 0;
        SetVisualSheath(0);
        _character?.CancelSpellVisual();
    }

    private void UpdateSpellPresentation()
    {
        double now = NowSeconds();
        _spellEffects?.Tick(now);
        for (int i = _pendingSpellImpacts.Count - 1; i >= 0; i--)
        {
            if (_pendingSpellImpacts[i].At > now) continue;
            var impact = _pendingSpellImpacts[i];
            _pendingSpellImpacts.RemoveAt(i);
            ApplySpellImpact(impact.Target, impact.Spell);
        }
        if (_castBarPhase is CastBarPhase.Success or CastBarPhase.Failed && now >= _castBarDisplayUntil)
            _castBarPhase = CastBarPhase.Hidden;
    }

    private void BeginCastBar(uint spell, uint durationMs, bool channel)
    {
        double now = NowSeconds();
        _castBarSpell = spell;
        _castBarText = _spellCatalog?.TryGet(spell, out SpellInfo info) == true ? info.Name : $"Spell {spell}";
        _castBarStarted = now;
        _castBarEnds = now + durationMs / 1000.0;
        _castBarPhase = channel ? CastBarPhase.Channel : CastBarPhase.Casting;
        _castBarPushbackTotalMs = 0;
        EmitCastBarVerdict(channel ? "CHANNEL_START" : "CAST_START", spell, durationMs);
    }

    private void CompleteCastBar(uint spell)
    {
        if (_castBarSpell != spell || _castBarPhase == CastBarPhase.Hidden) return;
        _castBarPhase = CastBarPhase.Success;
        _castBarText = _spellCatalog?.TryGet(spell, out SpellInfo info) == true ? info.Name : _castBarText;
        _castBarFinishedAt = NowSeconds();
        // CastingBar.xml: flash grows at .2 per 30 Hz tick, then the frame fades at .05/tick.
        _castBarDisplayUntil = _castBarFinishedAt + 1.0 / 6.0 + 1.0 / 1.5;
        EmitCastBarVerdict("CAST_COMPLETE", spell);
    }

    private void FailCastBar(uint spell, string text)
    {
        if (_castBarSpell != spell && _castBarPhase != CastBarPhase.Hidden) return;
        _castBarSpell = spell;
        _castBarPhase = CastBarPhase.Failed;
        _castBarText = text;
        _castBarFinishedAt = NowSeconds();
        _castBarDisplayUntil = _castBarFinishedAt + 1.0 + 1.0 / 1.5;
        EmitCastBarVerdict("CAST_FAILED", spell, cancelSource: text);
    }

    private void DelayCastBar(uint delayMs)
    {
        if (_castBarPhase != CastBarPhase.Casting) return;
        _castBarStarted += delayMs / 1000.0;
        _castBarEnds += delayMs / 1000.0;
        _castBarPushbackTotalMs += delayMs;
        EmitCastBarVerdict("PUSHBACK", _castBarSpell, delayMs);
    }

    private void UpdateChannel(uint remainingMs)
    {
        if (remainingMs == 0)
        {
            if (_castBarPhase == CastBarPhase.Channel)
            {
                _castBarPhase = CastBarPhase.Success;
                _castBarFinishedAt = NowSeconds();
                _castBarDisplayUntil = _castBarFinishedAt + 1.0 / 6.0 + 1.0 / 1.5;
                EmitCastBarVerdict("CHANNEL_STOP", _castBarSpell);
            }
            _character?.CancelSpellVisual();
            if (_net is not null) _spellEffects?.Reap(_net.PlayerGuid, _castBarSpell);
            return;
        }
        _castBarEnds = NowSeconds() + remainingMs / 1000.0;
        EmitCastBarVerdict("CHANNEL_UPDATE", _castBarSpell, remainingMs);
    }

    private void BeginChannel(uint spellId, uint durationMs)
    {
        BeginCastBar(spellId, durationMs, channel: true);
        uint visual = _spellCatalog?.TryGet(spellId, out SpellInfo info) == true ? info.VisualId : 0;
        ushort? animation = ResolveSpellKit(visual, static s => s.Channel)?.AnimationId;
        _character?.BeginSpellVisual(animation);
        if (_spellCatalog?.TryGet(spellId, out SpellInfo channelInfo) == true &&
            _spellVisualCatalog?.TryGetStages(channelInfo.VisualId, out SpellVisualStages channelStages) == true)
            EmitSpellAnimation(channelInfo, "CHANNEL", channelStages.Channel, animation, "SERVER_CHANNEL");
        if (ResolveSpellKit(visual, static s => s.Channel) is { } channelKit && _net is not null)
            _spellEffects?.SpawnKit(_net.PlayerGuid, spellId, channelKit, persistent: true, NowSeconds());
    }

    private void ApplyPushedVisual(ulong unit, uint kitId)
    {
        if (_spellVisualCatalog?.TryGetKit(kitId, out SpellVisualKitInfo kit) != true) return;
        _spellEffects?.SpawnKit(unit, 0, kit, persistent: false, NowSeconds());
        if (_net is not null && unit == _net.PlayerGuid)
            _character?.ReleaseSpellVisual(kit.AnimationId);
        else
            _creatures?.ReleaseSpellVisual(unit, kit.AnimationId);
    }

    private void DrawCastingBar()
    {
        if (_castBarPhase == CastBarPhase.Hidden || _gameplayArt is null) return;
        float s = GameplayUiScale();
        Vector2 display = ImGui.GetIO().DisplaySize;
        Vector2 size = new(256, 64);
        Vector2 p = new((display.X - size.X * s) * .5f, display.Y - 145f * s);
        Vector2 authored = p / s;
        CollectGameplayLayout("cast-bar", authored.X, authored.Y, size.X, size.Y, p, size * s);
        ImGui.SetNextWindowPos(p, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size * s, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoInputs;
        if (!ImGui.Begin("##casting-bar", flags)) { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 barMin = p + new Vector2(30.5f, 28f) * s;
        Vector2 barSize = new(195, 13);
        dl.AddRectFilled(barMin, barMin + barSize * s, 0x80000000);
        double now = NowSeconds();
        float fraction = _castBarPhase switch
        {
            CastBarPhase.Casting => (float)((now - _castBarStarted) / Math.Max(.001, _castBarEnds - _castBarStarted)),
            CastBarPhase.Channel => (float)((_castBarEnds - now) / Math.Max(.001, _castBarEnds - _castBarStarted)),
            _ => 1f,
        };
        fraction = Math.Clamp(fraction, 0, 1);
        Vector4 color = _castBarPhase switch
        {
            CastBarPhase.Success => new(0, 1, 0, 1),
            CastBarPhase.Failed => new(1, 0, 0, 1),
            _ => new(1, .7f, 0, 1),
        };
        DrawVanillaStatusBar(dl, barMin, barSize * s, fraction, color);
        DrawArt(dl, @"Interface\CastingBar\UI-CastingBar-Border", p, size, s);
        DrawCenteredText(dl, p + new Vector2(128, 17) * s, _castBarText, 12f * s, 0xffffffff);
        if (_castBarPhase == CastBarPhase.Success)
        {
            float flashAlpha = Math.Clamp((float)((now - _castBarFinishedAt) * 6.0), 0f, 1f);
            uint flash = _gameplayArt.AdditiveHandle(@"Interface\CastingBar\UI-CastingBar-Flash");
            if (flash != 0)
                dl.AddImage((nint)flash, p, p + size * s, Vector2.Zero, Vector2.One,
                    ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, flashAlpha)));
        }
        if (_castBarPhase is CastBarPhase.Casting or CastBarPhase.Channel)
        {
            // CastingBar.xml: the 32x32 moving spark is alphaMode="ADD".
            uint spark = _gameplayArt.AdditiveHandle(@"Interface\CastingBar\UI-CastingBar-Spark");
            if (spark != 0)
            {
                float x = barMin.X + barSize.X * s * fraction;
                dl.AddImage((nint)spark, new Vector2(x - 16 * s, barMin.Y - 10 * s),
                    new Vector2(x + 16 * s, barMin.Y + 22 * s), Vector2.Zero, Vector2.One);
            }
        }
        ImGui.End();
    }

    private bool TryUnitPosition(ulong guid, out Vector3 position)
    {
        if (_net is not null && guid == _net.PlayerGuid && _controller is not null)
        { position = _controller.Position; return true; }
        if (_entities.TryGet(guid, out WorldEntity unit)) { position = unit.Position; return true; }
        position = default; return false;
    }

    private (bool Found, Vector3 Position, float Yaw) SpellEffectUnitPose(ulong guid)
    {
        if (_net is not null && guid == _net.PlayerGuid && _controller is not null)
            return (true, _controller.Position, _controller.Yaw);
        if (_entities.TryGet(guid, out WorldEntity unit))
            return (true, unit.Position, unit.Orientation);
        return default;
    }

    private static double NowSeconds() => MovementInfo.ClientUptimeMs() / 1000.0;
}
