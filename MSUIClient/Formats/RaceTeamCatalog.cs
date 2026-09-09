namespace MSUIClient.Formats;

public enum RaceTeam : byte { Horde, Alliance }

/// <summary>Core Player::TeamForRace uses ChrRaces field8 (base language),1=Horde,7=Alliance.</summary>
public sealed class RaceTeamCatalog
{
    public const string Path = @"DBFilesClient\ChrRaces.dbc";
    private readonly Dictionary<uint, RaceTeam> _teams = [];
    public RaceTeam? Team(uint race) => _teams.TryGetValue(race, out var team) ? team : null;
    public static RaceTeamCatalog? Load(MpqMount mpq) => Parse(mpq.ReadFile(Path));
    public static RaceTeamCatalog? Parse(byte[]? bytes)
    {
        DbcFile? dbc = bytes is null ? null : DbcFile.Parse(bytes);
        if (dbc is null || dbc.FieldCount < 9 || dbc.RecordSize < 36) return null;
        var result = new RaceTeamCatalog();
        for (int row = 0; row < dbc.RecordCount; row++)
        {
            uint race = dbc.GetUInt(row, 0), team = dbc.GetUInt(row, 8);
            if (team == 1) result._teams[race] = RaceTeam.Horde;
            else if (team == 7) result._teams[race] = RaceTeam.Alliance;
        }
        return result;
    }
}
