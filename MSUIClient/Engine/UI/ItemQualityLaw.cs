using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>
/// Item-quality color/name lookup, driven by ClientConfig.ItemQualityColors.
///
/// Previously this exact switch expression was duplicated four times
/// (GroupLootFrameUiLaw, GameLoop.Inventory's tooltip color, GameLoop.Loot's
/// color, and the item Creator's array) with no name table at all, and every
/// copy silently assumed exactly 7 tiers (0 Poor .. 6 Artifact). A server
/// running extra tiers had no way to make the client aware of them short of
/// editing every copy. This is the single place that now owns it — servers
/// customize tiers via client-config.json, not by patching the client.
/// </summary>
public static class ItemQualityLaw
{
    private static Dictionary<int, Vector4> _colors = new();
    private static Dictionary<int, string> _names = new();

    /// <summary>Call once at startup, after ClientConfig.Load(). Idempotent —
    /// safe to call again if a config reload ever adds that feature.</summary>
    public static void Initialize(ClientConfig config)
    {
        var colors = new Dictionary<int, Vector4>();
        var names = new Dictionary<int, string>();

        foreach (var entry in config.ItemQualityColors)
        {
            colors[entry.Quality] = new Vector4(entry.R, entry.G, entry.B, 1f);
            names[entry.Quality] = entry.Name;
        }

        _colors = colors;
        _names = names;
    }

    /// <summary>White for any quality not present in config — same fallback the
    /// four duplicated switch expressions this replaces already used.</summary>
    public static Vector4 Color(uint quality) =>
        _colors.TryGetValue((int)quality, out var color) ? color : Vector4.One;

    /// <summary>"Quality N" for any quality not present in config — there was no
    /// shared name table before this to fall back to, so this fallback is new,
    /// not a behavior change.</summary>
    public static string Name(uint quality) =>
        _names.TryGetValue((int)quality, out var name) ? name : $"Quality {quality}";
}
