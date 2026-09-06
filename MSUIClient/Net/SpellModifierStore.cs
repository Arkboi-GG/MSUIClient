using MSUIClient.Formats;

namespace MSUIClient.Net;

public readonly record struct SpellModifierPacket(byte Bit, byte Operation, int Value)
{
    public static SpellModifierPacket Parse(byte[] body)
    {
        if (body.Length != 6) throw new InvalidDataException("Spell modifier requires bit, operation and signed value");
        var r = new PacketReader(body);
        var packet = new SpellModifierPacket(r.ReadU8(), r.ReadU8(), r.ReadI32());
        if (packet.Bit >= 64 || packet.Operation >= 29) throw new InvalidDataException("Spell modifier index outside the 5875 tables");
        return packet;
    }
}

public readonly record struct SpellModifierTotals(long Flat, long PercentDelta)
{
    // Integer division truncates toward zero, including a negative adjustment.
    public long ApplyInteger(long value) => (long)Math.Clamp(
        decimal.Truncate(((decimal)value + Flat) * (100 + PercentDelta) / 100), long.MinValue, long.MaxValue);
    public float ApplyFloat(float value) => Flat == 0 && PercentDelta == 0 ? value :
        (value + (float)Flat) * (float)(100 + PercentDelta) * .01f;
}

/// <summary>Two server-replaced 64-family-bit by 29-operation tables belonging to one actor.</summary>
public sealed class SpellModifierStore
{
    public const byte Range = 5, Radius = 6, CastTime = 10, Cooldown = 11, Cost = 14, GlobalCooldown = 21;
    private readonly int[,] _flat = new int[64, 29], _percent = new int[64, 29];
    public void Clear() { Array.Clear(_flat); Array.Clear(_percent); }
    public int Value(byte bit, byte operation, bool percentage) =>
        bit < 64 && operation < 29 ? (percentage ? _percent : _flat)[bit, operation]
            : throw new ArgumentOutOfRangeException(nameof(bit));
    public void Apply(SpellModifierPacket packet, bool percentage)
    {
        if (packet.Bit >= 64 || packet.Operation >= 29) throw new ArgumentOutOfRangeException(nameof(packet));
        (percentage ? _percent : _flat)[packet.Bit, packet.Operation] = packet.Value;
    }
    public SpellModifierTotals Totals(in SpellInfo spell, uint actorFamily, byte operation)
    {
        if (operation >= 29) throw new ArgumentOutOfRangeException(nameof(operation));
        if (actorFamily == 0 || spell.SpellFamily != actorFamily) return default;
        long flat = 0, pct = 0;
        for (int bit = 0; bit < 64; bit++)
            if ((spell.SpellFamilyFlags & (1UL << bit)) != 0)
            { flat += _flat[bit, operation]; pct += _percent[bit, operation]; }
        return new(flat, pct);
    }
}
