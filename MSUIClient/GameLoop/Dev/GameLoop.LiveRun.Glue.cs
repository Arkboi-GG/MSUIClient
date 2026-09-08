using System.Globalization;
using ImGuiNET;
using System.Numerics;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private Vector2? _liveGuiPointer;
    private bool _liveGuiDown;
    private bool _liveGuiRightDown;
    private string? _liveInventoryDragObservation;

    private void ObserveLiveInventoryDrag(int container, int slot)
    {
        if (_liveRunOptions is null || !HasCarriedItem ||
            container != _carriedContainer || slot != _carriedSlot) return;
        string observation = $"source={container}:{slot};active={ImGui.IsItemActive()};" +
            $"hovered={ImGui.IsItemHovered()};down={ImGui.IsMouseDown(ImGuiMouseButton.Left)};" +
            $"dragging={ImGui.IsMouseDragging(ImGuiMouseButton.Left)};" +
            $"rect={ImGui.GetItemRectMin()}..{ImGui.GetItemRectMax()}";
        if (observation == _liveInventoryDragObservation) return;
        _liveInventoryDragObservation = observation;
        Console.WriteLine("[live-drag-source] " + observation);
    }

    private bool RunLiveGlue(string line)
    {
        string[] p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 2 || _net is null) return false;
        switch (p[1])
        {
            case "pointer" when p.Length == 4:
                _liveGuiPointer = new(float.Parse(p[2], CultureInfo.InvariantCulture),
                    float.Parse(p[3], CultureInfo.InvariantCulture));
                return true;
            case "down": _liveGuiDown = true; return true;
            case "up": _liveGuiDown = false; return true;
            case "right-down": _liveGuiRightDown = true; return true;
            case "right-up": _liveGuiRightDown = false; return true;
            case "world-pointer-on":
                _window.ClearWorldClicks();
                _liveGuiDown = _liveGuiRightDown = false;
                _window.PointerInputSource = () => new MSUIClient.Engine.PointerInputState(
                    _liveGuiPointer ?? new Vector2(-1000, -1000), _liveGuiDown, _liveGuiRightDown);
                Console.WriteLine("[live-pointer] window gestures enabled; native cursor untouched; camera-look untested");
                return true;
            case "world-pointer-off":
                _liveGuiDown = _liveGuiRightDown = false;
                _window.ClearWorldClicks();
                _window.PointerInputSource = null;
                return true;
            case "world-pointer-inspect":
                Console.WriteLine($"[live-pointer] pixel={_window.MousePosition};ground={_groundCursorPoint};" +
                    $"spell={_groundCastSpell};captured={_window.MouseCaptured};uiCapture={ImGui.GetIO().WantCaptureMouse};" +
                    $"acceptable={(_groundCursorPoint is { } point && GroundPointAcceptable(_groundCastSpell, ControlledGuid, point))}");
                return _window.PointerInputSource is not null;
            case "drag-inspect":
                unsafe
                {
                    var payload = ImGui.GetDragDropPayload();
                    Console.WriteLine($"[live-drag] down={_liveGuiDown};position={_liveGuiPointer};" +
                        $"dragging={ImGui.IsMouseDragging(ImGuiMouseButton.Left)};delta={ImGui.GetMouseDragDelta()};" +
                        $"payload={payload.NativePtr != null};carried={ResolveCarriedItem()?.Guid ?? 0:X16};" +
                        $"source={_carriedContainer}:{_carriedSlot}");
                }
                return true;
            case "key-down" when p.Length == 3:
                ImGui.GetIO().AddKeyEvent(Enum.Parse<ImGuiKey>(p[2], true), true); return true;
            case "key-up" when p.Length == 3:
                ImGui.GetIO().AddKeyEvent(Enum.Parse<ImGuiKey>(p[2], true), false); return true;
            case "text" when p.Length == 3:
                ImGui.GetIO().AddInputCharactersUTF8(p[2]); return true;
            case "assert-create-open": return _charCreateOpen;
            case "assert-create-closed": return !_charCreateOpen;
            case "assert-count" when p.Length == 3: return _net.Characters.Count == int.Parse(p[2]);
            case "assert-selected" when p.Length == 3:
                return _selectedChar >= 0 && _selectedChar < _net.Characters.Count &&
                    _net.Characters[_selectedChar].Name.Equals(p[2], StringComparison.OrdinalIgnoreCase);
            case "inspect":
                Console.WriteLine($"[live-glue] selected={_selectedChar};roster=" +
                    string.Join('|', _net.Characters.Select((c, i) => $"{i}:{c.Name}:{c.Race}:{c.Class}:{c.Gender}")));
                foreach (var character in _net.Characters)
                    Console.WriteLine($"[live-roster] name={character.Name};guid=0x{character.Guid:X};level={character.Level};" +
                        $"race={character.Race};class={character.Class};gender={character.Gender}");
                return true;
            default: return false;
        }
    }
}
