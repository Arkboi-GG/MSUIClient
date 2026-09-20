namespace MSUIClient;

public sealed partial class GameLoop
{
    private string? _liveInboxPath;
    private int _liveInboxLines;
    private double _liveInboxNextPoll;

    private bool OpenLiveInbox(string line)
    {
        string relative = line["inbox ".Length..].Trim();
        string root = Path.GetFullPath(_config.RepoRoot) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(relative, root);
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        _liveInboxPath = path;
        _liveInboxLines = 0;
        return true;
    }

    // An append-only, newline-delimited queue allows capture -> inspect -> input
    // without restarting the client or sending desktop mouse/keyboard events.
    private bool PollLiveInbox()
    {
        if (_liveInboxPath is null) return false;
        if (NowSeconds() < _liveInboxNextPoll) return true;
        _liveInboxNextPoll = NowSeconds() + .25;
        if (!File.Exists(_liveInboxPath)) return true;
        string text;
        try { text = File.ReadAllText(_liveInboxPath); }
        catch (IOException) { return true; } // producer may still be appending
        string[] lines = text.Split('\n');
        // Only complete lines are committed; a partial write is retried.
        for (; _liveInboxLines < lines.Length - 1; _liveInboxLines++)
        {
            string command = lines[_liveInboxLines].Split('#')[0].Trim();
            if (command.Length == 0) continue;
            if (command == "inbox-close") { _liveInboxPath = null; break; }
            _liveSteps!.Add(command);
        }
        return _liveInboxPath is not null || _liveStep < _liveSteps!.Count;
    }
}
