using System.Buffers;
using System.Numerics;
using MSUIClient.Formats;

namespace MSUIClient.Net;

// A sparse SMSG_UPDATE_OBJECT descriptor set: a bitmask of u32 blocks followed
// by one u32 per set bit, kept as an index->value map with named accessors.
// Ported from benilla-protocol update_object/fields (indices verified there
// against vmangos UpdateFields_1_12_1.h, build 5875).
//
// Absent-field semantics: a CREATE block is a COMPLETE snapshot (the server masks
// in only non-zero fields), read into a zero-initialized descriptor — so on a
// create-seeded set an absent field reads 0. A bare VALUES delta carries only
// changed fields, so an absent field there means "untouched" (null), and a merge
// must not clobber it.
/// <summary>UnitStandStateType, confirmed via vmangos/core's Unit.h - the value
/// UNIT_FIELD_BYTES_1 byte 0 carries, and what CMSG_STANDSTATECHANGE sends. The
/// client may only request Stand/Sit/Sleep/Kneel (vmangos's own
/// WorldSession::HandleStandStateChangeOpcode rejects anything else from a
/// player packet); the SitChair family is server-only, set by GameObject-use
/// (a chair), never something the client asks for directly.</summary>
public enum UnitStandState : byte
{
    Stand = 0,
    Sit = 1,
    SitChair = 2,
    Sleep = 3,
    SitLowChair = 4,
    SitMediumChair = 5,
    SitHighChair = 6,
    Dead = 7,
    Kneel = 8,
    Custom = 9,
}

public sealed class ObjectFields
{
    // --- field indices (build 5875) ---
    public const ushort OBJECT_GUID = 0;             // 2 slots
    public const ushort OBJECT_TYPE = 2;
    public const ushort OBJECT_ENTRY = 3;            // creature/gameobject template entry
    public const ushort OBJECT_SCALE_X = 4;          // f32

    // GameObject update fields begin at the same build-5875 index as the Unit
    // family. This public creator GUID is written by VMaNGOS SetOwnerGuid for
    // summoned objects and lets portal cast prewarm bind to the caster's exact
    // spawned GameObject instead of guessing by entry or proximity.
    public const ushort GAMEOBJECT_CREATED_BY = 6;   // guid
    public const ushort GAMEOBJECT_FLAGS = 9;

    public const ushort UNIT_FIELD_CHARM = 6;        // guid of controlled/charmed unit
    public const ushort UNIT_FIELD_SUMMON = 8;       // guid of owned summon/pet
    public const ushort UNIT_FIELD_CHARMEDBY = 10;   // guid of controlling unit
    public const ushort UNIT_FIELD_SUMMONEDBY = 12;  // guid of summoner
    public const ushort UNIT_FIELD_CREATEDBY = 14;   // guid of creator (guardian/totem ownership)
    public const ushort UNIT_TARGET = 16;            // guid
    public const ushort UNIT_FIELD_CHANNEL_OBJECT = 20; // guid
    public const ushort UNIT_HEALTH = 22;
    public const ushort UNIT_POWER1 = 23;            // five slots, indexed by power type
    public const ushort UNIT_MAXHEALTH = 28;
    public const ushort UNIT_MAXPOWER1 = 29;         // five slots, indexed by power type
    public const ushort UNIT_LEVEL = 34;
    public const ushort UNIT_FACTIONTEMPLATE = 35;
    public const ushort UNIT_BYTES_0 = 36;           // race/class/gender/powertype
    public const ushort UNIT_VIRTUAL_ITEM_SLOT_DISPLAY = 37; // three display ids
    public const ushort UNIT_VIRTUAL_ITEM_INFO = 40; // two dwords per held slot
    public const ushort UNIT_FLAGS = 46;
    public const ushort UNIT_AURA = 47;              // 48 spell ids
    public const ushort UNIT_AURAFLAGS = 95;         // 8 nibbles per dword
    public const ushort UNIT_AURALEVELS = 101;       // 4 bytes per dword
    public const ushort UNIT_AURAAPPLICATIONS = 113; // stack-minus-one bytes
    public const ushort UNIT_AURASTATE = 125;
    public const ushort UNIT_BASEATTACKTIME = 126;
    public const ushort UNIT_RANGEDATTACKTIME = 128;
    public const ushort UNIT_BOUNDINGRADIUS = 129;   // f32, horizontal bounding radius (yd)
    public const ushort UNIT_COMBATREACH = 130;      // f32, melee-reach term (default 1.5)
    public const ushort UNIT_DISPLAYID = 131;        // the rendered CreatureDisplayInfo id
    public const ushort UNIT_MOUNTDISPLAYID = 133;
    public const ushort UNIT_MINDAMAGE = 134;
    public const ushort UNIT_MAXDAMAGE = 135;
    public const ushort UNIT_MINOFFHANDDAMAGE = 136;
    public const ushort UNIT_MAXOFFHANDDAMAGE = 137;
    // byte0: UnitStandStateType (see that enum below). Not in the original
    // build-5875 sweep that seeded this file - read out of vmangos/core's
    // UpdateFields.h directly (0x84 relative to OBJECT_END, i.e. exactly one
    // past MAXOFFHANDDAMAGE and one before PETNUMBER below), cross-checked
    // against the same header's CMSG/SMSG_EMOTE and CMSG/SMSG_TEXT_EMOTE
    // entries, which land on this project's own already-confirmed 0x0102-0x0105
    // values exactly - so the neighbouring 0x8A/138 here carries that same
    // confidence rather than being a fresh guess.
    public const ushort UNIT_FIELD_BYTES_1 = 138;
    public const ushort UNIT_FIELD_PETNUMBER = 139; // nonzero for a permanent pet/charm
    public const ushort UNIT_FIELD_PETEXPERIENCE = 141;
    public const ushort UNIT_FIELD_PETNEXTLEVELEXP = 142;
    public const ushort UNIT_DYNAMIC_FLAGS = 143;
    public const ushort UNIT_CHANNEL_SPELL = 144;
    public const ushort UNIT_MOD_CAST_SPEED = 145;
    public const ushort UNIT_CREATED_BY_SPELL = 146;
    public const ushort UNIT_BASE_MANA = 162;        // PRIVATE+OWNER_ONLY: only our own descriptor
    public const ushort UNIT_BASE_HEALTH = 163;
    public const ushort PLAYER_SELF_RES_SPELL = 1224;
    public const ushort UNIT_ATTACK_POWER_MULTIPLIER = 167;
    public const ushort UNIT_RANGED_ATTACK_POWER_MULTIPLIER = 170;
    public const ushort UNIT_POWER_COST_MODIFIER = 173;
    public const ushort UNIT_POWER_COST_MULTIPLIER = 180;
    public const ushort UNIT_NPC_FLAGS = 147;
    // Confirmed the same way as UNIT_FIELD_BYTES_1 above: read directly out of
    // vmangos/core's UpdateFields_1_12_1.h (OBJECT_END+0x8E), cross-checked
    // against CHANNEL_SPELL/NPC_FLAGS in that same header landing exactly on
    // this project's own already-confirmed 144/147. This is the field
    // Unit::HandleEmoteState writes for a "state" emote (EmoteType 1 or 2 in
    // Emotes.dbc) instead of the SMSG_EMOTE one-shot packet - Dance included:
    // /dance's EmotesText.dbc row targets Emotes.dbc id 10 (STATE_DANCE,
    // EmoteType 2), so it NEVER sends SMSG_EMOTE at all, confirmed by reading
    // dumps/EmotesText.dbc's own EmoteID column directly. Holds a raw Emotes.dbc
    // id (0 = no state emote active), not an AnimationData id - resolve it
    // through EmoteCatalog (the live Emotes.dbc) same as SMSG_EMOTE's payload.
    public const ushort UNIT_NPC_EMOTESTATE = 148;
    public const ushort UNIT_FIELD_TRAINING_POINTS = 149;
    public const ushort UNIT_STAT0 = 150;
    public const ushort UNIT_RESISTANCES = 155;
    public const ushort UNIT_BYTES_2 = 164;           // byte0: sheath state 0/1/2
    public const ushort UNIT_ATTACK_POWER = 165;
    public const ushort UNIT_ATTACK_POWER_MODS = 166;
    public const ushort UNIT_RANGED_ATTACK_POWER = 168;
    public const ushort UNIT_RANGED_ATTACK_POWER_MODS = 169;
    public const ushort UNIT_MINRANGEDDAMAGE = 171;
    public const ushort UNIT_MAXRANGEDDAMAGE = 172;

    public const ushort GAMEOBJECT_DISPLAYID = 8;
    public const ushort GAMEOBJECT_ROTATION = 10;    // f32 x4 quaternion (vmangos packs sin/cos of the half-yaw into z/w)
    public const ushort GAMEOBJECT_DYN_FLAGS = 19;
    public const ushort GAMEOBJECT_FACTION = 20;
    public const ushort GAMEOBJECT_TYPE_ID = 21;
    public const ushort GAMEOBJECT_STATE = 14;
    public const ushort GAMEOBJECT_ARTKIT = 23;
    public const ushort GAMEOBJECT_LEVEL = 22;

    public const ushort ITEM_STACK_COUNT = 14;
    public const ushort ITEM_FIELD_CREATOR = 10;     // guid
    public const ushort ITEM_FIELD_GIFTCREATOR = 12;  // guid
    public const ushort ITEM_SPELL_CHARGES = 16;
    public const ushort ITEM_FLAGS = 21;
    public const ushort ITEM_FIELD_ENCHANTMENT = 22; // seven triples: id, duration, charges
    public const ushort ITEM_RANDOM_PROPERTIES = 44;
    public const ushort ITEM_TEXT_ID = 45;
    public const ushort ITEM_DURABILITY = 46;
    public const ushort ITEM_MAXDURABILITY = 47;
    public const ushort CONTAINER_NUM_SLOTS = 48;
    public const ushort CONTAINER_SLOT_1 = 50;

    public const ushort PLAYER_QUEST_LOG_1_1 = 198;
    public const ushort PLAYER_FARSIGHT = 712; // guid
    public const ushort PLAYER_GUILDID = 191;
    public const ushort PLAYER_GUILDRANK = 192;
    // PLAYER_VISIBLE_ITEM_1_CREATOR begins at 258 (two u32 guid fields); the public worn
    // item ENTRY consumed by rendering/inspect is +2. Each equipment slot spans 12 fields.
    public const ushort PLAYER_VISIBLE_ITEM_1_0 = 260;
    public const ushort PLAYER_INV_SLOT_HEAD = 486;
    public const ushort PLAYER_PACK_SLOT_1 = 532;
    public const ushort PLAYER_BANK_SLOT_1 = 564;
    public const ushort PLAYER_BANK_BAG_SLOT_1 = 612;
    // The buyback trio is chain-locked between the bank-bag/keyring arrays and the
    // PLAYER_FIELD_COINAGE anchor for build 5875. Historical hex comments in the
    // vmangos header are six fields low here; the compiled enum arithmetic is not.
    public const ushort PLAYER_VENDOR_BUYBACK_SLOT_1 = 624;
    public const ushort PLAYER_KEYRING_SLOT_1 = 648;
    // Private combo-point owner GUID (two dwords); PLAYER_XP follows immediately at 716.
    public const ushort PLAYER_FIELD_COMBO_TARGET = 714;
    public const ushort PLAYER_XP = 716;
    public const ushort PLAYER_NEXT_LEVEL_XP = 717;
    public const ushort PLAYER_SKILL_INFO_1_1 = 718;
    // Signed reputation-list slot; -1 means no watched faction.
    public const ushort PLAYER_FIELD_WATCHED_FACTION_INDEX = 1261;
    public const ushort PLAYER_CHARACTER_POINTS1 = 1102;
    public const ushort PLAYER_CHARACTER_POINTS2 = 1103;
    // Private self-player fields. Tracking auras set bit (MiscValue - 1); resource
    // MiscValue is a LockType.dbc id (Herbalism=2, Mining=3).
    public const ushort PLAYER_TRACK_CREATURES = 1104;
    public const ushort PLAYER_TRACK_RESOURCES = 1105;
    public const ushort PLAYER_EXPLORED_ZONES_1 = 1111; // 64 u32 words, AreaTable.ExploreFlag indexed
    public const int PLAYER_EXPLORED_ZONES_SIZE = 64;
    public const ushort PLAYER_REST_STATE_EXPERIENCE = 1175;
    public const ushort PLAYER_COINAGE = 1176;
    public const ushort PLAYER_POSSTAT0 = 1177;
    public const ushort PLAYER_NEGSTAT0 = 1182;
    public const ushort PLAYER_RESISTANCEBUFFMODSPOSITIVE = 1187;
    public const ushort PLAYER_RESISTANCEBUFFMODSNEGATIVE = 1194;
    // Seven school-indexed owner-only fields. School zero is the physical decomposition returned
    // by the 1.12 UnitDamage / UnitRangedDamage globals. The percent field is a true f32 even
    // though the historical update-field header labels the family as integer.
    public const ushort PLAYER_FIELD_MOD_DAMAGE_DONE_POS = 1201;
    public const ushort PLAYER_FIELD_MOD_DAMAGE_DONE_NEG = 1208;
    public const ushort PLAYER_FIELD_MOD_DAMAGE_DONE_PCT = 1215;
    public const ushort PLAYER_FIELD_BYTES = 1222;
    public const ushort PLAYER_FIELD_BUYBACK_PRICE_1 = 1226;
    public const ushort PLAYER_FIELD_BUYBACK_TIMESTAMP_1 = 1238;
    public const ushort PLAYER_AMMO_ID = 1223;
    public const ushort PLAYER_FIELD_SESSION_KILLS = 1250;
    public const ushort PLAYER_FIELD_YESTERDAY_KILLS = 1251;
    public const ushort PLAYER_FIELD_LAST_WEEK_KILLS = 1252;
    public const ushort PLAYER_FIELD_THIS_WEEK_KILLS = 1253;
    public const ushort PLAYER_FIELD_THIS_WEEK_CONTRIBUTION = 1254;
    public const ushort PLAYER_FIELD_LIFETIME_HONORABLE_KILLS = 1255;
    public const ushort PLAYER_FIELD_LIFETIME_DISHONORABLE_KILLS = 1256;
    public const ushort PLAYER_FIELD_YESTERDAY_CONTRIBUTION = 1257;
    public const ushort PLAYER_FIELD_LAST_WEEK_CONTRIBUTION = 1258;
    public const ushort PLAYER_FIELD_LAST_WEEK_RANK = 1259;

    public const ushort PLAYER_BYTES = 193;          // skin/face/hairstyle/haircolor
    public const ushort PLAYER_BYTES_2 = 194;        // facial hair, etc.
    public const ushort PLAYER_BYTES_3 = 195;        // byte1: server-authored drunkenness (0..100)
    public const ushort PLAYER_DUEL_ARBITER = 188;   // public GUID; UNIT_END + 0
    public const ushort PLAYER_DUEL_TEAM = 196;      // public team; UNIT_END + 8 (header comments omit OBJECT_END)
    public const ushort PLAYER_FLAGS = 190;

    private readonly Dictionary<ushort, uint> _fields;
    private bool _created;

    public ObjectFields() => _fields = new Dictionary<ushort, uint>();
    private ObjectFields(Dictionary<ushort, uint> fields, bool created) { _fields = fields; _created = created; }

    /// <summary>
    /// Minimal descriptor snapshot for a DevTools-only synthetic creature. It is never merged
    /// into the live entity store or serialized onto the wire.
    /// </summary>
    public static ObjectFields ForSyntheticUnit(int displayId, float scale = 1f) =>
        new(new Dictionary<ushort, uint>
        {
            [UNIT_DISPLAYID] = unchecked((uint)displayId),
            [OBJECT_SCALE_X] = BitConverter.SingleToUInt32Bits(scale),
        }, created: true);

    public static ObjectFields Read(PacketReader r)
    {
        int blocks = r.ReadU8();
        uint[] mask = ArrayPool<uint>.Shared.Rent(Math.Max(1, blocks));
        try
        {
            int fieldCount = 0;
            for (int i = 0; i < blocks; i++)
            {
                mask[i] = r.ReadU32();
                fieldCount += BitOperations.PopCount(mask[i]);
            }

            var fields = new Dictionary<ushort, uint>(fieldCount);
            for (int i = 0; i < blocks; i++)
            {
                uint word = mask[i];
                for (int bit = 0; bit < 32; bit++)
                    if ((word & (1u << bit)) != 0)
                        fields[(ushort)(i * 32 + bit)] = r.ReadU32();
            }
            return new ObjectFields(fields, created: false);
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(mask);
        }
    }

    /// <summary>Mark this as a CREATE snapshot (absent fields then read as 0).</summary>
    public ObjectFields AsCreated() { _created = true; return this; }

    // --- SUI possession snapshot writes: client-side injection of owner-only data
    // (bags, talent points, coinage) for a possessed bot, which the wire never
    // streams to a non-owner session. See Program.Control.cs ApplySuiSnapshot. ---
    public void SetU32(ushort index, uint value) => _fields[index] = value;
    public void SetUnitStandState(byte state)
    {
        uint value = GetU32(UNIT_FIELD_BYTES_1) ?? 0;
        _fields[UNIT_FIELD_BYTES_1] = (value & 0xffff_ff00u) | state;
    }
    public void SetGuid(ushort index, ulong guid)
    {
        _fields[index] = unchecked((uint)guid);
        _fields[(ushort)(index + 1)] = (uint)(guid >> 32);
    }

    /// <summary>Overlay a VALUES delta's present fields; keeps this set's created flag.</summary>
    public void Merge(ObjectFields delta)
    {
        foreach (var kv in delta._fields) _fields[kv.Key] = kv.Value;
    }

    // --- typed reads ---
    public uint? GetU32(ushort index)
    {
        if (_fields.TryGetValue(index, out uint v)) return v;
        return _created ? 0u : (uint?)null;
    }
    public int? GetI32(ushort index) => GetU32(index) is { } v ? unchecked((int)v) : null;
    public float? GetF32(ushort index) => GetU32(index) is { } v ? BitConverter.UInt32BitsToSingle(v) : null;

    public ulong? GetGuid(ushort index)
    {
        // Raw low-half read (a guid slot is "present iff the low half is", even on a created set).
        if (!_fields.TryGetValue(index, out uint lo)) return null;
        uint hi = GetU32((ushort)(index + 1)) ?? 0;
        return (ulong)lo | ((ulong)hi << 32);
    }

    // --- named accessors ---
    public uint? Entry => GetU32(OBJECT_ENTRY);
    public ulong? PlayerFarsight =>
        GetGuid(PLAYER_FARSIGHT) is { } g && g != 0 ? g : null;
    public uint PlayerTrackCreatures => GetU32(PLAYER_TRACK_CREATURES) ?? 0;
    public uint PlayerTrackResources => GetU32(PLAYER_TRACK_RESOURCES) ?? 0;
    public bool PlayerHasExplored(uint exploreFlag)
    {
        uint word = exploreFlag / 32;
        if (word >= PLAYER_EXPLORED_ZONES_SIZE) return false;
        uint bits = GetU32((ushort)(PLAYER_EXPLORED_ZONES_1 + word)) ?? 0;
        return (bits & (1u << (int)(exploreFlag % 32))) != 0;
    }
    public uint GameObjectArtKit => GetU32(GAMEOBJECT_ARTKIT) ?? 0;
    public float Scale => GetF32(OBJECT_SCALE_X) ?? 1f;
    public int DisplayId => GetI32(UNIT_DISPLAYID) ?? 0;
    public uint GameObjectDisplayId => GetU32(GAMEOBJECT_DISPLAYID) ?? 0;
    public uint GameObjectFlags => GetU32(GAMEOBJECT_FLAGS) ?? 0;
    public uint GameObjectDynamicFlags => GetU32(GAMEOBJECT_DYN_FLAGS) ?? 0;
    public uint GameObjectFaction => GetU32(GAMEOBJECT_FACTION) ?? 0;
    public uint GameObjectType => GetU32(GAMEOBJECT_TYPE_ID) ?? 0;
    // A zero field is normally omitted from a CREATE mask. GAMEOBJECT_STATE 0
    // is ACTIVE/open, so absence must remain zero rather than being invented as
    // READY/closed (open portcullises otherwise seed in the closed state).
    public uint GameObjectState => GetU32(GAMEOBJECT_STATE) ?? LockCatalog.StateActive;
    public uint GameObjectLevel => GetU32(GAMEOBJECT_LEVEL) ?? 0;
    public ulong? GameObjectCreatedBy =>
        GetGuid(GAMEOBJECT_CREATED_BY) is { } g && g != 0 ? g : null;
    public ulong? ChannelObject =>
        GetGuid(UNIT_FIELD_CHANNEL_OBJECT) is { } g && g != 0 ? g : null;
    /// <summary>GAMEOBJECT_ROTATION as a quaternion. All-zero when the server
    /// left the fields unset — the renderer then falls back to the movement
    /// block's Orientation (see <see cref="WorldEntity.GameObjectFacing"/>).</summary>
    public Quaternion GameObjectRotation => new(
        GetF32(GAMEOBJECT_ROTATION) ?? 0f,
        GetF32((ushort)(GAMEOBJECT_ROTATION + 1)) ?? 0f,
        GetF32((ushort)(GAMEOBJECT_ROTATION + 2)) ?? 0f,
        GetF32((ushort)(GAMEOBJECT_ROTATION + 3)) ?? 0f);
    public uint MountDisplayId => GetU32(UNIT_MOUNTDISPLAYID) ?? 0;
    public uint Health => GetU32(UNIT_HEALTH) ?? 0;
    public uint MaxHealth => GetU32(UNIT_MAXHEALTH) ?? 0;
    public uint Level => GetU32(UNIT_LEVEL) ?? 0;
    public uint FactionTemplate => GetU32(UNIT_FACTIONTEMPLATE) ?? 0;
    public uint UnitFlags => GetU32(UNIT_FLAGS) ?? 0;
    public uint NpcFlags => GetU32(UNIT_NPC_FLAGS) ?? 0;
    /// <summary>Raw Emotes.dbc id of the active "state" emote (Dance, ...), or 0.
    /// Resolve through EmoteCatalog (the live Emotes.dbc), same as SMSG_EMOTE's
    /// payload - see UNIT_NPC_EMOTESTATE's doc comment for why this and not
    /// SMSG_EMOTE is what carries Dance.</summary>
    public uint NpcEmoteState => GetU32(UNIT_NPC_EMOTESTATE) ?? 0;
    public ulong? Target => GetGuid(UNIT_TARGET) is { } g && g != 0 ? g : null;
    public ulong? Charm => GetGuid(UNIT_FIELD_CHARM) is { } g && g != 0 ? g : null;
    public ulong? Summon => GetGuid(UNIT_FIELD_SUMMON) is { } g && g != 0 ? g : null;
    public ulong? CharmedBy => GetGuid(UNIT_FIELD_CHARMEDBY) is { } g && g != 0 ? g : null;
    public ulong? SummonedBy => GetGuid(UNIT_FIELD_SUMMONEDBY) is { } g && g != 0 ? g : null;
    public ulong? CreatedBy => GetGuid(UNIT_FIELD_CREATEDBY) is { } g && g != 0 ? g : null;
    public uint CreatedBySpell => GetU32(UNIT_CREATED_BY_SPELL) ?? 0;
    public uint PetNumber => GetU32(UNIT_FIELD_PETNUMBER) ?? 0;
    public bool IsPetOrCharm => PetNumber != 0;
    public uint PetExperience => GetU32(UNIT_FIELD_PETEXPERIENCE) ?? 0;
    public uint PetNextLevelExperience => GetU32(UNIT_FIELD_PETNEXTLEVELEXP) ?? 0;
    public uint PetTrainingPoints => GetU32(UNIT_FIELD_TRAINING_POINTS) ?? 0;
    public byte PetLoyaltyLevel => (byte)((GetU32(UNIT_FIELD_BYTES_1) ?? 0) >> 8);

    public byte PowerType => Bytes0.PowerType;
    public uint Power(byte powerType) => powerType <= 4 ? GetU32((ushort)(UNIT_POWER1 + powerType)) ?? 0 : 0;
    public uint MaxPower(byte powerType) => powerType <= 4 ? GetU32((ushort)(UNIT_MAXPOWER1 + powerType)) ?? 0 : 0;
    public uint ActivePower => Power(PowerType);
    public uint ActiveMaxPower => MaxPower(PowerType);
    public float PowerFraction => ActiveMaxPower > 0
        ? Math.Clamp((float)ActivePower / ActiveMaxPower, 0f, 1f)
        : 1f;
    public bool InCombat => (UnitFlags & 0x0008_0000u) != 0;
    public byte UnitStandState => (byte)(GetU32(UNIT_FIELD_BYTES_1) ?? 0);
    /// <summary>`UNIT_FIELD_BYTES_1` byte two — the active shapeshift form id.</summary>
    public float CastSpeedMultiplier => GetF32(UNIT_MOD_CAST_SPEED) ?? 1f;
    public byte ShapeshiftForm => (byte)((GetU32(UNIT_FIELD_BYTES_1) ?? 0) >> 16);
    /// <summary>UNIT_FIELD_BYTES_1 byte three bit 0x2: the archived CREEP/stealth gate.</summary>
    public bool UnitIsStealthed => (((GetU32(UNIT_FIELD_BYTES_1) ?? 0) >> 24) & 0x02u) != 0;
    public byte SheathState => (byte)(GetU32(UNIT_BYTES_2) ?? 0);

    /// <summary>UnitStandStateType from UNIT_FIELD_BYTES_1 byte 0 - confirmed via
    /// vmangos/core's Unit::SetStandState (UNIT_BYTES_1_OFFSET_STAND_STATE == 0).
    /// See <see cref="UnitStandState"/>.</summary>
    public byte StandState => (byte)(GetU32(UNIT_FIELD_BYTES_1) ?? 0);

    /// <summary>`UNIT_DYNAMIC_FLAGS` — per-viewer dynamic state. Absent counts as 0.</summary>
    public uint DynamicFlags => GetU32(UNIT_DYNAMIC_FLAGS) ?? 0;
    /// <summary>Bit 0x1 of UNIT_DYNAMIC_FLAGS — lootable BY ME (the server strips the bit
    /// per viewer before it ships). Gates the right-click loot route.</summary>
    public bool Lootable => (DynamicFlags & 0x1) != 0;
    /// <summary>
    /// The render/client-read death predicate: health death, UNIT_DYNFLAG_DEAD
    /// (Feign Death), or dead stand state. This is intentionally broader than
    /// server-health death and drives death presentation/audio.
    /// </summary>
    public bool ReadsDead => IsDead || (DynamicFlags & 0x20u) != 0 || UnitStandState == 7;
    /// <summary>Melee reach term for the edge-to-edge range gate. Vanilla default 1.5 when absent.</summary>
    public float CombatReach => GetF32(UNIT_COMBATREACH) ?? 1.5f;
    public float BoundingRadius => GetF32(UNIT_BOUNDINGRADIUS) ?? 0f;
    /// <summary>Base the percent-cost spells scale from; only present on our own descriptor.</summary>
    public uint BaseMana => GetU32(UNIT_BASE_MANA) ?? 0;
    public uint BaseHealth => GetU32(UNIT_BASE_HEALTH) ?? 0;
    public int PowerCostModifier(int school) => school is >= 0 and < 7
        ? GetI32((ushort)(UNIT_POWER_COST_MODIFIER + school)) ?? 0 : 0;
    public float PowerCostMultiplier(int school) => school is >= 0 and < 7
        ? GetF32((ushort)(UNIT_POWER_COST_MULTIPLIER + school)) ?? 0 : 0;

    public uint VirtualItemDisplay(int slot) => slot is >= 0 and < 3
        ? GetU32((ushort)(UNIT_VIRTUAL_ITEM_SLOT_DISPLAY + slot)) ?? 0 : 0;
    public (byte Class, byte Subclass, byte Material, byte InventoryType) VirtualItemInfo(int slot)
    {
        uint value = slot is >= 0 and < 3 ? GetU32((ushort)(UNIT_VIRTUAL_ITEM_INFO + slot * 2)) ?? 0 : 0;
        return ((byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24));
    }
    public byte VirtualItemSheath(int slot) => slot is >= 0 and < 3
        ? (byte)(GetU32((ushort)(UNIT_VIRTUAL_ITEM_INFO + slot * 2 + 1)) ?? 0) : (byte)0;

    public IEnumerable<(byte Slot, uint SpellId, byte Flags, byte Level, byte Stacks)> Auras()
    {
        for (byte slot = 0; slot < 48; slot++)
        {
            uint flagWord = GetU32((ushort)(UNIT_AURAFLAGS + (slot >> 3))) ?? 0;
            byte flags = (byte)((flagWord >> ((slot & 7) * 4)) & 0x0f);
            if ((flags & 0x0e) == 0) continue;
            uint spell = GetU32((ushort)(UNIT_AURA + slot)) ?? 0;
            if (spell == 0) continue;
            uint levelWord = GetU32((ushort)(UNIT_AURALEVELS + (slot >> 2))) ?? 0;
            uint stackWord = GetU32((ushort)(UNIT_AURAAPPLICATIONS + (slot >> 2))) ?? 0;
            int shift = (slot & 3) * 8;
            yield return (slot, spell, flags, (byte)(levelWord >> shift),
                (byte)Math.Min(255, ((stackWord >> shift) & 0xff) + 1));
        }
    }

    /// <summary>race, class, gender, powerType from UNIT_FIELD_BYTES_0 (players + humanoid NPCs).</summary>
    public (byte Race, byte Class, byte Gender, byte PowerType) Bytes0
    {
        get { uint v = GetU32(UNIT_BYTES_0) ?? 0; return ((byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)); }
    }

    /// <summary>skin, face, hair style and hair colour from PLAYER_BYTES.</summary>
    public (byte Skin, byte Face, byte HairStyle, byte HairColor) PlayerAppearance
    {
        get
        {
            uint value = GetU32(PLAYER_BYTES) ?? 0;
            return ((byte)value, (byte)(value >> 8), (byte)(value >> 16),
                (byte)(value >> 24));
        }
    }

    /// <summary>Facial-hair variation from PLAYER_BYTES_2 byte zero.</summary>
    public byte PlayerFacialHair => (byte)(GetU32(PLAYER_BYTES_2) ?? 0);

    /// <summary>Server-authored drunkenness from PLAYER_BYTES_3 byte one.</summary>
    public byte PlayerDrunkByte => (byte)((GetU32(PLAYER_BYTES_3) ?? 0) >> 8);

    /// <summary>Health fraction 0..1 (1 when maxhealth unknown).</summary>
    public float HealthFraction => MaxHealth > 0 ? Math.Clamp((float)Health / MaxHealth, 0f, 1f) : 1f;

    public bool IsDead => MaxHealth > 0 && Health == 0;

    public uint ItemStackCount => GetU32(ITEM_STACK_COUNT) ?? 1;
    public int? ItemSpellCharges(int block) => block is >= 0 and < 5 &&
        GetU32((ushort)(ITEM_SPELL_CHARGES + block)) is uint value
            ? unchecked((int)value) : null;
    public uint ItemFlags => GetU32(ITEM_FLAGS) ?? 0;
    public uint ItemEnchantmentId(int slot) => slot is >= 0 and < 7
        ? GetU32((ushort)(ITEM_FIELD_ENCHANTMENT + slot * 3)) ?? 0 : 0;
    public uint ItemEnchantmentDuration(int slot) => slot is >= 0 and < 7
        ? GetU32((ushort)(ITEM_FIELD_ENCHANTMENT + slot * 3 + 1)) ?? 0 : 0;
    public uint ItemEnchantmentCharges(int slot) => slot is >= 0 and < 7
        ? GetU32((ushort)(ITEM_FIELD_ENCHANTMENT + slot * 3 + 2)) ?? 0 : 0;
    public int ItemRandomProperty => unchecked((int)(GetU32(ITEM_RANDOM_PROPERTIES) ?? 0));
    public uint ItemTextId => GetU32(ITEM_TEXT_ID) ?? 0;
    public ulong ItemCreator => GetGuid(ITEM_FIELD_CREATOR) ?? 0;
    public ulong ItemGiftCreator => GetGuid(ITEM_FIELD_GIFTCREATOR) ?? 0;
    public uint ItemDurability => GetU32(ITEM_DURABILITY) ?? 0;
    public uint ItemMaxDurability => GetU32(ITEM_MAXDURABILITY) ?? 0;
    public uint ContainerNumSlots => GetU32(CONTAINER_NUM_SLOTS) ?? 0;
    public ulong ContainerSlot(int index) => index is >= 0 and < 36 ? GetGuid((ushort)(CONTAINER_SLOT_1 + index * 2)) ?? 0 : 0;
    public ulong PlayerInventorySlot(int index) => index is >= 0 and < 23 ? GetGuid((ushort)(PLAYER_INV_SLOT_HEAD + index * 2)) ?? 0 : 0;
    public uint PlayerVisibleItemEntry(int index) => index is >= 0 and < 19
        ? GetU32((ushort)(PLAYER_VISIBLE_ITEM_1_0 + index * 12)) ?? 0 : 0;
    /// <summary>
    /// Public inspected-player enchant ids. Each PLAYER_VISIBLE_ITEM block carries seven
    /// enchant fields immediately after its entry; no private item instance is consulted.
    /// </summary>
    public uint PlayerVisibleItemEnchant(int index, int enchantSlot) =>
        index is >= 0 and < 19 && enchantSlot is >= 0 and < 7
            ? GetU32((ushort)(PLAYER_VISIBLE_ITEM_1_0 + index * 12 + 1 + enchantSlot)) ?? 0
            : 0;
    public int PlayerVisibleItemRandomProperty(int index) => index is >= 0 and < 19
        ? unchecked((short)(GetU32((ushort)(PLAYER_VISIBLE_ITEM_1_0 + index * 12 + 8)) ?? 0)) : 0;
    public ulong PlayerBackpackSlot(int index) => index is >= 0 and < 16 ? GetGuid((ushort)(PLAYER_PACK_SLOT_1 + index * 2)) ?? 0 : 0;
    public ulong PlayerBankSlot(int index) => index is >= 0 and < 24 ? GetGuid((ushort)(PLAYER_BANK_SLOT_1 + index * 2)) ?? 0 : 0;
    public ulong PlayerBankBagSlot(int index) => index is >= 0 and < 6 ? GetGuid((ushort)(PLAYER_BANK_BAG_SLOT_1 + index * 2)) ?? 0 : 0;
    public ulong PlayerBuybackSlot(int index) => index is >= 0 and < 12 ? GetGuid((ushort)(PLAYER_VENDOR_BUYBACK_SLOT_1 + index * 2)) ?? 0 : 0;
    public ulong PlayerKeyringSlot(int index) => index is >= 0 and < 32 ? GetGuid((ushort)(PLAYER_KEYRING_SLOT_1 + index * 2)) ?? 0 : 0;
    public uint PlayerBuybackPrice(int index) => index is >= 0 and < 12 ? GetU32((ushort)(PLAYER_FIELD_BUYBACK_PRICE_1 + index)) ?? 0 : 0;
    public uint PlayerBuybackTimestamp(int index) => index is >= 0 and < 12 ? GetU32((ushort)(PLAYER_FIELD_BUYBACK_TIMESTAMP_1 + index)) ?? 0 : 0;
    public uint PlayerAmmoId => GetU32(PLAYER_AMMO_ID) ?? 0;
    public uint Experience => GetU32(PLAYER_XP) ?? 0;
    public uint NextLevelExperience => GetU32(PLAYER_NEXT_LEVEL_XP) ?? 0;
    public uint RestStateExperience => GetU32(PLAYER_REST_STATE_EXPERIENCE) ?? 0;
    public byte RestState => (byte)((GetU32(PLAYER_BYTES_2) ?? 0) >> 24);
    public uint TalentPoints => GetU32(PLAYER_CHARACTER_POINTS1) ?? 0;
    public uint FreeProfessions => GetU32(PLAYER_CHARACTER_POINTS2) ?? 0;
    public uint Coinage => GetU32(PLAYER_COINAGE) ?? 0;
    /// <summary>PLAYER_SELF_RES_SPELL (UNIT_END + 0x40C = 1224, PRIVATE): the soulstone / Reincarnation spell the release dialog may cast via CMSG_SELF_RES.</summary>
    public uint PlayerSelfResSpell => GetU32(PLAYER_SELF_RES_SPELL) ?? 0;
    public uint AuraState => GetU32(UNIT_AURASTATE) ?? 0;
    public byte PlayerComboPoints => (byte)((GetU32(PLAYER_FIELD_BYTES) ?? 0) >> 8);
    public ulong PlayerComboTarget => GetGuid(PLAYER_FIELD_COMBO_TARGET) ?? 0;
    public byte BankBagSlotCount => (byte)((GetU32(PLAYER_BYTES_2) ?? 0) >> 16);
    public IEnumerable<(byte Slot, uint QuestId, uint Counters, uint Timer)> QuestLog()
    {
        for (byte slot = 0; slot < 20; slot++)
        {
            ushort first = (ushort)(PLAYER_QUEST_LOG_1_1 + slot * 3);
            uint questId = GetU32(first) ?? 0;
            if (questId != 0)
                yield return (slot, questId, GetU32((ushort)(first + 1)) ?? 0, GetU32((ushort)(first + 2)) ?? 0);
        }
    }

    /// <summary>
    /// The 128 vanilla PLAYER_SKILL_INFO triples. The status-refresh watch consumes
    /// only id + current rank; maximum and temporary-bonus changes are not rank-ups.
    /// </summary>
    public IEnumerable<(byte Slot, ushort SkillId, ushort Value)> PlayerSkills()
    {
        for (byte slot = 0; slot < 128; slot++)
        {
            ushort first = (ushort)(PLAYER_SKILL_INFO_1_1 + slot * 3);
            ushort skillId = (ushort)(GetU32(first) ?? 0);
            if (skillId == 0) continue;
            ushort value = (ushort)(GetU32((ushort)(first + 1)) ?? 0);
            yield return (slot, skillId, value);
        }
    }

    /// <summary>Core GetSkillValueBase: trained rank plus permanent bonuses, excluding temporary/item bonuses.</summary>
    public uint PlayerSkillValueBase(uint skillId)
    {
        foreach (var skill in PlayerSkills())
            if (skill.SkillId == skillId)
            {
                uint bonuses = GetU32((ushort)(PLAYER_SKILL_INFO_1_1 + skill.Slot * 3 + 2)) ?? 0;
                return (uint)Math.Max(0, skill.Value + (short)(bonuses >> 16));
            }
        return 0;
    }

    public uint PlayerSkillValueWithBonuses(uint skillId)
    {
        for (byte slot = 0; slot < 128; slot++)
        {
            ushort first = (ushort)(PLAYER_SKILL_INFO_1_1 + slot * 3);
            if ((ushort)(GetU32(first) ?? 0) != skillId) continue;
            int value = (ushort)(GetU32((ushort)(first + 1)) ?? 0);
            uint bonuses = GetU32((ushort)(first + 2)) ?? 0;
            value += (short)(bonuses & 0xffff) + (short)(bonuses >> 16);
            return (uint)Math.Max(0, value);
        }
        return 0;
    }

    /// <summary>
    /// The chat-language fluency read: base + temporary bonus, plus permanent bonus only when the
    /// base is nonzero. This is the exact 0x5ec720 gate used before the 300-point fluent cutoff.
    /// </summary>
    public uint PlayerLanguageSkillValue(uint skillId)
    {
        for (byte slot = 0; slot < 128; slot++)
        {
            ushort first = (ushort)(PLAYER_SKILL_INFO_1_1 + slot * 3);
            if ((ushort)(GetU32(first) ?? 0) != skillId) continue;
            int value = (ushort)(GetU32((ushort)(first + 1)) ?? 0);
            uint bonuses = GetU32((ushort)(first + 2)) ?? 0;
            int temporary = unchecked((short)bonuses);
            int permanent = unchecked((short)(bonuses >> 16));
            if (value != 0) value += permanent;
            return (uint)Math.Max(0, value + temporary);
        }
        return 0;
    }

    public uint ChannelSpell => GetU32(UNIT_CHANNEL_SPELL) ?? 0;
    public int Stat(int index) => index is >= 0 and < 5 ? GetI32((ushort)(UNIT_STAT0 + index)) ?? 0 : 0;
    public int StatPositive(int index) => index is >= 0 and < 5 ? (int)MathF.Round(GetF32((ushort)(PLAYER_POSSTAT0 + index)) ?? 0) : 0;
    public int StatNegative(int index) => index is >= 0 and < 5 ? (int)MathF.Round(GetF32((ushort)(PLAYER_NEGSTAT0 + index)) ?? 0) : 0;
    public int Resistance(int school) => school is >= 0 and < 7 ? GetI32((ushort)(UNIT_RESISTANCES + school)) ?? 0 : 0;
    public int ResistancePositive(int school) => school is >= 0 and < 7 ? (int)MathF.Round(GetF32((ushort)(PLAYER_RESISTANCEBUFFMODSPOSITIVE + school)) ?? 0) : 0;
    public int ResistanceNegative(int school) => school is >= 0 and < 7 ? (int)MathF.Round(GetF32((ushort)(PLAYER_RESISTANCEBUFFMODSNEGATIVE + school)) ?? 0) : 0;
    public int DamageDonePositive(int school) => school is >= 0 and < 7
        ? GetI32((ushort)(PLAYER_FIELD_MOD_DAMAGE_DONE_POS + school)) ?? 0 : 0;
    public int DamageDoneNegative(int school) => school is >= 0 and < 7
        ? GetI32((ushort)(PLAYER_FIELD_MOD_DAMAGE_DONE_NEG + school)) ?? 0 : 0;
    public float DamageDonePercent(int school)
    {
        if (school is < 0 or >= 7) return 1f;
        ushort field = (ushort)(PLAYER_FIELD_MOD_DAMAGE_DONE_PCT + school);
        // A CREATE snapshot treats every absent scalar as zero, but this wire family's authored
        // identity is 1.0 until a multiplier is explicitly streamed. Inspect the merged backing
        // set so "absent" does not become a divide-by-zero pseudo-value.
        return _fields.TryGetValue(field, out uint raw) ? BitConverter.UInt32BitsToSingle(raw) : 1f;
    }
    public float MinDamage => GetF32(UNIT_MINDAMAGE) ?? 0;
    public float MaxDamage => GetF32(UNIT_MAXDAMAGE) ?? 0;
    public float MinOffhandDamage => GetF32(UNIT_MINOFFHANDDAMAGE) ?? 0;
    public float MaxOffhandDamage => GetF32(UNIT_MAXOFFHANDDAMAGE) ?? 0;
    public float MinRangedDamage => GetF32(UNIT_MINRANGEDDAMAGE) ?? 0;
    public float MaxRangedDamage => GetF32(UNIT_MAXRANGEDDAMAGE) ?? 0;
    public uint MainAttackTime => GetU32(UNIT_BASEATTACKTIME) ?? 0;
    public uint OffhandAttackTime => GetU32((ushort)(UNIT_BASEATTACKTIME + 1)) ?? 0;
    public uint RangedAttackTime => GetU32(UNIT_RANGEDATTACKTIME) ?? 0;
    public int AttackPower => (int)(Math.Max(0, AttackPowerBase + AttackPowerPositive + AttackPowerNegative)
        * (1f + (GetF32(UNIT_ATTACK_POWER_MULTIPLIER) ?? 0)));
    public int RangedAttackPower => (int)(Math.Max(0, RangedAttackPowerBase + RangedAttackPowerPositive + RangedAttackPowerNegative)
        * (1f + (GetF32(UNIT_RANGED_ATTACK_POWER_MULTIPLIER) ?? 0)));
    public int AttackPowerBase => GetI32(UNIT_ATTACK_POWER) ?? 0;
    public int AttackPowerPositive => PackedSignedLow(UNIT_ATTACK_POWER_MODS);
    public int AttackPowerNegative => PackedSignedHigh(UNIT_ATTACK_POWER_MODS);
    public int RangedAttackPowerBase => GetI32(UNIT_RANGED_ATTACK_POWER) ?? 0;
    public int RangedAttackPowerPositive => PackedSignedLow(UNIT_RANGED_ATTACK_POWER_MODS);
    public int RangedAttackPowerNegative => PackedSignedHigh(UNIT_RANGED_ATTACK_POWER_MODS);
    public (ushort Honorable, ushort Dishonorable) SessionKills => PackedKills(PLAYER_FIELD_SESSION_KILLS);
    public (ushort Honorable, ushort Dishonorable) YesterdayKills => PackedKills(PLAYER_FIELD_YESTERDAY_KILLS);
    public (ushort Honorable, ushort Dishonorable) LastWeekKills => PackedKills(PLAYER_FIELD_LAST_WEEK_KILLS);
    public (ushort Honorable, ushort Dishonorable) ThisWeekKills => PackedKills(PLAYER_FIELD_THIS_WEEK_KILLS);
    public uint ThisWeekContribution => GetU32(PLAYER_FIELD_THIS_WEEK_CONTRIBUTION) ?? 0;
    public uint LifetimeHonorableKills => GetU32(PLAYER_FIELD_LIFETIME_HONORABLE_KILLS) ?? 0;
    public uint LifetimeDishonorableKills => GetU32(PLAYER_FIELD_LIFETIME_DISHONORABLE_KILLS) ?? 0;
    public uint YesterdayContribution => GetU32(PLAYER_FIELD_YESTERDAY_CONTRIBUTION) ?? 0;
    public uint LastWeekContribution => GetU32(PLAYER_FIELD_LAST_WEEK_CONTRIBUTION) ?? 0;
    public uint LastWeekRank => GetU32(PLAYER_FIELD_LAST_WEEK_RANK) ?? 0;
    public uint PlayerGuildId => GetU32(PLAYER_GUILDID) ?? 0;
    public uint PlayerGuildRank => GetU32(PLAYER_GUILDRANK) ?? 0;
    public uint PlayerFlags => GetU32(PLAYER_FLAGS) ?? 0;
    /// <summary>PLAYER_FLAGS 0x10: the in-world ghost presentation flag.</summary>
    public bool PlayerIsGhost => (PlayerFlags & 0x10u) != 0;
    public bool PlayerShowsHelm => (PlayerFlags & 0x400u) == 0;
    public bool PlayerShowsCloak => (PlayerFlags & 0x800u) == 0;
    public byte? PlayerHighestHonorRank => GetU32(PLAYER_FIELD_BYTES) is { } bytes ? (byte)(bytes >> 24) : null;
    public uint PlayerFieldBytes => GetU32(PLAYER_FIELD_BYTES) ?? 0;
    public int WatchedFactionIndex =>
        GetI32(PLAYER_FIELD_WATCHED_FACTION_INDEX) ?? -1;

    private (ushort Honorable, ushort Dishonorable) PackedKills(ushort index)
    {
        uint value = GetU32(index) ?? 0;
        return ((ushort)value, (ushort)(value >> 16));
    }

    private int PackedSignedLow(ushort index) => unchecked((short)(GetU32(index) ?? 0));
    private int PackedSignedHigh(ushort index) => unchecked((short)((GetU32(index) ?? 0) >> 16));
}
