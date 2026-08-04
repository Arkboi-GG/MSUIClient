using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}

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
SkillLineCatalog spellbookSkills = SkillLineCatalog.Load(spellbookMpq) ??
    throw new InvalidDataException("Skill-line DBCs unavailable");
SpellInfo BookSpell(uint id) => spellbookSpells.TryGet(id, out SpellInfo value) ? value
    : throw new InvalidDataException($"spell {id} missing");
const byte Human = 1, Warrior = 1, Mage = 8;
Check(spellbookSkills.SpellTab(6603, Human, Mage) == 0, "Attack did not collapse into General");
Check(spellbookSkills.SpellTab(133, Human, Mage) == 8, "Fireball did not route to Fire");
Check(spellbookSkills.SpellTab(116, Human, Mage) == 6, "Frostbolt did not route to Frost");
Check(spellbookSkills.SpellTab(1459, Human, Mage) == 237, "Arcane Intellect did not route to Arcane");
Check(spellbookSkills.SpellTab(133, Human, Warrior) == 0,
    "cross-class Fireball did not collapse into General");
Check(SpellbookLaw.Eligible(BookSpell(133)), "Fireball failed the spellbook add-gate");
Check(!SpellbookLaw.Eligible(BookSpell(668)), "Common language survived the spellbook add-gate");
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
byte[] sendMail = WorldSession.BuildSendMailBody(trainerGuid, "Test", "Subject", "Body", 9, 100, 200);
var mailReader = new PacketReader(sendMail);
Check(mailReader.ReadU64() == trainerGuid && mailReader.ReadCString() == "Test" &&
      mailReader.ReadCString() == "Subject" && mailReader.ReadCString() == "Body" &&
      mailReader.ReadU32() == 41 && mailReader.ReadU32() == 0 && mailReader.ReadU64() == 9 &&
      mailReader.ReadU32() == 100 && mailReader.ReadU32() == 200 && mailReader.ReadU64() == 0 &&
      mailReader.ReadU8() == 0 && mailReader.Remaining == 0, "send mail body order and constants");

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
    ["Program.Inventory.cs"] = 3,
    ["Program.Keybindings.cs"] = 3,
    ["Program.Loot.cs"] = 2,
    ["Program.Macro.cs"] = 2,
    ["Program.Mail.cs"] = 7,
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
