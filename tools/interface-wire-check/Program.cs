using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Units;
using System.Numerics;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}

Check(BuffUiLaw.WarningAlpha(.75, 30) == 1f && BuffUiLaw.WarningAlpha(0, 30) == .3f &&
      BuffUiLaw.WarningAlpha(0, 31) == 1f,
    "BuffFrame shared 31-second flash drift");
Check(BuffUiLaw.DebuffColor(1) == new Vector4(.2f, .6f, 1f, 1f) &&
      BuffUiLaw.DebuffColor(4) == new Vector4(0f, .6f, 0f, 1f) &&
      BuffUiLaw.DebuffColor(0) == new Vector4(.8f, 0f, 0f, 1f),
    "DebuffTypeColor mapping drift");
Check(CastingBarUiLaw.BottomOffset(false, false, false) == 60f &&
      CastingBarUiLaw.BottomOffset(true, false, false) == 100f &&
      CastingBarUiLaw.BottomOffset(true, true, true) == 149f,
    "UIParent managed CastingBarFrame bottom stack drift");
Check(CastingBarUiLaw.FlashAlpha(1d / 12d) == .5f &&
      CastingBarUiLaw.FrameAlpha(1d / 6d, false) == 1f &&
      CastingBarUiLaw.FrameAlpha(5d / 6d, false) == 0f &&
      CastingBarUiLaw.FrameAlpha(1d, true) == 1f &&
      CastingBarUiLaw.FrameAlpha(5d / 3d, true) == 0f,
    "CastingBar 30-Hz-normalized flash/hold/fade timing drift");
Check((ushort)Op.CMSG_SET_AMMO == 0x0268 &&
      WorldSession.BuildSetAmmoBody(93012).SequenceEqual(Convert.FromHexString("546B0100")),
    "CMSG_SET_AMMO opcode/body drift");
Check(PaperDollUiLaw.ClickAction(true, false, true, false) == PaperDollUiLaw.SlotClickAction.None &&
      PaperDollUiLaw.ClickAction(true, false, true, false, true) == PaperDollUiLaw.SlotClickAction.PickupOrPlace &&
      PaperDollUiLaw.ClickAction(false, true, false, false) == PaperDollUiLaw.SlotClickAction.Use,
    "paper-doll modifier/drag/right-click routing drift");
Check(PaperDollUiLaw.FitsEquipmentSlot(11, 10) && PaperDollUiLaw.FitsEquipmentSlot(11, 11) &&
      PaperDollUiLaw.FitsEquipmentSlot(20, 4) && !PaperDollUiLaw.FitsEquipmentSlot(24, 17) &&
      PaperDollUiLaw.IsAmmo(24),
    "paper-doll inventoryType fit table/ammo fork drift");
Check(PaperDollUiLaw.IconTint(true, true) == PaperDollUiLaw.Locked &&
      PaperDollUiLaw.IconTint(false, true) == PaperDollUiLaw.Broken &&
      PaperDollUiLaw.RingTint(true, true) == PaperDollUiLaw.Fits &&
      MathF.Abs(PaperDollUiLaw.ClickFacing(0, true) + .12f) < .0001f &&
      MathF.Abs(PaperDollUiLaw.HeldFacing(0, true, 1) - MathF.PI) < .0001f,
    "paper-doll lock/broken/cursor tint or rotation law drift");
Check(PaperDollUiLaw.ModifierTextColor(1, 0) == 0xff20ff20u &&
      PaperDollUiLaw.ModifierTextColor(1, -1) == PaperDollUiLaw.Broken,
    "paper-doll positive/negative stat color drift");

var attackSpell = new SpellInfo(6603, "Attack", "", @"Interface\Icons\Temp.blp",
    0x10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, "", 0,
    EffectIds: [ActionIconLaw.SpellEffectAttack]);
Check(ActionIconLaw.Resolve(attackSpell, @"Interface\Icons\INV_Sword_04.blp", 7) ==
      @"Interface\Icons\INV_Sword_04.blp", "Attack borrows equipped main-hand icon");
Check(ActionIconLaw.Resolve(attackSpell, null, null) == ActionIconLaw.UnarmedAttackIcon,
    "unarmed Attack uses Spell-Reset rather than Temp");
var autoShot = attackSpell with { Id = 75, Name = "Auto Shot", IconPath = @"Interface\Icons\Ability_Whirlwind.blp",
    Attributes = 0x2, AttributesEx2 = 0x20, EffectIds = [0u] };
Check(ActionIconLaw.Resolve(autoShot, @"Interface\Icons\INV_Weapon_Rifle_01.blp", 3) ==
      @"Interface\Icons\INV_Weapon_Rifle_01.blp", "Auto Shot borrows equipped ranged icon");
Check(ActionIconLaw.Resolve(autoShot, @"Interface\Icons\INV_ThrowingKnife_01.blp", 16) ==
      autoShot.IconPath, "thrown weapon keeps ranged spell icon");

var northshire = MinimapProjection.FromWorld(new System.Numerics.Vector3(-8949.95f, -132.493f, 83.5312f));
Check(northshire.TileColumn == 32 && northshire.TileRow == 48 &&
      northshire.ChunkX == 3 && northshire.ChunkY == 12,
      "Northshire world position projects to Azeroth minimap/MCNK coordinates");
Check((ushort)Op.CMSG_ZONEUPDATE == 500, "CMSG_ZONEUPDATE opcode");
Check(WorldSession.BuildZoneUpdateBody(12).SequenceEqual(Convert.FromHexString("0C000000")),
      "zone update body");
string clientData = Path.Combine(ClientConfig.FindRepoRoot(), "GameData", "Data");
using var spellbookMpq = new MpqMount(clientData);
SpellCatalog spellbookSpells = SpellCatalog.Load(spellbookMpq) ??
    throw new InvalidDataException("Spell DBC unavailable");
EnchantCatalog enchantRows = EnchantCatalog.Load(spellbookMpq) ??
    throw new InvalidDataException("SpellItemEnchantment DBC unavailable");
Check(enchantRows.Name(2564) == "Agility +15" && enchantRows.Name(1900) == "Crusader",
    "SpellItemEnchantment name-column/locale drift");
Check(!Enum.TryParse("CMSG_REPLACE_ENCHANT", out Op _),
    "build 5875 must not invent a CMSG_REPLACE_ENCHANT opcode");
SkillLineCatalog spellbookSkills = SkillLineCatalog.Load(spellbookMpq) ??
    throw new InvalidDataException("Skill-line DBCs unavailable");
SpellInfo BookSpell(uint id) => spellbookSpells.TryGet(id, out SpellInfo value) ? value
    : throw new InvalidDataException($"spell {id} missing");
SpellInfo bracerEnchant = BookSpell(7418);
Check(bracerEnchant.EquippedItemClass == 4 &&
      bracerEnchant.EquippedItemInventoryTypeMask == (1u << 9),
    "Spell.dbc equipped-item enchant gates drift");
var itemTargetSpell = bracerEnchant with
{
    Targets = 0x0010,
    ImplicitTarget = 0,
};
Check(CastTargetLaw.Resolve(itemTargetSpell, null, null).Kind == CastTargetKind.Item,
    "item-only target word no longer arms the item cursor");
ulong enchantItemGuid = 0xF470000000123456ul;
var itemCastReader = new PacketReader(WorldSession.BuildCastSpellOnItemBody(
    bracerEnchant.Id, enchantItemGuid));
Check(itemCastReader.ReadU32() == bracerEnchant.Id && itemCastReader.ReadU16() == 0x0010 &&
      itemCastReader.ReadPackedGuid() == enchantItemGuid && itemCastReader.Remaining == 0,
    "CMSG_CAST_SPELL item-target body drift");

SpellInfo bindingEnchant = spellbookSpells.Spells.First(spell =>
{
    uint[] effects = spell.EffectIds ?? [];
    int[] misc = spell.EffectMiscValues ?? [];
    for (int i = 0; i < Math.Min(effects.Length, misc.Length); i++)
        if (effects[i] is 53 or 54 && misc[i] > 0 && enchantRows.BindsItem((uint)misc[i]))
            return true;
    return false;
});
uint[] bindingEffects = bindingEnchant.EffectIds ??
    throw new InvalidDataException("binding enchant effect lanes missing");
int bindingLane = Array.FindIndex(bindingEffects, effect => effect is 53 or 54);
uint bindingEffect = bindingEffects[bindingLane];
uint bindingNewId = (uint)bindingEnchant.EffectMiscValues![bindingLane];
uint matchingSubclass = bindingEnchant.EquippedItemSubclassMask == 0 ? 0u :
    (uint)System.Numerics.BitOperations.TrailingZeroCount(bindingEnchant.EquippedItemSubclassMask);
uint matchingInventoryType = bindingEnchant.EquippedItemInventoryTypeMask == 0 ? 13u :
    (uint)System.Numerics.BitOperations.TrailingZeroCount(bindingEnchant.EquippedItemInventoryTypeMask);
var bareEnchantTarget = new EnchantClickedItem(
    bindingEnchant.EquippedItemSubclassMask == 0 ? 2u : unchecked((uint)bindingEnchant.EquippedItemClass),
    matchingSubclass, matchingInventoryType, AlreadyBound: false);
Check(EnchantConfirmUiLaw.Decide(bindingEnchant, bareEnchantTarget, enchantRows, false).Kind ==
      EnchantBindKind.ConfirmBind,
    "bind-warning leg or SpellItemEnchantment Flags bit drift");
var alreadyEnchantedTarget = bindingEffect == 53
    ? bareEnchantTarget with { PermanentEnchant = bindingNewId }
    : bareEnchantTarget with { TemporaryEnchant = bindingNewId };
EnchantBindVerdict chained = EnchantConfirmUiLaw.Decide(
    bindingEnchant, alreadyEnchantedTarget, enchantRows, bindConfirmed: true);
Check(chained.Kind == EnchantBindKind.ConfirmReplace &&
      chained.ExistingEnchant == bindingNewId && chained.NewEnchant == bindingNewId,
    "bind-accept did not chain into the replacement confirmation");
Check(EnchantConfirmUiLaw.Decide(bindingEnchant,
        bareEnchantTarget with { AlreadyBound = true }, enchantRows, false).Kind ==
      EnchantBindKind.Bind,
    "already-bound item incorrectly raised the bind warning");
const byte Human = 1, Warrior = 1, Mage = 8;
Check(spellbookSkills.SpellTab(6603, Human, Mage) == 0, "Attack did not collapse into General");
Check(spellbookSkills.SpellTab(133, Human, Mage) == 8, "Fireball did not route to Fire");
Check(spellbookSkills.SpellTab(116, Human, Mage) == 6, "Frostbolt did not route to Frost");
Check(spellbookSkills.SpellTab(1459, Human, Mage) == 237, "Arcane Intellect did not route to Arcane");
Check(spellbookSkills.SpellTab(133, Human, Warrior) == 0,
    "cross-class Fireball did not collapse into General");
Check(SpellbookLaw.Eligible(BookSpell(133)), "Fireball failed the spellbook add-gate");
Check(!SpellbookLaw.Eligible(BookSpell(668)), "Common language survived the spellbook add-gate");
Check(!BookSpell(1953).MovementInterrupts,
    "Blink must remain movement-castable (Spell InterruptFlags movement bit is 0x01)");
Check(BookSpell(133).MovementInterrupts,
    "Fireball must retain ordinary movement interruption");
var iceBlockCamera = new MSUIClient.Engine.Camera { Yaw = 1.25f, OrbitYaw = 0.35f };
float iceBlockView = iceBlockCamera.ViewYaw;
iceBlockCamera.Rotate(0.8f, 0f);
iceBlockCamera.SetFacingKeepingView(1.25f);
Check(MathF.Abs(iceBlockCamera.Yaw - 1.25f) < 0.0001f &&
      MathF.Abs(iceBlockCamera.ViewYaw - iceBlockView - 0.8f) < 0.0001f,
    "Ice Block release must restore facing without moving the visible camera");
Check(SpellbookLaw.LeadingRankNumber("Rank 10") == 10 &&
      SpellbookLaw.LeadingRankNumber("Apprentice (75)") == 75, "numeric rank parser drift");
Check(SpellbookLaw.NameFontHeight == 12f && SpellbookLaw.RankFontHeight == 10f &&
      SpellbookLaw.ButtonSize == 37f && SpellbookLaw.NameWidth == 103f &&
      SpellbookLaw.NameMaxLines == 3 && SpellbookLaw.NameAnchorX == 4f &&
      SpellbookLaw.NameAnchorYWithRank == 4f && SpellbookLaw.NameAnchorYWithoutRank == 2f &&
      SpellbookLaw.RankWidth == 79f && SpellbookLaw.RankBoxHeight == 18f &&
      SpellbookLaw.RankAnchorY == 4f && SpellbookLaw.PassiveNameColor == 0xff00a3c4 &&
      SpellbookLaw.RankColor == 0xff003359,
    "SpellButtonTemplate/GameFontNormal/SubSpellFont geometry or color drift");
Check(SpellTooltipLaw.HeaderFontHeight == 14f && SpellTooltipLaw.TextFontHeight == 12f &&
      SpellTooltipLaw.Pad == 10f && SpellTooltipLaw.LineGap == 2f &&
      SpellTooltipLaw.DoubleGap == 40f && SpellTooltipLaw.WrapWidth == 260f,
    "build-5875 GameTooltip line-stack constants drift");
Check(spellbookMpq.ReadFile(SpellbookLaw.GeneralIcon + ".blp") is not null,
    $"General tab icon absent: {SpellbookLaw.GeneralIcon}.blp");
Check(spellbookMpq.ReadFile(@"Interface\Cooldown\star4.blp") is not null,
    "authored cooldown finish-flash texture absent");

static float CooldownQuadArea(CooldownVisualLaw.Quad q)
{
    static float Triangle(Vector2 a, Vector2 b, Vector2 c) =>
        MathF.Abs((b - a).X * (c - a).Y - (b - a).Y * (c - a).X) * 0.5f;
    return Triangle(q.A, q.B, q.C) + Triangle(q.A, q.C, q.D);
}
Vector2 cooldownMin = new(10f, 20f), cooldownMax = new(46f, 56f);
float previousCooldownArea = float.PositiveInfinity;
foreach (float fraction in Enumerable.Range(0, 101).Select(i => i / 100f))
{
    IReadOnlyList<CooldownVisualLaw.Quad> quads =
        CooldownVisualLaw.BuildWipe(cooldownMin, cooldownMax, fraction);
    Check(quads.Count <= 4, "cooldown wipe exceeded its four authored quadrants");
    foreach (Vector2 p in quads.SelectMany(q => new[] { q.A, q.B, q.C, q.D }))
        Check(p.X >= cooldownMin.X - 0.001f && p.X <= cooldownMax.X + 0.001f &&
              p.Y >= cooldownMin.Y - 0.001f && p.Y <= cooldownMax.Y + 0.001f,
            $"cooldown wipe escaped its icon at fraction {fraction}: {p}");
    float area = quads.Sum(CooldownQuadArea);
    Check(area <= previousCooldownArea + 0.1f,
        $"cooldown wipe reversed at fraction {fraction}");
    previousCooldownArea = area;
}
foreach ((float fraction, float covered) in new[]
         { (0f, 1f), (0.25f, 0.75f), (0.5f, 0.5f), (0.75f, 0.25f), (1f, 0f) })
    Check(MathF.Abs(CooldownVisualLaw.BuildWipe(cooldownMin, cooldownMax, fraction)
                        .Sum(CooldownQuadArea) - 36f * 36f * covered) < 0.1f,
        $"cooldown quadrant coverage drift at fraction {fraction}");
Check(MathF.Abs(CooldownVisualLaw.FlashScale(0.333f) - 1.853f) < 0.001f &&
      MathF.Abs(CooldownVisualLaw.FlashAlpha(0.4f) - 1f) < 0.001f &&
      CooldownVisualLaw.FlashAlpha(1f) == 0f,
    "cooldown finish-flash authored scale/alpha curve drift");
var cooldownPhases = new PlayerActions();
cooldownPhases.StartCooldown(133, 0, 1_000, 10.0);
Check(cooldownPhases.TryCooldownDisplay(133, 10.25, 0, out CooldownDisplay sweepPhase) &&
      MathF.Abs(sweepPhase.SweepFraction!.Value - 0.25f) < 0.001f &&
      sweepPhase.FlashProgress is null,
    "running cooldown did not expose its authored sweep phase");
Check(!cooldownPhases.IsOnCooldown(133, 11.4) &&
      cooldownPhases.TryCooldownDisplay(133, 11.4, 0, out CooldownDisplay flashPhase) &&
      flashPhase.SweepFraction is null &&
      MathF.Abs(flashPhase.FlashProgress!.Value - 0.4f) < 0.001f,
    "finished cooldown did not become ready while retaining its one-second flash");
Check(!cooldownPhases.TryCooldownDisplay(133, 12.01, 0, out _),
    "finished cooldown display survived beyond its one-second flash");
foreach (uint lineId in new uint[] { 6, 8, 237 })
{
    Check(spellbookSkills.TryGet(lineId, out SkillLineInfo line), $"mage line {lineId} missing");
    string iconPath = line.IconPath.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)
        ? line.IconPath : line.IconPath + ".blp";
    Check(!iconPath.Contains(@"Interface\Icons\Interface\Icons", StringComparison.OrdinalIgnoreCase),
        $"mage line {lineId} duplicated its icon prefix: {iconPath}");
    Check(spellbookMpq.ReadFile(iconPath) is not null, $"mage line {lineId} icon absent: {iconPath}");
}
SpellTooltipView arcaneExplosion = SpellTooltipLaw.Build(BookSpell(1449), spellbookSpells, 60);
const string ArcaneExplosionText = "Causes an explosion of arcane magic around the caster, causing 34 to 38 Arcane damage to all targets within 10 yards.";
Check(arcaneExplosion.Description == ArcaneExplosionText,
    $"Arcane Explosion tooltip drift: {arcaneExplosion.Description}; " +
    $"levels={BookSpell(1449).SpellLevel}/{BookSpell(1449).MaxLevel}/{BookSpell(1449).BaseLevel}; " +
    $"real={string.Join(',', BookSpell(1449).EffectRealPointsPerLevel ?? [])}; " +
    $"dice={string.Join(',', BookSpell(1449).EffectDicePerLevel ?? [])}");
Check(arcaneExplosion.Cost == "75 Mana", $"Arcane Explosion cost drift: {arcaneExplosion.Cost}");
Check(arcaneExplosion.CastTime == "Instant cast",
    $"Arcane Explosion cast line drift: {arcaneExplosion.CastTime}");
Check(!arcaneExplosion.Description.Contains('$'), "Arcane Explosion retained a raw tooltip token");
SpellTooltipView fireballTooltip = SpellTooltipLaw.Build(BookSpell(133), spellbookSpells);
Check(fireballTooltip.Description.Contains("14 to 22", StringComparison.Ordinal),
    $"Fireball effect bounds drift: {fireballTooltip.Description}");
Check(fireballTooltip.Range?.Contains("yd range", StringComparison.Ordinal) == true,
    "Fireball range line missing");
string[] unresolvedSpellbookTokens = spellbookSpells.Spells.Where(spell => SpellbookLaw.Eligible(spell))
    .Select(spell => (spell, resolved: SpellTooltipLaw.Substitute(spell.Description, spell, spellbookSpells)))
    .Where(pair =>
    {
        string resolved = pair.resolved;
        return resolved.Contains("$s", StringComparison.OrdinalIgnoreCase) ||
            resolved.Contains("$a", StringComparison.OrdinalIgnoreCase) ||
            resolved.Contains("$d", StringComparison.OrdinalIgnoreCase) ||
            resolved.Contains("$t", StringComparison.OrdinalIgnoreCase) ||
            resolved.Contains("$o", StringComparison.OrdinalIgnoreCase);
    })
    .Select(pair => $"{pair.spell.Id}:{pair.spell.Name} => {pair.resolved}")
    .ToArray();
Check(unresolvedSpellbookTokens.Length == 0,
    $"{unresolvedSpellbookTokens.Length} eligible descriptions retain supported raw tokens: " +
    string.Join(" | ", unresolvedSpellbookTokens));
var northshireAdt = AdtTerrainReader.ReadFromMpq(clientData, "Azeroth", 48, 32);
Check(northshire.AreaId(northshireAdt) == 9,
      "Northshire MCNK resolves live AreaTable ID 9 rather than login zone");

Check((ushort)Op.CMSG_GOSSIP_HELLO == 379, "CMSG_GOSSIP_HELLO opcode");
Check((ushort)Op.CMSG_GOSSIP_SELECT_OPTION == 380, "CMSG_GOSSIP_SELECT_OPTION opcode");
Check((ushort)Op.SMSG_GOSSIP_MESSAGE == 381, "SMSG_GOSSIP_MESSAGE opcode");
Check((ushort)Op.SMSG_GOSSIP_COMPLETE == 382, "SMSG_GOSSIP_COMPLETE opcode");
Check((ushort)Op.CMSG_NPC_TEXT_QUERY == 383, "CMSG_NPC_TEXT_QUERY opcode");
Check((ushort)Op.SMSG_NPC_TEXT_UPDATE == 384, "SMSG_NPC_TEXT_UPDATE opcode");
Check((ushort)Op.CMSG_LIST_INVENTORY == 414 && (ushort)Op.SMSG_LIST_INVENTORY == 415,
    "vendor list opcodes");
Check((ushort)Op.CMSG_SELL_ITEM == 416 && (ushort)Op.CMSG_BUY_ITEM == 418 &&
      (ushort)Op.CMSG_BUYBACK_ITEM == 656, "vendor transaction opcodes");

byte[] menuBytes = Convert.FromHexString(
    "463701FB040030F17B0000000100000000000000030042726F77736500" +
    "010000002A000000020000000A0000004120517565737400");
GossipMenu menu = GossipPackets.ParseMenu(menuBytes);
Check(menu.SourceGuid == 0xF1300004FB013746ul, "gossip menu full GUID");
Check(menu.TextId == 123 && menu.Options.Count == 1 && menu.Quests.Count == 1,
    "gossip menu header/counts");
Check(menu.Options[0] == new GossipOption(0, 3, false, "Browse"), "gossip option shape");
Check(menu.Quests[0] == new GossipQuest(42, 2, 10, "A Quest"), "gossip quest shape");

var textWriter = new PacketWriter();
textWriter.WriteU32(123);
for (int i = 0; i < 8; i++)
{
    textWriter.WriteF32(i == 0 ? 1f : 0f);
    textWriter.WriteCString(i == 0 ? "Hello $N" : "");
    textWriter.WriteCString(i == 0 ? "Hello $N" : "");
    for (int field = 0; field < 7; field++) textWriter.WriteU32(0);
}
NpcText text = GossipPackets.ParseText(textWriter.ToArray());
Check(text.TextId == 123 && text.MaleText == "Hello $N" && text.FemaleText == "Hello $N",
    "npc text first variant");

try
{
    GossipPackets.ParseMenu(Convert.FromHexString("00000000000000000000000010000000"));
    throw new InvalidDataException("gossip option bound did not reject 16 rows");
}
catch (InvalidDataException ex) when (ex.Message.Contains("exceeds 15")) { }

var vendorWriter=new PacketWriter();
vendorWriter.WriteU64(0xF1300004FB013746ul); vendorWriter.WriteU8(1);
foreach(uint value in new uint[]{1,117,1234,uint.MaxValue,25,0,5}) vendorWriter.WriteU32(value);
VendorInventory vendor=VendorPackets.ParseList(vendorWriter.ToArray());
Check(vendor.VendorGuid==0xF1300004FB013746ul&&vendor.Items.Count==1&&
      vendor.Items[0].ItemId==117&&vendor.Items[0].Price==25&&vendor.Items[0].BuyCount==5,
      "vendor list row shape");

Check((ushort)Op.CMSG_TRAINER_LIST == 432 && (ushort)Op.SMSG_TRAINER_LIST == 433 &&
      (ushort)Op.CMSG_TRAINER_BUY_SPELL == 434 && (ushort)Op.SMSG_TRAINER_BUY_SUCCEEDED == 435 &&
      (ushort)Op.SMSG_TRAINER_BUY_FAILED == 436, "trainer opcodes");
ulong trainerGuid = 0xF13000038F000001ul;
Check(WorldSession.BuildTrainerListBody(trainerGuid).SequenceEqual(Convert.FromHexString("0100008F030030F1")),
      "trainer list request full guid");
Check(WorldSession.BuildTrainerBuyBody(trainerGuid, 6673)
          .SequenceEqual(Convert.FromHexString("0100008F030030F1111A0000")),
      "trainer buy request full guid plus service spell");
var trainerWriter = new PacketWriter();
trainerWriter.WriteU64(trainerGuid); trainerWriter.WriteU32(0); trainerWriter.WriteU32(1);
trainerWriter.WriteU32(6673); trainerWriter.WriteU8(0); trainerWriter.WriteU32(100);
trainerWriter.WriteU32(0); trainerWriter.WriteU32(0); trainerWriter.WriteU8(1);
for (int i = 0; i < 5; i++) trainerWriter.WriteU32(0);
trainerWriter.WriteCString("Train");
TrainerList trainer = TrainerPackets.ParseList(trainerWriter.ToArray());
Check(trainer.TrainerGuid == trainerGuid && trainer.Spells.Count == 1 &&
      trainer.Spells[0].ServiceSpellId == 6673 && trainer.Spells[0].Cost == 100 &&
      trainer.Spells[0].RequiredLevel == 1 && trainer.Greeting == "Train", "trainer list 38-byte row shape");

Check((ushort)Op.CMSG_QUEST_QUERY == 92 && (ushort)Op.SMSG_QUEST_QUERY_RESPONSE == 93 &&
      (ushort)Op.CMSG_QUESTGIVER_STATUS_QUERY == 386 && (ushort)Op.SMSG_QUESTGIVER_STATUS == 387 &&
      (ushort)Op.CMSG_QUESTGIVER_HELLO == 388 && (ushort)Op.SMSG_QUESTGIVER_QUEST_LIST == 389 &&
      (ushort)Op.CMSG_QUESTGIVER_QUERY_QUEST == 390 && (ushort)Op.SMSG_QUESTGIVER_QUEST_DETAILS == 392 &&
      (ushort)Op.CMSG_QUESTGIVER_ACCEPT_QUEST == 393 && (ushort)Op.SMSG_QUESTGIVER_QUEST_COMPLETE == 401 &&
      (ushort)Op.SMSG_QUESTUPDATE_ADD_KILL == 409, "quest opcodes");
Check(WorldSession.BuildQuestQueryBody(0x12345678).SequenceEqual(Convert.FromHexString("78563412")),
      "quest query id body");
Check(WorldSession.BuildQuestGuidBody(trainerGuid, 7)
      .SequenceEqual(Convert.FromHexString("0100008F030030F107000000")), "quest guid plus id body");
var questDetailsWriter = new PacketWriter(); questDetailsWriter.WriteU64(trainerGuid); questDetailsWriter.WriteU32(7);
questDetailsWriter.WriteCString("A Quest"); questDetailsWriter.WriteCString("Details"); questDetailsWriter.WriteCString("Objectives");
questDetailsWriter.WriteU32(0); questDetailsWriter.WriteU32(1); questDetailsWriter.WriteU32(117);
questDetailsWriter.WriteU32(5); questDetailsWriter.WriteU32(1); questDetailsWriter.WriteU32(0);
questDetailsWriter.WriteI32(50); questDetailsWriter.WriteU32(0); questDetailsWriter.WriteU32(0);
QuestDetails questDetails = QuestPackets.ParseDetails(questDetailsWriter.ToArray());
Check(questDetails.QuestId == 7 && questDetails.Title == "A Quest" && questDetails.ChoiceRewards.Count == 1 &&
      questDetails.ChoiceRewards[0].ItemId == 117 && questDetails.Money == 50, "quest detail variable rows");
var killWriter = new PacketWriter(); killWriter.WriteU32(7); killWriter.WriteU32(6); killWriter.WriteU32(4);
killWriter.WriteU32(10); killWriter.WriteU64(trainerGuid);
Check(QuestPackets.ParseKill(killWriter.ToArray()) == new QuestKillUpdate(7, 6, 4, 10, trainerGuid),
      "quest kill objective shape");

Check((ushort)Op.CMSG_AUTOSTORE_LOOT_ITEM == 264 && (ushort)Op.CMSG_LOOT == 349 &&
      (ushort)Op.CMSG_LOOT_MONEY == 350 && (ushort)Op.CMSG_LOOT_RELEASE == 351 &&
      (ushort)Op.SMSG_LOOT_RESPONSE == 352 && (ushort)Op.SMSG_LOOT_RELEASE_RESPONSE == 353 &&
      (ushort)Op.SMSG_LOOT_REMOVED == 354 && (ushort)Op.SMSG_LOOT_CLEAR_MONEY == 357 &&
      (ushort)Op.SMSG_ITEM_PUSH_RESULT == 358, "loot opcodes");
ulong lootGuid = 0xF130000006000001ul;
Check(WorldSession.BuildLootGuidBody(lootGuid).SequenceEqual(Convert.FromHexString("01000006000030F1")),
      "loot/release full guid body");
Check(WorldSession.BuildAutostoreLootBody(3).SequenceEqual(new byte[] { 3 }), "loot slot body");
var lootWriter = new PacketWriter(); lootWriter.WriteU64(lootGuid); lootWriter.WriteU8(1);
lootWriter.WriteU32(37); lootWriter.WriteU8(1); lootWriter.WriteU8(3); lootWriter.WriteU32(117);
lootWriter.WriteU32(2); lootWriter.WriteU32(789); lootWriter.WriteU32(0); lootWriter.WriteU32(0); lootWriter.WriteU8(0);
var loot = LootPackets.ParseResponse(lootWriter.ToArray());
Check(loot.Guid == lootGuid && loot.LootType == 1 && loot.Gold == 37 && loot.Items.Count == 1 &&
      loot.Items[0] == new LootItem(3, 117, 2, 789, 0, 0), "loot response row shape");
var lootState = new LootState(); lootState.Open(lootGuid, 1, 37, loot.Items);
lootState.ClearMoney(); Check(!lootState.TakeAutoRelease(), "money clear retains item");
lootState.RemoveSlot(3); Check(lootState.TakeAutoRelease(), "last row arms auto release once");
Check(!lootState.TakeAutoRelease(), "auto release edge is one-shot");
var emptyLootWriter = new PacketWriter(); emptyLootWriter.WriteU64(lootGuid); emptyLootWriter.WriteU8(1);
emptyLootWriter.WriteU32(0); emptyLootWriter.WriteU8(0);
var emptyLoot = LootPackets.ParseResponse(emptyLootWriter.ToArray());
Check(emptyLoot.Gold == 0 && emptyLoot.Items.Count == 0, "empty corpse response shape");

Check(WorldSession.BuildAutoEquipBody(255, 24).SequenceEqual(Convert.FromHexString("FF18")),
      "autoequip bag/slot body");
Check(WorldSession.BuildSwapInventoryBody(15, 25).SequenceEqual(Convert.FromHexString("0F19")),
      "swap inventory source/destination body");
Check(WorldSession.BuildSwapItemsBody(255, 25, 19, 2).SequenceEqual(Convert.FromHexString("FF191302")),
      "swap bag destination/source body");
Check((ushort)Op.CMSG_SPLIT_ITEM == 0x010E,
      "split item opcode");
Check(WorldSession.BuildSplitItemBody(19, 2, 255, 25, 5)
      .SequenceEqual(Convert.FromHexString("1302FF1905")),
      "split item source/destination/count body");

Check((ushort)Op.CMSG_BANKER_ACTIVATE == 439 && (ushort)Op.SMSG_SHOW_BANK == 440 &&
      (ushort)Op.CMSG_BUY_BANK_SLOT == 441 && (ushort)Op.SMSG_BUY_BANK_SLOT_RESULT == 442,
      "bank opcodes");
Check(WorldSession.BuildBankGuidBody(trainerGuid).SequenceEqual(Convert.FromHexString("0100008F030030F1")),
      "bank open/purchase full guid body");
Check(WorldSession.BuildBuyItemBody(trainerGuid, 5976, 1)
      .SequenceEqual(Convert.FromHexString("0100008F030030F1581700000100")), "vendor buy body");

Check((ushort)Op.CMSG_SEND_MAIL == 568 && (ushort)Op.SMSG_SEND_MAIL_RESULT == 569 &&
      (ushort)Op.CMSG_GET_MAIL_LIST == 570 && (ushort)Op.SMSG_MAIL_LIST_RESULT == 571 &&
      (ushort)Op.CMSG_MAIL_TAKE_MONEY == 581 && (ushort)Op.CMSG_MAIL_TAKE_ITEM == 582 &&
      (ushort)Op.CMSG_MAIL_RETURN_TO_SENDER == 584 && (ushort)Op.CMSG_MAIL_DELETE == 585,
      "mail opcodes");
Check(WorldSession.BuildMailGuidBody(trainerGuid).SequenceEqual(Convert.FromHexString("0100008F030030F1")),
      "mail list full guid body");
Check(WorldSession.BuildMailActionBody(trainerGuid, 0x78563412)
      .SequenceEqual(Convert.FromHexString("0100008F030030F112345678")), "mail action guid/id body");
Check(WorldSession.BuildMailCreateTextItemBody(trainerGuid, 0x78563412)
      .SequenceEqual(Convert.FromHexString("0100008F030030F11234567800000000")),
      "mail permanent-copy guid/id/template body");
Check(WorldSession.BuildItemTextQueryBody(0x11223344, 0x78563412)
      .SequenceEqual(Convert.FromHexString("443322111234567800000000")),
      "mail item-text query body");
byte[] sendMail = WorldSession.BuildSendMailBody(trainerGuid, "Test", "Subject", "Body", 9, 100, 200);
var mailReader = new PacketReader(sendMail);
Check(mailReader.ReadU64() == trainerGuid && mailReader.ReadCString() == "Test" &&
      mailReader.ReadCString() == "Subject" && mailReader.ReadCString() == "Body" &&
      mailReader.ReadU32() == 41 && mailReader.ReadU32() == 0 && mailReader.ReadU64() == 9 &&
      mailReader.ReadU32() == 100 && mailReader.ReadU32() == 200 && mailReader.ReadU64() == 0 &&
      mailReader.ReadU8() == 0 && mailReader.Remaining == 0, "send mail body order and constants");
Check(MailUiLaw.PageCount(0) == 1 && MailUiLaw.PageCount(7) == 1 &&
      MailUiLaw.PageCount(8) == 2 && MailUiLaw.FirstIndex(2, 8) == 7,
      "mail seven-row paging law");
Check(MailUiLaw.ExpiryText(1.9f) == "1 Day" && MailUiLaw.ExpiryText(2.1f) == "2 Days" &&
      MailUiLaw.ExpiryText(.9f) == "< 1 day", "mail expiry display law");
Check(!MailUiLaw.CanDelete(0, 0, hasItem: true, money: 0) &&
      !MailUiLaw.CanDelete(0, 0, hasItem: false, money: 1) &&
      MailUiLaw.CanDelete(0, 0, hasItem: false, money: 0) &&
      MailUiLaw.CanDelete(0, MailUiLaw.CheckedReturned, hasItem: true, money: 1),
      "mail delete-versus-return law");
Check(MailUiLaw.CanSend("Jaina", "Hi", codMode: false, amount: 1,
          hasAttachment: false, pending: false) &&
      !MailUiLaw.CanSend("Jaina", "", codMode: false, amount: 0,
          hasAttachment: false, pending: false) &&
      !MailUiLaw.CanSend("Jaina", "Hi", codMode: true, amount: 1,
          hasAttachment: false, pending: false) &&
      !MailUiLaw.CanSend("Jaina", "Hi", codMode: true, amount: MailUiLaw.MaxCodCopper + 1,
          hasAttachment: true, pending: false), "mail compose enablement law");
Check(MailUiLaw.HasNewMail(0) && !MailUiLaw.HasNewMail(-86400) &&
      !MailUiLaw.HasNewMail(5), "mail pending countdown law");
Check(MultiActionBarUiLaw.WireSlot(BottomMultiActionBar.Left, 0) == 60 &&
      MultiActionBarUiLaw.WireSlot(BottomMultiActionBar.Left, 11) == 71 &&
      MultiActionBarUiLaw.WireSlot(BottomMultiActionBar.Right, 0) == 48 &&
      MultiActionBarUiLaw.WireSlot(BottomMultiActionBar.Right, 11) == 59,
      "bottom multibar page/base mapping law");
Check(!MultiActionBarUiLaw.ShowEmptyWell(false) &&
      MultiActionBarUiLaw.ShowEmptyWell(true) &&
      MultiActionBarUiLaw.UseOnKeyRelease(true, false, typing: false, inWorld: true) &&
      !MultiActionBarUiLaw.UseOnKeyRelease(false, true, typing: false, inWorld: true) &&
      !MultiActionBarUiLaw.UseOnKeyRelease(true, false, typing: true, inWorld: true),
      "bottom multibar empty-grid/key-release law");
Check(MultiActionBarUiLaw.FrameWidth == 500 && MultiActionBarUiLaw.FrameHeight == 38 &&
      MultiActionBarUiLaw.ButtonSize == 36 && MultiActionBarUiLaw.ButtonStep == 42 &&
      MultiActionBarUiLaw.BottomLeftRise == 17 && MultiActionBarUiLaw.BottomBarGap == 10,
      "bottom multibar quoted geometry law");
Check(PetActionBarUiLaw.FrameWidth == 509 && PetActionBarUiLaw.FrameHeight == 43 &&
      PetActionBarUiLaw.ButtonX(0) == 36 && PetActionBarUiLaw.ButtonX(5) == 226 &&
      PetActionBarUiLaw.ButtonX(6) == 263 && PetActionBarUiLaw.ButtonX(9) == 377,
      "pet action bar quoted geometry law");
uint petSpell = 123u | (1u << 24) | PetActionBarUiLaw.AutocastAllowed;
uint petAttack = 2u | (7u << 24);
Check(PetActionBarUiLaw.Action(petSpell) == 123 && PetActionBarUiLaw.Kind(petSpell) == 1 &&
      PetActionBarUiLaw.Autocastable(petSpell) &&
      PetActionBarUiLaw.Active(petAttack, 0, attacking: true) &&
      PetActionBarUiLaw.LatchPress(2, 1u | (7u << 24)) == 0x102,
      "pet action packed-word/local-state law");
uint[] petSlots = [7u << 24, petSpell, 0, 0, 0, 0, 0, 0, 0, 0];
Check(PetActionBarUiLaw.TryAssign(petSlots, 2, petSpell, passive: false, out var petAssign) &&
      petAssign.RelocationSlot == 1 && petSlots[1] == 0 && petSlots[2] == petSpell,
      "pet action duplicate relocation law");
Check((ushort)Op.CMSG_PET_SET_ACTION == 0x174 && (ushort)Op.CMSG_PET_ACTION == 0x175 &&
      (ushort)Op.SMSG_PET_SPELLS == 0x179 && (ushort)Op.SMSG_PET_MODE == 0x17A &&
      (ushort)Op.CMSG_PET_STOP_ATTACK == 0x2EA,
      "pet action protocol opcodes");
Check(QuestFrameUiLaw.Width == 384 && QuestFrameUiLaw.Height == 512 &&
      QuestFrameUiLaw.ScrollX == 23 && QuestFrameUiLaw.ScrollY == 81 &&
      QuestFrameUiLaw.ScrollWidth == 300 && QuestFrameUiLaw.ScrollHeight == 334 &&
      QuestFrameUiLaw.CloseMin == new Vector2(326, 15),
      "quest giver outer/scroll geometry law");
Check(QuestFrameUiLaw.ItemGridOffset(0) == Vector2.Zero &&
      QuestFrameUiLaw.ItemGridOffset(1) == new Vector2(148, 0) &&
      QuestFrameUiLaw.ItemGridOffset(2) == new Vector2(0, 43) &&
      QuestFrameUiLaw.ClampScroll(500, 700) == 366 &&
      !QuestFrameUiLaw.RewardCompleteEnabled(2, -1) &&
      QuestFrameUiLaw.RewardCompleteEnabled(2, 1) &&
      QuestFrameUiLaw.RewardCompleteEnabled(0, -1),
      "quest giver item grid/scroll/reward selection law");

Check((ushort)Op.MSG_AUCTION_HELLO == 597 && (ushort)Op.CMSG_AUCTION_SELL_ITEM == 598 &&
      (ushort)Op.CMSG_AUCTION_REMOVE_ITEM == 599 && (ushort)Op.CMSG_AUCTION_LIST_ITEMS == 600 &&
      (ushort)Op.CMSG_AUCTION_LIST_OWNER_ITEMS == 601 && (ushort)Op.CMSG_AUCTION_PLACE_BID == 602 &&
      (ushort)Op.SMSG_AUCTION_COMMAND_RESULT == 603 && (ushort)Op.SMSG_AUCTION_LIST_RESULT == 604 &&
      (ushort)Op.SMSG_AUCTION_BIDDER_NOTIFICATION == 606 &&
      (ushort)Op.SMSG_AUCTION_OWNER_NOTIFICATION == 607 &&
      (ushort)Op.SMSG_AUCTION_REMOVED_NOTIFICATION == 653,
      "auction opcodes");
Check(WorldSession.BuildAuctionGuidBody(trainerGuid).SequenceEqual(Convert.FromHexString("0100008F030030F1")),
      "auction hello full guid body");
Check(WorldSession.BuildAuctionBidBody(trainerGuid, 7, 123)
      .SequenceEqual(Convert.FromHexString("0100008F030030F1070000007B000000")), "auction bid body");
Check(WorldSession.BuildAuctionSellBody(trainerGuid, 9, 100, 200, 720).Length == 28,
      "auction sell fixed body");
var browseReader = new PacketReader(WorldSession.BuildAuctionBrowseBody(trainerGuid, 50, "Sword"));
Check(browseReader.ReadU64() == trainerGuid && browseReader.ReadU32() == 50 && browseReader.ReadCString() == "Sword" &&
      browseReader.ReadU8() == 0 && browseReader.ReadU8() == 0 && browseReader.ReadU32() == uint.MaxValue,
      "auction browse page/search/filter order");
Check(GameLoop.ProfessionSkillColor(1, 25, 70) == "orange" &&
      GameLoop.ProfessionSkillColor(30, 25, 70) == "yellow" &&
      GameLoop.ProfessionSkillColor(60, 25, 70) == "green" &&
      GameLoop.ProfessionSkillColor(70, 25, 70) == "gray", "profession skill-up range colors");
Check((ushort)Op.CMSG_GUILD_ROSTER == 137 && (ushort)Op.SMSG_GUILD_ROSTER == 138 &&
      (ushort)Op.CMSG_GUILD_PROMOTE == 139 && (ushort)Op.CMSG_GUILD_DEMOTE == 140 &&
      (ushort)Op.CMSG_GUILD_LEAVE == 141 && (ushort)Op.CMSG_GUILD_DISBAND == 143 &&
      (ushort)Op.CMSG_GUILD_MOTD == 145 && (ushort)Op.SMSG_GUILD_EVENT == 146 &&
      (ushort)Op.SMSG_GUILD_COMMAND_RESULT == 147, "guild opcodes");
Check(WorldSession.BuildCStringBody("Night").SequenceEqual(Convert.FromHexString("4E6967687400")), "guild CString bodies");

Check((ushort)Op.MSG_SAVE_GUILD_EMBLEM == 497 && (ushort)Op.SMSG_TABARDVENDOR_ACTIVATE == 498,
      "tabard opcodes");
Check(WorldSession.BuildSaveGuildEmblemBody(trainerGuid, 7, 3, 2, 5, 11)
      .SequenceEqual(Convert.FromHexString("0100008F030030F1070000000300000002000000050000000B000000")),
      "tabard save vendor guid plus five u32 fields");
var tabardEquipment = new MSUIClient.World.Units.CharacterEquipment
{
    GuildEmblem = new(7, 3, 2, 5, 11),
};
tabardEquipment.Add("Guild Tabard", 0, MSUIClient.World.Units.CharacterEquipment.Slot.Tabard);
var tabardPaths = new List<string>();
tabardEquipment.Composite(new byte[256 * 256 * 4], 256, 256, path =>
{
    tabardPaths.Add(path); return (new byte[128 * 64 * 4], 128, 64);
});
Check(tabardPaths.SequenceEqual(new[]
{
    @"Textures\GuildEmblems\Background_11_TU_U.blp",
    @"Textures\GuildEmblems\Border_02_05_TU_U.blp",
    @"Textures\GuildEmblems\Emblem_07_03_TU_U.blp",
    @"Textures\GuildEmblems\Background_11_TL_U.blp",
    @"Textures\GuildEmblems\Border_02_05_TL_U.blp",
    @"Textures\GuildEmblems\Emblem_07_03_TL_U.blp",
}), "tabard renderer binds exact six MPQ layers");

Check((ushort)Op.CMSG_UNLEARN_TALENTS == 531 && (ushort)Op.CMSG_LEARN_TALENT == 593 &&
      (ushort)Op.MSG_TALENT_WIPE_CONFIRM == 682, "talent opcodes");
Check((ushort)Op.SMSG_REMOVED_SPELL == 515, "talent reset spell-removal opcode");
Check(WorldSession.BuildLearnTalentBody(124, 0).SequenceEqual(Convert.FromHexString("7C00000000000000")),
      "learn talent id/requested-rank body");
Check(WorldSession.BuildTalentWipeBody(trainerGuid).SequenceEqual(Convert.FromHexString("0100008F030030F1")),
      "talent wipe full trainer guid body");

Check((ushort)Op.CMSG_GAMEOBJ_USE == 177 && (ushort)Op.SMSG_GAMEOBJECT_CUSTOM_ANIM == 179 &&
      (ushort)Op.CMSG_PAGE_TEXT_QUERY == 90 && (ushort)Op.SMSG_PAGE_TEXT_QUERY_RESPONSE == 91,
      "game object/page opcodes");
Check((ushort)Op.CMSG_GAMEOBJECT_QUERY == 94 && (ushort)Op.SMSG_GAMEOBJECT_QUERY_RESPONSE == 95,
      "game object template query opcodes");
ulong objectGuid = 0xF110000003000001ul;
Check(WorldSession.BuildGameObjectQueryBody(1731, objectGuid)
      .SequenceEqual(Convert.FromHexString("C306000001000003000010F1")),
      "game object query entry/full-guid body");
Check(WorldSession.BuildGameObjectUseBody(objectGuid).SequenceEqual(Convert.FromHexString("01000003000010F1")),
      "game object use full guid body");
Check(WorldSession.BuildPageTextQueryBody(77).SequenceEqual(Convert.FromHexString("4D000000")),
      "page text query body");
using (var professionMpq = new MpqMount(clientData))
{
    LockCatalog locks = LockCatalog.Load(professionMpq) ?? throw new InvalidDataException("Lock.dbc missing");
    Check(locks.ResourceLockType(29) == 2 && locks.ResourceLockType(38) == 3,
        "Silverleaf and Copper Lock.dbc skill-slot mapping");
    Check(locks.MatchesResourceMask(29, 0x2) && !locks.MatchesResourceMask(29, 0x4) &&
          locks.MatchesResourceMask(38, 0x4) && !locks.MatchesResourceMask(38, 0x2),
        "resource masks discriminate herb/mineral nodes");
    SpellFocusCatalog foci = SpellFocusCatalog.Load(professionMpq) ??
        throw new InvalidDataException("SpellFocusObject.dbc missing");
    Check(foci.Name(1) == "Anvil" && foci.Name(3) == "Forge" &&
          foci.Name(4) == "Cooking Fire" && foci.Name(543) == "Black Forge",
        "crafting focus names");
}
Check((ushort)Op.SMSG_LEVELUP_INFO == 468, "level-up info opcode");
Check(ObjectFields.PLAYER_REST_STATE_EXPERIENCE == 1175 && ObjectFields.PLAYER_XP == 716,
      "rest/experience build-5875 field indices");
Check(ObjectFields.PLAYER_VISIBLE_ITEM_1_0 == 260,
      "build-5875 public visible-item entry must be creator+2, not the pre-block offset");

var playerFieldsWriter = new PacketWriter();
var playerFieldValues = new SortedDictionary<ushort, uint>
{
    [ObjectFields.UNIT_BYTES_0] = 1u | (8u << 8), // Human Mage male
    [ObjectFields.PLAYER_BYTES] = 2u | (3u << 8) | (4u << 16) | (5u << 24),
    [ObjectFields.PLAYER_BYTES_2] = 6u,
    [ObjectFields.PLAYER_VISIBLE_ITEM_1_0] = 1000u,
    [(ushort)(ObjectFields.PLAYER_VISIBLE_ITEM_1_0 + 14 * 12)] = 1014u,
    [(ushort)(ObjectFields.PLAYER_VISIBLE_ITEM_1_0 + 18 * 12)] = 1018u,
};
const int playerFieldBlocks = 15;
playerFieldsWriter.WriteU8(playerFieldBlocks);
for (int block = 0; block < playerFieldBlocks; block++)
{
    uint mask = 0;
    foreach (ushort field in playerFieldValues.Keys)
        if (field / 32 == block) mask |= 1u << (field & 31);
    playerFieldsWriter.WriteU32(mask);
}
foreach (uint value in playerFieldValues.Values) playerFieldsWriter.WriteU32(value);
ObjectFields streamedFields = ObjectFields.Read(
    new PacketReader(playerFieldsWriter.ToArray())).AsCreated();
Check(streamedFields.Bytes0 == (1, 8, 0, 0) &&
      streamedFields.PlayerAppearance == (2, 3, 4, 5) &&
      streamedFields.PlayerFacialHair == 6,
    "streamed player appearance-byte decode drift");
Check(streamedFields.PlayerVisibleItemEntry(0) == 1000 &&
      streamedFields.PlayerVisibleItemEntry(14) == 1014 &&
      streamedFields.PlayerVisibleItemEntry(18) == 1018,
    "streamed player visible-item stride drift");
var streamedPlayer = new WorldEntity
{
    Guid = 0x1234,
    Type = ObjectTypeId.Player,
    Fields = streamedFields,
};
var basePlayerModel = new CreatureModelInfo(@"Character\Human\Male\HumanMale.m2", 1f,
    [], false, 0, 0, 0, 0, 0, 0, 0, 0, [], "");
Check(CreatureRenderer.TryBuildPlayerModelInfo(streamedPlayer, basePlayerModel,
        entry => (true, new ItemTemplate { Entry = entry, DisplayInfoId = entry + 10_000 }),
        out CreatureModelInfo playerModel) &&
      playerModel.HasExtended && playerModel.IsPlayerAppearance &&
      playerModel.ExtRace == 1 && playerModel.ExtSex == 0 &&
      playerModel.ExtSkin == 2 && playerModel.ExtFace == 3 &&
      playerModel.ExtHairStyle == 4 && playerModel.ExtHairColor == 5 &&
      playerModel.ExtFacialHair == 6 && playerModel.ExtEquipment.Length == 11 &&
      playerModel.ExtEquipment[0] == 11_000 && playerModel.ExtEquipment[9] == 11_018 &&
      playerModel.ExtEquipment[10] == 11_014,
    "remote-player render adapter lost customization or equipment-slot mapping");
Check(!CreatureRenderer.TryBuildPlayerModelInfo(streamedPlayer, basePlayerModel,
        _ => (false, null), out _),
    "remote-player adapter did not wait for public item-template settlement");
Check((ushort)Op.SMSG_FORCE_MOVE_ROOT == 0x00E8 &&
      (ushort)Op.CMSG_FORCE_MOVE_ROOT_ACK == 0x00E9 &&
      (ushort)Op.SMSG_FORCE_MOVE_UNROOT == 0x00EA &&
      (ushort)Op.CMSG_FORCE_MOVE_UNROOT_ACK == 0x00EB &&
      (uint)MovementFlags.Root == 0x00001000,
    "build-5875 force-root opcode/flag identities drift");
var rootInfo = new MovementInfo
{
    Flags = (uint)MovementFlags.Root,
    Timestamp = 0x01020304,
    Position = new System.Numerics.Vector3(1.25f, -2.5f, 3.75f),
    Orientation = 1.5f,
    FallTime = 0,
};
var rootAckReader = new PacketReader(WorldSession.BuildMoveRootAckBody(
    0x1122334455667788ul, 0xAABBCCDD, rootInfo));
Check(rootAckReader.ReadU64() == 0x1122334455667788ul &&
      rootAckReader.ReadU32() == 0xAABBCCDD &&
      MovementInfo.Read(rootAckReader) is { } decodedRoot &&
      decodedRoot.Flags == (uint)MovementFlags.Root &&
      decodedRoot.Timestamp == 0x01020304 &&
      decodedRoot.Position == rootInfo.Position &&
      decodedRoot.Orientation == rootInfo.Orientation && rootAckReader.Remaining == 0,
    "force-root acknowledgement body lost guid/counter/rooted MovementInfo");
Check((ushort)Op.CMSG_REPOP_REQUEST == 346 && (ushort)Op.SMSG_RESURRECT_REQUEST == 347 &&
      (ushort)Op.CMSG_RESURRECT_RESPONSE == 348 && (ushort)Op.CMSG_RECLAIM_CORPSE == 466 &&
      (ushort)Op.CMSG_SPIRIT_HEALER_ACTIVATE == 540, "death/resurrection opcodes");
Check(WorldSession.BuildResurrectResponseBody(0x1234, true).SequenceEqual(Convert.FromHexString("341200000000000001")),
      "resurrection response guid/accept body");
Check((ushort)Op.CMSG_BINDER_ACTIVATE == 437 && (ushort)Op.SMSG_BINDER_CONFIRM == 438 &&
      (ushort)Op.SMSG_BINDPOINTUPDATE == 341, "binder/bind-point opcodes");
Check(WorldSession.BuildBinderBody(trainerGuid).SequenceEqual(Convert.FromHexString("0100008F030030F1")),
      "binder full guid body");
Check((ushort)Op.CMSG_TAXINODE_STATUS_QUERY == 0x01AA && (ushort)Op.SMSG_SHOWTAXINODES == 0x01AD &&
      (ushort)Op.CMSG_ACTIVATETAXI == 0x01AE, "taxi opcodes");
Check(WorldSession.BuildActivateTaxiBody(0x0102030405060708, 12, 34).SequenceEqual(new byte[]
      { 8,7,6,5,4,3,2,1, 12,0,0,0, 34,0,0,0 }), "taxi activate body");

Check((ushort)Op.CMSG_INITIATE_TRADE == 278 && (ushort)Op.CMSG_BEGIN_TRADE == 279 &&
      (ushort)Op.CMSG_ACCEPT_TRADE == 282 && (ushort)Op.CMSG_SET_TRADE_ITEM == 285 &&
      (ushort)Op.CMSG_SET_TRADE_GOLD == 287 && (ushort)Op.SMSG_TRADE_STATUS == 288 &&
      (ushort)Op.SMSG_TRADE_STATUS_EXTENDED == 289, "trade opcodes");
Check(WorldSession.BuildAcceptTradeBody().SequenceEqual(new byte[] { 1, 0, 0, 0 }),
      "trade accept session marker");
Check(WorldSession.BuildSetTradeItemBody(2, 255, 25).SequenceEqual(new byte[] { 2, 255, 25 }),
      "trade item slot/bag/slot body");
Check(WorldSession.BuildSetTradeGoldBody(0x12345678).SequenceEqual(Convert.FromHexString("78563412")),
      "trade money body");

// GameMenuFrame/logout: both buttons use the same empty request packet; the local quitting bit
// only selects narration and whether completion returns to the roster or exits the process.
Check((ushort)Op.CMSG_LOGOUT_REQUEST == 0x004B &&
      (ushort)Op.SMSG_LOGOUT_RESPONSE == 0x004C &&
      (ushort)Op.SMSG_LOGOUT_COMPLETE == 0x004D &&
      (ushort)Op.CMSG_LOGOUT_CANCEL == 0x004E &&
      (ushort)Op.SMSG_LOGOUT_CANCEL_ACK == 0x004F,
    "build-5875 logout opcode identities drift");
Check(LogoutResponse.Parse(Convert.FromHexString("0000000000")) == new LogoutResponse(0, false) &&
      LogoutResponse.Parse(Convert.FromHexString("0300000001")) == new LogoutResponse(3, true),
    "SMSG_LOGOUT_RESPONSE u32 reason/u8 instant wire shape drift");
Check(LogoutUiLaw.Decide(new LogoutResponse(1, false), quitting: true) == LogoutResponseAction.Refused &&
      LogoutUiLaw.Decide(new LogoutResponse(0, true), quitting: false) == LogoutResponseAction.AwaitCompletion &&
      LogoutUiLaw.Decide(new LogoutResponse(0, false), quitting: false) == LogoutResponseAction.ShowCampCountdown &&
      LogoutUiLaw.Decide(new LogoutResponse(0, false), quitting: true) == LogoutResponseAction.ShowQuitCountdown,
    "logout response decision table drift");
Check(LogoutUiLaw.CountdownText(false, 20f) == "20 seconds until logout" &&
      LogoutUiLaw.CountdownText(true, .1f) == "1 second until exit",
    "CAMP/QUIT countdown text drift");

Check(InspectUiLaw.CanInspect(isPlayer: true, isSelf: false, attackable: false,
          distanceSquared: 100f) &&
      !InspectUiLaw.CanInspect(true, false, false, 100.001f) &&
      !InspectUiLaw.CanInspect(false, false, false, 0f) &&
      !InspectUiLaw.CanInspect(true, true, false, 0f) &&
      !InspectUiLaw.CanInspect(true, false, true, 0f),
    "inspect player/self/attackable/10-yard gate drift");
Check(MathF.Abs(InspectUiLaw.ClickFacing(.61f, left: true) - .58f) < .0001f &&
      MathF.Abs(InspectUiLaw.ClickFacing(.61f, left: false) - .64f) < .0001f &&
      MathF.Abs(InspectUiLaw.HeldFacing(0f, left: true, .5f) - MathF.PI * .5f) < .0001f &&
      MathF.Abs(InspectUiLaw.HeldFacing(0f, left: false, .5f) - MathF.PI * 1.5f) < .0001f,
    "inspect tap/held facing law drift");

Check(PartyFrameUiLaw.MemberY(0) == 128f && PartyFrameUiLaw.MemberY(1) == 191f &&
      PartyFrameUiLaw.MemberY(3) == 317f && PartyFrameUiLaw.FrameWidth == 128f &&
      PartyFrameUiLaw.FrameHeight == 53f,
    "party member frame origin/63-pixel petless cascade drift");
Check(MathF.Abs(PartyFrameUiLaw.LowHealthAlpha(0f) - 1f) < .0001f &&
      MathF.Abs(PartyFrameUiLaw.LowHealthAlpha(.5f) - 127f / 255f) < .0001f &&
      MathF.Abs(PartyFrameUiLaw.LowHealthAlpha(1f) - 1f) < .0001f,
    "party portrait low-health triangle drift");
Check(PartyFrameUiLaw.InviteWires(PartyInviteDismissal.Accept) == new PartyInviteWireCount(1, 0) &&
      PartyFrameUiLaw.InviteWires(PartyInviteDismissal.DeclineButton) == new PartyInviteWireCount(0, 2) &&
      PartyFrameUiLaw.InviteWires(PartyInviteDismissal.EscapeOrTimeout) == new PartyInviteWireCount(0, 1) &&
      PartyFrameUiLaw.InviteWires(PartyInviteDismissal.ServerCancel) == new PartyInviteWireCount(0, 0),
    "PARTY_INVITE accept/decline/OnHide wire law drift");
Check((ushort)Op.SMSG_GROUP_INVITE == 0x006f && (ushort)Op.CMSG_GROUP_ACCEPT == 0x0072 &&
      (ushort)Op.CMSG_GROUP_DECLINE == 0x0073 && (ushort)Op.SMSG_GROUP_LIST == 0x007d &&
      (ushort)Op.SMSG_PARTY_MEMBER_STATS == 0x007e &&
      (ushort)Op.CMSG_REQUEST_PARTY_MEMBER_STATS == 0x027f &&
      (ushort)Op.SMSG_PARTY_MEMBER_STATS_FULL == 0x02f2,
    "build-5875 party invite/roster/stats opcodes drift");

Check((ushort)Op.CMSG_FRIEND_LIST == 102 && (ushort)Op.SMSG_FRIEND_LIST == 103 &&
      (ushort)Op.SMSG_FRIEND_STATUS == 104 && (ushort)Op.CMSG_ADD_FRIEND == 105 &&
      (ushort)Op.CMSG_DEL_FRIEND == 106, "social opcodes");

Check((ushort)Op.CMSG_GMTICKET_CREATE == 517 && (ushort)Op.SMSG_GMTICKET_CREATE == 518 &&
      (ushort)Op.CMSG_GMTICKET_UPDATETEXT == 519 && (ushort)Op.CMSG_GMTICKET_GETTICKET == 529 &&
      (ushort)Op.CMSG_GMTICKET_DELETETICKET == 535 && (ushort)Op.CMSG_GMTICKET_SYSTEMSTATUS == 538,
      "help ticket opcodes");

// ---- gameplay text migration fence (docs/current/ui/UI_TEXT_PARITY_PLAYBOOK.md) ------------
// Gameplay panels must draw text through GameText/FontObjectLaw (the derived 1.12 text law),
// never raw AddText(ImGui.GetFont(), ...) - that path scales the supersampled atlas and
// reintroduces the unit mismatch and softness the law removed. This is a RATCHET: the baseline
// below is each file's remaining raw-draw count. New raw draws fail the check; migrating a
// panel to zero (or fewer) is allowed and should be followed by lowering its baseline here.
// FontObjectLaw's registry heights are asserted against the shipped Fonts.xml transcription.
Check(FontObjectLaw.Get("GameFontNormal") ==
          new FontObjectSpec(FontFace.FrizQt, 12f, 0xff00d1ff, 0xff000000, 0) &&
      FontObjectLaw.Get("SubSpellFont") ==
          new FontObjectSpec(FontFace.FrizQt, 10f, 0xff003359, null, 0) &&
      FontObjectLaw.Get("GameTooltipHeaderText") ==
          new FontObjectSpec(FontFace.FrizQt, 14f, 0xffffffff, null, 0) &&
      FontObjectLaw.Get("GameTooltipText") ==
          new FontObjectSpec(FontFace.FrizQt, 12f, 0xffffffff, null, 0) &&
      FontObjectLaw.Get("NumberFontNormal") ==
          new FontObjectSpec(FontFace.ArialN, 14f, 0xffffffff, null, 1) &&
      FontObjectLaw.Get("NumberFontNormalSmall").Outline == 2,
    "FontObjectLaw drift from the build-5875 Fonts.xml transcription");
Check(FontObjectLaw.Get("GameTooltipHeaderText").Height == SpellTooltipLaw.HeaderFontHeight &&
      FontObjectLaw.Get("GameTooltipText").Height == SpellTooltipLaw.TextFontHeight &&
      FontObjectLaw.Get("GameFontNormal").Height == SpellbookLaw.NameFontHeight &&
      FontObjectLaw.Get("SubSpellFont").Height == SpellbookLaw.RankFontHeight,
    "FontObjectLaw heights disagree with the spellbook/tooltip law constants");

var rawTextBaseline = new Dictionary<string, int>
{
    ["Program.Auction.cs"] = 5,
    ["Program.Bank.cs"] = 1,
    ["Program.CharacterPage.cs"] = 1,
    ["Program.Chat.cs"] = 3,
    ["Program.GameObjects.cs"] = 1,
    ["Program.Guild.cs"] = 3,
    ["Program.Help.cs"] = 4,
    ["Program.Inventory.cs"] = 0,
    ["Program.Keybindings.cs"] = 3,
    ["Program.Loot.cs"] = 2,
    ["Program.Macro.cs"] = 2,
    ["Program.Mail.cs"] = 0,
    ["Program.Minimap.cs"] = 1,
    ["Program.Professions.cs"] = 6,
    ["Program.Quest.cs"] = 4,
    ["Program.Social.cs"] = 3,
    ["Program.Tabard.cs"] = 1,
    ["Program.Talents.cs"] = 2,
    ["Program.Trade.cs"] = 2,
    ["Program.Trainer.cs"] = 4,
    ["Program.VanillaUi.cs"] = 4,
    ["Program.Vendor.cs"] = 4,
};
string panelSourceDir = Path.Combine(ClientConfig.FindRepoRoot(), "MSUIClient");
var rawTextPattern = new System.Text.RegularExpressions.Regex(
    @"AddText\s*\(\s*ImGui\s*\.\s*GetFont\s*\(\s*\)");
foreach (string panelFile in Directory.GetFiles(panelSourceDir, "Program.*.cs"))
{
    string name = Path.GetFileName(panelFile);
    int raw = rawTextPattern.Matches(File.ReadAllText(panelFile)).Count;
    int allowed = rawTextBaseline.GetValueOrDefault(name, 0);
    Check(raw <= allowed,
        $"{name}: {raw} raw AddText(ImGui.GetFont()) draw(s) exceed the migration baseline " +
        $"of {allowed}. Draw gameplay text through GameText/FontObjectLaw " +
        "(docs/current/ui/UI_TEXT_PARITY_PLAYBOOK.md); never add raw default-font draws.");
    if (raw < allowed)
        Console.WriteLine($"[text-fence] {name} is below baseline ({raw}/{allowed}) - " +
                          "lower its entry in interface-wire-check to lock in the migration");
}

Console.WriteLine("interface wire checks passed: minimap projection/area/zone + action icons + gossip + vendor + trainer + quest + loot + inventory + bank + mail + auction + profession + guild + social + trade + tabard + talents + gameobjects + taxi opcodes/bodies/bounds/state/render-binding + gameplay-text fence");
