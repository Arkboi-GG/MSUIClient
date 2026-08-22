using MSUIClient;
using MSUIClient.Net;

internal static class PetNameClinicalChecks
{
    public static void Run()
    {
        const ulong petGuid = 0xF140_0000_8900_1092ul;
        Check(GuidInfo.IsPet(petGuid) && GuidInfo.PetNumber(petGuid) == 137 &&
              GuidInfo.Entry(petGuid) is null &&
              GuidInfo.PetNumber(0xF130_0000_8900_1092ul) is null,
            "pet GUID number/entry split drift");

        byte[] request = WorldSession.BuildPetNameQueryBody(137, petGuid);
        Check(request.SequenceEqual(new byte[]
              {
                  0x89, 0x00, 0x00, 0x00,
                  0x92, 0x10, 0x00, 0x89, 0x00, 0x00, 0x40, 0xF1,
              }) &&
              (ushort)Op.CMSG_PET_NAME_QUERY == 0x0052 &&
              (ushort)Op.SMSG_PET_NAME_QUERY_RESPONSE == 0x0053,
            "pet-name request opcode/body drift");

        var writer = new PacketWriter(32);
        writer.WriteU32(137);
        writer.WriteCString("Bheezhem");
        writer.WriteU32(0x1234_5678);
        PetNameQueryResponse response = PetNamePackets.ParseResponse(writer.ToArray());
        Check(response == new PetNameQueryResponse(137, "Bheezhem", 0x1234_5678),
            "pet-name response parser drift");

        string root = ClientConfig.FindRepoRoot();
        string targeting = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.Targeting.cs"));
        string names = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.Nameplates.cs"));
        string network = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        Check(targeting.Contains("GuidInfo.PetNumber(identity.Guid)", StringComparison.Ordinal) &&
              targeting.Contains("ResolveCreatureOrPetName", StringComparison.Ordinal) &&
              names.Contains("_net.PetNameQuery(petNumber, unit.Guid)", StringComparison.Ordinal) &&
              network.Contains("case Op.SMSG_PET_NAME_QUERY_RESPONSE", StringComparison.Ordinal) &&
              network.Contains("_petNames[response.PetNumber]", StringComparison.Ordinal),
            "pet-name runtime request/cache wiring drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
