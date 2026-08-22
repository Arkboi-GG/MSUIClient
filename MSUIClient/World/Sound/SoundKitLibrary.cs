using MSUIClient.Formats;

namespace MSUIClient.World.Sound;

/// <summary>
/// SoundEntries.dbc, and the one question it answers: WHICH FILE does this kit
/// play right now.
///
/// This is shared policy, not a device concern and not a spell concern - a zone
/// ambience bed and a fireball impact resolve their file by the identical rules
/// (weighted variant choice, plus the 0x20 "no duplicates" flag which forbids the
/// same variant twice in a row). It lives here so both callers use those rules
/// rather than one of them borrowing the other's channels to get at them.
/// </summary>
public sealed class SoundKitLibrary
{
    private readonly SoundEntriesCatalog? _catalog;

    /// <summary>The last kit:file actually chosen, for the dev readouts.</summary>
    public string LastCue { get; private set; } = "";

    private readonly Dictionary<uint, string> _lastVariant = [];

    public SoundKitLibrary(MpqMount mpq) => _catalog = SoundEntriesCatalog.Load(mpq);

    public bool TryGet(uint? soundId, out SoundEntry entry)
    {
        entry = default;
        return soundId is uint id && id != 0 && _catalog?.TryGet(id, out entry) == true;
    }

    public bool TryGet(string soundName, out SoundEntry entry)
    {
        entry = default;
        return _catalog?.TryGet(soundName, out entry) == true;
    }

    /// <summary>Whether the authored flags say this kit loops. Callers may force a
    /// loop on top of this; they may not turn one off.</summary>
    public bool IsAuthoredLoop(uint? soundId)
        => TryGet(soundId, out SoundEntry entry) && entry.Looping;

    /// <summary>Roll the weighted variant list, honouring the no-duplicates flag.</summary>
    public SoundVariant PickVariant(in SoundEntry entry)
    {
        IReadOnlyList<SoundVariant> candidates = entry.Variants;
        uint total = 0;
        foreach (SoundVariant candidate in candidates) total += candidate.Weight;
        SoundVariant picked;
        if (total == 0) picked = candidates[Random.Shared.Next(candidates.Count)];
        else
        {
            uint roll = (uint)Random.Shared.NextInt64(total);
            picked = candidates[0];
            foreach (SoundVariant candidate in candidates)
            {
                if (roll < candidate.Weight) { picked = candidate; break; }
                roll -= candidate.Weight;
            }
        }
        if (entry.NoDuplicates && candidates.Count > 1 &&
            _lastVariant.GetValueOrDefault(entry.Id) == picked.Path)
        {
            SoundVariant alternate = candidates.FirstOrDefault(candidate => candidate.Path != picked.Path);
            if (!string.IsNullOrEmpty(alternate.Path)) picked = alternate;
        }
        _lastVariant[entry.Id] = picked.Path;
        LastCue = $"{entry.Id}:{picked.Path}";
        return picked;
    }

    /// <summary>Select an authored variation by its exact zero-based index.</summary>
    public SoundVariant PickVariantAt(in SoundEntry entry, int variation)
    {
        if ((uint)variation >= (uint)entry.Variants.Count)
            throw new ArgumentOutOfRangeException(nameof(variation));
        SoundVariant picked = entry.Variants[variation];
        _lastVariant[entry.Id] = picked.Path;
        LastCue = $"{entry.Id}:{picked.Path}";
        return picked;
    }
}
