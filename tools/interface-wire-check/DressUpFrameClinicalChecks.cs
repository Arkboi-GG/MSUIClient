using MSUIClient;
using MSUIClient.Engine.UI;

internal static class DressUpFrameClinicalChecks
{
    public static void Run()
    {
        Check(DressUpFrameUiLaw.Width == 384 && DressUpFrameUiLaw.Height == 512 &&
              DressUpFrameUiLaw.Top == 104 &&
              DressUpFrameUiLaw.Portrait == new DressUpFrameUiLaw.LogicalRect(7, 6, 60, 60) &&
              DressUpFrameUiLaw.Model == new DressUpFrameUiLaw.LogicalRect(23, 76, 316, 351) &&
              DressUpFrameUiLaw.RotateLeft == new DressUpFrameUiLaw.LogicalRect(21, 75, 35, 35) &&
              DressUpFrameUiLaw.RotateRight == new DressUpFrameUiLaw.LogicalRect(56, 75, 35, 35) &&
              DressUpFrameUiLaw.Reset == new DressUpFrameUiLaw.LogicalRect(185, 411, 80, 22) &&
              DressUpFrameUiLaw.Close == new DressUpFrameUiLaw.LogicalRect(265, 411, 80, 22) &&
              DressUpFrameUiLaw.CloseX == new DressUpFrameUiLaw.LogicalRect(322, 9, 32, 32),
            "DressUpFrame authored geometry drift");
        Check(DressUpFrameUiLaw.EquipmentSlot(1) == 0 &&
              DressUpFrameUiLaw.EquipmentSlot(20) == 4 &&
              DressUpFrameUiLaw.EquipmentSlot(16) == 14 &&
              DressUpFrameUiLaw.EquipmentSlot(13) == 15 &&
              DressUpFrameUiLaw.EquipmentSlot(23) == 16 &&
              DressUpFrameUiLaw.EquipmentSlot(26) == 17 &&
              DressUpFrameUiLaw.EquipmentSlot(0) == -1 &&
              DressUpFrameUiLaw.HeldLanesCoexist(13, 14) &&
              DressUpFrameUiLaw.HeldLanesCoexist(21, 23) &&
              !DressUpFrameUiLaw.HeldLanesCoexist(17, 14) &&
              !DressUpFrameUiLaw.HeldLanesCoexist(13, 15) &&
              DressUpFrameUiLaw.RangedUsesOffLane(15) &&
              !DressUpFrameUiLaw.RangedUsesOffLane(26) &&
              DressUpFrameUiLaw.BackgroundRace("Gnome") == "Dwarf" &&
              DressUpFrameUiLaw.BackgroundRace("Troll") == "Orc" &&
              DressUpFrameUiLaw.BackgroundRace("unknown") == "Orc",
            "DressUpFrame equipment-lane or race-backdrop law drift");

        string root = ClientConfig.FindRepoRoot();
        string dressUp = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.DressUp.cs"));
        string portraits = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.Portraits.cs"));
        string itemRef = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.ItemRef.cs"));
        string inventory = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Inventory.cs"));
        string paperDoll = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.CharacterPage.cs"));
        string inspect = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Inspect.cs"));
        string vendor = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Vendor.Render.cs"));
        string quest = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Quest.cs"));
        string loot = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Loot.cs"));
        string groupLoot = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.GroupLoot.cs"));
        Check(dressUp.Contains("BeginVanillaWindow(\"##dress-up\"", StringComparison.Ordinal) &&
              dressUp.Contains("DressUpFrameUiLaw.Model", StringComparison.Ordinal) &&
              dressUp.Contains("new CharacterRenderer(_gl, _config)", StringComparison.Ordinal) &&
              dressUp.Contains("_dressUpSubstitutions", StringComparison.Ordinal) &&
              dressUp.Contains("_dressUpHeldOrder", StringComparison.Ordinal) &&
              dressUp.Contains("ResolveDressUpPending", StringComparison.Ordinal) &&
              dressUp.Contains("RangedUsesOffLane", StringComparison.Ordinal) &&
              portraits.Contains("new PortraitRenderTarget(gl, 632, 702)", StringComparison.Ordinal) &&
              portraits.Contains("_dressUpRenderer.Render(camera, state)", StringComparison.Ordinal) &&
              itemRef.Contains("TryOnDressUp(entry)", StringComparison.Ordinal) &&
              inventory.Contains("TryOnDressUp(instance!.Entry)", StringComparison.Ordinal) &&
              paperDoll.Contains("TryOnDressUp(instance!.Entry)", StringComparison.Ordinal) &&
              inspect.Contains("TryOnDressUp(entry)", StringComparison.Ordinal) &&
              vendor.Contains("TryOnDressUp(row.ItemId)", StringComparison.Ordinal) &&
              quest.Contains("TryOnDressUp(row.ItemId)", StringComparison.Ordinal) &&
              loot.Contains("TryOnDressUp(row.ItemId)", StringComparison.Ordinal) &&
              groupLoot.Contains("TryOnDressUp(roll.ItemId)", StringComparison.Ordinal),
            "DressUpFrame lost its isolated model, pending cache, or principal ctrl-click entry points");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
