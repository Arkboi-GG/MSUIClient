using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;

internal static class AcquisitionSoundClinicalChecks
{
    public static void Run()
    {
        Check(!AcquisitionSoundLaw.PlayCoin(null, 100) &&
              !AcquisitionSoundLaw.PlayCoin(100, 100) &&
              AcquisitionSoundLaw.PlayCoin(100, 101) &&
              AcquisitionSoundLaw.PlayCoin(101, 1) &&
              AcquisitionSoundLaw.CoinCue == "LOOTWINDOWCOINSOUND",
            "PLAYER_FIELD_COINAGE seed/change audio law drift");

        ItemGroupSoundsCatalog sounds = ItemGroupSoundsCatalog.FromRows(
            (1, 273, 274, 275, 0), (7, 1185, 1202, 0, 0));
        Check(sounds.Count == 2 && sounds.Kit(1, ItemSoundGesture.Pickup) == 273 &&
              sounds.Kit(1, ItemSoundGesture.PutDown) == 274 &&
              sounds.Kit(7, ItemSoundGesture.Use) is null &&
              sounds.Kit(999, ItemSoundGesture.Pickup) is null,
            "ItemGroupSounds gesture lookup drift");

        // Exercise the actual display -> material -> gesture join, including unknown metadata.
        // Group zero deliberately has a sound: a missing display must not fall back to it.
        byte[] fixture = new byte[20 + 2 * 92 + 1];
        "WDBC"u8.CopyTo(fixture);
        BitConverter.GetBytes(2).CopyTo(fixture, 4);
        BitConverter.GetBytes(23).CopyTo(fixture, 8);
        BitConverter.GetBytes(92).CopyTo(fixture, 12);
        BitConverter.GetBytes(1).CopyTo(fixture, 16);
        BitConverter.GetBytes(100u).CopyTo(fixture, 20);
        BitConverter.GetBytes(7u).CopyTo(fixture, 20 + 11 * 4);
        BitConverter.GetBytes(101u).CopyTo(fixture, 20 + 92);
        ItemDisplayTable displays = ItemDisplayTable.Parse(fixture)!;
        ItemGroupSoundsCatalog materials = ItemGroupSoundsCatalog.FromRows(
            (0, 1183, 1200, 0, 0), (7, 1185, 1202, 0, 0));
        Check(AcquisitionSoundLaw.PickupKit(100, displays, materials) == 1185 &&
              AcquisitionSoundLaw.GestureKit(100, displays, materials, ItemSoundGesture.PutDown) == 1202 &&
              AcquisitionSoundLaw.GestureKit(100, displays, materials, ItemSoundGesture.Use) is null &&
              AcquisitionSoundLaw.PickupKit(101, displays, materials) == 1183 &&
              AcquisitionSoundLaw.PickupKit(999, displays, materials) is null &&
              AcquisitionSoundLaw.PickupKit(100, null, materials) is null &&
              AcquisitionSoundLaw.GestureKit(100, displays, null, ItemSoundGesture.PutDown) is null,
            "item cursor material/gesture join or unknown-display silence drift");

        string root = ClientConfig.FindRepoRoot();
        string inventory = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Inventory.cs"));
        string loot = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Loot.cs"));
        Check(inventory.Contains("ObserveMoneySound();", StringComparison.Ordinal) &&
              inventory.Contains("AcquisitionSoundLaw.GestureKit", StringComparison.Ordinal) &&
              loot.Contains("PlayItemPickupSound(_loot.Items[0].DisplayInfoId)",
                  StringComparison.Ordinal) &&
              loot.Contains("PlayItemPickupSound(item.DisplayInfoId)", StringComparison.Ordinal) &&
              !loot.Contains("ApplyItemPushResult(byte[] body)\n    {\n        PlayItemPickupSound",
                  StringComparison.Ordinal),
            "coin watcher or optimistic loot-row pickup cue wiring drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
