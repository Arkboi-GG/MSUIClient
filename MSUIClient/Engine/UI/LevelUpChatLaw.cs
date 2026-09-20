using System.Globalization;
using System.Text.RegularExpressions;

namespace MSUIClient.Engine.UI;

/// <summary>The mounted ChatFrame.lua PLAYER_LEVEL_UP message order and gates.</summary>
public static class LevelUpChatLaw
{
    public static IReadOnlyList<string> Lines(uint level, uint health, uint mana,
        IReadOnlyList<uint> stats, Func<string, string, string>? text = null)
    {
        string Format(string key, string fallback, params object[] values)
        {
            int index = 0;
            return Regex.Replace(text?.Invoke(key, fallback) ?? fallback, "%[ds]",
                _ => Convert.ToString(values[index++], CultureInfo.InvariantCulture) ?? "");
        }
        var lines = new List<string>
        {
            Format("LEVEL_UP", "Congratulations, you have reached level %d!", level),
            mana > 0
                ? Format("LEVEL_UP_HEALTH_MANA", "You have gained %d hit points and %d mana.", health, mana)
                : Format("LEVEL_UP_HEALTH", "You have gained %d hit points.", health),
        };
        if (level >= 10)
            lines.Add(Format("LEVEL_UP_CHAR_POINTS", "You have gained %d talent point.", 1));
        string[] names = ["Strength", "Agility", "Stamina", "Intellect", "Spirit"];
        for (int i = 0; i < names.Length && i < stats.Count; i++)
            if (stats[i] > 0)
                lines.Add(Format("LEVEL_UP_STAT", "Your %s increases by %d.",
                    text?.Invoke($"SPELL_STAT{i}_NAME", names[i]) ?? names[i], stats[i]));
        return lines;
    }
}
