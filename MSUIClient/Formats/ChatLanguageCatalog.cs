using System.Text;

namespace MSUIClient.Formats;

/// <summary>
/// Build-5875 LanguageWords.dbc and the byte-verified 0x49b560 chat substitution kernel.
/// The wire carries plaintext; this deterministic table/hash pass is what a listener hears.
/// </summary>
public sealed class ChatLanguageCatalog
{
    public const string Path = @"DBFilesClient\LanguageWords.dbc";
    public const uint FluentSkill = 300;

    private static readonly uint[] HashTable =
    [
        0x486e26ee, 0xdcaa16b3, 0xe1918eef, 0x202dafdb,
        0x341c7dc7, 0x1c365303, 0x40ef2d37, 0x65fd5e49,
        0xd6057177, 0x904ece93, 0x1c38024f, 0x98fd323b,
        0xe3061ae7, 0xa39b0fa1, 0x9797f25f, 0xe4444563,
    ];

    private sealed class Pool
    {
        public readonly List<byte[]> Words = [];
        public readonly List<List<int>> ByLength = [];

        public byte[]? Pick(int length, uint hash)
        {
            if (length < 0 || length >= ByLength.Count || ByLength[length].Count == 0)
                return null;
            List<int> bucket = ByLength[length];
            return Words[bucket[(int)(hash % (uint)bucket.Count)]];
        }
    }

    private readonly Dictionary<uint, Pool> _pools = [];
    public int LanguageCount => _pools.Count;
    public int WordCount => _pools.Values.Sum(pool => pool.Words.Count);

    public static ChatLanguageCatalog? Load(MpqMount mpq)
    {
        byte[]? bytes = mpq.ReadFile(Path);
        DbcFile? dbc = bytes is null ? null : DbcFile.Parse(bytes);
        if (dbc is null || dbc.FieldCount < 3) return null;

        var result = new ChatLanguageCatalog();
        for (int row = 0; row < dbc.RecordCount; row++)
        {
            uint language = dbc.GetUInt(row, 1);
            string word = dbc.GetString(row, 2);
            if (language == 0 || word.Length == 0) continue;
            byte[] utf8 = Encoding.UTF8.GetBytes(word);
            if (!result._pools.TryGetValue(language, out Pool? pool))
                result._pools[language] = pool = new Pool();
            int index = pool.Words.Count;
            pool.Words.Add(utf8);
            while (pool.ByLength.Count <= utf8.Length) pool.ByLength.Add([]);
            pool.ByLength[utf8.Length].Add(index);
        }
        return result;
    }

    public string GarbleChat(uint language, uint skill, string source)
    {
        const int cap = 0x800 - 1;
        if (language == 0 || skill >= FluentSkill) return TruncateUtf8(source, cap);
        if (!_pools.TryGetValue(language, out Pool? pool)) return TruncateUtf8(source, cap);

        byte[] src = Encoding.UTF8.GetBytes(source);
        var output = new List<byte>(Math.Min(src.Length, cap));
        int cursor = 0;
        while (cursor < src.Length)
        {
            bool emittedSpace = false;
            while (cursor < src.Length)
            {
                (uint codepoint, int consumed) = Decode(src, cursor);
                if (IsWordCharacter(codepoint)) break;
                if (!emittedSpace)
                {
                    Push(output, [(byte)' '], cap);
                    emittedSpace = true;
                }
                cursor += consumed;
            }
            if (cursor >= src.Length) break;

            int start = cursor;
            while (cursor < src.Length)
            {
                (uint codepoint, int consumed) = Decode(src, cursor);
                if (!IsWordCharacter(codepoint)) break;
                cursor += consumed;
            }

            int hashedLength = Math.Min(cursor - start, 0x100);
            ReadOnlySpan<byte> word = src.AsSpan(start, hashedLength);
            uint hash = HashFolded(word);
            if (hash % 300 < skill)
            {
                Push(output, word, cap);
                continue;
            }

            int key = Math.Min(word.Length, 0x12);
            byte[]? substitute = null;
            while (key >= 1 && (substitute = pool.Pick(key, hash)) is null) key--;
            if (substitute is not null) StampCase(output, word, substitute, cap);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    public static uint HashFolded(ReadOnlySpan<byte> bytes)
    {
        uint first = 0x7fed7fed;
        uint second = 0xeeeeeeee;
        unchecked
        {
            foreach (byte raw in bytes)
            {
                if (raw == 0) break;
                byte c = raw is >= (byte)'a' and <= (byte)'z'
                    ? (byte)(raw - 0x20) : raw;
                if (c == (byte)'/') c = (byte)'\\';
                uint sum = first + second;
                first = sum ^ (HashTable[c >> 4] - HashTable[c & 0x0f]);
                second = second * 0x21 + c + 3 + first;
            }
        }
        return first == 0 ? 1u : first;
    }

    public static bool IsWordCharacter(uint codepoint) =>
        IsLatin1Letter(codepoint) || codepoint is >= (uint)'0' and <= (uint)'9' ||
        codepoint == 0x27 || codepoint > 0xff;

    private static bool IsLatin1Letter(uint cp) =>
        cp is >= (uint)'A' and <= (uint)'Z' or >= (uint)'a' and <= (uint)'z' ||
        cp is >= 0xc0 and <= 0xdd && cp != 0xd7 || cp == 0xdf ||
        cp is >= 0xe0 and <= 0xff && cp != 0xf7;

    private static (uint Codepoint, int Consumed) Decode(byte[] bytes, int offset)
    {
        byte first = bytes[offset];
        if (first <= 0x7f) return (first, 1);
        int count;
        uint cp;
        if (first is >= 0xc0 and <= 0xdf) { count = 2; cp = (uint)(first & 0x1f); }
        else if (first is >= 0xe0 and <= 0xef) { count = 3; cp = (uint)(first & 0x0f); }
        else if (first is >= 0xf0 and <= 0xf7) { count = 4; cp = (uint)(first & 0x07); }
        else if (first is >= 0xf8 and <= 0xfb) { count = 5; cp = (uint)(first & 0x03); }
        else if (first is >= 0xfc and <= 0xfd) { count = 6; cp = (uint)(first & 0x01); }
        else return (0x80000000, 1);
        if (offset + count > bytes.Length) return (0x80000000, 1);
        for (int i = 1; i < count; i++)
        {
            byte continuation = bytes[offset + i];
            if ((continuation & 0xc0) != 0x80) return (0x80000000, 1);
            cp = (cp << 6) | (uint)(continuation & 0x3f);
        }
        return (cp, count);
    }

    private static void StampCase(
        List<byte> output, ReadOnlySpan<byte> source, ReadOnlySpan<byte> substitute, int cap)
    {
        int count = Math.Min(Math.Min(source.Length, substitute.Length), cap - output.Count);
        for (int i = 0; i < count; i++)
        {
            byte value = substitute[i];
            output.Add(source[i] is >= (byte)'A' and <= (byte)'Z'
                ? ToUpperAscii(value) : ToLowerAscii(value));
        }
    }

    private static byte ToUpperAscii(byte value) => value is >= (byte)'a' and <= (byte)'z'
        ? (byte)(value - 0x20) : value;
    private static byte ToLowerAscii(byte value) => value is >= (byte)'A' and <= (byte)'Z'
        ? (byte)(value + 0x20) : value;

    private static void Push(List<byte> output, ReadOnlySpan<byte> bytes, int cap)
    {
        int count = Math.Min(bytes.Length, cap - output.Count);
        for (int i = 0; i < count; i++) output.Add(bytes[i]);
    }

    private static string TruncateUtf8(string value, int cap)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        if (utf8.Length <= cap) return value;
        int count = cap;
        while (count > 0 && (utf8[count] & 0xc0) == 0x80) count--;
        return Encoding.UTF8.GetString(utf8, 0, count);
    }
}
