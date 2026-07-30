using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private readonly Dictionary<string, bool> _verdictChannelFilters =
        new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<IVerdict>? _pausedVerdicts;
    private IReadOnlyList<WirePacketDetail>? _pausedWire;
    private string _verdictTextFilter = "";
    private bool _verdictPaused;
    private int _verdictCopyLast = 20;
    private double _verdictCopiedUntil;
    private readonly record struct VerdictPanelRow(double Time, string Channel, string Text);

    private void DrawVerdictsPanel()
    {
        bool expanded = ImGui.CollapsingHeader("Verdicts");
        ImGui.SameLine();
        bool recording = _wireLog.IsRecording;
        if (ImGui.Checkbox("Record wire log##verdicts", ref recording))
        {
            if (recording)
            {
                try
                {
                    string path = _wireLog.Start(_config.RepoRoot);
                    Console.WriteLine($"[wire] recording to {path}");
                    CopyVerdictText(path);
                }
                catch (Exception ex)
                {
                    _wireLog.Stop();
                    Console.WriteLine($"[wire] could not start recording: {ex.Message}");
                }
            }
            else
            {
                _wireLog.Stop();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Dump (F10)##verdicts")) ArmGameplayDump();
        if (!expanded) return;

        IReadOnlyList<IVerdict> live = _verdicts.Snapshot();
        IReadOnlyList<WirePacketDetail> liveWire = _wire.SnapshotDetailed();
        bool pause = _verdictPaused;
        if (ImGui.Checkbox("Pause##verdicts", ref pause))
        {
            _verdictPaused = pause;
            _pausedVerdicts = pause ? live : null;
            _pausedWire = pause ? liveWire : null;
        }

        IReadOnlyList<IVerdict> displayed = _pausedVerdicts ?? live;
        IReadOnlyList<WirePacketDetail> displayedWire = _pausedWire ?? liveWire;
        List<VerdictPanelRow> liveRows = MergeVerdictRows(live, liveWire);
        List<VerdictPanelRow> displayedRows = MergeVerdictRows(displayed, displayedWire);
        foreach (string channel in displayedRows.Select(row => row.Channel)
                     .Append("wire")
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

        List<string> visibleRows = displayedRows
            .Where(row => !_verdictChannelFilters.TryGetValue(row.Channel, out bool enabled) || enabled)
            .Select(row => row.Text)
            .Where(text => string.IsNullOrEmpty(_verdictTextFilter) ||
                          text.Contains(_verdictTextFilter, StringComparison.OrdinalIgnoreCase))
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
                liveRows.TakeLast(_verdictCopyLast).Select(row => row.Text));
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

    private List<VerdictPanelRow> MergeVerdictRows(
        IReadOnlyList<IVerdict> verdicts, IReadOnlyList<WirePacketDetail> wire)
    {
        return verdicts.Select(verdict => new VerdictPanelRow(
                verdict.Time, verdict.Channel, FormatVerdictRow(verdict)))
            .Concat(wire.Select(item => new VerdictPanelRow(
                item.Packet.Time, "wire", FormatWireRow(item))))
            .OrderBy(row => row.Time)
            .ToList();
    }

    private string FormatVerdictRow(IVerdict verdict)
    {
        double age = Math.Max(0.0, NowSeconds() - verdict.Time);
        DateTime local = DateTime.Now - TimeSpan.FromSeconds(age);
        return $"{local:HH:mm:ss.f} [verdict:{verdict.Channel}] {verdict.ToLine()}";
    }

    private string FormatWireRow(WirePacketDetail detail)
    {
        double age = Math.Max(0.0, NowSeconds() - detail.Packet.Time);
        DateTime local = DateTime.Now - TimeSpan.FromSeconds(age);
        return $"{local:HH:mm:ss.f} [wire] " +
               WireLogRecorder.FormatText(detail.Packet, detail.Prefix);
    }

    private void CopyVerdictText(string text)
    {
        ImGui.SetClipboardText(text);
        _verdictCopiedUntil = NowSeconds() + 0.8;
    }
}
