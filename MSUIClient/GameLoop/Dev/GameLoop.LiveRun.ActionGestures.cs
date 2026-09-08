using System.Globalization;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private uint _liveCreatedMacroId;
    // Calls the same pickup and release handlers as the UI. The protocol supplies
    // the hit-tested slot; this proves live state/wire/audio, not pointer hit testing.
    private void RunLiveActionGesture(string line)
    {
        string[] args = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool pass = false;
        if (args.Length >= 2)
        {
            string command = args[1].ToLowerInvariant();
            if (command == "assert-main-wire" && args.Length == 4 &&
                int.TryParse(args[2], out int mainButton) && mainButton is >= 0 and < 12 &&
                int.TryParse(args[3], out int expectedWire))
                pass = ActionWireSlot(mainButton) == expectedWire;
            else if (command == "page" && args.Length == 3 && int.TryParse(args[2], out int pageDelta))
            { ChangeActionPage(pageDelta); pass = true; }
            else if (command == "create-test-macro" && _liveCreatedMacroId == 0)
            {
                EnsureMacrosLoaded();
                _macroCharacterSpecific = true;
                int before = CurrentMacroBook.Macros.Count;
                CreateMacro();
                pass = CurrentMacroBook.Macros.Count == before + 1;
                if (pass) _liveCreatedMacroId = _selectedMacroId;
            }
            else if (command == "pickup-test-macro" && _liveCreatedMacroId != 0 &&
                     FindMacro(_liveCreatedMacroId) is not null && !BarsReadOnly)
            {
                BeginMacroDrag(_liveCreatedMacroId);
                pass = _draggingMacroId == _liveCreatedMacroId;
            }
            else if (command == "delete-test-macro" && _liveCreatedMacroId != 0)
            {
                _macroDeletePendingId = _liveCreatedMacroId;
                ConfirmDeleteMacro();
                pass = FindMacro(_liveCreatedMacroId) is null;
                if (pass) _liveCreatedMacroId = 0;
            }
            else if (command == "assert-test-macro-slot" && args.Length == 3 &&
                     int.TryParse(args[2], out int macroSlot) && macroSlot is >= 0 and < 120)
                pass = _liveCreatedMacroId != 0 && _actions[macroSlot] is { Kind: ActionSlot.Macro } macro &&
                    macro.ActionId == _liveCreatedMacroId;
            else if (command == "pickup-spell" && args.Length == 3 &&
                uint.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out uint spell) &&
                _actions.KnownSpells.Contains(spell) && !BarsReadOnly)
            {
                PickupSpellToCursor(spell);
                pass = _draggingSpellId == spell;
            }
            else if (command == "pickup-slot" && args.Length == 3 &&
                     int.TryParse(args[2], out int pickup) && pickup is >= 0 and < 120)
                pass = PickupActionToCursor(pickup);
            else if (command == "drop" && args.Length == 3 &&
                     int.TryParse(args[2], out int drop) && drop is >= -1 and < 120)
            {
                ActionSlot? held = _actionCursor ?? (_draggingSpellId != 0
                    ? new ActionSlot(ActionSlot.Spell, _draggingSpellId) : _draggingMacroId != 0
                    ? new ActionSlot(ActionSlot.Macro, _draggingMacroId) : null);
                if (held is not null)
                {
                    FinishActionDragAt(drop);
                    pass = drop < 0 ? _actionCursor is null && _draggingSpellId == 0 && _draggingMacroId == 0
                        : _actions[drop] == held;
                }
            }
            else if (command == "assert-slot" && args.Length == 4 &&
                     int.TryParse(args[2], out int slot) && slot is >= 0 and < 120 &&
                     uint.TryParse(args[3], out uint expected))
                pass = expected == 0 ? _actions[slot] is null
                    : _actions[slot] is { Kind: ActionSlot.Spell } action && action.ActionId == expected;
            else if (command == "assert-cursor" && args.Length == 3 &&
                     uint.TryParse(args[2], out uint expectedCursor))
                pass = expectedCursor == 0 ? _actionCursor is null && _draggingSpellId == 0 && _draggingMacroId == 0
                    : _actionCursor is { Kind: ActionSlot.Spell } cursor && cursor.ActionId == expectedCursor;
        }
        Log(pass, $"{line} cursor={_actionCursor?.Packed ?? 0};bookCursor={_draggingSpellId};macroCursor={_draggingMacroId};" +
            $"actor=0x{ControlledGuid:X16};source=production-handler;pointerHitTest=false");
    }
}
