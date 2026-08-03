using System.Numerics;

namespace MSUIClient.Formats;

/// <summary>
/// Vanilla SpellVisual -> kit -> effect-model / body-animation / missile chain.
///
/// SCHEMA (byte-verified against build 5875; re-verified against our own MPQs by
/// tools/spellvis/spellvis.py, 2026-08-02 — that tool is the ORACLE for this
/// file: NIGHT_03/reference/spellvis-reference.csv is generated from the same
/// tables and any disagreement between it and this loader is a bug HERE.)
///
///   SpellVisual.dbc          2165 rec x 16 fields x 64 B, all u32
///     f0  id
///     f1  precastKit   f2 castKit   f3 impactKit   f4 stateKit   f5 channelKit
///     f6  hasMissile   -- NEVER READ. The spawn gate is Spell.dbc Speed > 0.
///     f7  missile SpellVisualEffectName id
///     f9  missile destination-attachment ORDINAL (index into MissileAttachTable)
///     f10 missile in-flight LOOP sound (SoundEntries id)
///
///   SpellVisualKit.dbc       1772 rec x 35 fields x 140 B, all u32
///     f0  id      f2 AnimationData.dbc id      f13 SoundEntries.dbc id
///     f3..f11  the NINE SpellVisualEffectName slots (see KitAttachmentIds)
///
///   SpellVisualEffectName.dbc  775 rec x 5 fields x 20 B
///     f0 id   f1 label   f2 model path
///
/// THE NONE-SENTINEL (this is a real trap, found empirically)
///   "No value" is written as EITHER 0 OR 0xFFFFFFFF, inconsistently, on the
///   same table. Of 1772 kits, 41 carry anim 0 and 875 carry 0xFFFFFFFF. Fold
///   BOTH or you silently drop most impact kits. Fk() is that fold and every
///   foreign-key read in this file goes through it.
///
/// THE EXTENSION LAW (this is why nothing rendered)
///   The DBC ships .mdx / .mdl paths. ZERO of the 530 distinct paths resolve as
///   written; 515 resolve after swapping to .m2. The swap lives HERE, at the
///   single point paths leave the catalog, so no consumer can forget it. The 15
///   that never resolve are stale pre-release rows (Particles\Frost_Precast_*,
///   Particles\BloodLust_*, DeathTouchCast.mdl): those are DEAD DBC ROWS, not
///   missing art — the reference client takes its fallback and moves on, so they
///   must NOT be treated as a blocking missing-asset.
///
/// STAGE SEMANTICS
///   The stage does NOT select "which visual" — every populated slot on a
///   reached row fires. The stage selects LIFETIME POLICY only. See StageLife.
///
/// NO GL, NO GAME LOGIC - Formats/ rule.
/// </summary>
public readonly record struct SpellVisualStages(
    uint Precast, uint Cast, uint Impact, uint State, uint Channel,
    uint MissileEffect, ushort MissileAttachment, uint MissileSound, uint StrikeSound);

public readonly record struct SpellVisualKitInfo(
    ushort? AnimationId,
    uint? Sound,
    IReadOnlyList<(ushort AttachmentId, string ModelPath)> Effects);

/// <summary>
/// Which owner's reap may kill an instance of this stage.
///
/// Persistent instances live until a spell-id-keyed reap; self-terminating ones
/// run out on their own clock (one pass of the model's first sequence). State
/// (aura) instances are deliberately a THIRD case: a cast's GO releasing its
/// precast must not sweep the same spell's aura models — re-eating while the
/// food buff holds must not take the bread with it.
/// </summary>
public enum StageLife { SelfTerminating, Persistent, AuraState }

public enum SpellStage { Precast, Cast, Impact, State, Channel }

public sealed class SpellVisualCatalog
{
    /// <summary>
    /// Kit fields 3..11 -> M2 AttachmentID, IN KIT-FIELD ORDER. These are
    /// compile-time immediates in the reference client's slot loop, not data:
    /// Head, Chest, Base, LeftHand, RightHand, Breath, Special1..3.
    /// </summary>
    public static readonly ushort[] KitAttachmentIds =
        [0x14, 0x22, 0x13, 0x15, 0x16, 0x11, 0x17, 0x18, 0x19];

    /// <summary>
    /// SpellVisual field 9 is an ORDINAL into this table, not an attachment id.
    /// Storing the raw ordinal and using it as a tag (the previous bug) aims
    /// every missile at whatever attachment happens to share that number.
    /// The nine kit tags, then 0x0F and 0x10 at ordinals 9 and 10.
    /// </summary>
    public static readonly ushort[] MissileAttachTable =
        [0x14, 0x22, 0x13, 0x15, 0x16, 0x11, 0x17, 0x18, 0x19, 0x0F, 0x10];

    /// <summary>The reference client's literal fallback when a missile chain resolves to nothing.</summary>
    public const string ErrorCube = @"Spells\ErrorCube.m2";

    private readonly Dictionary<uint, SpellVisualStages> _visuals = [];
    private readonly Dictionary<uint, (ushort? Anim, uint? Sound, uint[] Effects)> _kits = [];
    private readonly Dictionary<uint, string> _effectPaths = [];

    /// <summary>Fold BOTH none-sentinels. See the class remarks.</summary>
    private static uint? Fk(uint raw) => raw is 0 or uint.MaxValue ? null : raw;

    /// <summary>
    /// The extension law. DBC ships .mdx/.mdl; the archives hold .m2. Applied at
    /// every exit point of this catalog so a consumer cannot skip it.
    /// </summary>
    public static string ModelPath(string dbcPath)
    {
        if (string.IsNullOrEmpty(dbcPath)) return "";
        string p = dbcPath.Replace('/', '\\');
        return p.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase) ||
               p.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)
            ? string.Concat(p.AsSpan(0, p.Length - 4), ".m2")
            : p;
    }

    public static StageLife LifeOf(SpellStage stage) => stage switch
    {
        SpellStage.Precast or SpellStage.Channel => StageLife.Persistent,
        SpellStage.State => StageLife.AuraState,
        _ => StageLife.SelfTerminating,   // Cast, Impact
    };

    public static uint KitFor(in SpellVisualStages s, SpellStage stage) => stage switch
    {
        SpellStage.Precast => s.Precast,
        SpellStage.Cast => s.Cast,
        SpellStage.Impact => s.Impact,
        SpellStage.State => s.State,
        _ => s.Channel,
    };

    public bool TryGetStages(uint id, out SpellVisualStages stages)
        => _visuals.TryGetValue(id, out stages);

    public bool TryGetKit(uint id, out SpellVisualKitInfo kit)
    {
        if (Fk(id) is null || !_kits.TryGetValue(id, out var raw)) { kit = default; return false; }
        var effects = new List<(ushort, string)>(9);
        for (int i = 0; i < raw.Effects.Length; i++)
        {
            if (Fk(raw.Effects[i]) is not uint effect) continue;
            if (!_effectPaths.TryGetValue(effect, out string? path) || path.Length == 0) continue;
            effects.Add((KitAttachmentIds[i], ModelPath(path)));
        }
        kit = new SpellVisualKitInfo(raw.Anim, raw.Sound, effects);
        return true;
    }

    /// <summary>
    /// One stage of one visual, resolved: the kit and the lifetime policy that
    /// stage implies. Consumers should call THIS rather than re-deriving the
    /// stage-to-kit mapping and the lifetime rule at each call site.
    /// </summary>
    public bool TryGetStageKit(uint visualId, SpellStage stage,
        out SpellVisualKitInfo kit, out StageLife life)
    {
        life = LifeOf(stage);
        kit = default;
        return TryGetStages(visualId, out SpellVisualStages stages) &&
               TryGetKit(KitFor(stages, stage), out kit);
    }

    /// <summary>
    /// The projectile model, already extension-swapped. Null means the chain
    /// named nothing: the caller must then take the ammo/weapon ItemDisplayInfo
    /// path, and failing that <see cref="ErrorCube"/>. A missile with no model
    /// at all still flies and still impacts on schedule — it is simply invisible.
    /// NOTE the gate: a missile exists whenever Spell.dbc Speed > 0, regardless
    /// of whether this returns a path.
    /// </summary>
    public string? MissilePath(in SpellVisualStages stages)
        => Fk(stages.MissileEffect) is uint id &&
           _effectPaths.TryGetValue(id, out string? path) && path.Length > 0
            ? ModelPath(path) : null;

    public static SpellVisualCatalog? Load(MpqMount mpq)
    {
        DbcFile? visuals = Parse(mpq, @"DBFilesClient\SpellVisual.dbc");
        DbcFile? kits = Parse(mpq, @"DBFilesClient\SpellVisualKit.dbc");
        DbcFile? names = Parse(mpq, @"DBFilesClient\SpellVisualEffectName.dbc");
        if (visuals is null || kits is null || names is null ||
            visuals.FieldCount < 16 || kits.FieldCount < 35 || names.FieldCount < 5) return null;

        var result = new SpellVisualCatalog();

        for (int row = 0; row < names.RecordCount; row++)
        {
            uint id = names.GetUInt(row, 0);
            string path = names.GetString(row, 2);
            if (id != 0 && path.Length > 0) result._effectPaths[id] = path;
        }

        for (int row = 0; row < kits.RecordCount; row++)
        {
            uint id = kits.GetUInt(row, 0);
            var effects = new uint[9];
            for (int i = 0; i < effects.Length; i++) effects[i] = kits.GetUInt(row, 3 + i);
            result._kits[id] = (
                Fk(kits.GetUInt(row, 2)) is uint a ? (ushort)a : null,
                Fk(kits.GetUInt(row, 13)),
                effects);
        }

        for (int row = 0; row < visuals.RecordCount; row++)
        {
            uint id = visuals.GetUInt(row, 0);
            uint ordinal = visuals.GetUInt(row, 9);
            result._visuals[id] = new SpellVisualStages(
                Precast: visuals.GetUInt(row, 1),
                Cast: visuals.GetUInt(row, 2),
                Impact: visuals.GetUInt(row, 3),
                State: visuals.GetUInt(row, 4),
                Channel: visuals.GetUInt(row, 5),
                MissileEffect: visuals.GetUInt(row, 7),
                // The ordinal maps THROUGH the table. Out of range -> chest, the
                // reference's own practical default for a body-homing projectile.
                MissileAttachment: ordinal < (uint)MissileAttachTable.Length
                    ? MissileAttachTable[ordinal] : (ushort)0x22,
                MissileSound: visuals.GetUInt(row, 10) is var snd && Fk(snd) is uint s ? s : 0,
                StrikeSound: visuals.GetUInt(row, 14) is var strike && Fk(strike) is uint st ? st : 0);
        }

        return result;
    }

    private static DbcFile? Parse(MpqMount mpq, string path)
        => mpq.ReadFile(path) is { } bytes ? DbcFile.Parse(bytes) : null;
}
