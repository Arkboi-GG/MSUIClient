namespace MSUIClient.Net;

/// <summary>
/// The bounded realmd challenge-redial rule shared with current Benilla. Mangos hashes SRP
/// integers at their minimal width while the 1.12.1 client hashes their declared width. A
/// little-endian public key is therefore unambiguous exactly when its high-order byte is nonzero.
/// </summary>
public static class RealmLogonLaw
{
    public const int MaximumChallengeDials = 8;

    public static bool IsWidthStable(ReadOnlySpan<byte> littleEndian) =>
        littleEndian.Length > 0 && littleEndian[^1] != 0;

    /// <summary>
    /// Keep the first encoding-unambiguous challenge. If all eight are ambiguous, keep the last
    /// one rather than manufacturing a rejection the server did not send.
    /// </summary>
    public static bool KeepChallenge(int zeroBasedDial, ReadOnlySpan<byte> serverPublicKey) =>
        IsWidthStable(serverPublicKey) || zeroBasedDial >= MaximumChallengeDials - 1;
}
