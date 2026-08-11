using MSUIClient.Net;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.World.Units;
using System.IO.Compression;

static byte[] Hex(string value) => Convert.FromHexString(value.Replace(" ", ""));
static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}
static void CheckUnitGrounding()
{
    Check(MathF.Abs(CreatureRenderer.UnitRenderScale(0.65f) - 0.65f) < 0.0001f,
        "unit render scale does not square the server-folded DBC scale");
    Check(MathF.Abs(CreatureRenderer.GroundShadowRadius(0.8f, 0.65f) - 0.52f) < 0.0001f &&
          CreatureRenderer.GroundShadowRadius(0.1f, 0.5f) == 0.35f &&
          CreatureRenderer.GroundShadowRadius(100f, 1f) == 12f,
        "unit shadow radius follows rendered bounds with stable small/large clamps");
}
if (args.Contains("--unit-render-only", StringComparer.OrdinalIgnoreCase))
{
    CheckUnitGrounding();
    Console.WriteLine("unit render grounding checks passed");
    return;
}
static SpellInfo TargetSpell(uint targets, uint implicitTarget) => new(
    Id: 1, Name: "", Rank: "", IconPath: "", Attributes: 0, AttributesEx2: 0,
    AttributesEx3: 0, InterruptFlags: 0, ChannelInterruptFlags: 0,
    Targets: targets, ImplicitTarget: implicitTarget, RecoveryMs: 0, CategoryRecoveryMs: 0,
    PowerType: 0, ManaCost: 0, ManaCostPercent: 0, StartRecoveryCategory: 0,
    StartRecoveryMs: 0, VisualId: 0, Speed: 0, Description: "", RangeIndex: 0);
static PortraitVerdict Portrait(double time) => new(time, PortraitSubject.Player,
    PortraitOutcome.Ready, PortraitCameraSource.Bounds, false, 1, 0, 255, 255, 255,
    1, 0, 1, 1, 1, 45, .1f);
static CastVerdict Cast(double time) => new(time, 1, CastTargetReason.PendingCast, 0, 0, false);
static ActionButtonVerdict Action(double time) => new(time, 0, false, 1,
    ButtonUsability.Usable, ButtonRange.NoCheck, false, false, false, false, false,
    false, 0, 0, 0, 0, 0, 0, -1, 0);
static AnimChoice Anim(double time) => new(time, "creature:1", 0, 0, 0, AnimChoiceKind.Exact);

var start = (CombatAttackStarted)CombatPacketParser.Parse(
    Op.SMSG_ATTACKSTART,
    Hex("01000000000000002a000045000030f1"));
Check(start.Attacker == 1 && start.Victim == 0xF130_0000_4500_002Aul, "attack-start GUID layout");

var attackErrors = new Dictionary<Op, string>
{
    [Op.SMSG_ATTACKSWING_NOTINRANGE] = "You are too far away!",
    [Op.SMSG_ATTACKSWING_BADFACING] = "You are facing the wrong way!",
    [Op.SMSG_ATTACKSWING_NOTSTANDING] = "You must be standing to attack!",
    [Op.SMSG_ATTACKSWING_DEADTARGET] = "Your target is dead!",
    [Op.SMSG_ATTACKSWING_CANT_ATTACK] = "You can't attack that target!",
};
foreach ((Op opcode, string expected) in attackErrors)
    Check(CombatAttackErrorText.ForOpcode(opcode) == expected,
        $"copyable attack-error text for {opcode}");

var spell = (CombatSpellDamage)CombatPacketParser.Parse(
    Op.SMSG_SPELLNONMELEEDAMAGELOG,
    Hex("c92a4530f1 0101 85000000 f4010000 03 32000000 ecffffff 00 00 0a000000 02000000 00"));
Check(spell.Attacker == 1 && spell.SpellId == 133 && spell.Damage == 500, "spell damage fields");
Check(spell.Absorb == 50 && spell.Resist == -20 && (spell.HitInfo & 2) != 0, "spell mitigation/crit");
var spellCue = CombatFeedbackLaw.WorldText(spell, 1);
Check(spellCue.Count == 1 && spellCue[0].Text == "500" && spellCue[0].Critical &&
      spellCue[0].Style == WorldCombatTextStyle.PlayerSpell, "owned spell world-text law");
Check(CombatFeedbackLaw.WorldText(spell, 99).Count == 0, "foreign damage world-text suppression");

var blockedSwing = new CombatMeleeSwing(1, 2, 0, 0, 5, 0, 0, 12);
var blockCue = CombatFeedbackLaw.WorldText(blockedSwing, 1);
Check(blockCue.Count == 1 && blockCue[0].Text == "Block", "victim-state word precedence");
var incoming = CombatFeedbackLaw.CenterText(new CombatMeleeSwing(2, 1, 0, 17, 1, 0, 0, 0), 1);
Check(incoming.Count == 1 && incoming[0].Text == "-17" &&
      incoming[0].Style == CenterCombatTextStyle.Damage, "incoming center damage law");
var selfHeal = CombatFeedbackLaw.CenterText(new CombatHeal(1, 1, 42, 25, true), 1);
Check(selfHeal.Count == 1 && selfHeal[0].Text == "+25" && selfHeal[0].Critical,
      "self-heal center text law");

var periodic = (CombatPeriodicAura)CombatPacketParser.Parse(
    Op.SMSG_PERIODICAURALOG,
    Hex("c92a4530f1 0101 ac000000 01000000 03000000 58000000 06000000 0c000000 fbffffff"));
Check(periodic.Ticks.Count == 1 && periodic.Ticks[0].Kind == CombatPeriodicKind.Damage,
      "periodic tick kind");
Check(periodic.Ticks[0].Amount == 88 && periodic.Ticks[0].Resist == -5, "periodic tick payload");

var movement = MovementInfo.Create(
    new System.Numerics.Vector3(1, 2, 3), 0.75f,
    MovementFlags.Forward | MovementFlags.Falling);
movement.FallTime = 321;
movement.Jump = new JumpInfo(-7.955547f, 1, 0, 7);
var writer = new PacketWriter();
movement.Write(writer);
var decoded = MovementInfo.Read(new PacketReader(writer.ToArray()));
Check(decoded.Flags == movement.Flags && decoded.FallTime == 321, "movement flags/fall time round-trip");
Check(decoded.Jump is { ZSpeed: < 0, XySpeed: 7 }, "movement jump tail round-trip");

// Build-5875 client same-map teleport acknowledgement: full guid, counter,
// monotonic client time. The first twelve bytes match benilla's golden packet;
// the live clock is asserted structurally because it is deliberately not fixed.
byte[] teleportAck = WorldSession.BuildTeleportAckBody(0x123456789ABCDEF0ul, 7);
Check(teleportAck.Length == 16, "teleport ack body size");
Check(teleportAck.AsSpan(0, 12).SequenceEqual(Hex("f0debc9a7856341207000000")),
    "teleport ack full-guid/counter layout");
Check(new PacketReader(teleportAck, 12, 4).ReadU32() != 0, "teleport ack client time");
Check(WorldSession.BuildCastSpellBody(6673, 0).SequenceEqual(Hex("111a00000000")),
      "implicit-self cast body is spell id plus zero target mask");
Check(WorldSession.BuildCastSpellBody(133, 0xF13000004500002Aul)
          .SequenceEqual(Hex("850000000200c92a4530f1")),
      "unit cast body uses TARGET_FLAG_UNIT plus packed guid");
Check((ushort)Op.CMSG_CANCEL_AURA == 0x0136 && (ushort)Op.SMSG_UPDATE_AURA_DURATION == 0x0137,
      "build-5875 aura opcode pair");
Check(WorldSession.BuildCancelAuraBody(6673).SequenceEqual(Hex("111a0000")),
      "cancel aura body is the little-endian spell id, not the slot");

var hostile = new FactionTemplateRow { Faction = 1, EnemyGroupMask = 4 };
var monster = new FactionTemplateRow { Faction = 2, GroupMask = 4 };
Check(hostile.ReactionToward(monster) == FactionReaction.Hostile, "faction enemy-group precedence");
var friendly = new FactionTemplateRow { Faction = 3, FriendGroupMask = 8 };
var ally = new FactionTemplateRow { Faction = 4, GroupMask = 8 };
Check(friendly.ReactionToward(ally) == FactionReaction.Friendly, "faction friend-group comparison");

var playerTarget = new CastTargetCandidate(1, IsSelf: true, Friendly: true, Attackable: false, Dead: false);
var wolfTarget = new CastTargetCandidate(2, IsSelf: false, Friendly: false, Attackable: true, Dead: false);
var selfSpellTarget = CastTargetLaw.Resolve(TargetSpell(0, 1), wolfTarget, playerTarget);
Check(selfSpellTarget.Kind == CastTargetKind.SelfImplicit && selfSpellTarget.Guid == 0,
      "implicit-self spell ignores hostile selection");
Check(selfSpellTarget.Reason == CastTargetReason.ImplicitSelf,
      "implicit-self reason");
var holyLightTarget = CastTargetLaw.Resolve(TargetSpell(0, 21), wolfTarget, playerTarget);
Check(holyLightTarget.Kind == CastTargetKind.Unit && holyLightTarget.Guid == 1,
      "friendly spell on hostile selection auto-targets player");
Check(holyLightTarget.Reason == CastTargetReason.SelfFallback,
      "friendly hostile-selection fallback reason");
var fireballTarget = CastTargetLaw.Resolve(TargetSpell(0, 6), wolfTarget, playerTarget);
Check(fireballTarget.Kind == CastTargetKind.Unit && fireballTarget.Guid == 2,
      "hostile spell binds attackable selection");
Check(fireballTarget.Reason == CastTargetReason.SelectedUnit,
      "hostile selected-unit reason");
Check(Enum.IsDefined(CastTargetReason.PendingCast), "pending-cast refusal reason exists");
Check(SpellCastResultNames.Name(0x23) == "SPELL_FAILED_INTERRUPTED" &&
      SpellCastResultNames.Name(0x59) == "SPELL_FAILED_OUT_OF_RANGE" &&
      SpellCastResultNames.Name(0xFE) == "SPELL_FAILED_0xFE",
      "cast-result reasons are stable strings with an exact-byte fallback");
Check(SpellCastResultNames.Name(0x2F) == "SPELL_FAILED_LINE_OF_SIGHT" &&
      SpellCastResultNames.Text(0x2F) == "Target not in line of sight" &&
      SpellCastResultNames.Text(0x4D, "RAGE") == "Not enough rage" &&
      SpellCastResultNames.Text(0x59) == "Out of range." &&
      SpellCastResultNames.Text(0x17) == "" &&
      SpellCastResultNames.Text(0xFE) == "Spell failed.",
      "cast-result reason-to-display-text law");
Check((TargetSpell(0, 1) with { CastTimeMs = 1500 }).CastClassification == "CAST_TIME" &&
      (TargetSpell(0, 1) with { ChannelInterruptFlags = 8 }).CastClassification == "CHANNEL" &&
      TargetSpell(0, 1).CastClassification == "INSTANT",
      "DBC cast classification strings");

var verdicts = new VerdictRing();
Parallel.For(0, 10_000, i =>
{
    verdicts.Add(new CastVerdict(i, (uint)i, CastTargetReason.PendingCast, 0, 0, false));
    if ((i & 7) == 0) _ = verdicts.SnapshotAll();
});
Check(verdicts.Snapshot("cast").Count == 128, "verdict ring concurrent add/snapshot remains bounded and non-null");
verdicts.Add(Portrait(0));
for (int i = 0; i < 1100; i++) verdicts.Add(Anim(i + 1));
Check(verdicts.Snapshot("portrait").Count == 1,
      "chatty anim channel cannot evict portrait history");
for (int i = 0; i < 70; i++) verdicts.Add(Portrait(2000 + i));
for (int i = 0; i < 130; i++) verdicts.Add(Cast(3000 + i));
for (int i = 0; i < 514; i++) verdicts.Add(Action(4000 + i));
verdicts.Add(new SpellSweepVerdict(5000, "Test", 1, 78, "Heroic Strike", "PHYSICAL",
    "NEXT_SWING", "PRE_SEND_PASS", "Stand", "NO_VISUAL", "UNIT", true, true,
    "RAGE", 100, 15, 2, true));
verdicts.Add(new CastBarVerdict(5001, "Test", 133, "Fireball", "CAST_START",
    "CAST_TIME", 1500, 1500, 0, "CASTING", 5, 6.5, 0, "NONE", "SpellCastOmni"));
Check(verdicts.Snapshot("portrait").Count == 64 &&
      verdicts.Snapshot("cast").Count == 128 &&
      verdicts.Snapshot("action").Count == 512 &&
      verdicts.Snapshot("anim").Count == 1024 &&
      verdicts.Snapshot("spell-sweep").Count == 1 &&
      verdicts.Snapshot("cast-bar").Count == 1,
      "per-channel verdict capacities");
IReadOnlyList<IVerdict> allVerdicts = verdicts.SnapshotAll();
Check(allVerdicts.Count == 1730 &&
      allVerdicts.Zip(allVerdicts.Skip(1)).All(pair => pair.First.Time <= pair.Second.Time),
      "merged verdict snapshot count/time order");

var camera = new Camera
{
    Target = new System.Numerics.Vector3(10, 20, 30),
    Yaw = 0.7f,
    Pitch = 0.25f,
    AspectRatio = 16f / 9f,
};
var centerRay = camera.ScreenPointToRay(new System.Numerics.Vector2(960, 540),
                                        new System.Numerics.Vector2(1920, 1080));
Check(centerRay is { } ray && System.Numerics.Vector3.Dot(ray.Direction, camera.Forward) > 0.999f,
      "screen-center ray follows camera forward");
CheckUnitGrounding();
float faced = EntityStore.TurnToward(6.20f, 0.10f, 0.08f);
Check(faced < 0.01f || faced > 6.20f, "idle facing takes the short wrapped turn");
Check(MathF.Abs(EntityStore.TurnToward(0f, MathF.PI, 0.5f) - 0.5f) < 0.0001f,
      "idle facing respects the turn-rate cap");

var updateWriter = new PacketWriter();
updateWriter.WriteU32(1);
updateWriter.WriteU8(0);
updateWriter.WriteU8((byte)UpdateKind.Values);
updateWriter.WritePackedGuid(1);
updateWriter.WriteU8(1);
updateWriter.WriteU32(1u << ObjectFields.OBJECT_ENTRY);
updateWriter.WriteU32(42);
byte[] updateBody = updateWriter.ToArray();
using var compressedStream = new MemoryStream();
using (var compressedWriter = new BinaryWriter(compressedStream, System.Text.Encoding.UTF8, leaveOpen: true))
{
    compressedWriter.Write(updateBody.Length);
    using var zlib = new ZLibStream(compressedStream, CompressionLevel.Fastest, leaveOpen: true);
    zlib.Write(updateBody);
}
var reusedUpdates = new ObjectUpdateBuffer(10_000);
ObjectUpdateBuffer parsedUpdates = UpdateObjectParser.ParseCompressed(
    compressedStream.ToArray(), reusedUpdates);
Check(ReferenceEquals(parsedUpdates, reusedUpdates) && parsedUpdates.Count == 1 &&
      parsedUpdates[0].Guid == 1 && parsedUpdates[0].Fields?.Entry == 42,
      "compressed object-update parse reuses destination and preserves fields");

var wire = new WireRing();
for (int i = 0; i < 513; i++)
{
    ushort opcode = (ushort)(0x7000 + i);
    wire.Add(new WirePacket(i, false, opcode, WireRing.NameFor(opcode), 1), [(byte)i]);
}
IReadOnlyList<WirePacket> wireSnapshot = wire.Snapshot();
Check(wireSnapshot.Count == 512 && wireSnapshot[0].Time == 1 && wireSnapshot[^1].Time == 512,
      "wire ring capacity/order");
Check(WireRing.NameFor((ushort)Op.CMSG_LOOT) == "CMSG_LOOT" &&
      WireRing.NameFor(0x7FFF) == "0x7FFF", "wire opcode name cache/fallback");

string wireTemp = Path.Combine(Path.GetTempPath(), "msui-wire-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(wireTemp);
try
{
    string relative;
    using (var recorder = new WireLogRecorder())
    {
        relative = recorder.Start(wireTemp);
        var normal = new WirePacket(1.25, true, (ushort)Op.CMSG_LOOT,
            WireRing.NameFor((ushort)Op.CMSG_LOOT), 300);
        recorder.Enqueue(normal, Enumerable.Range(0, 300).Select(i => (byte)i).ToArray());
        var auth = new WirePacket(2.5, true, (ushort)Op.CMSG_AUTH_SESSION,
            WireRing.NameFor((ushort)Op.CMSG_AUTH_SESSION), 3);
        recorder.Enqueue(auth, [1, 2, 3]);
        recorder.Stop();
    }
    string binaryPath = Path.Combine(wireTemp, relative);
    using var binary = new BinaryReader(File.OpenRead(binaryPath));
    Check(binary.ReadByte() == 1 && Math.Abs(binary.ReadDouble() - 1.25) < 0.0001 &&
          binary.ReadUInt16() == (ushort)Op.CMSG_LOOT && binary.ReadUInt32() == 300 &&
          binary.ReadUInt16() == 256, "wire binary header/payload cap");
    Check(binary.ReadBytes(256).Length == 256, "wire stored payload prefix");
    Check(binary.ReadByte() == 1 && Math.Abs(binary.ReadDouble() - 2.5) < 0.0001 &&
          binary.ReadUInt16() == (ushort)Op.CMSG_AUTH_SESSION && binary.ReadUInt32() == 3 &&
          binary.ReadUInt16() == 0, "wire auth payload suppression");
    string text = File.ReadAllText(Path.ChangeExtension(binaryPath, ".txt"));
    Check(text.Contains("CMSG_LOOT(0x015D) 300B", StringComparison.Ordinal) &&
          text.Contains("CMSG_AUTH_SESSION(0x01ED) 3B  [payload omitted]", StringComparison.Ordinal),
          "wire text companion");
}
finally
{
    Directory.Delete(wireTemp, recursive: true);
}

Console.WriteLine("combat/movement/targeting/wire foundation checks passed");
