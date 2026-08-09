using System.Text;
using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}

Require(MailUiLaw.InboxItemsPerPage == 7 && MailUiLaw.PageCount(0) == 1 &&
        MailUiLaw.PageCount(7) == 1 && MailUiLaw.PageCount(8) == 2 &&
        MailUiLaw.FirstIndex(2, 8) == 7,
    "seven-row inbox/page boundary drift");
Require(BitConverter.SingleToUInt32Bits(MailUiLaw.NewMailEpsilon) == 0x3480_0000 &&
        MailUiLaw.HasNewMail(0) && MailUiLaw.HasNewMail(MailUiLaw.NewMailEpsilon / 2) &&
        MailUiLaw.HasNewMail(-MailUiLaw.NewMailEpsilon / 2) &&
        !MailUiLaw.HasNewMail(MailUiLaw.NewMailEpsilon) &&
        !MailUiLaw.HasNewMail(-MailUiLaw.NewMailEpsilon) &&
        !MailUiLaw.HasNewMail(float.NaN) && !MailUiLaw.HasNewMail(-86400),
    "strict symmetric 2^-22 pending-mail predicate drift");
Require(MailUiLaw.StepNewMailCountdown(-1, .5f) == -1 &&
        MailUiLaw.StepNewMailCountdown(0, .5f) == 0 &&
        MailUiLaw.StepNewMailCountdown(.5f, .125f) == .375f &&
        MailUiLaw.StepNewMailCountdown(.5f, 10) == 0,
    "pending-mail step/floor law drift");
Require(MailUiLaw.ApplyReceivedMailCountdown(-86400, 0, true) == -86400 &&
        MailUiLaw.ApplyReceivedMailCountdown(-1, 120, false) == 120 &&
        MailUiLaw.ApplyReceivedMailCountdown(120, 60, false) == 60 &&
        MailUiLaw.ApplyReceivedMailCountdown(60, 300, false) == 60,
    "received-mail busy/near-zero/tighten ladder drift");

string astralRunes = string.Concat(Enumerable.Repeat("A\U0001F600", 20));
string twelve = MailUiLaw.TruncateLetters(astralRunes, MailUiLaw.MaxRecipientLetters);
Require(twelve.EnumerateRunes().Count() == 12 && !twelve.EndsWith('\uD83D') &&
        MailUiLaw.MaxRecipientLetters == 12 && MailUiLaw.MaxSubjectLetters == 64 &&
        MailUiLaw.MaxBodyLetters == 500,
    "mail letter caps split a scalar or drifted from 12/64/500");
Require(MailUiLaw.CanSend(" ", "subject", false, 0, false, false) &&
        !MailUiLaw.CanSend("", "subject", false, 0, false, false) &&
        !MailUiLaw.CanSend("name", "", false, 0, false, false) &&
        !MailUiLaw.CanSend("name", "subject", true, 0, false, false) &&
        !MailUiLaw.CanSend("name", "subject", true, MailUiLaw.MaxCodCopper + 1, true, false) &&
        MailUiLaw.CanSend("name", "subject", true, MailUiLaw.MaxCodCopper, true, false),
    "reference send-enable predicate drift");

Require(MailUiLaw.Money(0).SequenceEqual([new MailUiLaw.MoneyDenomination(2, 0)]) &&
        MailUiLaw.Money(20_000).SequenceEqual([new MailUiLaw.MoneyDenomination(0, 2)]) &&
        MailUiLaw.Money(10_203).SequenceEqual([
            new MailUiLaw.MoneyDenomination(0, 1),
            new MailUiLaw.MoneyDenomination(1, 2),
            new MailUiLaw.MoneyDenomination(2, 3)]),
    "SmallMoneyFrame denomination collapse/order drift");

string repo = ClientConfig.FindRepoRoot();
using (var mpq = new MpqMount(Path.Combine(repo, "GameData", "Data")))
{
    StationeryCatalog stationery = StationeryCatalog.Load(mpq) ??
        throw new InvalidDataException("Stationery.dbc unavailable");
    Require(stationery.Count == 5 && stationery.Texture(1) == "STATIONERYTEST" &&
            stationery.Texture(41) == "STATIONERYTEST" &&
            stationery.Texture(61) == "GMSTATIONERY" &&
            stationery.Texture(62) == "AUCTIONSTATIONERY" &&
            stationery.Texture(64) == "STATIONERY_VAL" &&
            stationery.Texture(uint.MaxValue) == StationeryCatalog.DefaultTexture,
        "Stationery.dbc id-to-texture lookup drift");
}

string source = File.ReadAllText(Path.Combine(repo, "MSUIClient", "Program.Mail.cs"));
Require(!source.Contains("ImGui.IsItemClicked", StringComparison.Ordinal),
    "Mail click actions regressed from ButtonUp ownership to press-edge ownership");
Require(source.Contains("TexCoords: \"0|0|1|0.25\"", StringComparison.Ordinal) &&
        source.Contains("TexCoords: \"0|0.25|0.29296875|0.5\"", StringComparison.Ordinal),
    "two-slice horizontal-bar UV proof missing");
Require(source.Contains("DrawArt(dl, MailStationeryIcon(row), portraitMin", StringComparison.Ordinal) &&
        source.IndexOf("DrawArt(dl, MailStationeryIcon(row), portraitMin", StringComparison.Ordinal) <
        source.IndexOf("TraceMailShell(\"OpenMailFrame\"", StringComparison.Ordinal),
    "open-letter portrait is no longer behind its round shell aperture");
Require(source.Contains("WindowPadding, Vector2.Zero", StringComparison.Ordinal) &&
        source.Contains("WindowBorderSize, 0f", StringComparison.Ordinal),
    "mail owner windows no longer expose exact unclipped bounds");
Require(!source.Contains("DrawMailMoneyDisplay(dl, row.Cod", StringComparison.Ordinal) &&
        !source.Contains("DrawMailMoneyDisplay(dl, MailAmountCopper()", StringComparison.Ordinal),
    "named-inert popup money frames were rendered");
Require(source.Contains("ExpandQuestText(body)", StringComparison.Ordinal) &&
        source.Contains("MailAttachmentCount()", StringComparison.Ordinal),
    "mail body macro or attachment-stack dynamic content path missing");
Require(source.Contains("TraceMailStationery(\"SendStationeryBackground\"", StringComparison.Ordinal) &&
        source.Contains("TraceMailStationery(\"OpenStationeryBackground\"", StringComparison.Ordinal) &&
        source.Contains("\"SendMailScroll\", clip", StringComparison.Ordinal) &&
        source.Contains("\"OpenMailScroll\", clip", StringComparison.Ordinal) &&
        source.Contains("0.515625|0|1|0.4140625", StringComparison.Ordinal),
    "stationery or exact inert-scroll UV telemetry missing");

Console.WriteLine("mail-clinical-check PASS");
