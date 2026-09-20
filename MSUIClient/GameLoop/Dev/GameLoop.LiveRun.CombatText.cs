using MSUIClient.Engine.UI;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool InspectLiveCombatText(string line)
    {
        if (line == "combat-text capture-mixed")
        {
            // Only capture received critical feedback mixed with scrolling text.
            if (!_centerCombatText.Any(x => x.Critical) || !_centerCombatText.Any(x => !x.Critical)) return true;
            _currentVantage = "live-mixed-combat-text";
            ArmGameplayDump();
        }
        else if (line != "combat-text inspect") return false;
        Console.WriteLine($"[live-combat-text] actor=0x{ControlledGuid:X};count={_centerCombatText.Count}");
        foreach (CenterText item in _centerCombatText)
            Console.WriteLine(FormattableString.Invariant(
                $"[live-combat-text-row] text={item.Text};critical={item.Critical};age={item.Age:R};start={item.StartOffset:R};offset={CombatTextStateUiLaw.CenterMessageOffset(item.StartOffset, item.Age, item.Critical):R};lane={item.Lane}"));
        return true;
    }
}
