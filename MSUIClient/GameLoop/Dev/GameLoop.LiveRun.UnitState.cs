using System.Globalization;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    // Read-only evidence for ownership refreshes and follower movement. No packets
    // are requested and no entity fields are synthesized by this diagnostic.
    private bool InspectLiveUnitState(string line)
    {
        string[] args = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (args.Length != 2) return false;
        ulong guid;
        if (args[1] == "actor") guid = ControlledGuid;
        else if (args[1] == "selected") guid = _selectionGuid;
        else if (!args[1].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                 !ulong.TryParse(args[1].AsSpan(2), NumberStyles.HexNumber,
                     CultureInfo.InvariantCulture, out guid)) return false;
        if (!_entities.TryGet(guid, out WorldEntity unit)) return false;
        ObjectFields fields = unit.Fields;
        Console.WriteLine($"[live-unit-resources] guid=0x{guid:X};coinage={fields.GetU32(ObjectFields.PLAYER_COINAGE)};mana={fields.GetU32(ObjectFields.UNIT_POWER1)}/{fields.GetU32(ObjectFields.UNIT_MAXPOWER1)}");
        Console.WriteLine(FormattableString.Invariant(
            $"[live-unit-state] actor=0x{ControlledGuid:X};guid=0x{guid:X};position=({unit.Position.X:R},{unit.Position.Y:R},{unit.Position.Z:R});health={fields.Health}/{fields.MaxHealth};npcFlags=0x{fields.NpcFlags:X};owner=0x{fields.SummonedBy:X};charmer=0x{fields.CharmedBy:X};training=0x{fields.PetTrainingPoints:X};happiness={fields.GetU32(ObjectFields.UNIT_POWER1 + 4)};loyalty={fields.PetLoyaltyLevel}"));
        return true;
    }
}
