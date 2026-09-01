using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

/// <summary>
/// "Your skill in Swords has increased to 42." — the chat line every skill rank-up prints.
///
/// THERE IS NO PACKET FOR THIS, WHICH IS WHY IT WAS MISSING. The skill itself always worked;
/// only the announcement was absent, because the announcement was never the server's to send.
/// CHAT_MSG_SKILL (0x17) exists in the wire enum but vmangos never builds one — grep the core
/// and SharedDefines.h is the sole hit. The 1.12 client composes the line itself by watching its
/// own PLAYER_SKILL_INFO fields, which is exactly what <see cref="ChatFrameLaw.MsgType"/>
/// already records: "Client-composed lines - NEVER SMSG_MESSAGECHAT wire in 1.12". The Skill
/// type and its 1.12 blue were sitting there with nothing emitting them.
///
/// A rank-up is an increase in the CURRENT rank, the low half of the skill triple's second
/// field. The high half is the cap and the third field is the temporary bonus; both move for
/// reasons that are not rank-ups — the cap rises on every level for weapon skills, and a bonus
/// swings whenever a +skill item comes on or off. Watching the low half alone is what
/// ObjectFields.PlayerSkills already documents itself as being for.
/// </summary>
public sealed partial class GameLoop
{
    /// <summary>Last announced rank per skill id. Empty means "no baseline yet".</summary>
    private readonly Dictionary<ushort, ushort> _skillRanks = [];

    /// <summary>
    /// The descriptor arrives with every skill already at its current rank, so the first pass
    /// after login would otherwise announce the character's whole skill list at once. Seed
    /// silently, announce from then on.
    /// </summary>
    private bool _skillRanksSeeded;

    private void ObserveSkillRankUps()
    {
        if (_net is not { IsInWorld: true } net || net.PlayerGuid == 0 ||
            !_entities.TryGet(net.PlayerGuid, out WorldEntity player))
            return;

        foreach ((_, ushort skillId, ushort rank) in player.Fields.PlayerSkills())
        {
            if (rank == 0) continue;

            if (!_skillRanks.TryGetValue(skillId, out ushort previous))
            {
                _skillRanks[skillId] = rank;
                continue;                       // first sighting of this line is a baseline
            }
            if (rank <= previous) { _skillRanks[skillId] = rank; continue; }

            _skillRanks[skillId] = rank;
            if (!_skillRanksSeeded) continue;   // still filling the login baseline

            AddChatMessage(SkillRankUpText(skillId, rank), ChatFrameLaw.MsgType.Skill);
            EmitInterface("skill", "rank-up", "ANNOUNCED", net.PlayerGuid,
                $"line={skillId};before={previous};after={rank}");
        }

        _skillRanksSeeded = true;
    }

    /// <summary>
    /// GlobalStrings.lua SKILL_RANK_UP, verbatim: "Your skill in %s has increased to %d."
    /// (ERR_SKILL_UP_SI carries the identical text; SKILL_RANK_UP is the one whose own comment
    /// says "Tells player when a skill rank goes up".)
    /// </summary>
    private string SkillRankUpText(ushort skillId, ushort rank)
    {
        _skillLines ??= SkillLineCatalog.Load(_mpq);
        string name = _skillLines?.TryGet(skillId, out SkillLineInfo line) == true
            ? line.Name
            : $"skill {skillId}";
        return $"Your skill in {name} has increased to {rank}.";
    }

    private void ResetSkillRankWatch()
    {
        _skillRanks.Clear();
        _skillRanksSeeded = false;
    }
}
