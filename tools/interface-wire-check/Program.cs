using MSUIClient;
using MSUIClient.Net;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}

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

Check((ushort)Op.CMSG_QUESTGIVER_STATUS_QUERY == 386 && (ushort)Op.SMSG_QUESTGIVER_STATUS == 387 &&
      (ushort)Op.CMSG_QUESTGIVER_HELLO == 388 && (ushort)Op.SMSG_QUESTGIVER_QUEST_LIST == 389 &&
      (ushort)Op.CMSG_QUESTGIVER_QUERY_QUEST == 390 && (ushort)Op.SMSG_QUESTGIVER_QUEST_DETAILS == 392 &&
      (ushort)Op.CMSG_QUESTGIVER_ACCEPT_QUEST == 393 && (ushort)Op.SMSG_QUESTGIVER_QUEST_COMPLETE == 401 &&
      (ushort)Op.SMSG_QUESTUPDATE_ADD_KILL == 409, "quest opcodes");
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

Console.WriteLine("interface wire checks passed: gossip + vendor + trainer + quest + loot + inventory + bank + mail + auction + profession + guild opcodes/bodies/bounds/state");
