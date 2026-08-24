using MSUIClient;
using MSUIClient.Formats;

internal static class MountSheatheSoundClinicalChecks
{
    public static void Run()
    {
        SheatheSoundCatalog catalog = SheatheSoundCatalog.FromRows(
            (2, 0, 1, 698, 700), (2, 0, 2, 697, 699), (4, 6, 0, 696, 701));
        Check(catalog.Count == 3 &&
              catalog.TryGet(2, 15, 1, out SheatheSoundPair dagger) &&
              dagger == new SheatheSoundPair(698, 700) &&
              catalog.TryGet(2, 2, 2, out SheatheSoundPair bow) &&
              bow == new SheatheSoundPair(697, 699) &&
              catalog.TryGet(4, 6, 6, out SheatheSoundPair shield) &&
              shield == new SheatheSoundPair(696, 701),
            "SheatheSoundLookups material/subclass/shield fallback drift");

        string root = ClientConfig.FindRepoRoot();
        string worldSound = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Sound.cs"));
        string sheath = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.Sheath.cs"));
        Check(worldSound.Contains("SpiritWolf (DONOTRENAME)", StringComparison.Ordinal) &&
              worldSound.Contains("previous != 0 && mount == 0 && playbackAllowed",
                  StringComparison.Ordinal) &&
              worldSound.Contains("_knownMountSoundDisplays[unit.Guid] = mount;",
                  StringComparison.Ordinal),
            "silent first-sight/mount-up or fixed dismount trigger drift");
        string character = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Units",
            "CharacterRenderer.cs"));
        string animator = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Units",
            "M2Animator.cs"));
        string inventory = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Inventory.cs"));
        Check(sheath.Contains("PlayCeremonialSheatheSounds(ceremonialState);", StringComparison.Ordinal) &&
              !sheath.Contains("TriggerOneShot(animation)", StringComparison.Ordinal) &&
              sheath.Contains("foreach (int equipmentSlot in new[] { 15, 16 })",
                  StringComparison.Ordinal) &&
              sheath.Contains("drawing ? pair.Unsheathe : pair.Sheathe",
                  StringComparison.Ordinal) &&
              sheath.Contains("_character.BeginSheathCeremony()", StringComparison.Ordinal) &&
              sheath.Contains("_character.ConsumeSheathSwap()", StringComparison.Ordinal) &&
              character.Contains("89, 90, 92", StringComparison.Ordinal) &&
              character.Contains("EvaluateWithArmOverlays", StringComparison.Ordinal) &&
              character.Contains("$SHL", StringComparison.Ordinal) &&
              animator.Contains("ResolveArmRoots", StringComparison.Ordinal) &&
              animator.Contains("ApplyOverlayChannels", StringComparison.Ordinal) &&
              inventory.Contains("existing.EquipmentSlot == piece.EquipmentSlot", StringComparison.Ordinal) &&
              inventory.Contains("existing.Sheath == piece.Sheath", StringComparison.Ordinal) &&
              !sheath.Contains("SetVisualSheath(byte state, bool volunteer = true)\n    {\n        PlayCeremonialSheatheSounds",
                  StringComparison.Ordinal),
            "ceremony-only per-arm sheathe sound routing drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
