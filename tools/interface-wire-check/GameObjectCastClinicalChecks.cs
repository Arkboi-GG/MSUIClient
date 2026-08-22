using MSUIClient;
using MSUIClient.Net;

internal static class GameObjectCastClinicalChecks
{
    public static void Run()
    {
        byte[] body = WorldSession.BuildCastSpellOnGameObjectBody(1, 0x1234);
        Check(body.SequenceEqual(new byte[]
              {
                  0x01, 0x00, 0x00, 0x00,
                  0x00, 0x08,
                  0x03, 0x34, 0x12,
              }),
            "GameObject OPEN_LOCK cast body must carry GAMEOBJECT 0x0800 without LOCKED 0x4000");

        var reader = new PacketReader(body);
        Check(reader.ReadU32() == 1 && reader.ReadU16() == 0x0800 &&
              reader.ReadPackedGuid() == 0x1234 && reader.Remaining == 0,
            "GameObject cast packet field alignment drift");

        string root = ClientConfig.FindRepoRoot();
        string interaction = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.GameObjects.cs"));
        string session = SourceText.Read(Path.Combine(root, "MSUIClient", "Net",
            "WorldSession.cs"));
        Check(interaction.Contains("_net.CastSpellOnGameObject(opener, guid)",
                  StringComparison.Ordinal) &&
              session.Contains("w.WriteU16(0x0800)", StringComparison.Ordinal) &&
              !session.Contains("w.WriteU16(0x4800)", StringComparison.Ordinal),
            "GameObject OPEN_LOCK runtime/wire wiring drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
