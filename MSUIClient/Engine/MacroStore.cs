using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MSUIClient.Engine.UI;

namespace MSUIClient.Engine;

/// <summary>Local macro namespaces and non-destructive migration of the old shared store.</summary>
public static class MacroStore
{
    public static string AccountKey(string host, int port, string account)
    {
        string identity = JsonSerializer.Serialize(new[]
        {
            host.Trim().TrimEnd('.').ToLowerInvariant(),
            port.ToString(System.Globalization.CultureInfo.InvariantCulture), account.Trim().ToUpperInvariant(),
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    public static (string AccountPath, string CharacterPath) Paths(string root, string accountKey,
        string realm, string character)
    {
        if (accountKey.Length != 64 || !accountKey.All(char.IsAsciiHexDigit))
            throw new ArgumentException("Invalid macro account storage key", nameof(accountKey));
        string directory = Path.Combine(root, "macros", "accounts", accountKey);
        return (Path.Combine(directory, "account.txt"), Path.Combine(directory, LegacyCharacterFile(realm, character)));
    }

    public static string LegacyCharacterFile(string realm, string character) =>
        $"{MacroBookLaw.StoreFileToken(realm)}-{MacroBookLaw.StoreFileToken(character)}.txt";

    /// <summary>
    /// The formerly shared files have no account metadata. Their first authenticated reader claims
    /// migration once; originals remain untouched. Combined macros.json belongs to that first
    /// character only. Later characters of that same account may import their old text file.
    /// </summary>
    public static bool MigrateLegacy(string root, string accountKey, string realm, string character)
    {
        var paths = Paths(root, accountKey, realm, character);
        string directory = Path.Combine(root, "macros");
        string legacyCharacter = LegacyCharacterFile(realm, character);
        string legacyAccountPath = Path.Combine(directory, "account.txt");
        string legacyCharacterPath = Path.Combine(directory, legacyCharacter);
        string legacyJsonPath = Path.Combine(root, "macros.json");
        if (!File.Exists(legacyAccountPath) && !File.Exists(legacyCharacterPath) && !File.Exists(legacyJsonPath)) return false;
        Directory.CreateDirectory(directory);
        string claimPath = Path.Combine(directory, "legacy-owner.txt");
        try
        {
            using var claim = new FileStream(claimPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(claim, new UTF8Encoding(false));
            writer.WriteLine(accountKey); writer.WriteLine(legacyCharacter); writer.Flush(); claim.Flush(true);
        }
        catch (IOException) when (File.Exists(claimPath)) { }
        string[] owner = File.ReadAllLines(claimPath);
        if (owner.Length < 2 || owner[0] != accountKey) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(paths.AccountPath)!);
        CopyMissing(legacyAccountPath, paths.AccountPath);
        CopyMissing(legacyCharacterPath, paths.CharacterPath);
        return owner[1].Equals(legacyCharacter, StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyMissing(string source, string destination)
    {
        if (!File.Exists(source) || File.Exists(destination)) return;
        try { File.Copy(source, destination, overwrite: false); }
        catch (IOException) when (File.Exists(destination)) { }
    }
}
