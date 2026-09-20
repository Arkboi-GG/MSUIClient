using System.Text;
using System.Text.Json;
using MSUIClient.World.Encounters;

namespace MSUIClient.Net;

/// <summary>
/// The schema-1 projection of an encounter definition: exactly the words the deployed Core parser
/// accepts (its <c>Keys()</c> lists reject any other member, so the client's richer schema-2 fields -
/// mechanics, tuning, phase timing - must never reach the wire). Planning keeps the full definition;
/// only Apply carries this text.
/// </summary>
public static class CommanderEncounterWire
{
    public static byte[] Schema1Utf8(CommanderEncounterDefinition d)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", 1);
            w.WriteString("id", d.Id);
            w.WriteString("name", d.Name);
            w.WriteNumber("mapId", d.MapId);
            w.WriteNumber("bossEntry", d.BossEntry);
            w.WriteStartObject("bounds"); Point(w, "min", d.Bounds.Min); Point(w, "max", d.Bounds.Max); w.WriteEndObject();
            Point(w, "tankAnchor", d.TankAnchor);
            w.WriteStartArray("teams");
            foreach (var team in d.Teams)
            { w.WriteStartObject(); w.WriteString("name", team.Name); Point(w, "anchor", team.Anchor); w.WriteEndObject(); }
            w.WriteEndArray();
            w.WriteNumber("addTanksPerTeam", d.AddTanksPerTeam);
            w.WriteNumber("healersPerTeam", d.HealersPerTeam);
            w.WriteNumber("healerCount", d.HealerCount);
            Numbers(w, "addEntries", d.AddEntries ?? []);
            w.WriteStartArray("phases");
            foreach (var p in d.Phases)
            {
                w.WriteStartObject();
                w.WriteNumber("id", p.Id); w.WriteString("name", p.Name); w.WriteNumber("priority", p.Priority);
                w.WriteNumber("healthMin", p.HealthMin); w.WriteNumber("healthMax", p.HealthMax); w.WriteNumber("airborne", p.Airborne);
                w.WriteNumber("aura", p.Aura); w.WriteBoolean("alternate", p.Alternate); w.WriteBoolean("melee", p.Melee);
                w.WriteBoolean("ranged", p.Ranged); w.WriteNumber("threatRatio", p.ThreatRatio);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("rules");
            foreach (var r in d.Rules)
            {
                w.WriteStartObject();
                w.WriteString("id", r.Id); w.WriteNumber("priority", r.Priority); w.WriteString("action", r.Action); w.WriteString("trigger", r.Trigger);
                Numbers(w, "spells", r.Spells ?? []); w.WriteNumber("phase", r.Phase); w.WriteNumber("roles", r.Roles); w.WriteNumber("radius", r.Radius);
                w.WriteNumber("durationMs", r.DurationMs); w.WriteString("target", r.Target); w.WriteNumber("spell", r.Spell);
                w.WriteBoolean("missingAura", r.MissingAura); Numbers(w, "pointSpells", r.PointSpells ?? []); Point(w, "station", r.Station ?? [0, 0, 0]);
                w.WriteNumber("toggle", r.Toggle);
                if (r.TriggerRadius != 0) w.WriteNumber("triggerRadius", r.TriggerRadius);
                if (r.ReserveCasters != 0) w.WriteNumber("reserveCasters", r.ReserveCasters);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteNumber("immuneSchools", d.ImmuneSchools);
            Numbers(w, "objectives", d.Objectives ?? []);
            w.WriteString("coverage", d.Coverage);
            w.WriteStartArray("requiredAdds");
            foreach (var a in d.RequiredAdds)
            { w.WriteStartObject(); w.WriteNumber("entry", a.Entry); w.WriteNumber("count", a.Count); w.WriteEndObject(); }
            w.WriteEndArray();
            w.WriteString("addPolicy", d.AddPolicy);
            w.WriteEndObject();
        }
        return stream.ToArray();
    }

    public static string Schema1Json(CommanderEncounterDefinition d) => Encoding.UTF8.GetString(Schema1Utf8(d));

    private static void Point(Utf8JsonWriter w, string name, float[] point)
    {
        w.WriteStartArray(name);
        foreach (float v in point) w.WriteNumberValue(v);
        w.WriteEndArray();
    }
    private static void Numbers(Utf8JsonWriter w, string name, uint[] values)
    {
        w.WriteStartArray(name);
        foreach (uint v in values) w.WriteNumberValue(v);
        w.WriteEndArray();
    }
}
