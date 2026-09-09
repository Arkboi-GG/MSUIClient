using System.Buffers.Binary;
namespace MSUIClient.Net;

/// <summary>1.12 SMSG_PET_UNLEARN_CONFIRM: full pet GUID and quoted copper.</summary>
public readonly record struct PetUnlearnPacket(ulong Pet, uint Cost)
{
    public static bool TryParse(ReadOnlySpan<byte> body, out PetUnlearnPacket quote)
    {
        quote = default;
        if (body.Length != 12) return false;
        quote = new(BinaryPrimitives.ReadUInt64LittleEndian(body), BinaryPrimitives.ReadUInt32LittleEndian(body[8..]));
        return quote.Pet != 0;
    }
}
