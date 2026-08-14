using System.Numerics;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// CART KIT — what the thing you are riding can DO.
//
// A cart carries up to six slots. Each slot points at an ordinary 1.12 spell for
// its LOOK (Cone of Cold, Blizzard, Blink — played through the same spell-effect
// path the networked casts use) and at an EFFECT for its BEHAVIOUR. The two are
// deliberately separate fields: which spell dresses which effect is precisely the
// pass this exists to make cheap, and neither is load-bearing for the other.
//
// NOTHING HERE DEALS DAMAGE. The effects are control — a slow on everything in
// radius, or a dash that throws the cart forward. That is the design, not a stub.
//
// CHARGES are the resource, and where a spent charge comes back from is a policy:
//
//   Time   — a timer. The default, so the kit is playable today.
//   Token  — nothing regenerates on its own; something has to call
//            NoteMountKitToken(). That is the seam for the pickup on the track.
//            The toolkit's "Drop a token" button already calls it, so the whole
//            loop can be felt before the track exists.
//
// EVERYTHING IS CLIENT-SIDE. No cast is sent, no aura is real, and a live server
// knows none of it. That is the right shape for now: it makes the cart playable
// while the rules are still being invented, and the seams that a server would
// eventually own (the cast, the aura) are single calls.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private const int MountKitSlots = 6;

    /// <summary>Live charge state for one slot of the cart currently under you.</summary>
    private sealed class MountKitSlotState
    {
        public int Charges;
        public double ReadyAt;      // cooldown
        public double RechargeAt;   // next charge returns (Time policy only)
    }

    private int _mountKitDisplay;
    private readonly List<MountKitSlotState> _mountKitState = [];
    private string _mountKitStatus = "";
    private double _mountKitStatusAt;

    /// <summary>A slow this client believes in: who, how much, until when.</summary>
    private readonly record struct MountKitSlow(float Factor, double ExpiresAt);
    private readonly Dictionary<ulong, MountKitSlow> _mountKitSlows = [];

    /// <summary>Fired slots, for the probe and the panel to assert against.</summary>
    public int MountKitCastsFired { get; private set; }

    // ── state ────────────────────────────────────────────────────────────────

    private List<GameSettings.MountKitSlotSetting>? CurrentMountKit()
    {
        int display = SelfMountDisplayId();
        return display > 0 ? FindMountTune(display)?.Kit : null;
    }

    /// <summary>
    /// Ticked every frame: cooldowns, timed recharge, and expiring slows. Charge state is
    /// rebuilt when you change carts — a kit belongs to the cart, so its charges do too.
    /// </summary>
    private void UpdateMountKit(double now)
    {
        int display = SelfMountDisplayId();
        if (display != _mountKitDisplay)
        {
            _mountKitDisplay = display;
            _mountKitState.Clear();
        }

        var kit = CurrentMountKit();
        if (kit is not null)
        {
            while (_mountKitState.Count < kit.Count)
                _mountKitState.Add(new MountKitSlotState
                {
                    Charges = Math.Max(0, kit[_mountKitState.Count].MaxCharges),
                });

            if (Settings.Mounts.Recharge == GameSettings.MountKitRecharge.Time)
                for (int i = 0; i < kit.Count && i < _mountKitState.Count; i++)
                {
                    var slot = kit[i];
                    var state = _mountKitState[i];
                    if (state.Charges >= slot.MaxCharges) { state.RechargeAt = 0; continue; }
                    if (state.RechargeAt <= 0) state.RechargeAt = now + Math.Max(0.5f, slot.RechargeSeconds);
                    else if (now >= state.RechargeAt)
                    {
                        state.Charges++;
                        state.RechargeAt = state.Charges < slot.MaxCharges
                            ? now + Math.Max(0.5f, slot.RechargeSeconds) : 0;
                    }
                }
        }

        if (_mountKitSlows.Count > 0)
        {
            _mountKitStale.Clear();
            foreach (var pair in _mountKitSlows)
                if (now >= pair.Value.ExpiresAt) _mountKitStale.Add(pair.Key);
            foreach (ulong guid in _mountKitStale) _mountKitSlows.Remove(guid);
        }
    }

    private readonly List<ulong> _mountKitStale = [];

    /// <summary>
    /// How slowed a unit is right now, 1 = not. The seam every consumer should read: today
    /// the local controller does, and the panel lists them; a real debuff would fill the same
    /// table from the server instead of from a local cast.
    /// </summary>
    private float MountKitSlowFactor(ulong guid) =>
        _mountKitSlows.TryGetValue(guid, out MountKitSlow slow) ? slow.Factor : 1f;

    /// <summary>
    /// The token seam. Call this when the pickup on the track is collected — slot -1 means
    /// "whichever needs it most", which is the behaviour a single generic token wants.
    /// </summary>
    private void NoteMountKitToken(int slotIndex = -1)
    {
        var kit = CurrentMountKit();
        if (kit is null || kit.Count == 0) return;

        int target = slotIndex;
        if (target < 0)
        {
            int worst = int.MaxValue;
            for (int i = 0; i < kit.Count && i < _mountKitState.Count; i++)
            {
                int missing = kit[i].MaxCharges - _mountKitState[i].Charges;
                if (missing > 0 && kit[i].MaxCharges - missing < worst)
                {
                    worst = kit[i].MaxCharges - missing;
                    target = i;
                }
            }
        }
        if (target < 0 || target >= kit.Count || target >= _mountKitState.Count) return;

        _mountKitState[target].Charges =
            Math.Min(kit[target].MaxCharges, _mountKitState[target].Charges + 1);
        SetMountKitStatus($"token -> slot {target + 1} ({_mountKitState[target].Charges}/{kit[target].MaxCharges})");
    }

    // ── firing ───────────────────────────────────────────────────────────────

    private bool MountKitSlotReady(int index, double now)
    {
        var kit = CurrentMountKit();
        if (kit is null || index < 0 || index >= kit.Count || index >= _mountKitState.Count) return false;
        if (kit[index].SpellId == 0) return false;
        var state = _mountKitState[index];
        return state.Charges > 0 && now >= state.ReadyAt;
    }

    /// <summary>
    /// Fire a slot. Returns false when it did not go off, which is what lets the number keys
    /// fall through to the ordinary action bar for a slot this cart does not have.
    /// </summary>
    private bool FireMountKitSlot(int index)
    {
        var kit = CurrentMountKit();
        if (kit is null || index < 0 || index >= kit.Count) return false;
        var slot = kit[index];
        if (slot.SpellId == 0) return false;

        double now = NowSeconds();
        if (index >= _mountKitState.Count) return false;
        var state = _mountKitState[index];

        if (now < state.ReadyAt) { SetMountKitStatus($"slot {index + 1} cooling"); return true; }
        if (state.Charges <= 0) { SetMountKitStatus($"slot {index + 1} out of charges"); return true; }

        state.Charges--;
        state.ReadyAt = now + Math.Max(0.1f, slot.CooldownSeconds);
        if (state.RechargeAt <= 0) state.RechargeAt = now + Math.Max(0.5f, slot.RechargeSeconds);
        MountKitCastsFired++;

        // Look: the spell's own authored cast, on the rider, then its impact on whatever the
        // effect caught. Presentation only — PresentSpellEffect neither sends nor simulates.
        ulong target = _selectionGuid;
        PresentSpellEffect(slot.SpellId, "cast");

        string what = slot.Effect switch
        {
            GameSettings.MountKitEffectKind.Dash => ApplyMountKitDash(slot),
            GameSettings.MountKitEffectKind.Slow => ApplyMountKitSlow(slot, target, now),
            _ => "no effect",
        };

        SetMountKitStatus($"{MountKitSlotName(slot, index)}: {what}  " +
                          $"({state.Charges}/{slot.MaxCharges})");
        return true;
    }

    /// <summary>Slow everything within radius of the aim point, and say so over their heads.</summary>
    private string ApplyMountKitSlow(GameSettings.MountKitSlotSetting slot, ulong target, double now)
    {
        Vector3 centre = MountKitAimPoint(slot, target);
        float radiusSq = slot.Radius * slot.Radius;
        float factor = Math.Clamp(slot.SlowFactor, 0.05f, 1f);
        int caught = 0;

        foreach (WorldEntity entity in _entities.Units)
        {
            if (entity.Guid == ControlledGuid) continue;
            if (Vector3.DistanceSquared(entity.Position, centre) > radiusSq) continue;

            _mountKitSlows[entity.Guid] = new MountKitSlow(factor, now + Math.Max(0.5f, slot.SlowSeconds));
            caught++;
            if (entity.Guid == target || caught <= 4)
            {
                PresentSpellEffect(slot.SpellId, "impact", entity.Guid);
                SpawnMountKitText(entity, $"Slowed {(int)MathF.Round((1f - factor) * 100f)}%");
            }
        }

        return caught > 0
            ? $"slowed {caught} to {(int)MathF.Round(factor * 100f)}% for {slot.SlowSeconds:F0}s"
            : "caught nothing";
    }

    /// <summary>Blink, as a cart move: throw the whole thing forward along its facing.</summary>
    private string ApplyMountKitDash(GameSettings.MountKitSlotSetting slot)
    {
        if (_controller is null) return "no controller";
        float distance = MathF.Max(1f, slot.Radius);
        var forward = new Vector3(MathF.Cos(_controller.Yaw), MathF.Sin(_controller.Yaw), 0f);
        Vector3 wanted = _controller.Position + forward * distance;

        // Ride the ordinary ground query rather than teleporting into a hillside; the
        // controller's own collision then settles the frame.
        if (_terrain?.SampleHeight(wanted.X, wanted.Y) is float ground)
            wanted.Z = MathF.Max(ground, _controller.Position.Z - 4f);
        _controller.Teleport(wanted.X, wanted.Y, wanted.Z);
        return $"dashed {distance:F0} yd";
    }

    /// <summary>Where the effect lands: the target if there is one, else out in front of the cart.</summary>
    private Vector3 MountKitAimPoint(GameSettings.MountKitSlotSetting slot, ulong target)
    {
        if (target != 0 && _entities.TryGet(target, out WorldEntity entity)) return entity.Position;
        if (_controller is null) return Vector3.Zero;
        var forward = new Vector3(MathF.Cos(_controller.Yaw), MathF.Sin(_controller.Yaw), 0f);
        return _controller.Position + forward * MathF.Max(2f, slot.Radius * 0.5f);
    }

    private void SpawnMountKitText(WorldEntity entity, string text)
    {
        int lane = _floatingCombatText.Count(t => t.Target == entity.Guid);
        if (lane >= MaxWorldTextPerUnit) return;
        float scale = _creatures?.PickScale(entity) ?? MathF.Max(0.01f, entity.Scale);
        _floatingCombatText.Add(new FloatingCombatText
        {
            Target = entity.Guid,
            Anchor = entity.Position + new Vector3(0, 0, MathF.Max(1.5f, 2.2f * scale)),
            Text = text,
            Style = WorldCombatTextStyle.PlayerSpell,
            Lane = lane,
        });
    }

    private static string MountKitSlotName(GameSettings.MountKitSlotSetting slot, int index) =>
        slot.Label.Length > 0 ? slot.Label : $"slot {index + 1}";

    private void SetMountKitStatus(string text)
    {
        _mountKitStatus = text;
        _mountKitStatusAt = NowSeconds();
        Console.WriteLine($"[mount-kit] {text}");
    }

    /// <summary>
    /// 1..6 while mounted, and only for slots that hold a spell — an unconfigured cart leaves
    /// the action bar exactly as it was.
    /// </summary>
    private bool TryMountKitNumberKey(int oneBased)
    {
        if (!Settings.Mounts.KitOnNumberKeys || SelfMountDisplayId() <= 0) return false;
        var kit = CurrentMountKit();
        int index = oneBased - 1;
        if (kit is null || index < 0 || index >= kit.Count || kit[index].SpellId == 0) return false;
        return FireMountKitSlot(index);
    }

    /// <summary>
    /// A starting kit built from the spells by NAME, so it works on this machine's own
    /// catalog rather than on hard-coded ids that drift between builds.
    /// </summary>
    private int InstallDefaultMountKit(int display)
    {
        if (_spellCatalog is null || display <= 0) return 0;
        var tune = MountTuneFor(display);
        tune.Kit.Clear();

        (string Name, GameSettings.MountKitEffectKind Effect, float Radius, float Slow)[] wanted =
        [
            ("Cone of Cold", GameSettings.MountKitEffectKind.Slow, 12f, 0.5f),
            ("Blizzard", GameSettings.MountKitEffectKind.Slow, 18f, 0.65f),
            ("Blink", GameSettings.MountKitEffectKind.Dash, 20f, 1f),
        ];

        foreach (var (name, effect, radius, slow) in wanted)
        {
            SpellInfo? found = null;
            foreach (SpellInfo candidate in _spellCatalog.Spells)
            {
                if (candidate.VisualId == 0) continue;
                if (!candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                if (found is null || candidate.Id < found.Value.Id) found = candidate;
            }
            if (found is null) continue;

            tune.Kit.Add(new GameSettings.MountKitSlotSetting
            {
                SpellId = found.Value.Id,
                Label = found.Value.Name,
                Effect = effect,
                Radius = radius,
                SlowFactor = slow,
                MaxCharges = effect == GameSettings.MountKitEffectKind.Dash ? 2 : 3,
                RechargeSeconds = effect == GameSettings.MountKitEffectKind.Dash ? 12f : 8f,
                CooldownSeconds = 1.5f,
            });
        }

        _mountKitState.Clear();
        SetMountKitStatus($"installed {tune.Kit.Count} slot(s) on display {display}");
        return tune.Kit.Count;
    }
}
