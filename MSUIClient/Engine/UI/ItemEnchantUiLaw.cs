using System.Numerics;

namespace MSUIClient.Engine.UI;

public sealed class ItemEnchantTimerState
{
    private readonly Dictionary<(ulong Guid, uint Slot), double> _deadlines = [];

    public void Set(ulong guid, uint slot, uint seconds, double now)
    {
        if (seconds == 0) _deadlines.Remove((guid, slot));
        else _deadlines[(guid, slot)] = now + seconds;
    }

    public ulong? RemainingMilliseconds(ulong guid, uint slot, double now)
    {
        if (!_deadlines.TryGetValue((guid, slot), out double deadline)) return null;
        double left = deadline - now;
        return left > 0 ? (ulong)(left * 1000.0) : null;
    }

    public void Clear() => _deadlines.Clear();
}

/// <summary>Build-5875 item-tooltip enchant line color/countdown/charges law.</summary>
public static class ItemEnchantUiLaw
{
    public static Vector4 Color(int slot, int signedId) => slot < 2
        ? signedId < 0 ? new Vector4(1, 0, 0, 1) : new Vector4(0, 1, 0, 1)
        : Vector4.One;

    public static string Text(string name, ulong? remainingMs, uint charges)
    {
        string text = remainingMs is ulong ms ? Countdown(name, ms) : name;
        if (charges != 0)
            text += charges == 1 ? " (1 Charge)" : $" ({charges} Charges)";
        return text;
    }

    public static string Countdown(string name, ulong ms)
    {
        const ulong second = 1_000;
        const ulong minute = 60 * second;
        const ulong hour = 60 * minute;
        const ulong day = 24 * hour;
        static ulong Ceil(ulong value, ulong unit) => (value + unit - 1) / unit;
        if (ms >= day)
        {
            ulong count = Ceil(ms, day);
            return count == 1 ? $"{name} (1 day)" : $"{name} ({count} days)";
        }
        if (ms >= hour)
        {
            ulong count = Ceil(ms, hour);
            return count == 1 ? $"{name} (1 hour)" : $"{name} ({count} hrs)";
        }
        if (ms >= minute) return $"{name} ({Ceil(ms, minute)} min)";
        return $"{name} ({ms / second} sec)";
    }
}
