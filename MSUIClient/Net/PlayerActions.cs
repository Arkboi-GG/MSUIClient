namespace MSUIClient.Net;

public readonly record struct ActionSlot(byte Kind, uint ActionId)
{
    public const byte Spell = 0x00;
    public const byte Macro = 0x40;
    public const byte Item = 0x80;
    public uint Packed => ActionId | ((uint)Kind << 24);
}

public readonly record struct SpellCooldown(uint SpellId, uint Category, double StartedAt, double DurationSeconds);
public readonly record struct CooldownDisplay(float? SweepFraction, float? FlashProgress)
{
    public const double FinishFlashSeconds = 1.0;
}

/// <summary>Authoritative local 120-slot bar plus the server-fed spellbook.</summary>
public sealed class PlayerActions
{
    private readonly record struct HeldCooldown(uint SpellId, uint Category,
        uint SpellDurationMs, uint CategoryDurationMs);

    private readonly ActionSlot?[] _slots = new ActionSlot?[120];
    private readonly HashSet<uint> _knownSpells = new();
    private readonly Dictionary<uint, SpellCooldown> _spellCooldowns = new();
    private readonly Dictionary<uint, SpellCooldown> _categoryCooldowns = new();
    private readonly Dictionary<uint, List<HeldCooldown>> _heldCooldowns = new();

    public IReadOnlySet<uint> KnownSpells => _knownSpells;
    public int OccupiedCount => _slots.Count(s => s.HasValue);
    public ActionSlot? this[int wireSlot] => wireSlot is >= 0 and < 120 ? _slots[wireSlot] : null;

    public void Clear()
    {
        Array.Clear(_slots);
        _knownSpells.Clear();
        _spellCooldowns.Clear();
        _categoryCooldowns.Clear();
        _heldCooldowns.Clear();
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

        _spellCooldowns.Clear();
        _categoryCooldowns.Clear();
        _heldCooldowns.Clear();
        if (r.Remaining < 2) return;
        int cooldownCount = r.ReadU16();
        for (int i = 0; i < cooldownCount && r.Remaining >= 14; i++)
        {
            uint spell = r.ReadU16();
            r.ReadU16();
            uint category = r.ReadU16();
            uint spellMs = r.ReadU32();
            uint categoryMs = r.ReadU32();
            if (spellMs > 1)
                _spellCooldowns[spell] = new SpellCooldown(spell, category, nowSeconds, spellMs / 1000.0);
            if (category != 0 && categoryMs > 1)
                _categoryCooldowns[category] = new SpellCooldown(spell, category, nowSeconds,
                    categoryMs / 1000.0);
        }
    }

    /// <summary>Seed the spellbook from a client-side cache (free-view bot bars).</summary>
    public void SeedSpells(IEnumerable<uint> spells)
    {
        _knownSpells.Clear();
        foreach (uint spell in spells) _knownSpells.Add(spell);
    }

    public void Learn(uint spell) => _knownSpells.Add(spell);
    public void Remove(uint spell)
    {
        _knownSpells.Remove(spell);
        for (int i = 0; i < _slots.Length; i++)
            if (_slots[i] is { Kind: ActionSlot.Spell, ActionId: var id } && id == spell)
                _slots[i] = null;
    }

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
        StartCooldown(spell, category, durationMs, 0, nowSeconds);
    }

    /// <summary>Starts the independent spell and category clocks carried by SMSG_SPELL_GO.</summary>
    public void StartCooldown(uint spell, uint category, uint spellDurationMs,
        uint categoryDurationMs, double nowSeconds)
        => StartCooldown(spell, category, spellDurationMs, categoryDurationMs, nowSeconds,
            onHold: false);

    /// <summary>
    /// Insert a running cooldown or park its authored durations until SMSG_COOLDOWN_EVENT.
    /// Multiple held records are retained because a cast's GCD and own/category recovery are
    /// separate build-5875 history nodes even when they share the spell id.
    /// </summary>
    public void StartCooldown(uint spell, uint category, uint spellDurationMs,
        uint categoryDurationMs, double nowSeconds, bool onHold)
    {
        if (onHold)
        {
            if (spellDurationMs == 0 && categoryDurationMs == 0) return;
            if (!_heldCooldowns.TryGetValue(spell, out List<HeldCooldown>? records))
                _heldCooldowns[spell] = records = [];
            records.Add(new HeldCooldown(spell, category, spellDurationMs, categoryDurationMs));
            return;
        }
        if (spellDurationMs > 0)
            _spellCooldowns[spell] = new SpellCooldown(spell, category, nowSeconds,
                spellDurationMs / 1000.0);
        if (category != 0 && categoryDurationMs > 0)
            _categoryCooldowns[category] = new SpellCooldown(spell, category, nowSeconds,
                categoryDurationMs / 1000.0);
    }

    /// <summary>Start every parked recovery node for one spell at this packet's receive time.</summary>
    public void StartCooldownEvent(uint spell, double nowSeconds)
    {
        if (!_heldCooldowns.Remove(spell, out List<HeldCooldown>? records)) return;
        foreach (HeldCooldown held in records)
            StartCooldown(held.SpellId, held.Category, held.SpellDurationMs,
                held.CategoryDurationMs, nowSeconds);
    }

    /// <summary>Remove every own/category/held record belonging to one spell.</summary>
    public void ClearCooldown(uint spell)
    {
        _spellCooldowns.Remove(spell);
        foreach (uint category in _categoryCooldowns
                     .Where(pair => pair.Value.SpellId == spell)
                     .Select(pair => pair.Key).ToArray())
            _categoryCooldowns.Remove(category);
        _heldCooldowns.Remove(spell);
    }

    /// <summary>SMSG_COOLDOWN_CHEAT: wipe the addressed player or pet history.</summary>
    public void ClearAllCooldowns()
    {
        _spellCooldowns.Clear();
        _categoryCooldowns.Clear();
        _heldCooldowns.Clear();
    }

    public float CooldownFraction(uint spell, double nowSeconds, uint category = 0)
    {
        if (!TryActiveCooldown(spell, category, nowSeconds, out SpellCooldown cd, out _)) return 0f;
        double elapsed = nowSeconds - cd.StartedAt;
        return (float)Math.Clamp(elapsed / cd.DurationSeconds, 0, 1);
    }

    /// <summary>
    /// The authored CooldownFrame display phase. Expired clocks remain visible only for their
    /// one-second finish flash; cast readiness still becomes true at the exact cooldown end.
    /// </summary>
    public bool TryCooldownDisplay(uint spell, double nowSeconds, uint category,
        out CooldownDisplay display)
    {
        display = default;
        bool found = TryDisplayClock(_spellCooldowns, spell, nowSeconds, out SpellCooldown winner);
        if (category != 0 && TryDisplayClock(_categoryCooldowns, category, nowSeconds,
                out SpellCooldown categoryClock) &&
            (!found || categoryClock.StartedAt + categoryClock.DurationSeconds >
                       winner.StartedAt + winner.DurationSeconds))
        {
            winner = categoryClock;
            found = true;
        }
        if (!found || winner.DurationSeconds <= 0) return false;

        double end = winner.StartedAt + winner.DurationSeconds;
        if (nowSeconds < end)
        {
            float fraction = (float)Math.Clamp(
                (nowSeconds - winner.StartedAt) / winner.DurationSeconds, 0.0, 1.0);
            display = new CooldownDisplay(fraction, null);
        }
        else
        {
            float flash = (float)Math.Clamp(nowSeconds - end, 0.0, 1.0);
            display = new CooldownDisplay(null, flash);
        }
        return true;
    }

    public bool IsOnCooldown(uint spell, double nowSeconds, uint category = 0)
        => TryActiveCooldown(spell, category, nowSeconds, out _, out _);

    public double CooldownRemaining(uint spell, double nowSeconds, uint category = 0)
    {
        return TryActiveCooldown(spell, category, nowSeconds, out _, out double remaining)
            ? remaining
            : 0;
    }

    private bool TryActiveCooldown(uint spell, uint category, double nowSeconds,
        out SpellCooldown cooldown, out double remaining)
    {
        cooldown = default;
        remaining = 0;
        if (TryClock(_spellCooldowns, spell, nowSeconds, out SpellCooldown spellClock,
                out double spellRemaining))
        {
            cooldown = spellClock;
            remaining = spellRemaining;
        }
        if (category != 0 &&
            TryClock(_categoryCooldowns, category, nowSeconds, out SpellCooldown categoryClock,
                out double categoryRemaining) && categoryRemaining > remaining)
        {
            cooldown = categoryClock;
            remaining = categoryRemaining;
        }
        return remaining > 0;
    }

    private static bool TryClock(Dictionary<uint, SpellCooldown> clocks, uint key,
        double nowSeconds, out SpellCooldown cooldown, out double remaining)
    {
        if (!clocks.TryGetValue(key, out cooldown))
        {
            remaining = 0;
            return false;
        }
        remaining = cooldown.DurationSeconds - (nowSeconds - cooldown.StartedAt);
        if (remaining > 0) return true;
        // CooldownFrame's sequence 1 remains visible for one second after readiness returns.
        // Retaining the inert record does not affect the active result above.
        if (remaining <= -CooldownDisplay.FinishFlashSeconds) clocks.Remove(key);
        remaining = 0;
        return false;
    }

    private static bool TryDisplayClock(Dictionary<uint, SpellCooldown> clocks, uint key,
        double nowSeconds, out SpellCooldown cooldown)
    {
        if (!clocks.TryGetValue(key, out cooldown)) return false;
        double end = cooldown.StartedAt + cooldown.DurationSeconds;
        if (nowSeconds < end + CooldownDisplay.FinishFlashSeconds) return true;
        clocks.Remove(key);
        cooldown = default;
        return false;
    }
}
