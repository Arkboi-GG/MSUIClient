using System.IO.Compression;
using System.Text;
using MSUIClient;
using MSUIClient.Net;

internal static class AuthSessionAddonClinicalChecks
{
    public static void Run()
    {
        byte[] block = AuthSessionAddonLaw.Block(AuthSessionAddonLaw.StockSecureAddons);
        Check(block.Length == 342 && AuthSessionAddonLaw.StockSecureAddons.Length == 12,
            "stock secure-addon block count/length drift");

        var reader = new PacketReader(block);
        var names = new List<string>();
        while (reader.Remaining > 0)
        {
            names.Add(reader.ReadCString());
            Check(reader.ReadU8() == 1 &&
                  reader.ReadU32() == AuthSessionAddonLaw.StandardModulusCrc &&
                  reader.ReadU32() == 0,
                "stock secure-addon record drift");
        }
        Check(names.SequenceEqual(names.Order(StringComparer.Ordinal)) &&
              names.All(name => name.StartsWith("Blizzard_", StringComparison.Ordinal)),
            "stock secure-addon order/name drift");

        byte[] tail = AuthSessionAddonLaw.StockTail();
        Check(tail.Length == 134 && BitConverter.ToUInt32(tail, 0) == 342 &&
              tail[4] == 0x78 && tail[5] == 0x9c &&
              AuthSessionAddonLaw.Tail([]).Length == 0,
            "AUTH_SESSION addon tail size/framing/empty law drift");
        using var compressed = new MemoryStream(tail, 4, tail.Length - 4, writable: false);
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var inflated = new MemoryStream();
        zlib.CopyTo(inflated);
        Check(inflated.ToArray().SequenceEqual(block),
            "AUTH_SESSION stock addon zlib round-trip drift");

        byte[] proof = Enumerable.Range(0, 20).Select(i => (byte)(i * 7 + 3)).ToArray();
        byte[] body = WorldSession.BuildAuthSession(5875, "CLINICAL", 0x1234_5678, proof);
        int tailOffset = 4 + 4 + Encoding.ASCII.GetByteCount("CLINICAL") + 1 + 4 + 20;
        Check(BitConverter.ToUInt32(body, 0) == 5875 &&
              BitConverter.ToUInt32(body, tailOffset) == 342 &&
              body.AsSpan(tailOffset + 4).SequenceEqual(tail.AsSpan(4)) &&
              !SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(), "MSUIClient", "Net",
                  "WorldSession.cs")).Contains("EmptyAddonZlib", StringComparison.Ordinal),
            "CMSG_AUTH_SESSION did not append the stock secure-addon tail");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
