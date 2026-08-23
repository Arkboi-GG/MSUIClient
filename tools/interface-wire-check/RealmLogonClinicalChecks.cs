using MSUIClient;
using MSUIClient.Net;

internal static class RealmLogonClinicalChecks
{
    public static void Run()
    {
        Check(!RealmLogonLaw.IsWidthStable([]) &&
              !RealmLogonLaw.IsWidthStable([1, 2, 0]) &&
              RealmLogonLaw.IsWidthStable([0, 0, 1]),
            "SRP width stability must be decided by the high-order little-endian byte");
        Check(RealmLogonLaw.MaximumChallengeDials == 8 &&
              RealmLogonLaw.KeepChallenge(0, [0, 1]) &&
              !RealmLogonLaw.KeepChallenge(0, [1, 0]) &&
              !RealmLogonLaw.KeepChallenge(6, [1, 0]) &&
              RealmLogonLaw.KeepChallenge(7, [1, 0]),
            "ambiguous-B redial bound or final-dial fallback drift");

        string root = ClientConfig.FindRepoRoot();
        string realm = SourceText.Read(Path.Combine(root, "MSUIClient", "Net", "RealmClient.cs"));
        string srp = SourceText.Read(Path.Combine(root, "MSUIClient", "Net", "Srp6Client.cs"));
        int logon = realm.IndexOf("public static LogonResult Logon", StringComparison.Ordinal);
        int dialMethod = realm.IndexOf("private static (TcpClient Tcp, Stream Stream, ChallengeReply Challenge)",
            logon + 1,
            StringComparison.Ordinal);
        int challengeSection = realm.IndexOf("// --- challenge", dialMethod,
            StringComparison.Ordinal);
        string logonBody = realm[logon..dialMethod];
        string dialBody = realm[dialMethod..challengeSection];
        Check(logonBody.IndexOf("Srp6Client.Normalize(password)", StringComparison.Ordinal) <
              logonBody.IndexOf("DialEncodingUnambiguousChallenge", StringComparison.Ordinal) &&
              logonBody.Contains("WriteLogonProof(s, srp.A, srp.M1, challenge.CrcSalt);",
                  StringComparison.Ordinal),
            "credentials must normalize before dialing and proof must remain on the kept socket");
        Check(dialBody.Contains("RealmLogonLaw.MaximumChallengeDials", StringComparison.Ordinal) &&
              dialBody.Contains("RealmLogonLaw.KeepChallenge(dial, challenge.ServerPublicKey)",
                  StringComparison.Ordinal) &&
              dialBody.Contains("stream.Dispose();", StringComparison.Ordinal) &&
              !dialBody.Contains("WriteLogonProof", StringComparison.Ordinal),
            "ambiguous challenges must close before proof and redial on a fresh socket");
        Check(srp.Contains("MaximumEphemeralDraws = 512", StringComparison.Ordinal) &&
              srp.Contains("IsWidthStable(aLE) && !last", StringComparison.Ordinal) &&
              srp.Contains("sharedLE[0] == 0 && !last", StringComparison.Ordinal) &&
              srp.Contains("IsWidthStable(sessionKey) && !last", StringComparison.Ordinal) &&
              srp.Contains("IsWidthStable(m1) && !last", StringComparison.Ordinal),
            "client-generated A/S/K/M1 encoding-stability redraw guards drift");

        byte[] publicKey = Enumerable.Range(0, 32)
            .Select(i => (byte)(i * 7 + 9)).ToArray();
        byte[] mangosSalt = Convert.FromHexString("BAA31E99A00B2157FC373FB369CDD2F1");
        Check(Convert.ToHexString(RealmClient.VersionProof(mangosSalt, publicKey)) ==
              "9B95CD41EDD719FDDF237294B8ACA17010E71703",
            "build-5875 Windows integrity proof drift");
        byte[] unknownSalt = (byte[])mangosSalt.Clone();
        unknownSalt[0] ^= 0xff;
        Check(RealmClient.VersionProof(unknownSalt, publicKey).All(b => b == 0),
            "unknown version challenge reused the fixed mangos digest");

        byte[] m1 = Enumerable.Range(0, 20).Select(i => (byte)(i * 13 + 4)).ToArray();
        byte[] proofPacket = RealmClient.BuildLogonProof(publicKey, m1, mangosSalt);
        Check(proofPacket.Length == 75 && proofPacket[0] == 0x01 &&
              proofPacket.AsSpan(1, 32).SequenceEqual(publicKey) &&
              proofPacket.AsSpan(33, 20).SequenceEqual(m1) &&
              proofPacket.AsSpan(53, 20).SequenceEqual(
                  RealmClient.VersionProof(mangosSalt, publicKey)) &&
              proofPacket.AsSpan(73, 2).SequenceEqual(new byte[] { 0, 0 }),
            "CMD_AUTH_LOGON_PROOF version field or packet shape drift");

        byte[] serverPublicKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        byte[] salt = Enumerable.Range(0, 32).Select(i => (byte)(i * 7 + 3)).ToArray();
        for (int i = 0; i < 64; i++)
        {
            Srp6Result result = Srp6Client.ComputeChallenge(
                "clinical", "password", serverPublicKey, Srp6Client.Generator,
                Srp6Client.LargeSafePrimeLE, salt);
            Check(RealmLogonLaw.IsWidthStable(result.A) &&
                  RealmLogonLaw.IsWidthStable(result.SessionKey) &&
                  RealmLogonLaw.IsWidthStable(result.M1),
                $"SRP draw {i} escaped an encoding-stability guard");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
