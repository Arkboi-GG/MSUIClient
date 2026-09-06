using System.Reflection;
using System.Text.RegularExpressions;

namespace MSUIClient.Engine.UI;

/// <summary>
/// The Macro Book's linter and command reference (cam, 2026-09-04: "there is a limited amount of
/// commands and the syntax is pretty well documented, so a linter might be possible"). It is
/// deterministic and offline: the Core's whole command tree is exported from Chat.cpp into
/// Data/vmangos-commands.tsv (tools/macro-commands/export-commands.py) and embedded at build
/// time; the client's own slash verbs are the list below; emotes come from
/// <see cref="EmoteCommandLaw"/>. A macro line is one of four things - a comment, a slash
/// command, a dot command, or plain chat - and each is checked the way the receiving side would
/// parse it (the Core resolves dot commands by unique prefix per level, so ".addi 14460" is
/// accepted and named).
/// </summary>
public static partial class MacroLintLaw
{
    public const string ResourceName = "MSUIClient.Data.vmangos-commands.tsv";

    public enum Severity
    {
        Info,
        Warning,
        Error,
    }

    public readonly record struct Diagnostic(int Line, Severity Severity, string Message);

    public sealed record ServerCommand(string Name, string Security, bool Runnable,
        bool HasSubcommands);

    public enum MatchState
    {
        Resolved,
        Unknown,
        Ambiguous,
        NeedsSubcommand,
    }

    /// <summary><paramref name="Resolved"/> is the full command name the Core would run;
    /// <paramref name="Arguments"/> what follows it. <paramref name="Detail"/> names the
    /// offending token or the ambiguous candidates.</summary>
    public readonly record struct Match(MatchState State, ServerCommand? Command,
        string Resolved, string Arguments, string Detail);

    /// <summary>Slash verbs the client itself answers (GameLoop.Chat + ParseChatCommand). A
    /// verb missing here that 1.12 had is a verb this client does not arm yet - the linter is
    /// honest about that rather than hopeful.</summary>
    public static readonly IReadOnlySet<string> ClientVerbs = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "/addfriend", "/addignore", "/afk", "/away", "/battleground", "/bg", "/busy", "/camp",
        "/cast", "/chan", "/channel", "/chatexit", "/chatinfo", "/chatleave", "/chatlist",
        "/chatwho", "/comp", "/companions", "/convertraid", "/dance", "/dnd", "/duel", "/e",
        "/edithud", "/editui", "/em", "/emote", "/exit", "/f", "/fol", "/follow", "/friend",
        "/g", "/ginfo", "/guild", "/hudlayout", "/ignore", "/inspect", "/join", "/kneel", "/lay",
        "/laydown", "/leave", "/lie", "/liedown", "/logout", "/me", "/o", "/officer", "/p",
        "/party", "/partytest", "/played", "/pvp", "/quit", "/r", "/ra", "/raid", "/raidconvert",
        "/raidinfo", "/raidwarning", "/rand", "/random", "/rc", "/readycheck", "/reply", "/rnd",
        "/roll", "/rw", "/s", "/saved", "/say", "/sit", "/sleep", "/spell", "/stable",
        "/stables", "/stand", "/startattack", "/stopattack", "/t", "/tell", "/trade", "/use",
        "/w", "/wave", "/whisper", "/who", "/y", "/yell",
    };

    internal sealed class Node
    {
        public string Name = "";
        public string Security = "";
        public bool Runnable;
        public readonly SortedDictionary<string, Node> Children = new(StringComparer.Ordinal);
    }

    public sealed class CommandCatalog
    {
        private readonly Node _root;
        public IReadOnlyList<ServerCommand> ServerCommands { get; }

        internal CommandCatalog(Node root, IReadOnlyList<ServerCommand> commands)
        {
            _root = root;
            ServerCommands = commands;
        }

        internal Node Root => _root;
        public static CommandCatalog Empty { get; } = new(new Node(), []);
    }

    /// <summary>The build-time embedded export; empty (never null) when the resource is absent.</summary>
    public static CommandCatalog LoadEmbedded()
    {
        try
        {
            using Stream? stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(ResourceName);
            if (stream is null) return CommandCatalog.Empty;
            using var reader = new StreamReader(stream);
            return Parse(reader.ReadToEnd());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[macros] command catalog failed: {ex.Message}");
            return CommandCatalog.Empty;
        }
    }

    /// <summary>TSV: name, security, runnable, has_subcommands. vmangos lists a group twice
    /// (a bare-handler row and a table row); rows merge by name, flags OR together, and the
    /// runnable row's security wins because that is the one a bare call is checked against.</summary>
    public static CommandCatalog Parse(string tsv)
    {
        var root = new Node();
        var byName = new SortedDictionary<string, ServerCommand>(StringComparer.Ordinal);
        foreach (string raw in tsv.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith("name\t", StringComparison.Ordinal)) continue;
            string[] fields = line.Split('\t');
            if (fields.Length < 4) continue;
            string name = fields[0].Trim();
            if (name.Length == 0) continue;
            bool runnable = fields[2].Trim() == "1";
            bool hasSub = fields[3].Trim() == "1";
            string security = fields[1].Trim();
            Node node = root;
            foreach (string token in name.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!node.Children.TryGetValue(token, out Node? child))
                    node.Children[token] = child = new Node { Name = token };
                node = child;
            }
            if (runnable || node.Security.Length == 0) node.Security = security;
            node.Runnable |= runnable;
            if (byName.TryGetValue(name, out ServerCommand? existing))
                byName[name] = existing with
                {
                    Runnable = existing.Runnable | runnable,
                    HasSubcommands = existing.HasSubcommands | hasSub,
                    Security = runnable ? security : existing.Security,
                };
            else byName[name] = new ServerCommand(name, security, runnable, hasSub);
        }
        return new CommandCatalog(root, byName.Values.ToArray());
    }

    /// <summary>The Core's SEC_* rank as a short shelf tag ("" for anyone).</summary>
    public static string SecurityLabel(string security) => security switch
    {
        "SEC_PLAYER" => "",
        "SEC_MODERATOR" => "Mod",
        "SEC_TICKETMASTER" => "Ticket",
        "SEC_GAMEMASTER" => "GM",
        "SEC_BASIC_ADMIN" => "Admin",
        "SEC_DEVELOPER" => "Dev",
        "SEC_ADMINISTRATOR" => "Admin",
        "SEC_CONSOLE" => "Console",
        _ => security.StartsWith("SEC_", StringComparison.Ordinal) ? security[4..] : security,
    };

    /// <summary>Shelf search: prefix matches first, then anywhere, capped. Groups that cannot
    /// run on their own are left out - the shelf inserts runnable lines.</summary>
    public static IReadOnlyList<ServerCommand> Search(CommandCatalog catalog, string filter,
        int limit)
    {
        filter = filter.Trim();
        IEnumerable<ServerCommand> runnable = catalog.ServerCommands
            .Where(command => command.Runnable);
        if (filter.Length == 0) return runnable.Take(limit).ToArray();
        ServerCommand[] prefixed = runnable
            .Where(command => command.Name.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        ServerCommand[] inner = runnable
            .Where(command => !command.Name.StartsWith(filter, StringComparison.OrdinalIgnoreCase) &&
                command.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return prefixed.Concat(inner).Take(limit).ToArray();
    }

    public static string InsertionText(ServerCommand command) => "." + command.Name + " ";

    /// <summary>
    /// The Core's own resolution (ChatHandler::FindCommand): at each level a token matches a
    /// subcommand exactly or as a unique prefix; once the deepest matched node has a handler the
    /// rest of the line is its arguments. A group without a handler and no matching subcommand
    /// is "needs a subcommand"; several prefix matches at the top level is "ambiguous".
    /// </summary>
    public static Match ResolveServerCommand(CommandCatalog catalog, string lineWithoutDot)
    {
        string[] tokens = lineWithoutDot.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Node node = catalog.Root;
        var path = new List<string>();
        int consumed = 0;
        while (consumed < tokens.Length && node.Children.Count > 0)
        {
            string token = tokens[consumed];
            if (node.Children.TryGetValue(token.ToLowerInvariant(), out Node? exact))
            {
                node = exact;
                path.Add(exact.Name);
                consumed++;
                continue;
            }
            Node[] prefixed = node.Children.Values
                .Where(child => child.Name.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (prefixed.Length == 1)
            {
                node = prefixed[0];
                path.Add(node.Name);
                consumed++;
                continue;
            }
            if (prefixed.Length > 1 && !node.Runnable)
            {
                string candidates = string.Join(", ", prefixed.Take(4).Select(child => child.Name));
                return new Match(MatchState.Ambiguous, null, string.Join(' ', path),
                    string.Join(' ', tokens.Skip(consumed)), $"{token}: {candidates}");
            }
            break;
        }
        string resolved = string.Join(' ', path);
        string arguments = string.Join(' ', tokens.Skip(consumed));
        if (path.Count == 0)
            return new Match(MatchState.Unknown, null, "", arguments,
                tokens.Length > 0 ? tokens[0] : "");
        var command = new ServerCommand(resolved, node.Security, node.Runnable,
            node.Children.Count > 0);
        if (!node.Runnable)
            return new Match(MatchState.NeedsSubcommand, command, resolved, arguments,
                string.Join(", ", node.Children.Keys.Take(6)));
        return new Match(MatchState.Resolved, command, resolved, arguments, "");
    }

    [GeneratedRegex(@"<[^<>\n]+>")]
    private static partial Regex Placeholder();

    /// <summary>
    /// Lint one body. <paramref name="spellKnown"/> / <paramref name="itemKnown"/> let the host
    /// check /cast and /use names against what the character actually knows; null skips them.
    /// </summary>
    public static IReadOnlyList<Diagnostic> Lint(string body, CommandCatalog catalog,
        Func<string, bool>? spellKnown = null, Func<string, bool>? itemKnown = null)
    {
        var diagnostics = new List<Diagnostic>();
        string[] lines = body.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n').Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            int number = index + 1;
            string line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.Length > MacroBookLaw.ChatLineLimit)
                diagnostics.Add(new Diagnostic(number, Severity.Error,
                    $"{line.Length} characters; the server drops lines over {MacroBookLaw.ChatLineLimit}."));
            System.Text.RegularExpressions.Match placeholder = Placeholder().Match(line);
            if (placeholder.Success)
            {
                diagnostics.Add(new Diagnostic(number, Severity.Error,
                    $"Fill in {placeholder.Value}."));
                continue;
            }
            if (line.StartsWith('/'))
            {
                LintSlash(number, line, diagnostics, spellKnown, itemKnown);
                continue;
            }
            if (line.StartsWith('.'))
            {
                LintDot(number, line, catalog, diagnostics);
                continue;
            }
            diagnostics.Add(new Diagnostic(number, Severity.Info,
                "Plain text is sent as /say."));
        }
        return diagnostics;
    }

    private static void LintSlash(int number, string line, List<Diagnostic> diagnostics,
        Func<string, bool>? spellKnown, Func<string, bool>? itemKnown)
    {
        int split = line.IndexOf(' ');
        string verb = (split < 0 ? line : line[..split]).ToLowerInvariant();
        string arguments = split < 0 ? "" : line[(split + 1)..].Trim();
        bool known = ClientVerbs.Contains(verb) || EmoteCommandLaw.Resolve(verb) is not null;
        if (!known)
        {
            diagnostics.Add(new Diagnostic(number, Severity.Warning,
                $"Unknown slash command {verb}."));
            return;
        }
        switch (verb)
        {
            case "/cast" or "/spell":
                if (arguments.Length == 0)
                    diagnostics.Add(new Diagnostic(number, Severity.Error,
                        $"{verb} needs a spell name."));
                else if (spellKnown is not null && !spellKnown(arguments))
                    diagnostics.Add(new Diagnostic(number, Severity.Warning,
                        $"Unknown spell: {arguments}"));
                break;
            case "/use":
                if (arguments.Length == 0)
                    diagnostics.Add(new Diagnostic(number, Severity.Error,
                        "/use needs an item name."));
                else if (itemKnown is not null && !itemKnown(arguments))
                    diagnostics.Add(new Diagnostic(number, Severity.Warning,
                        $"Unknown item: {arguments}"));
                break;
            case "/w" or "/whisper" or "/t" or "/tell":
                if (arguments.IndexOf(' ') < 0)
                    diagnostics.Add(new Diagnostic(number, Severity.Warning,
                        $"{verb} needs a name and a message."));
                break;
        }
    }

    private static void LintDot(int number, string line, CommandCatalog catalog,
        List<Diagnostic> diagnostics)
    {
        string rest = line[1..].Trim();
        if (rest.Length == 0)
        {
            diagnostics.Add(new Diagnostic(number, Severity.Error, "A lone dot does nothing."));
            return;
        }
        if (catalog.ServerCommands.Count == 0) return;
        Match match = ResolveServerCommand(catalog, rest);
        switch (match.State)
        {
            case MatchState.Unknown:
                diagnostics.Add(new Diagnostic(number, Severity.Warning,
                    $"Unknown server command .{match.Detail}."));
                return;
            case MatchState.Ambiguous:
                diagnostics.Add(new Diagnostic(number, Severity.Warning,
                    $"Ambiguous: {match.Detail}."));
                return;
            case MatchState.NeedsSubcommand:
                diagnostics.Add(new Diagnostic(number, Severity.Warning,
                    $".{match.Resolved} needs a subcommand ({match.Detail})."));
                return;
        }
        string[] arguments = match.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (match.Resolved)
        {
            case "additem":
                if (arguments.Length == 0)
                    diagnostics.Add(new Diagnostic(number, Severity.Error,
                        ".additem needs an item id."));
                else if (!IsItemArgument(arguments[0]))
                    diagnostics.Add(new Diagnostic(number, Severity.Error,
                        $".additem: '{arguments[0]}' is not an item id."));
                else if (arguments.Length > 1 && !int.TryParse(arguments[1], out _))
                    diagnostics.Add(new Diagnostic(number, Severity.Error,
                        $".additem: '{arguments[1]}' is not a count."));
                break;
            case "additemset":
            case "learn":
            case "aura":
            case "levelup":
                if (arguments.Length > 0 && !uint.TryParse(arguments[0], out _) &&
                    !arguments[0].StartsWith('|') && !arguments[0].StartsWith('['))
                    diagnostics.Add(new Diagnostic(number, Severity.Error,
                        $".{match.Resolved}: '{arguments[0]}' is not an id."));
                else if (arguments.Length == 0 && match.Resolved != "levelup")
                    diagnostics.Add(new Diagnostic(number, Severity.Error,
                        $".{match.Resolved} needs an id."));
                break;
        }
    }

    /// <summary>An item id, or a shift-clicked item link (|Hitem:... or [Name]).</summary>
    public static bool IsItemArgument(string token) =>
        uint.TryParse(token, out uint id) && id > 0 ||
        token.StartsWith('|') || token.StartsWith('[');
}
