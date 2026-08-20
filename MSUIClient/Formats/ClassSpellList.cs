namespace MSUIClient.Formats;

/// <summary>
/// The nine 1.12 playable classes and their "fully trained at level N" spell
/// rosters, derived entirely offline from SkillLineAbility.dbc class masks plus
/// Spell.dbc levels — no trainer packets, no server. Rank chains collapse to the
/// best learnable rank per spell name, which is what a level-60 character with
/// every trainer visit behind them would actually have on their bars.
/// </summary>
public static class ClassSpellList
{
    public readonly record struct PlayableClass(byte Id, string Name);

    /// <summary>ChrClasses ids; 6 and 10 do not exist in 1.12.</summary>
    public static readonly IReadOnlyList<PlayableClass> Classes =
    [
        new(1, "Warrior"), new(2, "Paladin"), new(3, "Hunter"), new(4, "Rogue"),
        new(5, "Priest"), new(7, "Shaman"), new(8, "Mage"), new(9, "Warlock"),
        new(11, "Druid"),
    ];

    public static string ClassName(uint id)
    {
        foreach (PlayableClass entry in Classes)
            if (entry.Id == id) return entry.Name;
        return id == 0 ? "no class" : $"class {id}";
    }

    /// <summary>
    /// Active, castable spells this class has trained by the given level: best
    /// rank per name, alphabetical. Passives and hidden client-side rows are
    /// excluded, as are race-gated rows (no race is being chosen here).
    /// Talent-granted actives are INCLUDED when a talent catalog is supplied —
    /// this is a sandbox roster for authoring rotations, not a single-build
    /// legality check. Their rows carry classMask 0 in SkillLineAbility, so they
    /// enter through the talent trees and then pull their trainer rank upgrades
    /// in by (name, skill line).
    /// </summary>
    public static List<SpellInfo> TrainedAt(SpellCatalog spells, SkillLineCatalog skills,
        byte classId, uint level = 60, TalentCatalog? talents = null)
    {
        if (classId is 0 or > 32) return [];
        uint bit = 1u << (classId - 1);

        bool Usable(in SpellInfo info) =>
            !info.Passive && info.InSpellbook &&
            Math.Max(info.BaseLevel, info.SpellLevel) <= level;

        // Pass 1: rows that literally say "this class learns this spell".
        var pool = new Dictionary<uint, SpellInfo>();
        // name -> the class's skill lines that teach it, for the rank-completion pass.
        var nameLines = new Dictionary<string, HashSet<uint>>(StringComparer.OrdinalIgnoreCase);
        void Admit(in SpellInfo info, uint skillLine)
        {
            pool[info.Id] = info;
            if (!nameLines.TryGetValue(info.Name, out HashSet<uint>? lines))
                nameLines[info.Name] = lines = [];
            if (skillLine != 0) lines.Add(skillLine);
        }

        foreach (ClassAbilityRow row in skills.AbilityRows)
        {
            if ((row.ClassMask & bit) == 0 || (row.ClassMaskNot & bit) != 0) continue;
            if (row.RaceMask != 0) continue;   // racials need a race; none is chosen
            if (!spells.TryGet(row.SpellId, out SpellInfo info) || !Usable(info)) continue;
            Admit(info, row.SkillLineId);
        }

        // Pass 2: talent actives (Aimed Shot, Cold Snap, Mortal Strike ...).
        if (talents is not null)
            foreach (TalentTabInfo tab in talents.TabsForClass(classId))
                foreach (TalentInfo talent in talents.TalentsForTab(tab.Id))
                    foreach (uint rankSpell in talent.RankSpells)
                    {
                        if (!spells.TryGet(rankSpell, out SpellInfo info) ||
                            !Usable(info)) continue;
                        Admit(info, skills.SpellLine(rankSpell));
                    }

        // Pass 3: rank completion. Trainer upgrades of talent actives carry
        // classMask 0; the (name, class skill line) pair proves they belong.
        foreach (ClassAbilityRow row in skills.AbilityRows)
        {
            if (row.ClassMask != 0 || row.RaceMask != 0) continue;
            if ((row.ClassMaskNot & bit) != 0) continue;
            if (!spells.TryGet(row.SpellId, out SpellInfo info) || !Usable(info)) continue;
            if (!nameLines.TryGetValue(info.Name, out HashSet<uint>? lines) ||
                !lines.Contains(row.SkillLineId)) continue;
            pool[info.Id] = info;
        }

        // Ranks share a name; the learnable one with the highest level (id as
        // the tiebreak) is the rank a fully trained character actually uses.
        var byName = new Dictionary<string, SpellInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (SpellInfo info in pool.Values)
            if (!byName.TryGetValue(info.Name, out SpellInfo best) ||
                info.BaseLevel > best.BaseLevel ||
                (info.BaseLevel == best.BaseLevel && info.Id > best.Id))
                byName[info.Name] = info;
        return byName.Values
            .OrderBy(spell => spell.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
