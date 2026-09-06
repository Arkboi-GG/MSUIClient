using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using MSUIClient.Formats;

namespace MSUIClient.Engine.UI;

public static partial class WorldStateUiLaw
{
    public readonly record struct Display(WorldStateUiRow Row, uint State, string Text, string Tooltip,
        string DynamicTooltip, uint CaptureValue);
    [GeneratedRegex(@"%(\d+)[wW]", RegexOptions.CultureInvariant)]
    private static partial Regex WorldToken();
    public static string Expand(string text, IReadOnlyDictionary<uint,uint> states) => WorldToken().Replace(text,
        match => uint.TryParse(match.Groups[1].Value, out uint id)
            ? unchecked((int)states.GetValueOrDefault(id)).ToString(CultureInfo.InvariantCulture) : match.Value);

    public static IReadOnlyList<Display> Evaluate(WorldStateUiCatalog catalog, uint map, uint zone,
        IReadOnlyDictionary<uint,uint> states)
    {
        var result = new List<Display>();
        foreach (var row in catalog.Rows)
        {
            // Mounted rows use only unrestricted faction sentinels0/-1. Preserve other rows in the
            // catalog but do not invent a faction predicate for data not yet empirically traced.
            if (row.Map != map || row.Area != 0 && row.Area != zone || row.Type > 1 ||
                row.Faction is not (0 or -1)) continue;
            uint state = row.StateVariable == 0 ? 1 : states.GetValueOrDefault(row.StateVariable);
            if (state == 0) continue;
            result.Add(new(row,state,Expand(row.Text,states),Expand(row.Tooltip,states),
                Expand(row.DynamicTooltip,states),states.GetValueOrDefault(row.ExtendedState1)));
        }
        return result;
    }
    // WorldStateAlwaysUpFrame TOP(-5,-15), first child TOP(-23,-20),45x24.
    public static Vector2 AlwaysUpMin(Vector2 display, float scale, int index) =>
        new(display.X / 2 - 50.5f * scale, (35 + 24 * index) * scale);
    public static float CaptureIndicator(uint value) => 25 + 124 * (1 - Math.Clamp(value,0u,100u) / 100f);
    public static bool CaptureAllianceHighlight(uint value) => value > 60;
    public static bool CaptureHordeHighlight(uint value) => value < 40;
}
