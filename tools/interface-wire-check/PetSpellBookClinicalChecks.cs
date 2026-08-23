using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

internal static class PetSpellBookClinicalChecks
{
    public static void Run()
    {
        Check(PetSpellBookUiLaw.PlayerTab ==
                  new PetSpellBookUiLaw.LogicalRect(15, 419, 128, 64) &&
              PetSpellBookUiLaw.PetTab ==
                  new PetSpellBookUiLaw.LogicalRect(123, 419, 128, 64) &&
              PetSpellBookUiLaw.TabHitMin.X == 15 &&
              PetSpellBookUiLaw.TabHitMax.Y == 49,
            "pet book bottom-tab geometry drift");
        Check(PetSpellBookUiLaw.Eligible(0) &&
              PetSpellBookUiLaw.Eligible(0x20) &&
              !PetSpellBookUiLaw.Eligible(0x80) &&
              PetSpellBookUiLaw.CastWord(0x12345) == 0x0100_2345 &&
              PetSpellBookUiLaw.Title(9) == "Demon" &&
              PetSpellBookUiLaw.Title(3) == "Pet",
            "pet book add-gate, cast word, or class token drift");

        var book = new List<uint> { 0x8100_0123, 0x0100_0456 };
        uint[] bar = [0x8100_0123, 0x0100_0456, 0x0100_0123];
        Check(PetSpellBookUiLaw.TryToggleAutocast(book, bar, 0x123, out bool enabled) &&
              enabled && book[0] == 0xC100_0123 &&
              bar[0] == 0xC100_0123 && bar[2] == 0x4100_0123 &&
              !PetSpellBookUiLaw.TryToggleAutocast(book, bar, 0x456, out _),
            "pet book autocast flip/mirror law drift");

        Check((ushort)Op.CMSG_PET_SPELL_AUTOCAST == 0x02F3,
            "CMSG_PET_SPELL_AUTOCAST opcode drift");
        byte[] body = WorldSession.BuildPetSpellAutocastBody(
            0x0102_0304_0506_0708, 0x1122_3344, true);
        Check(body.Length == 13 &&
              BitConverter.ToUInt64(body, 0) == 0x0102_0304_0506_0708 &&
              BitConverter.ToUInt32(body, 8) == 0x1122_3344 && body[12] == 1,
            "CMSG_PET_SPELL_AUTOCAST must be guid/u32/u8 (13 bytes)");

        string root = ClientConfig.FindRepoRoot();
        string pet = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Pet.cs"));
        string spellbook = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Spellbook.cs"));
        string bindings = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Bindings.cs"));
        Check(pet.Contains("bookSpells.Add(r.ReadU32())", StringComparison.Ordinal) &&
              pet.Contains("_petBookSpells.AddRange(bookSpells)", StringComparison.Ordinal) &&
              spellbook.Contains("PetSpellBookUiLaw.PlayerTab", StringComparison.Ordinal) &&
              spellbook.Contains("PetSpellBookUiLaw.PetTab", StringComparison.Ordinal) &&
              spellbook.Contains("TogglePetBookAutocast(id)", StringComparison.Ordinal) &&
              spellbook.Contains("PetSpellBookUiLaw.CastWord(spell.Id)",
                  StringComparison.Ordinal) &&
              spellbook.Contains("_petCooldowns : _actions", StringComparison.Ordinal) &&
              bindings.Contains("GameBinding.OpenPetSpellbook, \"Pet Spellbook\", Key.P",
                  StringComparison.Ordinal) &&
              bindings.Contains("Shift: row.Binding == GameBinding.OpenPetSpellbook",
                  StringComparison.Ordinal),
            "pet book packet-to-window/autocast/binding integration drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
