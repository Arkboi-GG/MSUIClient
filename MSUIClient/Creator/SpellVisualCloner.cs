namespace MSUIClient.Creator;

/// <summary>
/// Clone the direct build-5875 visual/kit/effect-name references for offline authoring.
/// Visual kits are columns 1..5 and 13; missile and area effects are 7 and 12.
/// Kit effect slots are 3..11. Sound and character-procedure references remain shared.
/// Model copying/recoloring and writing an archive belong to the caller.
/// </summary>
public class SpellVisualCloner
{
    /// <summary>Result of cloning a visual chain.</summary>
    public class CloneResult
    {
        public uint NewVisualId { get; set; }
        public Dictionary<uint, uint> KitIdMap { get; set; } = new();         // old kit ID â†’ new kit ID
        public Dictionary<uint, uint> EffectNameIdMap { get; set; } = new();  // old effectName ID â†’ new effectName ID
        public List<EffectFileMapping> EffectFiles { get; set; } = new();     // new effect IDs with their M2 paths
        public uint MissileEffectId { get; set; }                              // new missile effect ID (if any)
    }

    /// <summary>Maps an effect name ID to its M2 file path (original and custom).</summary>
    public class EffectFileMapping
    {
        public uint NewEffectId { get; set; }
        public string OriginalName { get; set; } = "";    // DBC effect name (e.g. "Fire Cast Hand")
        public string OriginalM2Path { get; set; } = "";  // Derived M2 path (e.g. "Spells\\Fire_Cast_Hand.m2")
        public string CustomName { get; set; } = "";      // New DBC effect name (e.g. "Voidstrike Cast Hand")
        public string CustomM2Path { get; set; } = "";    // New M2 path (e.g. "Spells\\Voidstrike_Cast_Hand.m2")
        public string EffectRole { get; set; } = "";      // "cast_leftHand", "missile", "impact_chest", etc.
    }

    private static readonly int[] KitEffectFields = { 3, 4, 5, 6, 7, 8, 9, 10, 11 };
    private static readonly string[] KitEffectNames = {
        "head", "chest", "base", "leftHand", "rightHand", "breath", "special1", "special2", "special3"
    };
    private static readonly int[] VisualKitFields = { 1, 2, 3, 4, 5, 13 };
    private static readonly string[] VisualKitNames = { "precast", "cast", "impact", "state", "channel", "area" };
    private static bool IsReference(uint id) => id is not 0 and not uint.MaxValue;

    /// <summary>
    /// Derive the M2 file path from a SpellVisualEffectName display name.
    /// Convention: spaces â†’ underscores, prepend "Spells\\", append ".m2"
    /// This path is used BOTH for the MPQ file path AND the DBC FilePath field [2].
    /// </summary>
    public static string EffectNameToM2Path(string effectName)
    {
        return $"Spells\\{effectName.Replace(' ', '_')}.m2";
    }

    /// <summary>
    /// Normalize a DBC FilePath to the actual MPQ file extension.
    /// Vanilla DBC uses .mdx/.mdl extensions but actual MPQ files are .m2.
    /// e.g. "Spells\Fire_Cast_Hand.mdx" â†’ "Spells\Fire_Cast_Hand.m2"
    ///      "Particles\FireShield_Cast_Base.mdl" â†’ "Particles\FireShield_Cast_Base.m2"
    /// </summary>
    public static string NormalizeM2Extension(string dbcFilePath)
    {
        if (string.IsNullOrEmpty(dbcFilePath))
            return dbcFilePath;

        // Replace .mdx or .mdl with .m2 for MPQ lookup
        if (dbcFilePath.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase) ||
            dbcFilePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
        {
            return dbcFilePath.Substring(0, dbcFilePath.Length - 4) + ".m2";
        }
        return dbcFilePath;
    }

    /// <summary>
    /// Build a custom effect name from a spell name and a role descriptor.
    /// e.g. ("Voidstrike", "cast_leftHand") â†’ "Voidstrike Cast LeftHand"
    /// The M2 path is then derived: "Spells\\Voidstrike_Cast_LeftHand.m2"
    /// </summary>
    public static string BuildCustomEffectName(string spellName, string role)
    {
        // Convert role like "cast_leftHand" to "Cast LeftHand"
        string rolePart = string.Join(" ", role.Split('_')
            .Select(p => p.Length > 0 ? char.ToUpper(p[0]) + p.Substring(1) : p));
        return $"{spellName} {rolePart}";
    }

    /// <summary>Clone direct DBC references, preserving shared links and unrelated fields.</summary>
    public static CloneResult Clone(
        DbcWriterService spellVisualDbc,
        DbcWriterService spellVisualKitDbc,
        DbcWriterService spellVisualEffectNameDbc,
        uint sourceVisualId,
        uint newVisualId,
        uint baseKitId,
        uint baseEffectId,
        string spellName)
    {
        if (spellVisualDbc.FieldCount < 16 || spellVisualKitDbc.FieldCount < 35 || spellVisualEffectNameDbc.FieldCount < 5)
            throw new InvalidDataException("Visual cloning requires the build-5875 visual, kit and effect schemas");
        uint[] source = spellVisualDbc.GetRow(sourceVisualId)
            ?? throw new KeyNotFoundException($"Source visual {sourceVisualId} not found");
        var kitIds = VisualKitFields.Select(i => source[i]).Where(IsReference).Distinct().ToArray();
        var effectIds = new HashSet<uint>();
        // Preflight the complete direct graph and destination ranges before changing any table.
        foreach (uint id in kitIds)
        {
            uint[] kit = spellVisualKitDbc.GetRow(id) ?? throw new KeyNotFoundException($"Source kit {id} not found");
            foreach (int col in KitEffectFields) if (IsReference(kit[col])) effectIds.Add(kit[col]);
        }
        foreach (int col in new[] { 7, 12 }) if (IsReference(source[col])) effectIds.Add(source[col]);
        foreach (uint id in effectIds)
            if (spellVisualEffectNameDbc.GetRow(id) is null) throw new KeyNotFoundException($"Source effect {id} not found");
        static void CheckDestination(DbcWriterService table, uint first, int count)
        {
            for (int i = 0; i < count; i++)
            {
                uint id = checked(first + (uint)i);
                if (!IsReference(id) || table.GetRow(id) is not null)
                    throw new ArgumentException($"Destination row {id} is reserved or already exists");
            }
        }
        CheckDestination(spellVisualDbc, newVisualId, 1);
        CheckDestination(spellVisualKitDbc, baseKitId, kitIds.Length);
        CheckDestination(spellVisualEffectNameDbc, baseEffectId, effectIds.Count);
        var result = new CloneResult { NewVisualId = newVisualId };
        uint nextKit = baseKitId, nextEffect = baseEffectId;
        uint CloneEffect(uint oldId, string role)
        {
            if (!IsReference(oldId)) return oldId;
            if (result.EffectNameIdMap.TryGetValue(oldId, out uint existing)) return existing;
            uint id = nextEffect++;
            uint[] row = spellVisualEffectNameDbc.CloneRow(oldId, id);
            string originalName = spellVisualEffectNameDbc.ReadString(row[1]);
            string originalPath = NormalizeM2Extension(spellVisualEffectNameDbc.ReadString(row[2]));
            string name = BuildCustomEffectName(spellName, role), path = EffectNameToM2Path(name);
            spellVisualEffectNameDbc.PatchRow(id, 1, spellVisualEffectNameDbc.AddString(name));
            spellVisualEffectNameDbc.PatchRow(id, 2, spellVisualEffectNameDbc.AddString(path));
            result.EffectNameIdMap.Add(oldId, id);
            result.EffectFiles.Add(new EffectFileMapping { NewEffectId = id, OriginalName = originalName,
                OriginalM2Path = originalPath, CustomName = name, CustomM2Path = path, EffectRole = role });
            return id;
        }
        uint[] visual = spellVisualDbc.CloneRow(sourceVisualId, newVisualId);
        for (int i = 0; i < VisualKitFields.Length; i++)
        {
            int col = VisualKitFields[i]; uint oldId = visual[col];
            if (!IsReference(oldId)) continue;
            if (!result.KitIdMap.TryGetValue(oldId, out uint newId))
            {
                newId = nextKit++;
                result.KitIdMap.Add(oldId, newId);
                uint[] kit = spellVisualKitDbc.CloneRow(oldId, newId);
                for (int j = 0; j < KitEffectFields.Length; j++)
                {
                    int effectCol = KitEffectFields[j];
                    kit[effectCol] = CloneEffect(kit[effectCol], $"{VisualKitNames[i]}_{KitEffectNames[j]}");
                }
            }
            visual[col] = newId;
        }
        visual[7] = CloneEffect(visual[7], "missile");
        if (IsReference(visual[7])) result.MissileEffectId = visual[7];
        visual[12] = CloneEffect(visual[12], "area");
        return result;
    }
}
