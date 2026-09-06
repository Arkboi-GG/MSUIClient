namespace MSUIClient.Engine.UI;

public static class InstanceBootUiLaw
{
    public const string PopupType = "INSTANCE_BOOT";
    public const string FallbackText = "You are not in this instance's group. You will be teleported to %s in %d %s.";

    public static StaticPopupCoordinatorLaw.Definition Definition(double seconds) => new(
        PopupType, WhileDead: true, UsesTimeoutText: true, TimeoutSeconds: seconds);

    public static (int Count, bool Minutes) TimeUnit(double seconds)
    {
        double rounded = Math.Max(0, Math.Ceiling(seconds));
        return rounded < 60 ? ((int)rounded, false) : ((int)Math.Ceiling(rounded / 60), true);
    }

    public static string Text(string template, string destination, int count, string unit)
    {
        static string ReplaceFirst(string text, string token, string value)
        {
            int index = text.IndexOf(token, StringComparison.Ordinal);
            return index < 0 ? text : text.Remove(index, token.Length).Insert(index, value);
        }
        return ReplaceFirst(ReplaceFirst(ReplaceFirst(template, "%s", destination), "%d",
            count.ToString(System.Globalization.CultureInfo.InvariantCulture)), "%s", unit);
    }
}
