using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private string ItemHonorRankName(uint rank, WorldEntity? actor)
    {
        string fallback = rank is >= 5 and <= 18 ? $"PvP Rank {rank - 4}" : "a higher PvP rank";
        if (actor is null) return fallback;
        var (race, _, sex, _) = actor.Fields.Bytes0;
        if (race is not (1 or 2 or 3 or 4 or 5 or 6 or 7 or 8)) return fallback;
        int faction = race is 1 or 3 or 4 or 7 ? 1 : 0;
        string key = $"PVP_RANK_{rank}_{faction}";
        string title = InventoryGlobalString(key, fallback);
        return sex == 1 ? InventoryGlobalString(key + "_FEMALE", title) : title;
    }

    private IEnumerable<string> ItemLocationRequirements(ItemTemplate item)
    {
        // These are carrying restrictions. The core removes limited items on
        // zone transitions; a tooltip must not turn them into invented use gates.
        if (item.RestrictedArea != 0)
        {
            string? area = _areas?.AreaName(item.RestrictedArea);
            yield return string.IsNullOrWhiteSpace(area) ? "Limited to a specific zone" : $"Limited to {area}";
        }
        if (item.RestrictedMap != 0)
        {
            string? map = _maps?.Get(unchecked((int)item.RestrictedMap))?.Name;
            yield return string.IsNullOrWhiteSpace(map) ? "Limited to a specific map" : $"Limited to {map}";
        }
    }
}
