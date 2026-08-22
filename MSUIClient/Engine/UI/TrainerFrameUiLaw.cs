using System.Numerics;
using MSUIClient.Formats;

namespace MSUIClient.Engine.UI;

/// <summary>Current TrainerFrame window identity, top-level seat, portrait, money, and sound law.</summary>
public static class TrainerFrameUiLaw
{
    public const int VisibleRows = 11;
    public const byte AvailableState = 0;
    public const byte UsedState = 2;
    public const uint TradeskillTrainerType = 2;
    public const uint MountTrainerType = 1;
    public const uint KnownMountGroup = uint.MaxValue;
    public const float Width = 384f;
    public const float Height = 512f;
    public const float Top = 104f;
    public const string FallbackTitle = "Trainer";
    public const string OpenSound = "igCharacterInfoOpen";
    public const string CloseSound = "igCharacterInfoClose";
    public const string SoundCategory = "ui.trainer";
    public static readonly Vector2 PortraitOffset = new(7, 6);
    public const float PortraitSize = 60f;
    public const float TitleTop = 17f;
    public static readonly Vector2 PurseRightTop = new(180, 413);
    public static readonly Vector2 DetailCostLabel = new(30, 340);
    public static readonly Vector2 CollapseAllOffset = new(23, 74);
    public static readonly Vector2 FilterOffset = new(245, 65);
    public static readonly Vector2 FilterMenuOffset = new(224, 88);
    public const float MoneyGap = 4f;
    public const float MoneyIconSize = 13f;

    public static Vector2 FrameOrigin(float scale) => new(0, Top * scale);
    public static Vector2 FrameSize(float scale) => new(Width * scale, Height * scale);

    public static string Title(string? npcName) =>
        string.IsNullOrWhiteSpace(npcName) ? FallbackTitle : npcName.Trim();

    public static Vector2 TitleCenter(float fontEm) => new(Width * .5f, TitleTop + fontEm * .5f);

    public static bool StateVisible(byte state, bool available, bool unavailable, bool used) =>
        state == AvailableState ? available : state == UsedState ? used : unavailable;

    public static uint TaughtSpell(in SpellInfo wire)
    {
        if (wire.EffectIds is null || wire.EffectTriggerSpells is null) return wire.Id;
        int count = Math.Min(wire.EffectIds.Length, wire.EffectTriggerSpells.Length);
        for (int i = 0; i < count; i++)
            if (wire.EffectIds[i] is 36 or 57 && wire.EffectTriggerSpells[i] != 0)
                return wire.EffectTriggerSpells[i];
        return wire.Id;
    }

    public static (uint Key, string Name) ServiceGroup(uint trainerType, byte state,
        in SpellInfo wire, SkillLineCatalog? skillLines)
    {
        if (trainerType == TradeskillTrainerType)
            return wire.EffectIds?.Contains(44u) == true
                ? (1u, "Development Skills") : (2u, "Recipes");
        if (trainerType == MountTrainerType && state == UsedState)
            return (KnownMountGroup, "My Talents");
        uint line = skillLines?.SpellLine(TaughtSpell(wire)) ?? 0;
        if (line == 0) return (0, "");
        return (line, skillLines?.TryGet(line, out SkillLineInfo info) == true
            ? info.Name : $"Skill {line}");
    }

    public readonly record struct ServiceNode(int ServiceIndex, uint GroupKey, string GroupName,
        string Name, byte State, byte RequiredLevel);
    public readonly record struct TreeRow(bool Header, uint GroupKey, string Text,
        int ServiceIndex, byte State, bool Expanded);

    public static IReadOnlyList<TreeRow> BuildTree(IEnumerable<ServiceNode> services,
        uint trainerType, IReadOnlySet<uint> collapsed, bool available, bool unavailable, bool used)
    {
        var groups = services
            .Where(s => s.GroupKey != 0 && StateVisible(s.State, available, unavailable, used))
            .GroupBy(s => new { s.GroupKey, s.GroupName })
            .Select(group => new
            {
                Key = group.Key.GroupKey,
                Name = group.Key.GroupName,
                Services = group.OrderBy(s => s.RequiredLevel)
                    .ThenBy(s => s.State == AvailableState ? 0 : s.State == UsedState ? 2 : 1)
                    .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            });
        groups = trainerType == TradeskillTrainerType
            ? groups.OrderBy(g => g.Key)
            : groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Key);

        var rows = new List<TreeRow>();
        foreach (var group in groups)
        {
            bool expanded = !collapsed.Contains(group.Key);
            rows.Add(new(true, group.Key, group.Name, -1, 0, expanded));
            if (!expanded) continue;
            rows.AddRange(group.Services.Select(service => new TreeRow(false, group.Key,
                service.Name, service.ServiceIndex, service.State, false)));
        }
        return rows;
    }
}
