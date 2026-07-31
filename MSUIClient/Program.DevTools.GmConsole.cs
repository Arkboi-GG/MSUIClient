using ImGuiNET;
using MSUIClient.Engine;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private string _gmCommand = ".gps";
    private readonly List<string> _gmCommandHistory = [];
    private int _gmHistoryIndex;
    private double _gmCopiedUntil;

    private void DrawGmConsolePanel()
    {
        if (!ImGui.CollapsingHeader("GM console")) return;
        ImGui.SetNextItemWidth(360f);
        bool submit = ImGui.InputText("Command##gm-console", ref _gmCommand, 512,
            ImGuiInputTextFlags.EnterReturnsTrue);
        if (submit || ImGui.Button("Send##gm-console")) SendGmCommand();
        ImGui.SameLine();
        if (ImGui.Button("Previous##gm-console") && _gmCommandHistory.Count > 0)
        {
            _gmHistoryIndex = Math.Max(0, _gmHistoryIndex - 1);
            _gmCommand = _gmCommandHistory[_gmHistoryIndex];
        }
        ImGui.SameLine();
        if (ImGui.Button("Next##gm-console") && _gmCommandHistory.Count > 0)
        {
            _gmHistoryIndex = Math.Min(_gmCommandHistory.Count, _gmHistoryIndex + 1);
            _gmCommand = _gmHistoryIndex == _gmCommandHistory.Count ? "" : _gmCommandHistory[_gmHistoryIndex];
        }
        if (_gmCommandHistory.Count > 0)
        {
            string last = _gmCommandHistory[^1];
            ImGui.TextUnformatted($"last: {last}");
            if (ImGui.IsItemClicked()) { ImGui.SetClipboardText(last); _gmCopiedUntil = NowSeconds() + 0.8; }
            if (NowSeconds() < _gmCopiedUntil) { ImGui.SameLine(); ImGui.TextUnformatted("copied"); }
        }
    }

    private void SendGmCommand()
    {
        string command = _gmCommand.Trim();
        if (command.Length == 0) return;
        bool sent = _net?.SendChatSay(command) == true;
        if (_gmCommandHistory.Count == 32) _gmCommandHistory.RemoveAt(0);
        _gmCommandHistory.Add(command); _gmHistoryIndex = _gmCommandHistory.Count;
        var verdict = new CombatVerdict(NowSeconds(), "GmCommand", "user-console", 0,
            $"sent={sent};text={command}");
        _verdicts.Add(verdict);
        Console.WriteLine($"[verdict:combat] {verdict.ToLine()}");
    }
}
