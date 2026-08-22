namespace MSUIClient.Engine.UI;

public readonly record struct QuestMarkerStyle(string ModelPath);

/// <summary>Build-5875 dialog-status to TalkToMe marker mapping.</summary>
public static class QuestMarkerUiLaw
{
    public static readonly QuestMarkerStyle UnknownFlightMaster =
        new(@"Interface\Buttons\TalkToMeGreen.m2");

    public static QuestMarkerStyle? Style(uint status) => status switch
    {
        1 => new(@"Interface\Buttons\TalkToMeGrey.m2"),
        3 => new(@"Interface\Buttons\TalkToMeQuestion_Grey.m2"),
        4 => new(@"Interface\Buttons\TalkToMeQuestion_LTBlue.m2"),
        5 => new(@"Interface\Buttons\TalkToMe.m2"),
        6 or 7 => new(@"Interface\Buttons\TalkToMeQuestionMark.m2"),
        _ => null,
    };
}
