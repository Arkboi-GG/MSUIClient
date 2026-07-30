using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private readonly Dictionary<string, bool> _verdictChannelFilters =
        new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<IVerdict>? _pausedVerdicts;
    private string _verdictTextFilter = "";
    private bool _verdictPaused;
    private int _verdictCopyLast = 20;
    private double _verdictCopiedUntil;

    private void DrawVerdictsPanel()
    {
        if (!ImGui.CollapsingHeader("Verdicts")) return;

        IReadOnlyList<IVerdict> live = _verdicts.Snapshot();
        bool pause = _verdictPaused;
        if (ImGui.Checkbox("Pause##verdicts", ref pause))
        {
            _verdictPaused = pause;
            _pausedVerdicts = pause ? live : null;
        }

        IReadOnlyList<IVerdict> displayed = _pausedVerdicts ?? live;
        foreach (string channel in displayed.Select(v => v.Channel)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
        {
            if (!_verdictChannelFilters.TryGetValue(channel, out bool enabled)) enabled = true;
            ImGui.SameLine();
            if (ImGui.Checkbox($"{channel}##verdict-channel-{channel}", ref enabled))
                _verdictChannelFilters[channel] = enabled;
            else if (!_verdictChannelFilters.ContainsKey(channel))
                _verdictChannelFilters[channel] = true;
        }

        ImGui.SetNextItemWidth(210f);
        ImGui.InputText("Filter##verdicts", ref _verdictTextFilter, 128u);

        List<string> visibleRows = displayed
            .Where(v => !_verdictChannelFilters.TryGetValue(v.Channel, out bool enabled) || enabled)
            .Select(FormatVerdictRow)
            .Where(row => string.IsNullOrEmpty(_verdictTextFilter) ||
                          row.Contains(_verdictTextFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (ImGui.Button("Copy visible##verdicts"))
            CopyVerdictText(string.Join(Environment.NewLine, visibleRows));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(55f);
        ImGui.InputInt("##verdict-copy-last", ref _verdictCopyLast, 0, 0);
        _verdictCopyLast = Math.Clamp(_verdictCopyLast, 1, 256);
        ImGui.SameLine();
        if (ImGui.Button($"Copy last {_verdictCopyLast}##verdicts"))
        {
            string text = string.Join(Environment.NewLine,
                live.TakeLast(_verdictCopyLast).Select(FormatVerdictRow));
            CopyVerdictText(text);
        }
        if (NowSeconds() < _verdictCopiedUntil)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.45f, 1f, 0.55f, 1f), "copied");
        }

        if (ImGui.BeginChild("##verdict-log", new Vector2(0f, 220f), true,
                ImGuiWindowFlags.HorizontalScrollbar))
        {
            bool wasAtBottom = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 4f;
            foreach (string row in visibleRows)
            {
                ImGui.TextUnformatted(row);
                if (ImGui.IsItemClicked(ImGuiMouseButton.Left)) CopyVerdictText(row);
            }
            if (wasAtBottom) ImGui.SetScrollHereY(1f);
        }
        ImGui.EndChild();
    }

    private string FormatVerdictRow(IVerdict verdict)
    {
        double age = Math.Max(0.0, NowSeconds() - verdict.Time);
        DateTime local = DateTime.Now - TimeSpan.FromSeconds(age);
        return $"{local:HH:mm:ss.f} [verdict:{verdict.Channel}] {verdict.ToLine()}";
    }

    private void CopyVerdictText(string text)
    {
        ImGui.SetClipboardText(text);
        _verdictCopiedUntil = NowSeconds() + 0.8;
    }
}
