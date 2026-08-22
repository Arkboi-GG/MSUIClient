namespace MSUIClient.Engine.UI;

public enum MicroMenuButtonId
{
    Character,
    Spellbook,
    Talents,
    QuestLog,
    Social,
    WorldMap,
    MainMenu,
    Help,
}

public readonly record struct MicroMenuButtonSpec(
    MicroMenuButtonId Id, string Art, string Label, string NewbieText);

/// <summary>Current Benilla micro-button order, level gate, geometry, and detailed hover copy.</summary>
public static class MicroMenuUiLaw
{
    public const uint TalentUnlockLevel = 10;
    public const float ButtonStride = 26f;

    public static readonly MicroMenuButtonSpec[] Buttons =
    [
        new(MicroMenuButtonId.Character, "Character", "Character Info",
            "Information about your character, including equipment, statistics, skills, and reputation."),
        new(MicroMenuButtonId.Spellbook, "Spellbook", "Spellbook & Abilities",
            "All of your spells and abilities. To move a spell or ability to your Action Bar, open the Spellbook & Abilities window, left-click that spell or ability, and drag it down to your Action Bar."),
        new(MicroMenuButtonId.Talents, "Talents", "Talents",
            "The array of talents available to enhance and specialize your character."),
        new(MicroMenuButtonId.QuestLog, "Quest", "Quest Log",
            "A list of all the active quests you currently have. You can have up to 20 active quests at one time."),
        new(MicroMenuButtonId.Social, "Socials", "Social",
            "Information about other people in the game. You can use the Social window to manage your friends list and ignore list, as well as see who is online."),
        new(MicroMenuButtonId.WorldMap, "World", "World Map",
            "As you explore a zone, more areas of interest will become visible on the world map. You can use left-click to zoom in, or right-click to zoom out."),
        new(MicroMenuButtonId.MainMenu, "MainMenu", "Game Menu",
            "Here you can modify your video, sound, and interface settings, or create custom hotkeys. You can also choose to log out or exit the program altogether."),
        new(MicroMenuButtonId.Help, "Help", "Help Request",
            "Through this menu, you can report a bug. You can also use the Help Request menu to talk to one of our Game Masters (GMs) in the game if you have questions or need assistance."),
    ];

    public static bool TalentVisible(uint playerLevel) => playerLevel >= TalentUnlockLevel;

    public static IEnumerable<MicroMenuButtonSpec> VisibleButtons(uint playerLevel) =>
        Buttons.Where(button => button.Id != MicroMenuButtonId.Talents || TalentVisible(playerLevel));

    public static float ButtonX(int visibleOrdinal) => Math.Max(0, visibleOrdinal) * ButtonStride;

    public static string TooltipTitle(string label, string? bindingText) =>
        string.IsNullOrWhiteSpace(bindingText) ? label : $"{label} ({bindingText})";
}
