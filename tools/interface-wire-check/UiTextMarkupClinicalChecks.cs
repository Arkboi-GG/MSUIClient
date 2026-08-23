using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;

internal static class UiTextMarkupClinicalChecks
{
    public static void Run()
    {
        Func<string, float> charMeasure = text => text.Length;
        Check(FontStringOverflowLaw.LinesAllowed(38, 12) == 4 &&
              FontStringOverflowLaw.LinesFitting(38, 12) == 3,
            "FontString render/fitting line-count split drift");
        Func<string, int> rows10 = text => Math.Max(1, (text.Length + 9) / 10);
        string lootOverflow = FontStringOverflowLaw.Ellipsize(
            "Schematic: Small Seaforium Charge", 3, rows10);
        string pouchOverflow = FontStringOverflowLaw.Ellipsize(
            "Small Brown Pouch", 10, 12, 12, charMeasure);
        string unicodeOverflow = FontStringOverflowLaw.Ellipsize(
            "Ancêtre éternel", 10, 12, 12, charMeasure);
        Check(lootOverflow == "Schematic: Small Seaforium ..." &&
              pouchOverflow == "Small B..." && unicodeOverflow == "Ancêtre...",
            $"FontString ASCII-marker/scalar-safe ellipsis drift: " +
            $"loot='{lootOverflow}', pouch='{pouchOverflow}', unicode='{unicodeOverflow}'");
        Check(FontStringOverflowLaw.Ellipsize("145", 10, 10, 12, charMeasure) == "145" &&
              FontStringOverflowLaw.Ellipsize("Small Brown Pouch",
                  10, 10, 12, charMeasure) == "Small B..." &&
              FontStringOverflowLaw.Ellipsize("abcdef", 1.1f, 1.1f, 12, charMeasure) == "...",
            "FontString min-one-line/bare-marker floor drift");
        Check(FontStringOverflowLaw.WrappedRows("Refreshing Spring Water", 12,
                  charMeasure) == 2 &&
              FontStringOverflowLaw.WrappedRows("Supercalifragilistic ok", 8,
                  charMeasure) == 3 &&
              FontStringOverflowLaw.WrappedRows("Hello.  World", 6,
                  charMeasure) == 2,
            "FontString greedy whitespace/force-break wrapping drift");

        Vector4 white = Vector4.One;
        IReadOnlyList<UiTextMarkupLine> color = UiTextMarkupLaw.Parse(
            "a|c80ff0000b|rc", white);
        Check(color.Count == 1 && color[0].Runs.Count == 3 &&
              color[0].VisibleText == "abc" &&
              color[0].Runs[1].Color == new Vector4(1, 0, 0, 1) &&
              color[0].Runs[2].Color == white,
            "ui-text color/reset parsing drift");

        IReadOnlyList<UiTextMarkupLine> link = UiTextMarkupLaw.Parse(
            "|cff1eff00|Hitem:2000:0:0:0|h[Another Helm]|h|r ok", white);
        Check(link.Count == 1 && link[0].VisibleText == "[Another Helm] ok" &&
              link[0].Runs[0].Link is { Payload: "item:2000:0:0:0",
                  Markup: "|Hitem:2000:0:0:0|h[Another Helm]|h" } &&
              link[0].Runs[^1].Link is null,
            "ui-text hyperlink parsing/reconstruction drift");

        IReadOnlyList<UiTextMarkupLine> tokens = UiTextMarkupLaw.Parse(
            "a||b|none|TInterface\\Icons\\Foo:16:16|tz", white);
        Check(tokens.Count == 2 && tokens[0].VisibleText == "a|b" &&
              tokens[1].VisibleText == @"one|TInterface\Icons\Foo:16:16|tz",
            "ui-text escaped-pipe/line-break/build-5875 literal-T law drift");

        IReadOnlyList<UiTextMarkupLine> wrapped = UiTextMarkupLaw.Wrap(
            "|Hplayer:Bob|h[Bob Smith]|h says hello", white,
            text => text.Length, 10);
        Check(wrapped.Count >= 2 && wrapped[0].Runs.All(run => run.Link is not null) &&
              wrapped.SelectMany(line => line.Runs).Any(run => run.Link is null),
            "ui-text wrap lost hyperlink identity");

        Vector2 size = ItemRefTooltipUiLaw.Size(100, 2);
        Check(size == new Vector2(148, 64) &&
              ItemRefTooltipUiLaw.Origin(new Vector2(1024, 768), size) ==
                  new Vector2(438, 624) &&
              ItemRefTooltipUiLaw.CloseOrigin(size) == new Vector2(117, 0),
            "item-ref bottom-center dynamic window law drift");

        string formatted = ChatFrameLaw.FormatLine(ChatFrameLaw.MsgType.Say,
            "Nico", "", "hello");
        Check(formatted.Contains("|Hplayer:Nico|h[Nico]|h", StringComparison.Ordinal),
            "chat sender does not emit a player hyperlink");

        string root = ClientConfig.FindRepoRoot();
        string chat = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Chat.cs"));
        string itemRef = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.ItemRef.cs"));
        string social = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Social.cs"));
        string guild = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Guild.cs"));
        Check(chat.Contains("UiTextMarkupLaw.Wrap", StringComparison.Ordinal) &&
              chat.Contains("ActivateChatLink", StringComparison.Ordinal) &&
              !chat.Contains("text.Replace('|', ' ')", StringComparison.Ordinal) &&
              itemRef.Contains("ItemRefTooltipUiLaw.Origin", StringComparison.Ordinal) &&
              itemRef.Contains("OpenFriendPopup", StringComparison.Ordinal) &&
              itemRef.Contains("_chatInput += link.Markup", StringComparison.Ordinal) &&
              !itemRef.Contains("BeginVanillaWindow(\"##item-ref-tooltip\", new Vector2",
                  StringComparison.Ordinal),
            "chat/item-ref runtime bypasses markup or positioning law");
        Check(social.Contains("GameText.EllipsizeToBox", StringComparison.Ordinal) &&
              guild.Contains("GameText.EllipsizeToBox", StringComparison.Ordinal),
            "fixed-size Social/Guild FontStrings bypass the shared overflow law");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
