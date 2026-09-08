using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    // Supplies a slot to shipping handlers; pointer geometry has separate evidence.
    private bool RunLivePetGesture(string line)
    {
        string[] p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 2 || _petGuid == 0) return false;
        _entities.TryGet(_petGuid, out WorldEntity? pet);
        if (p[1] == "inspect")
        {
            Console.WriteLine($"[live-pet] guid=0x{_petGuid:X};book={string.Join('|', _petBookSpells)};bar={string.Join('|', _petActions)}");
            if (pet is not null)
                Console.WriteLine($"[live-pet-stats] actor=0x{ControlledGuid:X};pet=0x{pet.Guid:X};" +
                    $"name={ResolveCreatureOrPetName(pet, "")};health={pet.Fields.Health};" +
                    $"happiness={pet.Fields.GetU32(ObjectFields.UNIT_POWER1 + 4)};" +
                    $"loyalty={pet.Fields.PetLoyaltyLevel};training={pet.Fields.PetTrainingPoints};nameTimestamp={pet.Fields.PetNameTimestamp}");
            return true;
        }
        if (p.Length == 3 && p[1] == "pickup-book" && uint.TryParse(p[2], out uint id))
        {
            uint packed = _petBookSpells.FirstOrDefault(word => PetSpellBookUiLaw.SpellId(word) == id);
            if (packed == 0 || _spellCatalog?.TryGet(id, out SpellInfo spell) != true) return false;
            PickupPetBookSpell(packed, spell);
            return _draggingPetAction == packed;
        }
        if (p.Length == 3 && int.TryParse(p[2], out int slot) && slot is >= 0 and < 10)
        {
            if (p[1] == "pickup-slot") { PickupPetAction(slot, _petGuid, pet); return _draggingPetAction.HasValue; }
            if (p[1] == "drop") { PlacePetAction(slot, _petGuid, pet); return true; }
        }
        if (p.Length == 4 && p[1] == "assert-slot" && int.TryParse(p[2], out int target)
            && target is >= 0 and < 10 && uint.TryParse(p[3], out uint expected))
            return PetActionBarUiLaw.Action(_petActions[target]) == expected;
        if (p.Length == 3 && p[1] == "assert-cursor" && uint.TryParse(p[2], out uint cursor))
            return cursor == 0 ? !_draggingPetAction.HasValue
                : _draggingPetAction.HasValue && PetActionBarUiLaw.Action(_draggingPetAction.Value) == cursor;
        if (p[1] == "clear") { ClearPetActionCursor(); return true; }
        return false;
    }
}
