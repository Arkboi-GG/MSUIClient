namespace MSUIClient.Formats;

public readonly record struct CreatureVoice(
    uint ExertionSound,
    uint ExertionCriticalSound,
    uint InjurySound,
    uint InjuryCriticalSound,
    uint InjuryCrushingSound,
    uint DeathSound,
    uint StunSound,
    uint StandSound,
    uint FootstepClass,
    uint AggroSound,
    uint WingFlapSound,
    uint WingGlideSound,
    uint AlertSound,
    uint Fidget1Sound,
    uint Fidget2Sound,
    uint Fidget3Sound,
    uint Fidget4Sound,
    uint CustomAttack1Sound,
    uint CustomAttack2Sound,
    uint CustomAttack3Sound,
    uint CustomAttack4Sound,
    uint LoopSound,
    uint ImpactType,
    uint JumpStartSound,
    uint JumpEndSound);

/// <summary>CreatureDisplayInfo.SoundID -> CreatureSoundData voice kits, with model fallback.</summary>
public sealed class CreatureVoiceCatalog
{
    public const float DefaultCollisionHeight = 2.0277777f;
    public const string SoundPath = @"DBFilesClient\CreatureSoundData.dbc";
    private readonly Dictionary<uint, CreatureVoice> _byDisplay = [];
    private readonly Dictionary<uint, float> _collisionHeightByDisplay = [];

    public bool TryGet(uint displayId, out CreatureVoice voice) =>
        _byDisplay.TryGetValue(displayId, out voice);

    public float CollisionHeight(uint displayId, float renderScale)
    {
        float raw = _collisionHeightByDisplay.GetValueOrDefault(
            displayId, DefaultCollisionHeight);
        if (!(raw > 0f) || !float.IsFinite(raw)) raw = DefaultCollisionHeight;
        return raw * MathF.Max(float.Epsilon, renderScale);
    }

    public static CreatureVoiceCatalog? Load(MpqMount mpq)
    {
        byte[]? sounds = mpq.ReadFile(SoundPath);
        byte[]? displays = mpq.ReadFile(CreatureDisplayInfoTable.MpqPath);
        byte[]? models = mpq.ReadFile(CreatureModelDataTable.MpqPath);
        if (sounds is null || displays is null || models is null) return null;
        DbcFile? soundDbc = DbcFile.Parse(sounds);
        DbcFile? displayDbc = DbcFile.Parse(displays);
        DbcFile? modelDbc = DbcFile.Parse(models);
        if (soundDbc is null || displayDbc is null || modelDbc is null) return null;

        var voices = new Dictionary<uint, CreatureVoice>();
        for (int row = 0; row < soundDbc.RecordCount; row++)
        {
            uint id = soundDbc.GetUInt(row, 0);
            if (id != 0)
                voices[id] = new(
                    soundDbc.GetUInt(row, 1), soundDbc.GetUInt(row, 2),
                    soundDbc.GetUInt(row, 3), soundDbc.GetUInt(row, 4),
                    soundDbc.GetUInt(row, 5), soundDbc.GetUInt(row, 6),
                    soundDbc.GetUInt(row, 7), soundDbc.GetUInt(row, 8),
                    soundDbc.GetUInt(row, 9), soundDbc.GetUInt(row, 10),
                    soundDbc.GetUInt(row, 11), soundDbc.GetUInt(row, 12),
                    soundDbc.GetUInt(row, 13), soundDbc.GetUInt(row, 14),
                    soundDbc.GetUInt(row, 15), soundDbc.GetUInt(row, 16),
                    soundDbc.GetUInt(row, 17), soundDbc.GetUInt(row, 18),
                    soundDbc.GetUInt(row, 19), soundDbc.GetUInt(row, 20),
                    soundDbc.GetUInt(row, 21), soundDbc.GetUInt(row, 23),
                    soundDbc.GetUInt(row, 24), soundDbc.GetUInt(row, 25),
                    soundDbc.GetUInt(row, 26));
        }
        var modelSounds = new Dictionary<uint, uint>();
        var modelHeights = new Dictionary<uint, float>();
        for (int row = 0; row < modelDbc.RecordCount; row++)
        {
            uint id = modelDbc.GetUInt(row, 0), sound = modelDbc.GetUInt(row, 13);
            if (id != 0 && sound != 0) modelSounds[id] = sound;
            float height = modelDbc.GetFloat(row, 15);
            if (id != 0 && height > 0f && float.IsFinite(height)) modelHeights[id] = height;
        }

        var result = new CreatureVoiceCatalog();
        for (int row = 0; row < displayDbc.RecordCount; row++)
        {
            uint displayId = displayDbc.GetUInt(row, 0);
            uint modelId = displayDbc.GetUInt(row, 1);
            uint soundId = displayDbc.GetUInt(row, 2);
            if (soundId == 0)
                modelSounds.TryGetValue(modelId, out soundId);
            if (displayId != 0 && voices.TryGetValue(soundId, out CreatureVoice voice))
                result._byDisplay[displayId] = voice;
            if (displayId != 0 && modelHeights.TryGetValue(modelId, out float height))
                result._collisionHeightByDisplay[displayId] = height;
        }
        Console.WriteLine($"[dbc] Creature voices: {voices.Count} row(s), " +
            $"{result._byDisplay.Count} display mapping(s)");
        return result;
    }
}
