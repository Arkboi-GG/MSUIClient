namespace MSUIClient.Net;

public readonly record struct ActionSlot(byte Kind, uint ActionId)
{
    public const byte Spell = 0x00;
    public const byte Macro = 0x40;
    public const byte Item = 0x80;
    public uint Packed => ActionId | ((uint)Kind << 24);
}

public readonly record struct SpellCooldown(uint SpellId, uint Category, double StartedAt, double DurationSeconds);

/// <summary>Authoritative local 120-slot bar plus the server-fed spellbook.</summary>
public sealed class PlayerActions
{
    private readonly ActionSlot?[] _slots = new ActionSlot?[120];
    private readonly HashSet<uint> _knownSpells = new();
    private readonly Dictionary<uint, SpellCooldown> _cooldowns = new();

    public IReadOnlySet<uint> KnownSpells => _knownSpells;
    public int OccupiedCount => _slots.Count(s => s.HasValue);
    public ActionSlot? this[int wireSlot] => wireSlot is >= 0 and < 120 ? _slots[wireSlot] : null;

    public void Clear()
    {
        Array.Clear(_slots);
        _knownSpells.Clear();
        _cooldowns.Clear();
    }

    public void ApplyButtons(byte[] body)
    {
        Array.Clear(_slots);
        var r = new PacketReader(body);
        int slot = 0;
        while (r.Remaining >= 4 && slot < _slots.Length)
        {
            uint packed = r.ReadU32();
            if (packed != 0)
                _slots[slot] = new ActionSlot((byte)(packed >> 24), packed & 0x00ff_ffffu);
            slot++;
        }
    }

    public void ApplyInitialSpells(byte[] body, double nowSeconds)
    {
        var r = new PacketReader(body);
        r.ReadU8();
        int count = r.ReadU16();
        _knownSpells.Clear();
        for (int i = 0; i < count && r.Remaining >= 4; i++)
        {
            _knownSpells.Add(r.ReadU16());
            r.ReadU16();
        }

        _cooldowns.Clear();
        if (r.Remaining < 2) return;
        int cooldownCount = r.ReadU16();
        for (int i = 0; i < cooldownCount && r.Remaining >= 14; i++)
        {
            uint spell = r.ReadU16();
            r.ReadU16();
            uint category = r.ReadU16();
            uint spellMs = r.ReadU32();
            uint categoryMs = r.ReadU32();
            uint duration = Math.Max(spellMs, categoryMs);
            if (duration > 1)
                _cooldowns[spell] = new SpellCooldown(spell, category, nowSeconds, duration / 1000.0);
        }
    }

    public void Learn(uint spell) => _knownSpells.Add(spell);

    public void Supercede(uint oldSpell, uint newSpell)
    {
        _knownSpells.Remove(oldSpell);
        _knownSpells.Add(newSpell);
        for (int i = 0; i < _slots.Length; i++)
            if (_slots[i] is { Kind: ActionSlot.Spell, ActionId: var id } && id == oldSpell)
                _slots[i] = new ActionSlot(ActionSlot.Spell, newSpell);
    }

    public void Set(int wireSlot, ActionSlot? value)
    {
        if (wireSlot is >= 0 and < 120) _slots[wireSlot] = value;
    }

    public void StartCooldown(uint spell, uint category, uint durationMs, double nowSeconds)
    {
        if (durationMs == 0) return;
        _cooldowns[spell] = new SpellCooldown(spell, category, nowSeconds, durationMs / 1000.0);
    }

    public float CooldownFraction(uint spell, double nowSeconds)
    {
        if (!_cooldowns.TryGetValue(spell, out var cd) || cd.DurationSeconds <= 0) return 0f;
        double elapsed = nowSeconds - cd.StartedAt;
        if (elapsed >= cd.DurationSeconds) { _cooldowns.Remove(spell); return 0f; }
        return (float)Math.Clamp(elapsed / cd.DurationSeconds, 0, 1);
    }

    public bool IsOnCooldown(uint spell, double nowSeconds)
        => CooldownFraction(spell, nowSeconds) > 0f;
}
