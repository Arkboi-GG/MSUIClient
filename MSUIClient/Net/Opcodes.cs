namespace MSUIClient.Net;

// 1.12.1 (client build 5875) opcodes. Values verified against
// benilla-protocol/src/messages/opcode.rs (which cites vmangos). This is the
// working subset for connect + entities + movement + combat; add more from
// opcode.rs as new packets are handled. Note: 1.12 has NO SMSG_HEALTH_UPDATE /
// SMSG_POWER_UPDATE / TIME_SYNC — health, power and stats all arrive as UNIT
// fields in SMSG_UPDATE_OBJECT.
public enum Op : ushort
{
    // --- Auth / session ---
    SMSG_AUTH_CHALLENGE          = 0x01EC,
    CMSG_AUTH_SESSION            = 0x01ED,
    SMSG_AUTH_RESPONSE           = 0x01EE,
    SMSG_WARDEN_DATA             = 0x02E6,
    CMSG_PING                    = 0x01DC,
    SMSG_PONG                    = 0x01DD,

    // --- Character select / login ---
    CMSG_CHAR_CREATE             = 0x0036,
    CMSG_CHAR_ENUM               = 0x0037,
    CMSG_CHAR_DELETE             = 0x0038,
    SMSG_CHAR_CREATE             = 0x003A,
    SMSG_CHAR_ENUM               = 0x003B,
    SMSG_CHAR_DELETE             = 0x003C,
    CMSG_PLAYER_LOGIN            = 0x003D,
    SMSG_NEW_WORLD               = 0x003E,
    SMSG_TRANSFER_PENDING        = 0x003F,
    SMSG_TRANSFER_ABORTED        = 0x0040,
    SMSG_CHARACTER_LOGIN_FAILED  = 0x0041,
    SMSG_LOGIN_SETTIMESPEED      = 0x0042,
    SMSG_LOGIN_VERIFY_WORLD      = 0x0236,
    CMSG_SET_ACTIVE_MOVER        = 0x026A,
    CMSG_LOGOUT_REQUEST          = 0x004B,
    SMSG_LOGOUT_RESPONSE         = 0x004C,
    SMSG_LOGOUT_COMPLETE         = 0x004D,
    CMSG_COMPLETE_CINEMATIC      = 0x00FC,
    SMSG_TUTORIAL_FLAGS          = 0x00FD,
    CMSG_MOVE_WORLDPORT_ACK      = 0x00DC,

    // --- Object updates ---
    SMSG_UPDATE_OBJECT           = 0x00A9,
    SMSG_DESTROY_OBJECT          = 0x00AA,
    SMSG_COMPRESSED_UPDATE_OBJECT = 0x01F6,

    // --- Queries ---
    CMSG_NAME_QUERY              = 0x0050,
    SMSG_NAME_QUERY_RESPONSE     = 0x0051,
    CMSG_CREATURE_QUERY          = 0x0060,
    SMSG_CREATURE_QUERY_RESPONSE = 0x0061,

    // --- Movement (MSG_* are bidirectional) ---
    MSG_MOVE_START_FORWARD       = 0x00B5,
    MSG_MOVE_START_BACKWARD      = 0x00B6,
    MSG_MOVE_STOP                = 0x00B7,
    MSG_MOVE_JUMP                = 0x00BB,
    MSG_MOVE_FALL_LAND           = 0x00C9,
    MSG_MOVE_SET_FACING          = 0x00DA,
    MSG_MOVE_HEARTBEAT           = 0x00EE,
    MSG_MOVE_TELEPORT_ACK        = 0x00C7,
    SMSG_MONSTER_MOVE            = 0x00DD,
    CMSG_MOVE_SPLINE_DONE        = 0x02C9,
    SMSG_FORCE_RUN_SPEED_CHANGE  = 0x00E2,
    CMSG_FORCE_RUN_SPEED_CHANGE_ACK = 0x00E3,

    // --- Combat / spells ---
    CMSG_SET_SELECTION           = 0x013D,
    CMSG_ATTACKSWING             = 0x0141,
    CMSG_ATTACKSTOP              = 0x0142,
    SMSG_ATTACKSTART             = 0x0143,
    SMSG_ATTACKSTOP              = 0x0144,
    SMSG_ATTACKERSTATEUPDATE     = 0x014A,
    SMSG_AI_REACTION             = 0x013C,
    CMSG_CAST_SPELL              = 0x012E,
    SMSG_CAST_RESULT             = 0x0130,
    SMSG_SPELL_START             = 0x0131,
    SMSG_SPELL_GO                = 0x0132,

    // --- Misc world state seen at login (parsed leniently / ignored for now) ---
    SMSG_INITIAL_SPELLS          = 0x012A,
    SMSG_ACTION_BUTTONS          = 0x0129,
    SMSG_INITIALIZE_FACTIONS     = 0x0122,
    SMSG_BINDPOINTUPDATE         = 0x0155,
    CMSG_MESSAGECHAT             = 0x0095,
    SMSG_MESSAGECHAT             = 0x0096,
    SMSG_EMOTE                   = 0x0103,
    SMSG_TEXT_EMOTE              = 0x0105,
}
