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

Console.WriteLine("interface wire checks passed: gossip + vendor + trainer + quest opcodes/bodies/bounds");
