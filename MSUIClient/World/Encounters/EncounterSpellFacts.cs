using System.Numerics;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient.World.Encounters;

// ─────────────────────────────────────────────────────────────────────────────
// The bridge between the encounter subsystem's IEncounterSpellFacts contract and
// the client's two real spell sources.
//
// There are TWO of them and they are not the same thing:
//   * Spell.dbc (SpellCatalog) — what the CLIENT believes. Radii, cast times,
//     missile speed, names.
//   * the world DB (spell_cone, spell_target_position) — what the SERVER acts
//     on. Cone arcs and literal landing coordinates live only here; no DBC field
//     carries them.
// A resolver that consulted only the DBC would draw Onyxia's breath as a disc on
// her own feet and Tail Sweep as a circle instead of a rear arc. Both sources,
// always, with the DB winning where they overlap — the server is the authority
// on what actually happens.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class EncounterSpellFacts(SpellCatalog? catalog, EncounterWorldData? data)
    : IEncounterSpellFacts
{
    public SpellCatalog? Catalog { get; } = catalog;
    public EncounterWorldData? Data { get; } = data;

    public bool TryGetRadius(uint spellId, out float radius)
    {
        radius = 0f;
        if (Catalog is null || !Catalog.TryGet(spellId, out SpellInfo spell)) return false;
        // TryGetTargetingRadius walks every populated effect lane and takes the
        // largest — mixed-radius spells must show their whole affected area.
        return Catalog.TryGetTargetingRadius(spell, out radius) && radius > 0f;
    }

    /// <summary>Cone arc in degrees, sign preserved (negative = rear arc). Only the
    /// world DB knows this; there is no Spell.dbc cone field in 1.12.</summary>
    public bool TryGetConeDegrees(uint spellId, out float degrees)
    {
        degrees = 0f;
        return Data is not null && Data.ConeDegrees.TryGetValue(spellId, out degrees) && degrees != 0f;
    }

    public bool TryGetSpeed(uint spellId, out float yardsPerSecond)
    {
        yardsPerSecond = 0f;
        if (Catalog is null || !Catalog.TryGet(spellId, out SpellInfo spell)) return false;
        yardsPerSecond = spell.Speed;
        return yardsPerSecond > 0f;
    }

    public bool TryGetCastTimeMs(uint spellId, out int castTimeMs)
    {
        castTimeMs = 0;
        if (Catalog is null || !Catalog.TryGet(spellId, out SpellInfo spell)) return false;
        castTimeMs = Math.Max(spell.CastTimeMs, 0);
        return castTimeMs > 0;
    }

    /// <summary>The literal world coordinates a TARGET_LOCATION_DATABASE spell lands
    /// on. This is the single fact that makes a breath lane exact-db rather than a
    /// hand-drawn approximation.</summary>
    public bool TryGetDatabasePosition(uint spellId, out Vector3 position)
    {
        position = default;
        if (Data is null || !Data.TargetPositions.TryGetValue(spellId, out SpellTargetPosition? row))
            return false;
        position = row.Position;
        return true;
    }

    public string? SpellName(uint spellId) =>
        Catalog is not null && Catalog.TryGet(spellId, out SpellInfo spell) && spell.Name.Length > 0
            ? spell.Name
            : null;

    /// <summary>Which map a DB-positioned spell belongs to, for the "this lane is not
    /// on your map" guard in the overlay.</summary>
    public int? DatabasePositionMap(uint spellId) =>
        Data is not null && Data.TargetPositions.TryGetValue(spellId, out SpellTargetPosition? row)
            ? row.Map : null;
}
