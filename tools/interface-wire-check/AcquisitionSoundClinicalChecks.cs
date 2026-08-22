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

        string root = ClientConfig.FindRepoRoot();
        string inventory = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Inventory.cs"));
        string loot = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Loot.cs"));
        Check(inventory.Contains("ObserveMoneySound();", StringComparison.Ordinal) &&
              inventory.Contains("AcquisitionSoundLaw.PickupKit", StringComparison.Ordinal) &&
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
